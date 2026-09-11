using GRBKP.Core.Adapters;
using GRBKP.Core.Discovery;
using GRBKP.Core.Modules.Printing;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class PrinterDriverMigrationServiceTests
{
    [Fact]
    public async Task CaptureAsync_CopiesDiscoveredPackages_AndWritesManifest()
    {
        using var fixture = new TemporaryDirectory();
        var volumeRoot = fixture.PathFor("F");
        fixture.CreatePackage(
            volumeRoot,
            "brprm15a.inf_amd64_7036458cd876f71d",
            "oem23.inf",
            "[Version]\nProvider=Brother Industries, Ltd.\n");

        var fileSystem = new WindowsFileSystemAdapter();
        var service = new PrinterDriverMigrationService(
            fileSystem, new RecordingCommandAdapter(), new PrinterDriverDiscovery(fileSystem));
        var packageRoot = fixture.PathFor("package");

        var result = await service.CaptureAsync(volumeRoot, packageRoot, CancellationToken.None);

        var captured = Assert.Single(result.Drivers);
        Assert.Equal("brprm15a.inf_amd64_7036458cd876f71d", captured.PackageFolderName);
        Assert.Equal(PrinterDriverVendor.Brother, captured.Vendor);

        var copiedInfPath = Path.Combine(
            packageRoot, "modules", "printer-drivers",
            "brprm15a.inf_amd64_7036458cd876f71d", "oem23.inf");
        Assert.True(File.Exists(copiedInfPath));
        Assert.Contains("Brother", await File.ReadAllTextAsync(copiedInfPath));

        var manifestPath = Path.Combine(packageRoot, "modules", "printer-drivers", "manifest.json");
        Assert.True(File.Exists(manifestPath));
    }

    [Fact]
    public async Task RestoreAsync_InstallsEachCapturedPackage_ViaPnpUtil()
    {
        using var fixture = new TemporaryDirectory();
        var volumeRoot = fixture.PathFor("F");
        fixture.CreatePackage(
            volumeRoot, "brprm15a.inf_amd64_aaaa", "oem23.inf", "Provider=Brother\n");
        fixture.CreatePackage(
            volumeRoot, "zdesigner.inf_amd64_bbbb", "oem45.inf", "Provider=Zebra\n");

        var fileSystem = new WindowsFileSystemAdapter();
        var commandAdapter = new RecordingCommandAdapter();
        var service = new PrinterDriverMigrationService(
            fileSystem, commandAdapter, new PrinterDriverDiscovery(fileSystem));
        var packageRoot = fixture.PathFor("package-restore");
        await service.CaptureAsync(volumeRoot, packageRoot, CancellationToken.None);

        var outcomes = await service.RestoreAsync(packageRoot, CancellationToken.None);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.True(outcome.Succeeded));
        Assert.Equal(2, commandAdapter.Requests.Count);
        Assert.All(commandAdapter.Requests, request =>
        {
            Assert.Equal(ApprovedWindowsCommand.PnpUtil, request.Command);
            Assert.Equal("/add-driver", request.Arguments[0]);
            Assert.Equal("/install", request.Arguments[2]);
            Assert.EndsWith(".inf", request.Arguments[1], StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task RestoreAsync_ReportsFailure_WhenPnpUtilReturnsNonZeroExitCode()
    {
        using var fixture = new TemporaryDirectory();
        var volumeRoot = fixture.PathFor("F");
        fixture.CreatePackage(
            volumeRoot, "brprm15a.inf_amd64_cccc", "oem23.inf", "Provider=Brother\n");

        var fileSystem = new WindowsFileSystemAdapter();
        var commandAdapter = new RecordingCommandAdapter { NextExitCode = 87 };
        var service = new PrinterDriverMigrationService(
            fileSystem, commandAdapter, new PrinterDriverDiscovery(fileSystem));
        var packageRoot = fixture.PathFor("package-fail");
        await service.CaptureAsync(volumeRoot, packageRoot, CancellationToken.None);

        var outcomes = await service.RestoreAsync(packageRoot, CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task RestoreAsync_ReturnsEmpty_WhenNoManifestWasCaptured()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var service = new PrinterDriverMigrationService(
            fileSystem, new RecordingCommandAdapter(), new PrinterDriverDiscovery(fileSystem));

        var outcomes = await service.RestoreAsync(
            fixture.PathFor("package-sem-drivers"), CancellationToken.None);

        Assert.Empty(outcomes);
    }

    private sealed class RecordingCommandAdapter : IWindowsCommandAdapter
    {
        public List<WindowsCommandRequest> Requests { get; } = [];

        public int NextExitCode { get; set; }

        public Task<WindowsCommandResult> ExecuteApprovedAsync(
            WindowsCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new WindowsCommandResult(NextExitCode, "ok", string.Empty));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            System.IO.Path.GetTempPath(), "grbkp-printer-service", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string PathFor(string relative) =>
            System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void CreatePackage(
            string volumeRoot, string packageFolderName, string infFileName, string infContent)
        {
            var packagePath = System.IO.Path.Combine(
                volumeRoot, "Windows", "System32", "DriverStore", "FileRepository", packageFolderName);
            Directory.CreateDirectory(packagePath);
            File.WriteAllText(System.IO.Path.Combine(packagePath, infFileName), infContent);
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
