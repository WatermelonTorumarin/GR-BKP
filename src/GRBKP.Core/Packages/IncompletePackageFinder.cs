using GRBKP.Core.Adapters;
using GRBKP.Core.Operations;

namespace GRBKP.Core.Packages;

public sealed record IncompletePackage(
    string PackageRoot,
    PackageState Status,
    DateTimeOffset StartedAtUtc,
    DurableCounters Counters);

/// <summary>
/// Procura, entre os backups já feitos de um funcionário, um que tenha ficado pelo caminho.
///
/// Os motores sempre souberam retomar (<c>MigrationOrchestrator.RunOrResumeBackupAsync</c> detecta
/// um pacote existente e continua de onde parou), mas a interface nunca acionava isso: cada
/// execução criava uma pasta nova com data e hora, então a retomada era código inalcançável. Se o
/// SSD desconectasse em 80% de um backup de 200 GB, recomeçava do zero.
/// </summary>
public sealed class IncompletePackageFinder(
    IFileSystemAdapter fileSystem,
    BackupPackageModule packageModule)
{
    public async Task<IncompletePackage?> FindMostRecentAsync(
        string personFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personFolder);

        if (await fileSystem.GetInfoAsync(personFolder, cancellationToken).ConfigureAwait(false)
            is not { Kind: PathEntryKind.Directory })
        {
            return null;
        }

        IncompletePackage? mostRecent = null;
        await foreach (var directory in fileSystem
            .EnumerateDirectoriesAsync(personFolder, cancellationToken)
            .ConfigureAwait(false))
        {
            PackageMetadata metadata;
            try
            {
                metadata = await packageModule
                    .ReadMetadataAsync(directory.FullPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Pasta que não é pacote, ou package.json ilegível: só significa que este não é
                // retomável. Nunca pode derrubar a tela de backup.
                continue;
            }

            if (metadata.Status == PackageState.Complete)
            {
                continue;
            }

            if (mostRecent is null || metadata.StartedAtUtc > mostRecent.StartedAtUtc)
            {
                mostRecent = new IncompletePackage(
                    directory.FullPath,
                    metadata.Status,
                    metadata.StartedAtUtc,
                    metadata.Counters);
            }
        }

        return mostRecent;
    }
}
