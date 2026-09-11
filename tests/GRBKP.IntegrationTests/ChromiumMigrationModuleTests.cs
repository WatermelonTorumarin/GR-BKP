using System.Text;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Modules.Chromium;
using GRBKP.Core.Operations;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class ChromiumMigrationModuleTests
{
    [Fact]
    public async Task DiscoverAsync_ReportsNotApplicable_WhenBrowserWasNeverUsedOnSource()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var sourceRoot = fixture.CreateTree("no-chrome/Users/auxatc");
        var module = new ChromiumMigrationModule(
            fileSystem, new RecordingProcessAdapter(), ChromiumBrowserDescriptor.Chrome);

        var discovery = await module.DiscoverAsync(
            new MigrationContext(sourceRoot, "auxatc", fileSystem),
            CancellationToken.None);

        Assert.False(discovery.IsApplicable);
    }

    [Theory]
    [MemberData(nameof(Descriptors))]
    public async Task RestoreAsync_ReplacesWholeUserDataTree_AndClosesTheRightProcess(
        ChromiumBrowserDescriptor descriptor)
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        const string bookmarkMarker = "CHROMIUM-BOOKMARKS-FROM-OLD-SSD";

        var sourceRoot = fixture.CreateChromiumInstall(
            "source", descriptor, bookmarkContent: bookmarkMarker, includeCacheJunk: true);
        var destinationProfileRoot = fixture.CreateChromiumInstall(
            "destination", descriptor, bookmarkContent: "OLD-EMPTY-PROFILE", includeCacheJunk: false);

        var processAdapter = new RecordingProcessAdapter();
        var module = new ChromiumMigrationModule(fileSystem, processAdapter, descriptor);
        var discovery = await module.DiscoverAsync(
            new MigrationContext(sourceRoot, "auxatc", fileSystem),
            CancellationToken.None);
        Assert.True(discovery.IsApplicable);

        var packageRoot = fixture.PathFor($"package-{descriptor.ModuleId}");
        var cacheItemFound = false;
        await foreach (var item in module.EnumerateBackupAsync(discovery, CancellationToken.None))
        {
            if (item.PackageRelativePath.Contains("/Cache/", StringComparison.OrdinalIgnoreCase))
            {
                cacheItemFound = true;
            }

            var destinationPath = Path.Combine(
                packageRoot,
                item.PackageRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(item.SourcePath, destinationPath);
        }

        Assert.False(cacheItemFound);

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

        Assert.Equal(ModuleOutcome.Applied, result.Outcome);
        Assert.Contains(descriptor.ProcessName, processAdapter.TerminatedProcessNames);

        var userDataRoot = Path.Combine(
            [destinationProfileRoot, .. descriptor.AppDataRelativeSegments]);
        var bookmarksPath = Path.Combine(userDataRoot, "Default", "Bookmarks");
        Assert.True(File.Exists(bookmarksPath));
        Assert.Equal(bookmarkMarker, File.ReadAllText(bookmarksPath));
    }

    [Fact]
    public async Task RestoreAsync_RevertsSwap_WhenLocalStateReferencesAMissingProfile()
    {
        // "Local State" existir nao basta: se ele referencia um perfil (ex.: "Default") em
        // profile.info_cache mas esse perfil nao tem Preferences no que foi recuperado, e sinal
        // de backup parcial/corrompido — nao de um User Data valido.
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var descriptor = ChromiumBrowserDescriptor.Chrome;

        var sourceRoot = fixture.CreateChromiumInstall(
            "source-corrupt",
            descriptor,
            bookmarkContent: "algo",
            includeCacheJunk: false,
            omitPreferencesFile: true);
        var destinationProfileRoot = fixture.CreateChromiumInstall(
            "destination-for-corrupt",
            descriptor,
            bookmarkContent: "OLD-EMPTY-PROFILE",
            includeCacheJunk: false);

        var processAdapter = new RecordingProcessAdapter();
        var module = new ChromiumMigrationModule(fileSystem, processAdapter, descriptor);
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
        var userDataRoot = Path.Combine(
            [destinationProfileRoot, .. descriptor.AppDataRelativeSegments]);
        Assert.Equal(
            "OLD-EMPTY-PROFILE",
            File.ReadAllText(Path.Combine(userDataRoot, "Default", "Bookmarks")));
    }

    public static IEnumerable<object[]> Descriptors()
    {
        yield return [ChromiumBrowserDescriptor.Chrome];
        yield return [ChromiumBrowserDescriptor.Edge];
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
            "grbkp-chromium-phase3",
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

        public string CreateChromiumInstall(
            string label,
            ChromiumBrowserDescriptor descriptor,
            string bookmarkContent,
            bool includeCacheJunk,
            bool omitPreferencesFile = false)
        {
            var profileRoot = PathFor($"{label}/Users/auxatc");
            var userDataRoot = System.IO.Path.Combine(
                [profileRoot, .. descriptor.AppDataRelativeSegments]);
            var defaultProfile = System.IO.Path.Combine(userDataRoot, "Default");
            Directory.CreateDirectory(defaultProfile);

            File.WriteAllText(
                System.IO.Path.Combine(userDataRoot, "Local State"),
                """{"profile":{"info_cache":{"Default":{"name":"Person 1"}}}}""",
                Encoding.UTF8);
            if (!omitPreferencesFile)
            {
                File.WriteAllText(
                    System.IO.Path.Combine(defaultProfile, "Preferences"),
                    "{}",
                    Encoding.UTF8);
            }

            File.WriteAllText(
                System.IO.Path.Combine(defaultProfile, "Bookmarks"),
                bookmarkContent,
                Encoding.UTF8);

            if (includeCacheJunk)
            {
                var cacheDirectory = System.IO.Path.Combine(defaultProfile, "Cache");
                Directory.CreateDirectory(cacheDirectory);
                File.WriteAllText(
                    System.IO.Path.Combine(cacheDirectory, "data_0"),
                    "LIXO-DE-CACHE-NAO-DEVE-IR-PRO-BACKUP",
                    Encoding.UTF8);
            }

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
