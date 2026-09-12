using System.Buffers;
using System.Security.Cryptography;
using System.Threading.Channels;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Packages;

namespace GRBKP.Core.Transfers;

public sealed record StreamingBackupOptions(int ChannelCapacity = 8, int BufferSize = 1024 * 1024)
{
    public int ValidatedChannelCapacity => ChannelCapacity > 0
        ? ChannelCapacity
        : throw new ArgumentOutOfRangeException(nameof(ChannelCapacity));

    public int ValidatedBufferSize => BufferSize > 0
        ? BufferSize
        : throw new ArgumentOutOfRangeException(nameof(BufferSize));
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

        try
        {
            await foreach (var item in channel.Reader
                .ReadAllAsync(linkedCancellation.Token)
                .ConfigureAwait(false))
            {
                if (item.Sequence <= resumeAfterSequence)
                {
                    continue;
                }

                FileManifestEntry copied;
                try
                {
                    copied = await CopyOneAsync(
                        request,
                        item,
                        linkedCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsSingleFileFailure(exception))
                {
                    // Um arquivo ilegível não pode derrubar o backup inteiro: num perfil real
                    // sempre há arquivos travados por outro processo (bases de e-mail/navegador
                    // abertos) ou sem permissão de leitura (ACL de um SID de outra máquina).
                    counters = counters with { FailedFiles = counters.FailedFiles + 1 };
                    failures.Add(new OperationError(
                        "file-copy-failed", exception.Message, item.PackageRelativePath));
                    await writer.AppendFailedFileAsync(
                        item.Sequence,
                        item.ModuleId,
                        item.PackageRelativePath,
                        exception.Message,
                        counters,
                        linkedCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                counters = counters with
                {
                    CopiedFiles = counters.CopiedFiles + 1,
                    CopiedBytes = checked(counters.CopiedBytes + copied.Length)
                };
                await writer.AppendCommittedFileAsync(
                    copied,
                    counters,
                    linkedCancellation.Token).ConfigureAwait(false);
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
        catch
        {
            linkedCancellation.Cancel();
            await ObserveProducerAfterCancellationAsync(producer).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Falha isolada de UM arquivo (ilegível, travado, sem permissão) — o backup continua e
    /// contabiliza. Nunca inclui <see cref="StorageDisconnectedException"/> (que é IOException,
    /// mas significa a mídia inteira ter sumido e precisa pausar a operação) nem cancelamento.
    /// </summary>
    private static bool IsSingleFileFailure(Exception exception) =>
        exception is not StorageDisconnectedException &&
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
