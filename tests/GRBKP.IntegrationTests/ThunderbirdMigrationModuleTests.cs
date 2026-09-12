using System.Text;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Modules.Mozilla;
using GRBKP.Core.Modules.Thunderbird;
using GRBKP.Core.Operations;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class ThunderbirdMigrationModuleTests
{
    [Fact]
    public async Task DiscoverAsync_FindsDefaultProfile_FromLegacyProfilesIni()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var sourceRoot = fixture.CreateThunderbirdInstall(
            "source",
            installId: "Install4A1B2C3D4E5F6789",
            profileFolder: "78ypv6h6.default-release",
            mailContent: "De: chefe@ravelfiat.com.br\nAssunto: importante");
        var processAdapter = new RecordingProcessAdapter();
        var module = new ThunderbirdMigrationModule(fileSystem, processAdapter);

        var discovery = await module.DiscoverAsync(
            new MigrationContext(sourceRoot, "auxatc", fileSystem),
            CancellationToken.None);

        Assert.True(discovery.IsApplicable);
        Assert.Equal(
            "Profiles/78ypv6h6.default-release",
            discovery.Facts["defaultProfileRelativePath"]);
    }

    [Fact]
    public async Task DiscoverAsync_ReportsNotApplicable_WhenThunderbirdWasNeverUsedOnSource()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var sourceRoot = fixture.CreateTree("no-thunderbird/Users/auxatc");
        var processAdapter = new RecordingProcessAdapter();
        var module = new ThunderbirdMigrationModule(fileSystem, processAdapter);

        var discovery = await module.DiscoverAsync(
            new MigrationContext(sourceRoot, "auxatc", fileSystem),
            CancellationToken.None);

        Assert.False(discovery.IsApplicable);
    }

    [Fact]
    public async Task RestoreAsync_ReactivatesRecoveredProfileOnNewInstallation_ReproducingAndFixingTheOriginalBug()
    {
        // Este e o cenario exato do bug original registrado na conversa: o SSD antigo tinha e-mails
        // no perfil do Thunderbird, mas apos a extracao o Thunderbird abria vazio, pedindo conta
        // nova. A causa raiz era o Install ID do profiles.ini recuperado nao bater com o da
        // instalacao nova. Este teste falha se essa causa raiz nao estiver corrigida.
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        const string mailContent = "De: cliente@exemplo.com\nAssunto: pedido 4821\n\nSegue anexo.";

        var sourceRoot = fixture.CreateThunderbirdInstall(
            "source",
            installId: "Install4A1B2C3D4E5F6789",
            profileFolder: "78ypv6h6.default-release",
            mailContent: mailContent);

        // O computador novo ja tem o Thunderbird instalado (processo real da empresa) e ja rodou
        // uma vez, criando seu PROPRIO perfil vazio com um Install ID DIFERENTE do da origem —
        // exatamente a condicao que quebrava a v0.9.
        var destinationProfileRoot = fixture.CreateThunderbirdInstall(
            "destination",
            installId: "Install9F8E7D6C5B4A3210",
            profileFolder: "k2n5x8qw.default-release",
            mailContent: null);

        var processAdapter = new RecordingProcessAdapter();
        var module = new ThunderbirdMigrationModule(fileSystem, processAdapter);
        var discovery = await module.DiscoverAsync(
            new MigrationContext(sourceRoot, "auxatc", fileSystem),
            CancellationToken.None);
        Assert.True(discovery.IsApplicable);

        var packageRoot = fixture.PathFor("package");
        var operationId = Guid.Parse("30303030-3030-3030-3030-303030303030");
        var sequence = 0L;
        await foreach (var item in module.EnumerateBackupAsync(discovery, CancellationToken.None))
        {
            sequence++;
            Assert.Equal(sequence, item.Sequence);
            var destinationPath = Path.Combine(
                packageRoot,
                item.PackageRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(item.SourcePath, destinationPath);
        }

        Assert.True(sequence > 0);

        var restoreContext = new RestoreContext(
            operationId,
            packageRoot,
            destinationProfileRoot,
            fileSystem,
            new OperationCheckpoint(
                operationId,
                Guid.NewGuid(),
                OperationKind.Restore,
                RestoreState.RestoringAppModules.ToString(),
                null,
                0,
                null,
                DurableCounters.Empty));

        var result = await module.RestoreAsync(restoreContext, CancellationToken.None);

        Assert.Equal(ModuleOutcome.Applied, result.Outcome);
        Assert.Contains("thunderbird", processAdapter.TerminatedProcessNames);

        var destinationThunderbirdRoot = Path.Combine(
            destinationProfileRoot,
            "AppData", "Roaming", "Thunderbird");
        var reboundIni = MozillaProfilesIni.Parse(
            File.ReadAllText(Path.Combine(destinationThunderbirdRoot, "profiles.ini")));

        // A prova de que o bug foi corrigido: a secao Install da instalacao NOVA (destino) agora
        // aponta para o perfil recuperado — nao ficou o Install ID antigo, nem os dois coexistindo.
        Assert.Equal("Install9F8E7D6C5B4A3210", reboundIni.FindInstallSectionName());
        Assert.Equal(
            "Profiles/78ypv6h6.default-release",
            reboundIni.ResolveDefaultProfilePath());

        var restoredMailPath = Path.Combine(
            destinationThunderbirdRoot,
            "Profiles", "78ypv6h6.default-release",
            "Mail", "pop.exemplo.com-1", "Inbox");
        Assert.True(File.Exists(restoredMailPath));
        Assert.Equal(mailContent, File.ReadAllText(restoredMailPath));

        // O perfil vazio antigo da instalacao nova nao deve ter sobrado misturado no destino.
        Assert.False(Directory.Exists(Path.Combine(
            destinationThunderbirdRoot,
            "Profiles", "k2n5x8qw.default-release")));
    }

    [Fact]
    public async Task RestoreAsync_RevertsSwap_WhenRecoveredProfileIsMissingPrefsJs()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();

        var sourceRoot = fixture.CreateThunderbirdInstall(
            "source-corrupt",
            installId: "InstallAAAAAAAAAAAAAAAA",
            profileFolder: "corrupt.default-release",
            mailContent: "conteudo",
            includePrefsJs: false);
        var destinationProfileRoot = fixture.CreateThunderbirdInstall(
            "destination-for-corrupt",
            installId: "InstallBBBBBBBBBBBBBBBB",
            profileFolder: "novo.default-release",
            mailContent: null);

        var processAdapter = new RecordingProcessAdapter();
        var module = new ThunderbirdMigrationModule(fileSystem, processAdapter);
        var discovery = await module.DiscoverAsync(
            new MigrationContext(sourceRoot, "auxatc", fileSystem),
            CancellationToken.None);

        var packageRoot = fixture.PathFor("package-corrupt");
        await foreach (var item in module.EnumerateBackupAsync(discovery, CancellationToken.None))
        {
            var destinationPath = Path.Combine(
                packageRoot,
                item.PackageRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(item.SourcePath, destinationPath);
        }

        var operationId = Guid.Parse("40404040-4040-4040-4040-404040404040");
        var restoreContext = new RestoreContext(
            operationId,
            packageRoot,
            destinationProfileRoot,
            fileSystem,
            new OperationCheckpoint(
                operationId,
                Guid.NewGuid(),
                OperationKind.Restore,
                RestoreState.RestoringAppModules.ToString(),
                null,
                0,
                null,
                DurableCounters.Empty));

        var result = await module.RestoreAsync(restoreContext, CancellationToken.None);

        Assert.Equal(ModuleOutcome.Failed, result.Outcome);
        Assert.NotNull(result.DiagnosticPath);

        // O destino precisa voltar exatamente ao que era antes (perfil novo vazio da instalacao
        // atual), nunca ficar com uma arvore recuperada quebrada no lugar.
        var destinationThunderbirdRoot = Path.Combine(
            destinationProfileRoot,
            "AppData", "Roaming", "Thunderbird");
        Assert.True(Directory.Exists(Path.Combine(
            destinationThunderbirdRoot,
            "Profiles", "novo.default-release")));
        Assert.False(Directory.Exists(Path.Combine(
            destinationThunderbirdRoot,
            "Profiles", "corrupt.default-release")));
    }

    [Fact]
    public async Task RestoreAsync_RevertsSwap_WhenRecoveredPrefsJsIsEmpty()
    {
        // "prefs.js existe" nao e suficiente evidencia de um perfil valido — um arquivo de 0
        // bytes passaria pela checagem antiga (so verificava presenca), mas e claramente uma
        // copia corrompida/truncada. A validacao precisa pegar isso tambem.
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();

        var sourceRoot = fixture.CreateThunderbirdInstall(
            "source-empty-prefs",
            installId: "InstallCCCCCCCCCCCCCCCC",
            profileFolder: "empty-prefs.default-release",
            mailContent: "conteudo",
            emptyPrefsJs: true);
        var destinationProfileRoot = fixture.CreateThunderbirdInstall(
            "destination-for-empty-prefs",
            installId: "InstallDDDDDDDDDDDDDDDD",
            profileFolder: "novo2.default-release",
            mailContent: null);

        var processAdapter = new RecordingProcessAdapter();
        var module = new ThunderbirdMigrationModule(fileSystem, processAdapter);
        var discovery = await module.DiscoverAsync(
            new MigrationContext(sourceRoot, "auxatc", fileSystem),
            CancellationToken.None);

        var packageRoot = fixture.PathFor("package-empty-prefs");
        await foreach (var item in module.EnumerateBackupAsync(discovery, CancellationToken.None))
        {
            var destinationPath = Path.Combine(
                packageRoot,
                item.PackageRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(item.SourcePath, destinationPath);
        }

        var operationId = Guid.Parse("45454545-4545-4545-4545-454545454545");
        var restoreContext = new RestoreContext(
            operationId,
            packageRoot,
            destinationProfileRoot,
            fileSystem,
            new OperationCheckpoint(
                operationId,
                Guid.NewGuid(),
                OperationKind.Restore,
                RestoreState.RestoringAppModules.ToString(),
                null,
                0,
                null,
                DurableCounters.Empty));

        var result = await module.RestoreAsync(restoreContext, CancellationToken.None);

        Assert.Equal(ModuleOutcome.Failed, result.Outcome);
        var destinationThunderbirdRoot = Path.Combine(
            destinationProfileRoot,
            "AppData", "Roaming", "Thunderbird");
        Assert.True(Directory.Exists(Path.Combine(
            destinationThunderbirdRoot,
            "Profiles", "novo2.default-release")));
    }

    private sealed class RecordingProcessAdapter : IProcessAdapter
    {
        public List<string> TerminatedProcessNames { get; } = [];

        public Task TerminateProcessTreesAsync(
            IReadOnlyCollection<string> processNames,
            CancellationToken cancellationToken = default)
        {
            TerminatedProcessNames.AddRange(processNames);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> GetRunningProcessesAsync(
            IReadOnlyCollection<string> processNames,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            System.IO.Path.GetTempPath(),
            "grbkp-thunderbird-phase3",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string PathFor(string relative) =>
            System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public string CreateTree(string relativePath)
        {
            var path = PathFor(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateThunderbirdInstall(
            string label,
            string installId,
            string profileFolder,
            string? mailContent,
            bool includePrefsJs = true,
            bool emptyPrefsJs = false)
        {
            var profileRoot = PathFor($"{label}/Users/auxatc");
            var thunderbirdRoot = System.IO.Path.Combine(
                profileRoot, "AppData", "Roaming", "Thunderbird");
            var profilePath = System.IO.Path.Combine(thunderbirdRoot, "Profiles", profileFolder);
            Directory.CreateDirectory(profilePath);

            if (includePrefsJs)
            {
                File.WriteAllText(
                    System.IO.Path.Combine(profilePath, "prefs.js"),
                    emptyPrefsJs
                        ? string.Empty
                        : "user_pref(\"mail.server.server1.hostname\", \"pop.exemplo.com\");",
                    Encoding.UTF8);
            }

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
