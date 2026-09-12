using System.Buffers;
using System.Security.Cryptography;
using System.Threading.Channels;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Packages;

namespace GRBKP.Core.Transfers;

public sealed record StreamingBackupOptions(
    int ChannelCapacity = 8,
    int BufferSize = 1024 * 1024,
    int MaxConcurrentCopies = 4)
{
    public int ValidatedChannelCapacity => ChannelCapacity > 0
        ? ChannelCapacity
        : throw new ArgumentOutOfRangeException(nameof(ChannelCapacity));

    public int ValidatedBufferSize => BufferSize > 0
        ? BufferSize
        : throw new ArgumentOutOfRangeException(nameof(BufferSize));

    /// <summary>
    /// Quantos arquivos são copiados ao mesmo tempo. Em SSD externo por USB, a latência por
    /// arquivo (abrir, criar temporário, renomear, ajustar metadados) domina o tempo com arquivos
    /// pequenos, e sobrepor algumas cópias esconde boa parte dela.
    /// </summary>
    public int ValidatedMaxConcurrentCopies => MaxConcurrentCopies > 0
        ? MaxConcurrentCopies
        : throw new ArgumentOutOfRangeException(nameof(MaxConcurrentCopies));
}

public sealed record BackupRequest(
    Guid OperationId,
    string PackageRoot,
    PackageMetadata Metadata);

public sealed record BackupOperationResult(
    Guid OperationId,
    PackageState State,
    DurableCounters Counters,
    IReadOnlyList<OperationError> Errors);

public sealed record OperationError(string Code, string Message, string? RelativePath = null);

