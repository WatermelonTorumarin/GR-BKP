using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Packages;
using GRBKP.Core.Transfers;

namespace GRBKP.Core.Orchestration;

public enum OrchestratedBackupOutcome
{
    Started,
    Resumed
}

public sealed record OrchestratedBackupResult(
    OrchestratedBackupOutcome Outcome,
    BackupOperationResult Result);

/// <summary>
/// Ponto de entrada único para acionar backup/restauração — o chamador (futura UI)
/// não precisa saber se uma operação em <c>packageRoot</c> já existe e deve ser retomada,
/// nem conhecer as classes internas do Core (StreamingBackupEngine, ApplicationTreeRestorer etc.).
/// </summary>
public sealed class MigrationOrchestrator(
    IFileSystemAdapter fileSystem,
    BackupPackageModule packageModule,
    StreamingBackupEngine backupEngine)
{
    public async Task<OrchestratedBackupResult> RunOrResumeBackupAsync(
        BackupRequest request,
        IAsyncEnumerable<TransferItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var packageJsonPath = Path.Combine(request.PackageRoot, "package.json");
        var packageExists = await fileSystem.GetInfoAsync(packageJsonPath, cancellationToken)
            .ConfigureAwait(false) is not null;

        var result = packageExists
            ? await backupEngine.ResumeAsync(request, items, cancellationToken).ConfigureAwait(false)
            : await backupEngine.RunAsync(request, items, cancellationToken).ConfigureAwait(false);

        return new OrchestratedBackupResult(
            packageExists ? OrchestratedBackupOutcome.Resumed : OrchestratedBackupOutcome.Started,
            result);
    }

    public async Task<OperationCheckpoint> GetBackupStatusAsync(
        string packageRoot,
        CancellationToken cancellationToken = default) =>
        await packageModule.ReadOperationAsync(packageRoot, cancellationToken).ConfigureAwait(false);
}
