using GRBKP.Core.Adapters;
using GRBKP.Core.Modules.CorporateCleanup;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class CorporateCleanupServiceTests
{
    [Fact]
    public async Task DetectOneDriveAsync_PrefersPerMachine_UnderProgramFiles()
    {
        using var fixture = new TemporaryDirectory();
        var destinationVolumeRoot = fixture.PathFor("C");
        var destinationProfileRoot = fixture.PathFor(@"C\Users\auxatc.DOMINIO");
        fixture.CreateOneDriveSetup(
            Path.Combine(destinationVolumeRoot, "Program Files", "Microsoft OneDrive"));

        var service = new CorporateCleanupService(
            new WindowsFileSystemAdapter(), new RecordingUninstallerAdapter(), new RecordingCommandAdapter());

        var installation = await service.DetectOneDriveAsync(
            destinationVolumeRoot, destinationProfileRoot, CancellationToken.None);

        Assert.NotNull(installation);
        Assert.Equal(OneDriveInstallScope.PerMachine, installation.Scope);
    }

    [Fact]
    public async Task DetectOneDriveAsync_PrefersPerMachine_UnderProgramFilesX86()
    {
        using var fixture = new TemporaryDirectory();
        var destinationVolumeRoot = fixture.PathFor("C");
        var destinationProfileRoot = fixture.PathFor(@"C\Users\auxatc.DOMINIO");
        fixture.CreateOneDriveSetup(
            Path.Combine(destinationVolumeRoot, "Program Files (x86)", "Microsoft OneDrive"));

        var service = new CorporateCleanupService(
            new WindowsFileSystemAdapter(), new RecordingUninstallerAdapter(), new RecordingCommandAdapter());

        var installation = await service.DetectOneDriveAsync(
            destinationVolumeRoot, destinationProfileRoot, CancellationToken.None);

        Assert.NotNull(installation);
        Assert.Equal(OneDriveInstallScope.PerMachine, installation.Scope);
    }

    [Fact]
    public async Task DetectOneDriveAsync_FallsBackToPerUser_WhenNoPerMachineInstallExists()
    {
        using var fixture = new TemporaryDirectory();
        var destinationVolumeRoot = fixture.PathFor("C");
        var destinationProfileRoot = fixture.PathFor(@"C\Users\auxatc.DOMINIO");
        fixture.CreateOneDriveSetup(
            Path.Combine(destinationProfileRoot, "AppData", "Local", "Microsoft", "OneDrive"));

        var service = new CorporateCleanupService(
            new WindowsFileSystemAdapter(), new RecordingUninstallerAdapter(), new RecordingCommandAdapter());

        var installation = await service.DetectOneDriveAsync(
            destinationVolumeRoot, destinationProfileRoot, CancellationToken.None);

        Assert.NotNull(installation);
        Assert.Equal(OneDriveInstallScope.PerUser, installation.Scope);
    }

    [Fact]
    public async Task DetectOneDriveAsync_ReturnsNull_WhenNotInstalledAnywhere()
    {
        using var fixture = new TemporaryDirectory();
        var service = new CorporateCleanupService(
            new WindowsFileSystemAdapter(), new RecordingUninstallerAdapter(), new RecordingCommandAdapter());

        var installation = await service.DetectOneDriveAsync(
            fixture.PathFor("C-vazio"), fixture.PathFor(@"C-vazio\Users\auxatc"), CancellationToken.None);

        Assert.Null(installation);
    }

    [Fact]
    public async Task ApplyAsync_RunsOneDriveSetupUninstall_AndCopilotRemovalScript_WhenOneDriveIsInstalled()
    {
        using var fixture = new TemporaryDirectory();
        var destinationVolumeRoot = fixture.PathFor("C");
        var destinationProfileRoot = fixture.PathFor(@"C\Users\auxatc.DOMINIO");
        fixture.CreateOneDriveSetup(
            Path.Combine(destinationVolumeRoot, "Program Files", "Microsoft OneDrive"));

        var uninstallerAdapter = new RecordingUninstallerAdapter();
        var commandAdapter = new RecordingCommandAdapter();
        var service = new CorporateCleanupService(
            new WindowsFileSystemAdapter(), uninstallerAdapter, commandAdapter);

        var result = await service.ApplyAsync(
            destinationVolumeRoot, destinationProfileRoot, CancellationToken.None);

        Assert.NotNull(result.OneDrive);
        Assert.True(result.OneDriveUninstallSucceeded);
        var uninstallCall = Assert.Single(uninstallerAdapter.Calls);
        Assert.Equal(
            Path.Combine(destinationVolumeRoot, "Program Files", "Microsoft OneDrive", "OneDriveSetup.exe"),
            uninstallCall.ExecutablePath);
        Assert.Equal(["/uninstall"], uninstallCall.Arguments);

        Assert.True(result.CopilotRemovalSucceeded);
        var copilotCall = Assert.Single(commandAdapter.Requests);
        Assert.Equal(ApprovedWindowsCommand.PowerShell, copilotCall.Command);
        Assert.Contains("Copilot", copilotCall.Arguments[^1]);
    }

    [Fact]
    public async Task ApplyAsync_SkipsOneDriveUninstall_ButStillRemovesCopilot_WhenOneDriveIsNotInstalled()
    {
        using var fixture = new TemporaryDirectory();
        var destinationVolumeRoot = fixture.PathFor("C-sem-onedrive");
        var destinationProfileRoot = fixture.PathFor(@"C-sem-onedrive\Users\auxatc.DOMINIO");

        var uninstallerAdapter = new RecordingUninstallerAdapter();
        var commandAdapter = new RecordingCommandAdapter();
        var service = new CorporateCleanupService(
            new WindowsFileSystemAdapter(), uninstallerAdapter, commandAdapter);

        var result = await service.ApplyAsync(
            destinationVolumeRoot, destinationProfileRoot, CancellationToken.None);

        Assert.Null(result.OneDrive);
        Assert.False(result.OneDriveUninstallSucceeded);
        Assert.Empty(uninstallerAdapter.Calls);
        Assert.True(result.CopilotRemovalSucceeded);
        Assert.Single(commandAdapter.Requests);
    }

    [Fact]
    public async Task ApplyAsync_ReportsFailure_WhenOneDriveSetupReturnsNonZeroExitCode()
    {
        using var fixture = new TemporaryDirectory();
        var destinationVolumeRoot = fixture.PathFor("C");
        var destinationProfileRoot = fixture.PathFor(@"C\Users\auxatc.DOMINIO");
        fixture.CreateOneDriveSetup(
            Path.Combine(destinationVolumeRoot, "Program Files", "Microsoft OneDrive"));

        var uninstallerAdapter = new RecordingUninstallerAdapter { NextExitCode = 1 };
        var service = new CorporateCleanupService(
            new WindowsFileSystemAdapter(), uninstallerAdapter, new RecordingCommandAdapter());

        var result = await service.ApplyAsync(
            destinationVolumeRoot, destinationProfileRoot, CancellationToken.None);

        Assert.False(result.OneDriveUninstallSucceeded);
        Assert.Contains(
            result.Messages, message => message.Contains("Falha", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingUninstallerAdapter : IExternalUninstallerAdapter
    {
        public List<(string ExecutablePath, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public int NextExitCode { get; set; }

        public Task<WindowsCommandResult> RunUninstallerAsync(
            string verifiedExecutablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((verifiedExecutablePath, arguments));
            return Task.FromResult(new WindowsCommandResult(NextExitCode, "ok", string.Empty));
        }
    }

    private sealed class RecordingCommandAdapter : IWindowsCommandAdapter
    {
        public List<WindowsCommandRequest> Requests { get; } = [];

        public int NextExitCode { get; set; }

        public Task<WindowsCommandResult> ExecuteApprovedAsync(
            WindowsCommandRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new WindowsCommandResult(NextExitCode, "ok", string.Empty));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            System.IO.Path.GetTempPath(), "grbkp-corporate-cleanup", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string PathFor(string relative) =>
            System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void CreateOneDriveSetup(string directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "OneDriveSetup.exe"), [0x4D, 0x5A]);
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