public sealed class StreamingBackupEngine(
    IFileSystemAdapter fileSystem,
    BackupPackageModule packageModule,
    IClock clock,
    StreamingBackupOptions? options = null)
{
    private readonly StreamingBackupOptions options = options ?? new StreamingBackupOptions();

    public async Task<BackupOperationResult> RunAsync(
        BackupRequest request,
        IAsyncEnumerable<TransferItem> items,
        CancellationToken cancellationToken = default)
        => await RunCoreAsync(request, items, resume: false, cancellationToken).ConfigureAwait(false);

    public async Task<BackupOperationResult> ResumeAsync(
        BackupRequest request,
        IAsyncEnumerable<TransferItem> items,
        CancellationToken cancellationToken = default)
        => await RunCoreAsync(request, items, resume: true, cancellationToken).ConfigureAwait(false);

    private async Task<BackupOperationResult> RunCoreAsync(
        BackupRequest request,
        IAsyncEnumerable<TransferItem> items,
        bool resume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(clock);

        var capabilities = await fileSystem.GetCapabilitiesAsync(
            request.PackageRoot,
            cancellationToken).ConfigureAwait(false);
        if (!capabilities.SupportsLargeFiles)
        {
            return new BackupOperationResult(
                request.OperationId,
                PackageState.Failed,
                DurableCounters.Empty,
                [new OperationError("destination-filesystem-large-files-unsupported", capabilities.Format)]);
        }

        if (resume)
        {
            var existing = await packageModule.ReadOperationAsync(
                request.PackageRoot,
                cancellationToken).ConfigureAwait(false);
            if (Enum.TryParse<PackageState>(existing.State, ignoreCase: true, out var existingState) &&
                existingState == PackageState.Complete)
            {
                return new BackupOperationResult(
                    request.OperationId,
                    PackageState.Complete,
                    existing.Counters,
                    []);
            }
        }

        await using var writer = resume
            ? await packageModule.OpenWriterAsync(
                request.PackageRoot,
                cancellationToken).ConfigureAwait(false)
            : await packageModule.CreateWriterAsync(
                request.PackageRoot,
                request.OperationId,
                request.Metadata,
                cancellationToken).ConfigureAwait(false);
        if (!resume)
        {
            await writer.TransitionAsync(PackageState.Inspecting, cancellationToken).ConfigureAwait(false);
        }

        await writer.TransitionAsync(PackageState.Copying, cancellationToken).ConfigureAwait(false);

        var channel = Channel.CreateBounded<TransferItem>(new BoundedChannelOptions(
            options.ValidatedChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceAsync(items, channel.Writer, linkedCancellation.Token);
        var counters = writer.Checkpoint.Counters;
        var resumeAfterSequence = resume ? writer.Checkpoint.LastConfirmedSequence : 0;
        var failures = new List<OperationError>();
        var window = new Queue<(TransferItem Item, Task<FileManifestEntry> Copy)>();

        try
        {
            // Janela deslizante: até N cópias acontecem ao mesmo tempo, mas o COMMIT é sempre na
            // ordem do produtor (sempre a cópia mais antiga da janela). Isso é obrigatório, não
            // estético: a restauração percorre o manifesto pulando tudo com sequência menor ou
            // igual ao último confirmado, então um manifesto fora de ordem faria a restauração
            // pular arquivos — perda de dados silenciosa. Manter o commit sequencial também
            // mantém um único escritor do manifesto/journal, sem necessidade de trava.
            await foreach (var item in channel.Reader
                .ReadAllAsync(linkedCancellation.Token)
                .ConfigureAwait(false))
            {
                if (item.Sequence <= resumeAfterSequence)
                {
                    continue;
                }

                window.Enqueue((item, CopyOneAsync(request, item, linkedCancellation.Token)));
                if (window.Count >= options.ValidatedMaxConcurrentCopies)
                {
                    counters = await CommitOldestAsync(
                        writer, window, counters, failures, linkedCancellation.Token)
                        .ConfigureAwait(false);
                }
            }

            while (window.Count > 0)
            {
                counters = await CommitOldestAsync(
                    writer, window, counters, failures, linkedCancellation.Token)
                    .ConfigureAwait(false);
            }

            await producer.ConfigureAwait(false);
            await writer.CompleteAsync(counters, cancellationToken).ConfigureAwait(false);
            return new BackupOperationResult(
                request.OperationId,
                PackageState.Complete,
                counters,
                failures);
        }
        catch (StorageDisconnectedException exception)
        {
            linkedCancellation.Cancel();
            await ObserveWindowAsync(request, window).ConfigureAwait(false);
            await ObserveProducerAfterCancellationAsync(producer).ConfigureAwait(false);
            try
            {
                await writer.TransitionAsync(PackageState.Paused, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // A mídia pode estar totalmente ausente; o último checkpoint durável prevalece.
            }

            return new BackupOperationResult(
                request.OperationId,
                PackageState.Paused,
                counters,
                [.. failures, new OperationError("storage-disconnected", exception.Message)]);
        }
        catch (Exception exception) when (FileSystemFailures.IsDestinationDiskFull(exception))
        {
            // Pausa (não falha definitiva): liberar espaço e repetir aproveita tudo que já foi
            // copiado, em vez de recomeçar o backup do zero.
            linkedCancellation.Cancel();
            await ObserveWindowAsync(request, window).ConfigureAwait(false);
            await ObserveProducerAfterCancellationAsync(producer).ConfigureAwait(false);
            try
            {
                await writer.TransitionAsync(PackageState.Paused, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception transitionFailure) when (
                transitionFailure is IOException or UnauthorizedAccessException)
            {
                // Sem espaço nem para o próprio checkpoint; o último durável prevalece.
            }

            return new BackupOperationResult(
                request.OperationId,
                PackageState.Paused,
                counters,
                [.. failures, new OperationError(
                    "destination-disk-full",
                    "O disco de destino ficou sem espaço. Libere espaço antes de refazer o " +
                    "backup deste perfil — o pacote incompleto pode ser apagado.")]);
        }
        catch
        {
            linkedCancellation.Cancel();
            await ObserveWindowAsync(request, window).ConfigureAwait(false);
            await ObserveProducerAfterCancellationAsync(producer).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Fecha a cópia mais antiga da janela: espera terminar e a registra no pacote. É aqui que a
    /// ordem do manifesto é garantida, porque a janela é uma fila alimentada na ordem do produtor.
    /// </summary>
    private static async Task<DurableCounters> CommitOldestAsync(
        BackupPackageWriter writer,
        Queue<(TransferItem Item, Task<FileManifestEntry> Copy)> window,
        DurableCounters counters,
        List<OperationError> failures,
        CancellationToken cancellationToken)
    {
        var (item, copyTask) = window.Dequeue();

        FileManifestEntry copied;
        try
        {
            copied = await copyTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (IsSingleFileFailure(exception))
        {
            // Um arquivo ilegível não pode derrubar o backup inteiro: num perfil real sempre há
            // arquivos travados por outro processo (bases de e-mail/navegador abertos) ou sem
            // permissão de leitura (ACL de um SID de outra máquina).
            var updated = counters with { FailedFiles = counters.FailedFiles + 1 };
            failures.Add(new OperationError(
                "file-copy-failed", exception.Message, item.PackageRelativePath));
            await writer.AppendFailedFileAsync(
                item.Sequence,
                item.ModuleId,
                item.PackageRelativePath,
                exception.Message,
                updated,
                cancellationToken).ConfigureAwait(false);
            return updated;
        }

        var advanced = counters with
        {
            CopiedFiles = counters.CopiedFiles + 1,
            CopiedBytes = checked(counters.CopiedBytes + copied.Length)
        };
        await writer.AppendCommittedFileAsync(copied, advanced, cancellationToken)
            .ConfigureAwait(false);
        return advanced;
    }

    /// <summary>
    /// Depois de abortar (mídia desconectada, disco cheio), fecha a janela de duas formas:
    ///
    /// 1. Observa as cópias em voo — senão suas exceções virariam exceções de Task não observada.
    /// 2. APAGA do pacote as que chegaram a terminar mas nunca foram registradas no manifesto.
    ///    Com cópia paralela isso acontece naturalmente: a cópia do arquivo 6 pode terminar antes
    ///    de o arquivo 4 abortar a operação. Deixá-las ali criaria arquivos dentro do pacote que
    ///    o manifesto não menciona — invisíveis para a restauração e para a conferência, mas
    ///    ocupando espaço. No caso de disco cheio, apagá-las ainda libera espaço.
    /// </summary>
    private async Task ObserveWindowAsync(
        BackupRequest request,
        Queue<(TransferItem Item, Task<FileManifestEntry> Copy)> window)
    {
        while (window.Count > 0)
        {
            var (item, copyTask) = window.Dequeue();
            try
            {
                await copyTask.ConfigureAwait(false);
            }
            catch
            {
                // A falha que abortou a operação é a evidência relevante.
                continue;
            }

            try
            {
                await fileSystem.DeleteAsync(
                    PackagePath.ResolveInside(request.PackageRoot, item.PackageRelativePath),
                    recursive: false,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A mídia pode ter sumido por completo; o manifesto continua sendo a verdade.
            }
        }
    }

    /// <summary>
    /// Falha isolada de UM arquivo (ilegível, travado, sem permissão) — o backup continua e
    /// contabiliza. Nunca inclui <see cref="StorageDisconnectedException"/> (que é IOException,
    /// mas significa a mídia inteira ter sumido e precisa pausar a operação) nem cancelamento.
    /// </summary>
    private static bool IsSingleFileFailure(Exception exception) =>
        exception is not StorageDisconnectedException &&
        !FileSystemFailures.IsDestinationDiskFull(exception) &&
        exception is IOException or UnauthorizedAccessException;

    private static async Task ObserveProducerAfterCancellationAsync(Task producer)
    {
        try
        {
            await producer.ConfigureAwait(false);
        }
        catch
        {
            // O resultado do consumidor é a evidência relevante após cancelar o produtor.
        }
    }

    private static async Task ProduceAsync(
        IAsyncEnumerable<TransferItem> items,
        ChannelWriter<TransferItem> writer,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await foreach (var item in items
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                await writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    private async Task<FileManifestEntry> CopyOneAsync(
        BackupRequest request,
        TransferItem item,
        CancellationToken cancellationToken)
    {
        var destinationPath = PackagePath.ResolveInside(
            request.PackageRoot,
            item.PackageRelativePath);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Destino sem diretório pai.");
        await fileSystem.CreateDirectoryAsync(destinationDirectory, cancellationToken)
            .ConfigureAwait(false);

        var temporaryPath = destinationPath + $".grbkp.partial.{request.Metadata.BackupId:N}";
        long sourceLength;
        long bytesCopied = 0;
        string? sha256 = null;
        var buffer = ArrayPool<byte>.Shared.Rent(options.ValidatedBufferSize);
        IncrementalHash? hash = item.RequiresSha256
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            : null;

        try
        {
            await using var source = await fileSystem.OpenReadAsync(
                item.SourcePath,
                cancellationToken).ConfigureAwait(false);
            sourceLength = source.Length;
            await using (var destination = await fileSystem.OpenWriteAsync(
                temporaryPath,
                FileWriteMode.CreateOrOverwrite,
                cancellationToken).ConfigureAwait(false))
            {
                while (true)
                {
                    var read = await source.ReadAsync(
                        buffer.AsMemory(0, options.ValidatedBufferSize),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    hash?.AppendData(buffer, 0, read);
                    await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken).ConfigureAwait(false);
                    bytesCopied = checked(bytesCopied + read);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (bytesCopied != sourceLength)
            {
                throw new IOException(
                    $"O tamanho copiado ({bytesCopied}) difere do handle aberto ({sourceLength}).");
            }

            sha256 = hash is null ? null : Convert.ToHexString(hash.GetHashAndReset());
        }
        catch
        {
            await fileSystem.DeleteAsync(temporaryPath, recursive: false, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            hash?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await fileSystem.MoveAsync(
            temporaryPath,
            destinationPath,
            overwrite: true,
            cancellationToken).ConfigureAwait(false);

        var metadata = item.Metadata ?? new FileMetadata(
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            FileAttributes.Normal);
        await fileSystem.SetMetadataAsync(destinationPath, metadata, cancellationToken)
            .ConfigureAwait(false);

        return new FileManifestEntry(
            item.Sequence,
            item.ModuleId,
            item.Classification,
            item.SourceRelativePath ?? item.PackageRelativePath,
            item.PackageRelativePath,
            bytesCopied,
            metadata.CreationTimeUtc,
            metadata.LastWriteTimeUtc,
            metadata.Attributes,
            FileTransferState.Copied,
            sha256);
    }
}

public sealed class StorageDisconnectedException(string message, Exception? innerException = null)
    : IOException(message, innerException);

/// <summary>
/// Distingue falhas que valem para a operação INTEIRA das que valem para um arquivo só. Disco
/// cheio é o caso crítico: sem essa distinção, a resiliência por arquivo transformaria um SSD de
/// destino lotado em "backup concluído, com 50 mil arquivos não copiados" — verdade técnica,
/// desastre prático, porque todo arquivo seguinte também falharia pelo mesmo motivo.
/// </summary>
public static class FileSystemFailures
{
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);

    public static bool IsDestinationDiskFull(Exception exception) =>
        exception is IOException &&
        (exception.HResult == ErrorDiskFull || exception.HResult == ErrorHandleDiskFull);
}
