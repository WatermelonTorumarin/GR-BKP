using GRBKP.Core.Adapters;
using GRBKP.Core.Modules.Adobe;
using GRBKP.Core.Modules.Chromium;
using GRBKP.Core.Modules.Firefox;
using GRBKP.Core.Modules.Office;
using GRBKP.Core.Modules.Thunderbird;

namespace GRBKP.Core.Modules;

/// <summary>
/// Ponto unico de montagem de todos os Modules de aplicativo implementados — o que o app
/// (ou os testes de integracao) usam para construir o registro passado ao
/// <see cref="Restore.AppModuleRestoreCoordinator"/>. Adicionar um Module novo (Office, Adobe…)
/// significa registra-lo aqui, em um unico lugar.
/// </summary>
public static class MigrationModuleCatalog
{
    public static IReadOnlyDictionary<string, IMigrationModule> CreateAll(
        IFileSystemAdapter fileSystem,
        IProcessAdapter processAdapter)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(processAdapter);

        var thunderbird = new ThunderbirdMigrationModule(fileSystem, processAdapter);
        var firefox = new FirefoxMigrationModule(fileSystem, processAdapter);
        var chrome = new ChromiumMigrationModule(fileSystem, processAdapter, ChromiumBrowserDescriptor.Chrome);
        var edge = new ChromiumMigrationModule(fileSystem, processAdapter, ChromiumBrowserDescriptor.Edge);
        var office = new OfficeMigrationModule(fileSystem, processAdapter);
        var adobeAcrobat = new AdobeAcrobatMigrationModule(fileSystem, processAdapter);

        return new Dictionary<string, IMigrationModule>
        {
            [thunderbird.Id] = thunderbird,
            [firefox.Id] = firefox,
            [chrome.Id] = chrome,
            [edge.Id] = edge,
            [office.Id] = office,
            [adobeAcrobat.Id] = adobeAcrobat
        };
    }

    /// <summary>
    /// Caminhos de AppData (relativos a raiz do perfil, com "/" como separador) que cada Module
    /// implementado ja captura por conta propria. A politica de selecao generica precisa desta
    /// lista para nao varrer e empacotar os mesmos arquivos duas vezes — uma como ModuleData
    /// (pelo Module) e outra como UnclassifiedApplicationData (pela varredura generica).
    /// </summary>
    public static IReadOnlyList<string> KnownAppDataFootprints() =>
    [
        string.Join('/', Thunderbird.ThunderbirdMigrationModule.AppDataRelativeSegments),
        string.Join('/', Firefox.FirefoxMigrationModule.AppDataRelativeSegments),
        string.Join('/', Chromium.ChromiumBrowserDescriptor.Chrome.AppDataRelativeSegments),
        string.Join('/', Chromium.ChromiumBrowserDescriptor.Edge.AppDataRelativeSegments),
        .. OfficeMigrationModule.AppDataRelativeTrees,
        string.Join('/', AdobeAcrobatMigrationModule.AppDataRelativeSegments)
    ];
}
