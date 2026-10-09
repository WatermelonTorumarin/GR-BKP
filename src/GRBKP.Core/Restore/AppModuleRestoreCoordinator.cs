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
    public Task<AppModuleRestoreOutcome> RestoreAsync(
        RestoreContext context,
        CancellationToken cancellationToken = default) =>
        RestoreAsync(context, null, cancellationToken);

    /// <summary>
    /// Restaura os Modules do pacote. Com <paramref name="onlyModuleIds"/> preenchido, restaura
    /// apenas os escolhidos — útil quando uma restauração já foi feita e só um aplicativo ficou
    /// para trás: repetir 50 GB de arquivos pessoais para recuperar o navegador seria perda de
    /// tempo e risco desnecessário.
    /// </summary>
    public async Task<AppModuleRestoreOutcome> RestoreAsync(
        RestoreContext context,
        IReadOnlyCollection<string>? onlyModuleIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var package = await packageModule.ReadMetadataAsync(context.PackageRoot, cancellationToken)
            .ConfigureAwait(false);
        var filtro = onlyModuleIds is null
            ? null
            : new HashSet<string>(onlyModuleIds, StringComparer.OrdinalIgnoreCase);

        var results = new List<ModuleResult>();
        foreach (var moduleId in package.Modules)
        {
            if (!modules.TryGetValue(moduleId, out var module) ||
                (filtro is not null && !filtro.Contains(moduleId)))
            {
                continue;
            }

            // TODOS os Modules são tentados, mesmo depois de um falhar. Cada um é uma troca de
            // árvore independente, com reversão própria: a falha de um não contamina os outros.
            // Parar no primeiro erro já custou caro em produção (09/10/2026) — o Thunderbird é o
            // primeiro da lista, falhou, e Chrome e Edge sequer chegaram a ser TENTADOS; a
            // restauração terminou sem os dados dos navegadores do funcionário.
            ModuleResult result;
            try
            {
                result = await module.RestoreAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result = new ModuleResult(
                    moduleId,
                    ModuleOutcome.Failed,
                    [new MigrationEvidence(
                        "module-restore-exception", exception.Message, EvidenceSeverity.Error)]);
            }

            results.Add(result);
        }

        return new AppModuleRestoreOutcome(
            results,
            Succeeded: results.All(result => result.Outcome != ModuleOutcome.Failed));
    }
}
