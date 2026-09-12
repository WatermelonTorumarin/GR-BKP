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
    string OperationRoot);

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
    IReadOnlySet<string>? modulesHandledSeparately = null)
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

    public Task<RestoreOperationResult> RunAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(request, resume: false, cancellationToken);

    public Task<RestoreOperationResult> ResumeAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(request, resume: true, cancellationToken);

    private async Task<RestoreOperationResult> RunCoreAsync(
        RestoreRequest request,
        bool resume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        }

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
        try
        {
            await foreach (var entry in packageModule
                .ReadFilesAsync(request.PackageRoot, cancellationToken)
                .ConfigureAwait(false))
            {
                if (entry.Sequence <= checkpoint.LastConfirmedSequence)
                {
                    continue;
                }

                if (entry.Classification == TransferClassification.ModuleData &&
                    modulesHandledSeparately.Contains(entry.ModuleId))
                {
                    checkpoint = checkpoint with
                    {
                        LastConfirmedSequence = entry.Sequence,
                        LastConfirmedPath = entry.PackageRelativePath,
                        Counters = counters
                    };
                    await checkpointStore.SaveAsync(
                        request.OperationRoot,
                        checkpoint,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (entry.Classification is TransferClassification.UnclassifiedApplicationData or
                    TransferClassification.ApplicationData or
                    TransferClassification.ModuleData)
                {
                    preserved.Add(new PreservedItem(
                        entry.ModuleId,
                        entry.PackageRelativePath,
                        "preserved-not-reintegrated"));
                    counters = counters with { IgnoredFiles = counters.IgnoredFiles + 1 };
                    checkpoint = checkpoint with
                    {
                        LastConfirmedSequence = entry.Sequence,
                        LastConfirmedPath = entry.PackageRelativePath,
                        Counters = counters
                    };
                    await checkpointStore.SaveAsync(
                        request.OperationRoot,
                        checkpoint,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var destinationPath = ResolveDestination(request, package, entry);
                await EnsureNoReparseParentAsync(
                    entry.Classification == TransferClassification.PublicFile
                        ? request.DestinationPublicRoot
                        : request.DestinationProfileRoot,
                    destinationPath,
                    cancellationToken).ConfigureAwait(false);

                long copiedLength;
                try
                {
                    copiedLength = await CopyOneAsync(
                        request,
                        entry,
                        destinationPath,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsSingleFileFailure(exception))
                {
                    // Mesmo raciocínio do backup: um arquivo travado no destino (antivírus,
                    // aplicativo aberto) não pode abortar a restauração e deixar o perfil pela
                    // metade. Registra, contabiliza e segue para o próximo.
                    counters = counters with { FailedFiles = counters.FailedFiles + 1 };
                    failures.Add(new OperationError(
                        "file-restore-failed", exception.Message, entry.PackageRelativePath));
                    checkpoint = checkpoint with
                    {
                        LastConfirmedSequence = entry.Sequence,
                        LastConfirmedPath = entry.PackageRelativePath,
                        Counters = counters
                    };
                    await checkpointStore.SaveAsync(
                        request.OperationRoot,
                        checkpoint,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                counters = counters with
                {
                    CopiedFiles = counters.CopiedFiles + 1,
                    CopiedBytes = checked(counters.CopiedBytes + copiedLength)
                };
                checkpoint = checkpoint with
                {
                    LastConfirmedSequence = entry.Sequence,
                    LastConfirmedPath = entry.PackageRelativePath,
                    Counters = counters
                };
                await checkpointStore.SaveAsync(
                    request.OperationRoot,
                    checkpoint,
                    cancellationToken).ConfigureAwait(false);
            }

            checkpoint = checkpoint with
            {
                State = RestoreState.Complete.ToString(),
                Counters = counters
            };
            await checkpointStore.SaveAsync(request.OperationRoot, checkpoint, cancellationToken)
                .ConfigureAwait(false);
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
        catch (InvalidPackagePathException exception)
        {
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
        (exception is IOException or UnauthorizedAccessException ||
            // Tamanho divergente do manifesto: aquele arquivo do pacote está corrompido (setor
            // ruim no SSD, cópia truncada). Vale pular esse e restaurar todo o resto.
            exception is InvalidDataException);

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
        await fileSystem.CreateDirectoryAsync(destinationDirectory, cancellationToken)
            .ConfigureAwait(false);
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
