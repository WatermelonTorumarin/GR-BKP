using System.Text;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Modules.Firefox;
using GRBKP.Core.Modules.Mozilla;
using GRBKP.Core.Operations;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class FirefoxMigrationModuleTests
{
    [Fact]
    public async Task DiscoverAsync_ReportsNotApplicable_WhenFirefoxWasNeverUsedOnSource()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var sourceRoot = fixture.CreateTree("no-firefox/Users/auxatc");
        var module = new FirefoxMigrationModule(fileSystem, new RecordingProcessAdapter());

        var discovery = await module.DiscoverAsync(
            new MigrationContext(sourceRoot, "auxatc", fileSystem),
            CancellationToken.None);

        Assert.False(discovery.IsApplicable);
    }

    [Fact]
    public async Task RestoreAsync_ReactivatesRecoveredProfileOnNewInstallation_AndClosesFirefoxFirst()
    {
        // Mesmo mecanismo do Thunderbird (mesmo formato de profiles.ini da Mozilla): a instalacao
        // nova tem seu proprio Install ID e um perfil vazio proprio; a restauracao precisa
        // revincular esse Install ID ao perfil recuperado, nao apenas copiar o profiles.ini antigo.
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        const string bookmarkMarker = "BOOKMARKS-BACKUP-CONTENT";

        var sourceRoot = fixture.CreateFirefoxInstall(
            "source",
            installId: "InstallFF00000000000001",
            profileFolder: "abc12345.default-release",
            bookmarkContent: bookmarkMarker);
        var destinationProfileRoot = fixture.CreateFirefoxInstall(
            "destination",
            installId: "InstallFF99999999999999",
            profileFolder: "xyz98765.default-release",
            bookmarkContent: null);

        var processAdapter = new RecordingProcessAdapter();
        var module = new FirefoxMigrationModule(fileSystem, processAdapter);
        var discovery = await module.DiscoverAsync(
            new MigrationContext(sourceRoot, "auxatc", fileSystem),
            CancellationToken.None);
        Assert.True(discovery.IsApplicable);

        var packageRoot = fixture.PathFor("package");
        var operationId = Guid.Parse("80808080-8080-8080-8080-808080808080");
        await foreach (var item in module.EnumerateBackupAsync(discovery, CancellationToken.None))
        {
            var destinationPath = Path.Combine(
                packageRoot,
                item.PackageRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(item.SourcePath, destinationPath);
        }

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
        Assert.Contains("firefox", processAdapter.TerminatedProcessNames);

        var destinationFirefoxRoot = Path.Combine(
            destinationProfileRoot, "AppData", "Roaming", "Mozilla", "Firefox");
        var reboundIni = MozillaProfilesIni.Parse(
            File.ReadAllText(Path.Combine(destinationFirefoxRoot, "profiles.ini")));
        Assert.Equal("InstallFF99999999999999", reboundIni.FindInstallSectionName());
        Assert.Equal(
            "Profiles/abc12345.default-release",
            reboundIni.ResolveDefaultProfilePath());

        var restoredBookmarksPath = Path.Combine(
            destinationFirefoxRoot,
            "Profiles", "abc12345.default-release",
            "bookmarkbackups", "bookmarks.jsonlz4");
        Assert.True(File.Exists(restoredBookmarksPath));
        Assert.Equal(bookmarkMarker, File.ReadAllText(restoredBookmarksPath));

        Assert.False(Directory.Exists(Path.Combine(
            destinationFirefoxRoot,
            "Profiles", "xyz98765.default-release")));
    }

    [Fact]
    public async Task RestoreAsync_RevertsSwap_WhenRecoveredPrefsJsIsEmpty()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();

        var sourceRoot = fixture.CreateFirefoxInstall(
            "source-empty-prefs",
            installId: "InstallFFCCCCCCCCCCCC",
            profileFolder: "empty-prefs.default-release",
            bookmarkContent: "algo",
            emptyPrefsJs: true);
        var destinationProfileRoot = fixture.CreateFirefoxInstall(
            "destination-for-empty-prefs",
            installId: "InstallFFDDDDDDDDDDDD",
            profileFolder: "novo2.default-release",
            bookmarkContent: null);

        var processAdapter = new RecordingProcessAdapter();
        var module = new FirefoxMigrationModule(fileSystem, processAdapter);
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

        var operationId = Guid.NewGuid();
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
        var destinationFirefoxRoot = Path.Combine(
            destinationProfileRoot, "AppData", "Roaming", "Mozilla", "Firefox");
        Assert.True(Directory.Exists(Path.Combine(
            destinationFirefoxRoot,
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
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            System.IO.Path.GetTempPath(),
            "grbkp-firefox-phase3",
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

        public string CreateFirefoxInstall(
            string label,
            string installId,
            string profileFolder,
            string? bookmarkContent,
            bool emptyPrefsJs = false)
        {
            var profileRoot = PathFor($"{label}/Users/auxatc");
            var firefoxRoot = System.IO.Path.Combine(
                profileRoot, "AppData", "Roaming", "Mozilla", "Firefox");
            var profilePath = System.IO.Path.Combine(firefoxRoot, "Profiles", profileFolder);
            Directory.CreateDirectory(profilePath);

            File.WriteAllText(
                System.IO.Path.Combine(profilePath, "prefs.js"),
                emptyPrefsJs ? string.Empty : "user_pref(\"browser.startup.homepage\", \"about:home\");",
                Encoding.UTF8);

            if (bookmarkContent is not null)
            {
                var backupsDirectory = System.IO.Path.Combine(profilePath, "bookmarkbackups");
                Directory.CreateDirectory(backupsDirectory);
                File.WriteAllText(
                    System.IO.Path.Combine(backupsDirectory, "bookmarks.jsonlz4"),
                    bookmarkContent,
                    Encoding.UTF8);
            }

            File.WriteAllText(
                System.IO.Path.Combine(firefoxRoot, "profiles.ini"),
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
