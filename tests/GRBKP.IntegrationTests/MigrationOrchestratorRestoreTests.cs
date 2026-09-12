using System.Text;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Modules.Mozilla;
using GRBKP.Core.Modules.Thunderbird;
using GRBKP.Core.Operations;
using GRBKP.Core.Orchestration;
using GRBKP.Core.Packages;
using GRBKP.Core.Restore;
using GRBKP.Core.Transfers;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class MigrationOrchestratorRestoreTests
{
    [Fact]
    public async Task RunOrResumeRestoreAsync_RestoresPersonalFilesAndThunderbirdTogether_InOneCall()
    {
        // Esta e a prova de ponta a ponta de que a lacuna de orquestracao foi fechada: antes,
        // nada no motor de restauracao acionava de fato um IMigrationModule — StreamingRestoreEngine
        // so marcava ModuleData como "preservado, nao reintegrado". Uma unica chamada ao
        // orquestrador agora precisa deixar tanto o documento pessoal quanto o e-mail do
        // Thunderbird acessiveis no destino.
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 11, 18, 0, 0, TimeSpan.Zero));
        var packageModule = new BackupPackageModule(fileSystem, clock);
        var backupEngine = new StreamingBackupEngine(fileSystem, packageModule, clock);

        const string mailContent = "De: fornecedor@exemplo.com\nAssunto: nota fiscal 991";
        const string documentContent = "RELATORIO MENSAL - SETEMBRO";

        var sourceProfileRoot = fixture.CreateThunderbirdInstall(
            "source",
            installId: "InstallAAAA1111AAAA1111",
            profileFolder: "src9k2m.default-release",
            mailContent: mailContent);
        var documentSourcePath = fixture.WriteFile(
            "source/Users/auxatc/Documents/relatorio.txt",
            documentContent);

        var destinationProfileRoot = fixture.CreateThunderbirdInstall(
            "destination",
            installId: "InstallBBBB2222BBBB2222",
            profileFolder: "dst7h4p.default-release",
            mailContent: null);

        var thunderbirdModule = new ThunderbirdMigrationModule(fileSystem, new NoOpProcessAdapter());
        var discovery = await thunderbirdModule.DiscoverAsync(
            new MigrationContext(sourceProfileRoot, "auxatc", fileSystem),
            CancellationToken.None);
        Assert.True(discovery.IsApplicable);

        var backupId = Guid.Parse("50505050-5050-5050-5050-505050505050");
        var backupOperationId = Guid.Parse("60606060-6060-6060-6060-606060606060");
        var packageRoot = fixture.PathFor("package");
        var metadata = new PackageMetadata(
            "1.0",
            "1.0.0-fase3",
            backupId,
            PackageState.Created,
            clock.UtcNow,
            null,
            new SourceComputer("ORCH-RESTORE-TEST", "F:", "Windows 11 Pro", "x64"),
            new SourceProfile("auxatc", @"F:\Users\auxatc", null),
            "orchestrator-restore-test",
            ["thunderbird"],
            DurableCounters.Empty,
            new IntegrityPolicy("SHA256", false, "size-and-handle"),
            IsEncrypted: false);

        var backupResult = await backupEngine.RunAsync(
            new BackupRequest(backupOperationId, packageRoot, metadata),
            AllItems(documentSourcePath, thunderbirdModule, discovery));
        Assert.Equal(PackageState.Complete, backupResult.State);

        var restoreEngine = new StreamingRestoreEngine(
            fileSystem,
            packageModule,
            clock,
            modulesHandledSeparately: new HashSet<string> { "thunderbird" });
        var moduleCoordinator = new AppModuleRestoreCoordinator(
            packageModule,
            new Dictionary<string, IMigrationModule> { ["thunderbird"] = thunderbirdModule });
        var orchestrator = new MigrationOrchestrator(
            fileSystem,
            packageModule,
            clock,
            backupEngine,
            restoreEngine,
            moduleCoordinator);

        var restoreOperationId = Guid.Parse("70707070-7070-7070-7070-707070707070");
        var operationRoot = fixture.PathFor("operations/70707070");
        var restoreRequest = new RestoreRequest(
            restoreOperationId,
            packageRoot,
            destinationProfileRoot,
            fixture.PathFor("public"),
            operationRoot);

        var result = await orchestrator.RunOrResumeRestoreAsync(restoreRequest);

        Assert.Equal(RestoreState.Complete, result.OverallState);
        Assert.Equal(RestoreState.Complete, result.PersonalDataResult.State);
        Assert.True(result.ModuleOutcome.Succeeded);
        Assert.Equal(ModuleOutcome.Applied, Assert.Single(result.ModuleOutcome.Results).Outcome);

        // Efeito 1: o documento pessoal foi copiado normalmente.
        var restoredDocumentPath = Path.Combine(
            destinationProfileRoot,
            "Documents", "relatorio.txt");
        Assert.True(File.Exists(restoredDocumentPath));
        Assert.Equal(documentContent, File.ReadAllText(restoredDocumentPath));

        // Efeito 2: o Module do Thunderbird foi realmente acionado — nao apenas "preservado".
        var destinationThunderbirdRoot = Path.Combine(
            destinationProfileRoot, "AppData", "Roaming", "Thunderbird");
        var reboundIni = MozillaProfilesIni.Parse(
            File.ReadAllText(Path.Combine(destinationThunderbirdRoot, "profiles.ini")));
        Assert.Equal("InstallBBBB2222BBBB2222", reboundIni.FindInstallSectionName());
        var restoredMailPath = Path.Combine(
            destinationThunderbirdRoot,
            "Profiles", "src9k2m.default-release",
            "Mail", "pop.exemplo.com-1", "Inbox");
        Assert.True(File.Exists(restoredMailPath));
        Assert.Equal(mailContent, File.ReadAllText(restoredMailPath));
    }

    private static async IAsyncEnumerable<TransferItem> AllItems(
        string documentSourcePath,
        ThunderbirdMigrationModule thunderbirdModule,
        ModuleDiscovery discovery)
    {
        yield return new TransferItem(
            1,
            "user-files",
            documentSourcePath,
            "data/Users/auxatc/Documents/relatorio.txt",
            TransferClassification.PersonalFile,
            SourceRelativePath: "Documents/relatorio.txt");

        var sequence = 1L;
        await foreach (var item in thunderbirdModule.EnumerateBackupAsync(discovery, CancellationToken.None))
        {
            sequence++;
            yield return item with { Sequence = sequence };
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class NoOpProcessAdapter : IProcessAdapter
    {
        public Task TerminateProcessTreesAsync(
            IReadOnlyCollection<string> processNames,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetRunningProcessesAsync(
            IReadOnlyCollection<string> processNames,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            System.IO.Path.GetTempPath(),
            "grbkp-orchestrator-restore",
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

        public string CreateThunderbirdInstall(
            string label,
            string installId,
            string profileFolder,
            string? mailContent)
        {
            var profileRoot = PathFor($"{label}/Users/auxatc");
            var thunderbirdRoot = System.IO.Path.Combine(
                profileRoot, "AppData", "Roaming", "Thunderbird");
            var profilePath = System.IO.Path.Combine(thunderbirdRoot, "Profiles", profileFolder);
            Directory.CreateDirectory(profilePath);

            File.WriteAllText(
                System.IO.Path.Combine(profilePath, "prefs.js"),
                "user_pref(\"mail.server.server1.hostname\", \"pop.exemplo.com\");",
                Encoding.UTF8);

            if (mailContent is not null)
            {
                var mailDirectory = System.IO.Path.Combine(
                    profilePath, "Mail", "pop.exemplo.com-1");
                Directory.CreateDirectory(mailDirectory);
                File.WriteAllText(
                    System.IO.Path.Combine(mailDirectory, "Inbox"),
                    mailContent,
                    Encoding.UTF8);
            }

            File.WriteAllText(
                System.IO.Path.Combine(thunderbirdRoot, "profiles.ini"),
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

            return profileRoot;
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
