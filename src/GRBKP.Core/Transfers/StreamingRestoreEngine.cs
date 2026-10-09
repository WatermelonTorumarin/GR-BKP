using System.Buffers;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Packages;
using GRBKP.Core.Reporting;

namespace GRBKP.Core.Transfers;

public sealed record RestoreRequest(
    Guid OperationId,
    string PackageRoot,
    string DestinationProfileRoot,
    string DestinationPublicRoot,
    string OperationRoot,
    /// <summary>
    /// Quais pastas pessoais devolver à máquina (Desktop, Documents, Downloads, Pictures).
    /// <c>null</c> = todas. Só existe na restauração: o pacote continua inteiro, e o técnico
    /// escolhe o que recoloca — útil para devolver só o que faltou, sem repetir o resto.
    /// </summary>
    IReadOnlyCollection<string>? OnlyPersonalFolders = null);

public sealed record RestoreOperationResult(
    Guid OperationId,
    RestoreState State,
    DurableCounters Counters,
    IReadOnlyList<PreservedItem> PreservedItems,
    IReadOnlyList<OperationError> Errors);

public sealed record PreservedItem(string ModuleId, string PackageRelativePath, string ReasonCode);

public sealed class StreamingRestoreEngine(
    IFileSystemAdapter fileSystem,
    BackupPackageModule packageModule,
    IClock clock,
    int bufferSize = 1024 * 1024,
    ReportModule? reportModule = null,
    IReadOnlySet<string>? modulesHandledSeparately = null,
    int maxConcurrentCopies = 4)
{
    private readonly OperationCheckpointStore checkpointStore = new(fileSystem, clock);
    private readonly ReportModule reportModule = reportModule ?? new ReportModule(fileSystem, clock);

    /// <summary>
    /// IDs de Module com um <c>IMigrationModule</c> real registrado — suas entradas
    /// <c>ModuleData</c> sao restauradas por <c>AppModuleRestoreCoordinator</c> via troca
    /// atomica de arvore, entao o loop arquivo-a-arquivo abaixo deve ignora-las silenciosamente
    /// (nao contar como "preservado, nao reintegrado", que continua reservado para Modules
    /// ainda nao implementados).
    /// </summary>
    private readonly IReadOnlySet<string> modulesHandledSeparately =
        modulesHandledSeparately ?? new HashSet<string>();

    /// <summary>
    /// Mesmo gargalo medido no backup: gravar o checkpoint a cada arquivo custa uma serialização
    /// + arquivo temporário + renomeação por arquivo do usuário. Grava em lotes; numa queda, a
    /// retomada refaz no máximo estes N arquivos. Estado final (concluído, pausado, falho) é
    /// sempre gravado na hora.
    /// </summary>
    private const int CheckpointEveryFiles = 64;

    private static readonly TimeSpan CheckpointEveryInterval = TimeSpan.FromSeconds(2);

    private int filesSinceCheckpoint;
    private DateTimeOffset lastCheckpointAtUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Pastas já criadas nesta operação — evita pedir ao disco a criação da mesma pasta uma vez
    /// por arquivo restaurado. Ver o mesmo no motor de backup.
    /// </summary>
    private readonly HashSet<string> createdDirectories = new(StringComparer.OrdinalIgnoreCase);

    public Task<RestoreOperationResult> RunAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(request, resume: false, null, cancellationToken);

    public Task<RestoreOperationResult> RunAsync(
        RestoreRequest request,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(request, resume: false, progress, cancellationToken);

    public Task<RestoreOperationResult> ResumeAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(request, resume: true, null, cancellationToken);

    public Task<RestoreOperationResult> ResumeAsync(
        RestoreRequest request,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(request, resume: true, progress, cancellationToken);

    private async Task<RestoreOperationResult> RunCoreAsync(
        RestoreRequest request,
        bool resume,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        }

        // O motor é reutilizado entre operações (o app o constrói uma vez): cada operação começa
        // com o lote de checkpoint zerado, para o comportamento não depender da operação anterior.
        filesSinceCheckpoint = 0;
        lastCheckpointAtUtc = DateTimeOffset.MinValue;
        createdDirectories.Clear();

        var package = await packageModule.ReadMetadataAsync(
            request.PackageRoot,
            cancellationToken).ConfigureAwait(false);
        if (package.Status != PackageState.Complete)
        {
            return new RestoreOperationResult(
                request.OperationId,
                RestoreState.Failed,
                DurableCounters.Empty,
                [],
                [new OperationError("package-not-complete", package.Status.ToString())]);
        }

        OperationCheckpoint checkpoint;
        if (resume)
        {
            checkpoint = await checkpointStore.ReadAsync(request.OperationRoot, cancellationToken)
                .ConfigureAwait(false);
            if (Enum.TryParse<RestoreState>(checkpoint.State, true, out var state) &&
                state == RestoreState.Complete)
            {
                return new RestoreOperationResult(
                    request.OperationId,
                    RestoreState.Complete,
                    checkpoint.Counters,
                    [],
                    []);
            }
        }
        else
        {
            checkpoint = new OperationCheckpoint(
                request.OperationId,
                package.BackupId,
                OperationKind.Restore,
                RestoreState.RestoringUserData.ToString(),
                null,
                0,
                null,
                DurableCounters.Empty,
                UpdatedAtUtc: clock.UtcNow);
            await checkpointStore.SaveAsync(request.OperationRoot, checkpoint, cancellationToken)
                .ConfigureAwait(false);
        }

        var counters = checkpoint.Counters;
        var preserved = new List<PreservedItem>();
        var failures = new List<OperationError>();
        var window = new Queue<(FileManifestEntry Entry, Task<EntryOutcome> Work)>();
        try
        {
            // Janela deslizante, igual à do backup: até N arquivos são copiados ao mesmo tempo,
            // mas o checkpoint SEMPRE avança na ordem do manifesto. Isso é obrigatório, não
            // estético — o ponto de retomada é uma única sequência, e avançá-lo fora de ordem
            // faria uma retomada pular arquivos que ainda não tinham sido restaurados.
            // As decisões por entrada (pular, preservar, filtrar pasta) continuam no laço serial,
            // porque são baratas; só a CÓPIA vai para a janela.
            // Linhas ilegíveis do manifesto são anotadas e puladas, não abortam a restauração.
            // Num pacote já fechado elas são perda consumada: parar por causa delas só acrescenta
            // a perda de tudo o que vinha depois — foi o que aconteceu com o pacote do marcelo,
            // onde uma linha destruída impedia a restauração de outras 51 mil entradas boas.
            // O técnico é avisado pelo resultado e pela conferência prévia do pacote.
            var unreadableLines = new List<long>();
            await foreach (var entry in packageModule
                .ReadFilesAsync(request.PackageRoot, unreadableLines, cancellationToken)
                .ConfigureAwait(false))
            {
                if (entry.Sequence <= checkpoint.LastConfirmedSequence)
                {
                    continue;
                }

                window.Enqueue((entry, StartEntryAsync(request, package, entry, cancellationToken)));
                if (window.Count >= maxConcurrentCopies)
                {
                    (counters, checkpoint) = await CommitOldestEntryAsync(
                        request, window, counters, checkpoint, preserved, failures,
                        progress, cancellationToken).ConfigureAwait(false);
                }
            }

            while (window.Count > 0)
            {
                (counters, checkpoint) = await CommitOldestEntryAsync(
                    request, window, counters, checkpoint, preserved, failures,
                    progress, cancellationToken).ConfigureAwait(false);
            }

            checkpoint = checkpoint with
            {
                State = RestoreState.Complete.ToString(),
                Counters = counters
            };
            await checkpointStore.SaveAsync(request.OperationRoot, checkpoint, cancellationToken)
                .ConfigureAwait(false);
            foreach (var linha in unreadableLines)
            {
                failures.Add(new OperationError(
                    "manifest-line-unreadable",
                    $"A linha {linha} da lista de arquivos do backup está ilegível: o arquivo que " +
                    "ela descrevia não pôde ser restaurado.",
                    "files.jsonl"));
            }

            var result = new RestoreOperationResult(
                request.OperationId,
                RestoreState.Complete,
                counters,
                preserved,
                failures);
            await reportModule.WriteRestoreReportAsync(
                request.OperationRoot,
                result,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (StorageDisconnectedException exception)
        {
            await ObserveWindowAsync(window).ConfigureAwait(false);
            checkpoint = checkpoint with
            {
                State = RestoreState.Paused.ToString(),
                ResumeState = RestoreState.RestoringUserData.ToString(),
                Counters = counters
            };
            await checkpointStore.SaveAsync(
                request.OperationRoot,
                checkpoint,
                CancellationToken.None).ConfigureAwait(false);
            return new RestoreOperationResult(
                request.OperationId,
                RestoreState.Paused,
                counters,
                preserved,
                [.. failures, new OperationError("storage-disconnected", exception.Message)]);
        }
        catch (Exception exception) when (FileSystemFailures.IsDestinationDiskFull(exception))
        {
            await ObserveWindowAsync(window).ConfigureAwait(false);
            checkpoint = checkpoint with
            {
                State = RestoreState.Paused.ToString(),
                ResumeState = RestoreState.RestoringUserData.ToString(),
                Counters = counters
            };
            await checkpointStore.SaveAsync(
                request.OperationRoot,
                checkpoint,
                CancellationToken.None).ConfigureAwait(false);
            return new RestoreOperationResult(
                request.OperationId,
                RestoreState.Paused,
                counters,
                preserved,
                [.. failures, new OperationError(
                    "destination-disk-full",
                    "O disco de destino ficou sem espaço durante a restauração. Libere espaço e " +
                    "repita a restauração deste pacote.")]);
        }
        catch (InvalidPackagePathException exception)
        {
            await ObserveWindowAsync(window).ConfigureAwait(false);
            checkpoint = checkpoint with
            {
                State = RestoreState.Failed.ToString(),
                Counters = counters
            };
            await checkpointStore.SaveAsync(
                request.OperationRoot,
                checkpoint,
                CancellationToken.None).ConfigureAwait(false);
            return new RestoreOperationResult(
                request.OperationId,
                RestoreState.Failed,
                counters,
                preserved,
                [.. failures, new OperationError(
                    exception.ErrorCode, exception.Message, exception.InvalidPath)]);
        }
    }

    /// <summary>
    /// Falha isolada de UM arquivo no destino — a restauração continua e contabiliza. Nunca
    /// inclui <see cref="StorageDisconnectedException"/> (mídia inteira ausente: precisa pausar)
    /// nem <see cref="InvalidPackagePathException"/> (pacote malformado: precisa falhar).
    /// </summary>
    private static bool IsSingleFileFailure(Exception exception) =>
        exception is not StorageDisconnectedException &&
        exception is not InvalidPackagePathException &&
        !FileSystemFailures.IsDestinationDiskFull(exception) &&
        (exception is IOException or UnauthorizedAccessException ||
            // Tamanho divergente do manifesto: aquele arquivo do pacote está corrompido (setor
            // ruim no SSD, cópia truncada). Vale pular esse e restaurar todo o resto.
            exception is InvalidDataException);

    private async Task SaveCheckpointIfDueAsync(
        string operationRoot,
        OperationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        filesSinceCheckpoint++;
        if (filesSinceCheckpoint < CheckpointEveryFiles &&
            clock.UtcNow - lastCheckpointAtUtc < CheckpointEveryInterval)
        {
            return;
        }

        await checkpointStore.SaveAsync(operationRoot, checkpoint, cancellationToken)
            .ConfigureAwait(false);
        filesSinceCheckpoint = 0;
        lastCheckpointAtUtc = clock.UtcNow;
    }

    /// <summary>
    /// O que aconteceu com uma entrada do manifesto. Calculado fora de ordem (em paralelo), mas
    /// aplicado aos contadores e ao checkpoint sempre na ordem do manifesto.
    /// </summary>
    private sealed record EntryOutcome(
        bool Copied,
        long Length,
        bool Preserved,
        bool Ignored,
        Exception? Failure);

    /// <summary>
    /// Decide e executa o trabalho de UMA entrada. Só a cópia é cara; o resto é decisão barata
    /// que poderia ficar no laço, mas fica aqui para a janela tratar todas as entradas de forma
    /// uniforme — o checkpoint precisa avançar por TODAS elas, na ordem, inclusive as puladas.
    /// </summary>
    private async Task<EntryOutcome> StartEntryAsync(
        RestoreRequest request,
        PackageMetadata package,
        FileManifestEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Classification == TransferClassification.ModuleData &&
            modulesHandledSeparately.Contains(entry.ModuleId))
        {
            return new EntryOutcome(false, 0, false, false, null);
        }

        if (entry.Classification is TransferClassification.UnclassifiedApplicationData or
            TransferClassification.ApplicationData or
            TransferClassification.ModuleData)
        {
            return new EntryOutcome(false, 0, true, false, null);
        }

        if (!IsWithinChosenFolders(request, package, entry))
        {
            return new EntryOutcome(false, 0, false, true, null);
        }

        try
        {
            var destinationPath = ResolveDestination(request, package, entry);
            await EnsureNoReparseParentAsync(
                entry.Classification == TransferClassification.PublicFile
                    ? request.DestinationPublicRoot
                    : request.DestinationProfileRoot,
                destinationPath,
                cancellationToken).ConfigureAwait(false);

            var copiedLength = await CopyOneAsync(
                request, entry, destinationPath, cancellationToken).ConfigureAwait(false);
            return new EntryOutcome(true, copiedLength, false, false, null);
        }
        catch (Exception exception) when (IsSingleFileFailure(exception))
        {
            // Um arquivo travado no destino (antivírus, aplicativo aberto) não pode abortar a
            // restauração e deixar o perfil pela metade.
            return new EntryOutcome(false, 0, false, false, exception);
        }
    }

    /// <summary>
    /// Fecha a entrada mais antiga da janela e aplica o resultado: é aqui que contadores e
    /// checkpoint avançam, sempre na ordem do manifesto.
    /// </summary>
    private async Task<(DurableCounters Counters, OperationCheckpoint Checkpoint)> CommitOldestEntryAsync(
        RestoreRequest request,
        Queue<(FileManifestEntry Entry, Task<EntryOutcome> Work)> window,
        DurableCounters counters,
        OperationCheckpoint checkpoint,
        List<PreservedItem> preserved,
        List<OperationError> failures,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var (entry, work) = window.Dequeue();
        var outcome = await work.ConfigureAwait(false);

        if (outcome.Copied)
        {
            counters = counters with
            {
                CopiedFiles = counters.CopiedFiles + 1,
                CopiedBytes = checked(counters.CopiedBytes + outcome.Length)
            };
        }
        else if (outcome.Preserved)
        {
            preserved.Add(new PreservedItem(
                entry.ModuleId, entry.PackageRelativePath, "preserved-not-reintegrated"));
            counters = counters with { IgnoredFiles = counters.IgnoredFiles + 1 };
        }
        else if (outcome.Ignored)
        {
            counters = counters with { IgnoredFiles = counters.IgnoredFiles + 1 };
        }
        else if (outcome.Failure is not null)
        {
            counters = counters with { FailedFiles = counters.FailedFiles + 1 };
            failures.Add(new OperationError(
                "file-restore-failed", outcome.Failure.Message, entry.PackageRelativePath));
        }

        checkpoint = checkpoint with
        {
            LastConfirmedSequence = entry.Sequence,
            LastConfirmedPath = entry.PackageRelativePath,
            Counters = counters
        };
        await SaveCheckpointIfDueAsync(request.OperationRoot, checkpoint, cancellationToken)
            .ConfigureAwait(false);

        if (outcome.Copied)
        {
            progress?.Report(new TransferProgress(counters.CopiedFiles, counters.CopiedBytes));
        }

        return (counters, checkpoint);
    }

    /// <summary>
    /// Depois de abortar, as cópias em voo precisam ser observadas — senão suas exceções viram
    /// exceções de Task não observada.
    /// </summary>
    private static async Task ObserveWindowAsync(
        Queue<(FileManifestEntry Entry, Task<EntryOutcome> Work)> window)
    {
        while (window.Count > 0)
        {
            var (_, work) = window.Dequeue();
            try
            {
                await work.ConfigureAwait(false);
            }
            catch
            {
                // A falha que abortou a operação é a evidência relevante.
            }
        }
    }

    /// <summary>
    /// O técnico escolheu só algumas pastas pessoais? Decide pelo primeiro trecho do caminho
    /// depois do prefixo do perfil (ou de Public). Entradas de AppData não passam por aqui: elas
    /// são preservadas no pacote e nunca reintegradas, com ou sem filtro.
    /// </summary>
    private static bool IsWithinChosenFolders(
        RestoreRequest request,
        PackageMetadata package,
        FileManifestEntry entry)
    {
        if (request.OnlyPersonalFolders is null)
        {
            return true;
        }

        var prefix = entry.Classification == TransferClassification.PublicFile
            ? "data/Users/Public/"
            : $"data/Users/{package.Profile.Name}/";
        if (!entry.PackageRelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var relative = entry.PackageRelativePath[prefix.Length..];
        var separator = relative.IndexOf('/');
        var folder = separator < 0 ? relative : relative[..separator];

        return request.OnlyPersonalFolders.Contains(folder, StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveDestination(
        RestoreRequest request,
        PackageMetadata package,
        FileManifestEntry entry)
    {
        var profilePrefix = $"data/Users/{package.Profile.Name}/";
        const string publicPrefix = "data/Users/Public/";
        string root;
        string relative;

        if (entry.Classification == TransferClassification.PublicFile &&
            entry.PackageRelativePath.StartsWith(publicPrefix, StringComparison.Ordinal))
        {
            root = request.DestinationPublicRoot;
            relative = entry.PackageRelativePath[publicPrefix.Length..];
        }
        else if (entry.Classification == TransferClassification.PersonalFile &&
            entry.PackageRelativePath.StartsWith(profilePrefix, StringComparison.Ordinal))
        {
            root = request.DestinationProfileRoot;
            relative = entry.PackageRelativePath[profilePrefix.Length..];
        }
        else
        {
            throw new InvalidPackagePathException(
                entry.PackageRelativePath,
                "path-namespace-mismatch");
        }

        return PackagePath.ResolveInside(root, relative);
    }

    private async Task EnsureNoReparseParentAsync(
        string destinationRoot,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var relative = Path.GetRelativePath(destinationRoot, destinationPath);
        var current = Path.GetFullPath(destinationRoot);
        var parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length - 1; index++)
        {
            current = Path.Combine(current, parts[index]);
            var info = await fileSystem.GetInfoAsync(current, cancellationToken).ConfigureAwait(false);
            if (info?.IsReparsePoint == true)
            {
                throw new InvalidPackagePathException(relative, "path-reparse-parent");
            }
        }
    }

    private async Task<long> CopyOneAsync(
        RestoreRequest request,
        FileManifestEntry entry,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var sourcePath = PackagePath.ResolveInside(request.PackageRoot, entry.PackageRelativePath);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Destino sem diretório pai.");
        if (createdDirectories.Add(destinationDirectory))
        {
            await fileSystem.CreateDirectoryAsync(destinationDirectory, cancellationToken)
                .ConfigureAwait(false);
        }

        var temporaryPath = destinationPath + $".grbkp.partial.{request.OperationId:N}";
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        long copied = 0;

        try
        {
            await using var source = await fileSystem.OpenReadAsync(sourcePath, cancellationToken)
                .ConfigureAwait(false);
            var openedLength = source.Length;
            await using (var destination = await fileSystem.OpenWriteAsync(
                temporaryPath,
                FileWriteMode.CreateOrOverwrite,
                cancellationToken).ConfigureAwait(false))
            {
                while (true)
                {
                    var read = await source.ReadAsync(
                        buffer.AsMemory(0, bufferSize),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken).ConfigureAwait(false);
                    copied = checked(copied + read);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (copied != openedLength || copied != entry.Length)
            {
                throw new InvalidDataException(
                    $"Tamanho restaurado inválido para {entry.PackageRelativePath}.");
            }
        }
        catch
        {
            await fileSystem.DeleteAsync(temporaryPath, recursive: false, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await fileSystem.MoveAsync(
            temporaryPath,
            destinationPath,
            overwrite: true,
            cancellationToken).ConfigureAwait(false);
        await fileSystem.SetMetadataAsync(
            destinationPath,
            new FileMetadata(entry.CreationTimeUtc, entry.LastWriteTimeUtc, entry.Attributes),
            cancellationToken).ConfigureAwait(false);
        return copied;
    }
}
