using System.Text;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Modules.Chromium;
using GRBKP.Core.Modules.Mozilla;
using GRBKP.Core.Operations;
using GRBKP.Core.Orchestration;
using GRBKP.Core.Packages;
using GRBKP.Core.Restore;
using GRBKP.Core.Transfers;
using GRBKP.Windows.FileSystem;
using GRBKP.Windows.Processes;

namespace GRBKP.IntegrationTests;

/// <summary>
/// Prova de integracao real: todos os quatro Modules implementados (Thunderbird, Firefox,
/// Chrome, Edge) mais um arquivo pessoal, restaurados numa unica chamada ao
/// MigrationOrchestrator, usando exatamente o registro que MigrationModuleCatalog monta — o
/// mesmo caminho que o app real vai usar, nao um atalho so de teste.
/// </summary>
public sealed class MigrationModuleCatalogEndToEndTests
{
    [Fact]
    public async Task RunOrResumeRestoreAsync_RestoresPersonalFileAndAllFourAppModules_InOneCall()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero));
        var packageModule = new BackupPackageModule(fileSystem, clock);
        var backupEngine = new StreamingBackupEngine(fileSystem, packageModule, clock);

        const string documentContent = "CONTRATO ASSINADO - CLIENTE ACME";
        const string thunderbirdMarker = "De: financeiro@acme.com\nAssunto: fatura 3321";
        const string firefoxMarker = "FIREFOX-BOOKMARKS-BACKUP";
        const string chromeMarker = "CHROME-BOOKMARKS-BACKUP";
        const string edgeMarker = "EDGE-BOOKMARKS-BACKUP";

        // --- Origem: um funcionario com os quatro apps em uso ---
        var sourceProfileRoot = fixture.PathFor("source/Users/auxatc");
        var documentSourcePath = fixture.WriteFile(
            "source/Users/auxatc/Documents/contrato.txt", documentContent);
        fixture.CreateMozillaInstall(
            sourceProfileRoot,
            ["AppData", "Roaming", "Thunderbird"],
            installId: "InstallSRC-TB-0001",
            profileFolder: "tb-src.default-release",
            mailMarker: thunderbirdMarker);
        fixture.CreateMozillaInstall(
            sourceProfileRoot,
            ["AppData", "Roaming", "Mozilla", "Firefox"],
            installId: "InstallSRC-FF-0001",
            profileFolder: "ff-src.default-release",
            mailMarker: null,
            bookmarkMarker: firefoxMarker);
        fixture.CreateChromiumInstall(
            sourceProfileRoot, ChromiumBrowserDescriptor.Chrome, chromeMarker);
        fixture.CreateChromiumInstall(
            sourceProfileRoot, ChromiumBrowserDescriptor.Edge, edgeMarker);

        // --- Destino: SSD novo, cada app ja instalado com seu proprio perfil vazio ---
        var destinationProfileRoot = fixture.PathFor("destination/Users/auxatc");
        fixture.CreateMozillaInstall(
            destinationProfileRoot,
            ["AppData", "Roaming", "Thunderbird"],
            installId: "InstallDST-TB-9999",
            profileFolder: "tb-dst.default-release",
            mailMarker: null);
        fixture.CreateMozillaInstall(
            destinationProfileRoot,
            ["AppData", "Roaming", "Mozilla", "Firefox"],
            installId: "InstallDST-FF-9999",
            profileFolder: "ff-dst.default-release",
            mailMarker: null,
            bookmarkMarker: "EMPTY-NEW-PROFILE");
        fixture.CreateChromiumInstall(
            destinationProfileRoot, ChromiumBrowserDescriptor.Chrome, "EMPTY-NEW-PROFILE");
        fixture.CreateChromiumInstall(
            destinationProfileRoot, ChromiumBrowserDescriptor.Edge, "EMPTY-NEW-PROFILE");

        var processAdapter = new WindowsProcessAdapter();
        var modules = MigrationModuleCatalog.CreateAll(fileSystem, processAdapter);

        // --- Backup: descobre e enumera os quatro Modules + o arquivo pessoal, tudo num pacote ---
        var discoveries = new Dictionary<string, ModuleDiscovery>();
        foreach (var (moduleId, module) in modules)
        {
            var discovery = await module.DiscoverAsync(
                new MigrationContext(sourceProfileRoot, "auxatc", fileSystem),
                CancellationToken.None);
            Assert.True(discovery.IsApplicable, $"Module '{moduleId}' deveria ter sido descoberto.");
            discoveries[moduleId] = discovery;
        }

        var backupId = Guid.Parse("90909090-9090-9090-9090-909090909090");
        var backupOperationId = Guid.Parse("91919191-9191-9191-9191-919191919191");
        var packageRoot = fixture.PathFor("package");
        var metadata = new PackageMetadata(
            "1.0",
            "1.0.0-fase3",
            backupId,
            PackageState.Created,
            clock.UtcNow,
            null,
            new SourceComputer("CATALOG-E2E-TEST", "F:", "Windows 11 Pro", "x64"),
            new SourceProfile("auxatc", @"F:\Users\auxatc", null),
            "catalog-end-to-end-test",
            [.. modules.Keys],
            DurableCounters.Empty,
            new IntegrityPolicy("SHA256", false, "size-and-handle"),
            IsEncrypted: false);

        var backupResult = await backupEngine.RunAsync(
            new BackupRequest(backupOperationId, packageRoot, metadata),
            AllItems(documentSourcePath, modules, discoveries));
        Assert.Equal(PackageState.Complete, backupResult.State);

        // --- Restauracao: uma unica chamada ao orquestrador, com o mesmo registro que o app usa ---
        var restoreEngine = new StreamingRestoreEngine(
            fileSystem,
            packageModule,
            clock,
            modulesHandledSeparately: modules.Keys.ToHashSet());
        var moduleCoordinator = new AppModuleRestoreCoordinator(packageModule, modules);
        var orchestrator = new MigrationOrchestrator(
            fileSystem,
            packageModule,
            clock,
            backupEngine,
            restoreEngine,
            moduleCoordinator);

        var restoreOperationId = Guid.Parse("92929292-9292-9292-9292-929292929292");
        var restoreRequest = new RestoreRequest(
            restoreOperationId,
            packageRoot,
            destinationProfileRoot,
            fixture.PathFor("public"),
            fixture.PathFor("operations/92929292"));

        var result = await orchestrator.RunOrResumeRestoreAsync(restoreRequest);

        Assert.Equal(RestoreState.Complete, result.OverallState);
        Assert.True(result.ModuleOutcome.Succeeded);
        Assert.Equal(4, result.ModuleOutcome.Results.Count);
        Assert.All(result.ModuleOutcome.Results, r => Assert.Equal(ModuleOutcome.Applied, r.Outcome));

        // Efeito 1: documento pessoal.
        Assert.Equal(
            documentContent,
            File.ReadAllText(Path.Combine(destinationProfileRoot, "Documents", "contrato.txt")));

        // Efeito 2: Thunderbird — perfil reativado, e-mail acessivel.
        var thunderbirdRoot = Path.Combine(
            destinationProfileRoot, "AppData", "Roaming", "Thunderbird");
        var thunderbirdIni = MozillaProfilesIni.Parse(
            File.ReadAllText(Path.Combine(thunderbirdRoot, "profiles.ini")));
        Assert.Equal("InstallDST-TB-9999", thunderbirdIni.FindInstallSectionName());
        Assert.Equal(
            thunderbirdMarker,
            File.ReadAllText(Path.Combine(
                thunderbirdRoot, "Profiles", "tb-src.default-release",
                "Mail", "pop.exemplo.com-1", "Inbox")));

        // Efeito 3: Firefox — perfil reativado, favoritos recuperados.
        var firefoxRoot = Path.Combine(
            destinationProfileRoot, "AppData", "Roaming", "Mozilla", "Firefox");
        var firefoxIni = MozillaProfilesIni.Parse(
            File.ReadAllText(Path.Combine(firefoxRoot, "profiles.ini")));
        Assert.Equal("InstallDST-FF-9999", firefoxIni.FindInstallSectionName());
        Assert.Equal(
            firefoxMarker,
            File.ReadAllText(Path.Combine(
                firefoxRoot, "Profiles", "ff-src.default-release",
                "bookmarkbackups", "bookmarks.jsonlz4")));

        // Efeito 4 e 5: Chrome e Edge — User Data inteiro trocado.
        var chromeRoot = Path.Combine(
            [destinationProfileRoot, .. ChromiumBrowserDescriptor.Chrome.AppDataRelativeSegments]);
        Assert.Equal(
            chromeMarker,
            File.ReadAllText(Path.Combine(chromeRoot, "Default", "Bookmarks")));

        var edgeRoot = Path.Combine(
            [destinationProfileRoot, .. ChromiumBrowserDescriptor.Edge.AppDataRelativeSegments]);
        Assert.Equal(
            edgeMarker,
            File.ReadAllText(Path.Combine(edgeRoot, "Default", "Bookmarks")));
    }

    private static async IAsyncEnumerable<TransferItem> AllItems(
        string documentSourcePath,
        IReadOnlyDictionary<string, IMigrationModule> modules,
        IReadOnlyDictionary<string, ModuleDiscovery> discoveries)
    {
        var sequence = 1L;
        yield return new TransferItem(
            sequence,
            "user-files",
            documentSourcePath,
            "data/Users/auxatc/Documents/contrato.txt",
            TransferClassification.PersonalFile,
            SourceRelativePath: "Documents/contrato.txt");

        foreach (var (moduleId, module) in modules)
        {
            await foreach (var item in module.EnumerateBackupAsync(
                discoveries[moduleId], CancellationToken.None))
            {
                sequence++;
                yield return item with { Sequence = sequence };
            }
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            System.IO.Path.GetTempPath(),
            "grbkp-catalog-e2e",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string PathFor(string relative) =>
            System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public string WriteFile(string relativePath, string content)
        {
            var path = PathFor(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Encoding.UTF8);
            return path;
        }

        public void CreateMozillaInstall(
            string profileRoot,
            IReadOnlyList<string> appRelativeSegments,
            string installId,
            string profileFolder,
            string? mailMarker,
            string? bookmarkMarker = null)
        {
            var appRoot = System.IO.Path.Combine([profileRoot, .. appRelativeSegments]);
            var profilePath = System.IO.Path.Combine(appRoot, "Profiles", profileFolder);
            Directory.CreateDirectory(profilePath);

            File.WriteAllText(
                System.IO.Path.Combine(profilePath, "prefs.js"),
                "user_pref(\"app.configured\", true);",
                Encoding.UTF8);

            if (mailMarker is not null)
            {
                var mailDirectory = System.IO.Path.Combine(profilePath, "Mail", "pop.exemplo.com-1");
                Directory.CreateDirectory(mailDirectory);
                File.WriteAllText(
                    System.IO.Path.Combine(mailDirectory, "Inbox"), mailMarker, Encoding.UTF8);
            }

            if (bookmarkMarker is not null)
            {
                var backupsDirectory = System.IO.Path.Combine(profilePath, "bookmarkbackups");
                Directory.CreateDirectory(backupsDirectory);
                File.WriteAllText(
                    System.IO.Path.Combine(backupsDirectory, "bookmarks.jsonlz4"),
                    bookmarkMarker,
                    Encoding.UTF8);
            }

            File.WriteAllText(
                System.IO.Path.Combine(appRoot, "profiles.ini"),
                $"""
                [General]
                StartWithLastProfile=1
                Version=2

                [Profile0]
                Name=default
                IsRelative=1
                Path=Profiles/{profileFolder}
                Default=1

                [{installId}]
                Default=Profiles/{profileFolder}
                Locked=1
                """,
                Encoding.UTF8);
        }

        public void CreateChromiumInstall(
            string profileRoot,
            ChromiumBrowserDescriptor descriptor,
            string bookmarkMarker)
        {
            var userDataRoot = System.IO.Path.Combine(
                [profileRoot, .. descriptor.AppDataRelativeSegments]);
            var defaultProfile = System.IO.Path.Combine(userDataRoot, "Default");
            Directory.CreateDirectory(defaultProfile);

            File.WriteAllText(
                System.IO.Path.Combine(userDataRoot, "Local State"),
                """{"profile":{"info_cache":{"Default":{"name":"Person 1"}}}}""",
                Encoding.UTF8);
            File.WriteAllText(
                System.IO.Path.Combine(defaultProfile, "Preferences"), "{}", Encoding.UTF8);
            File.WriteAllText(
                System.IO.Path.Combine(defaultProfile, "Bookmarks"), bookmarkMarker, Encoding.UTF8);
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
