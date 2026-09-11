using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Packages;
using GRBKP.Core.Restore;
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

public sealed record OrchestratedRestoreResult(
    RestoreOperationResult PersonalDataResult,
    AppModuleRestoreOutcome ModuleOutcome,
    RestoreState OverallState);

/// <summary>
/// Ponto de entrada único para acionar backup/restauração — o chamador (futura UI)
/// não precisa saber se uma operação em <c>packageRoot</c>/<c>operationRoot</c> já existe e deve
/// ser retomada, nem conhecer ou coordenar manualmente as classes internas do Core
/// (StreamingBackupEngine, StreamingRestoreEngine, AppModuleRestoreCoordinator,
/// ApplicationTreeRestorer etc.).
///
/// <see cref="RunOrResumeRestoreAsync"/> fecha a lacuna que existia entre os dois motores: antes
/// dela, nada acionava de fato os Modules (Thunderbird, navegadores…) durante uma restauração —
/// StreamingRestoreEngine apenas marcava as entradas ModuleData como "preservadas, não
/// reintegradas" e retornava Complete só por ter copiado os arquivos pessoais. Agora os dados
/// pessoais e os Modules conhecidos são restaurados em uma única chamada, e o resultado só é
/// Complete quando as duas etapas realmente terminaram com sucesso.
/// </summary>
public sealed class MigrationOrchestrator(
    IFileSystemAdapter fileSystem,
    BackupPackageModule packageModule,
    IClock clock,
    StreamingBackupEngine backupEngine,
    StreamingRestoreEngine? restoreEngine = null,
    AppModuleRestoreCoordinator? moduleCoordinator = null)
{
    private readonly OperationCheckpointStore restoreCheckpointStore = new(fileSystem, clock);


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

    /// <summary>
    /// Restaura dados pessoais (StreamingRestoreEngine) e, em seguida, os Modules de aplicativo
    /// conhecidos (AppModuleRestoreCoordinator) para o mesmo destino, detectando sozinho se a
    /// operação em <c>request.OperationRoot</c> já existe e deve ser retomada. O resultado geral
    /// só é <see cref="RestoreState.Complete"/> quando ambas as etapas terminam com sucesso —
    /// uma falha de Module (ex.: validação do Thunderbird reprovada) marca o resultado geral como
    /// <see cref="RestoreState.Failed"/>, mesmo que os arquivos pessoais tenham sido copiados.
    /// </summary>
    public async Task<OrchestratedRestoreResult> RunOrResumeRestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (restoreEngine is null || moduleCoordinator is null)
        {
            throw new InvalidOperationException(
                "Este MigrationOrchestrator não foi construído com suporte a restauração " +
                "(restoreEngine/moduleCoordinator ausentes).");
        }

        var operationJsonPath = Path.Combine(request.OperationRoot, "operation.json");
        var operationAlreadyStarted = await fileSystem
            .GetInfoAsync(operationJsonPath, cancellationToken)
            .ConfigureAwait(false) is not null;

        var personalDataResult = operationAlreadyStarted
            ? await restoreEngine.ResumeAsync(request, cancellationToken).ConfigureAwait(false)
            : await restoreEngine.RunAsync(request, cancellationToken).ConfigureAwait(false);

        if (personalDataResult.State != RestoreState.Complete)
        {
            return new OrchestratedRestoreResult(
                personalDataResult,
                new AppModuleRestoreOutcome([], Succeeded: false),
                personalDataResult.State);
        }

        var moduleContext = new RestoreContext(
            request.OperationId,
            request.PackageRoot,
            request.DestinationProfileRoot,
            fileSystem,
            await restoreCheckpointStore.ReadAsync(request.OperationRoot, cancellationToken)
                .ConfigureAwait(false));
        var moduleOutcome = await moduleCoordinator
            .RestoreAsync(moduleContext, cancellationToken)
            .ConfigureAwait(false);

        return new OrchestratedRestoreResult(
            personalDataResult,
            moduleOutcome,
            moduleOutcome.Succeeded ? RestoreState.Complete : RestoreState.Failed);
    }
}
