using GRBKP.Core.Modules;
using GRBKP.Core.Packages;

namespace GRBKP.Core.Restore;

public sealed record AppModuleRestoreOutcome(
    IReadOnlyList<ModuleResult> Results,
    bool Succeeded);

/// <summary>
/// Fecha a lacuna entre o pacote de backup e os Modules reais: le quais Modules contribuiram
/// dados durante o backup (<c>PackageMetadata.Modules</c>) e, para cada um que tiver um
/// <see cref="IMigrationModule"/> registrado, aciona sua propria restauracao (troca atomica de
/// arvore via <see cref="ApplicationTreeRestorer"/>, internamente). Modules sem implementacao
/// registrada permanecem como estavam: preservados no pacote, nao reintegrados
/// (ver <see cref="Transfers.StreamingRestoreEngine"/>).
/// </summary>
public sealed class AppModuleRestoreCoordinator(
    BackupPackageModule packageModule,
    IReadOnlyDictionary<string, IMigrationModule> modules)
{
    public async Task<AppModuleRestoreOutcome> RestoreAsync(
        RestoreContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var package = await packageModule.ReadMetadataAsync(context.PackageRoot, cancellationToken)
            .ConfigureAwait(false);
        var results = new List<ModuleResult>();
        foreach (var moduleId in package.Modules)
        {
            if (!modules.TryGetValue(moduleId, out var module))
            {
                continue;
            }

            var result = await module.RestoreAsync(context, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (result.Outcome == ModuleOutcome.Failed)
            {
                return new AppModuleRestoreOutcome(results, Succeeded: false);
            }
        }

        return new AppModuleRestoreOutcome(results, Succeeded: true);
    }
}
