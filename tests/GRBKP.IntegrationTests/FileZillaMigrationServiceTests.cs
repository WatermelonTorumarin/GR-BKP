using System.Text;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules.FileZilla;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class FileZillaMigrationServiceTests
{
    private const string ClassicConfigXml = """
        <FileZillaServer>
          <Users>
            <User Name="1">
              <Option Name="Pass">1</Option>
              <Permissions>
                <Permission Dir="{PROFILE_ROOT}\Desktop\Scan">
                  <FileWrite>1</FileWrite>
                  <IsHome>1</IsHome>
                </Permission>
              </Permissions>
            </User>
          </Users>
        </FileZillaServer>
        """;

    [Fact]
    public async Task CaptureAsync_CopiesClassicConfig_WhenPresentOnSource()
    {
        using var fixture = new TemporaryDirectory();
        var sourceVolumeRoot = fixture.PathFor("F");
        fixture.CreateFileZillaInstall(
            sourceVolumeRoot, ClassicConfigXml.Replace("{PROFILE_ROOT}", @"F:\Users\auxatc"));

        var fileSystem = new WindowsFileSystemAdapter();
        var service = new FileZillaMigrationService(fileSystem, new RecordingServiceAdapter());
        var packageRoot = fixture.PathFor("package");

        var result = await service.CaptureAsync(sourceVolumeRoot, packageRoot, CancellationToken.None);

        Assert.True(result.Captured);
        var copiedPath = Path.Combine(
            packageRoot, "modules", "filezilla", "FileZilla Server.xml");
        Assert.True(File.Exists(copiedPath));
    }

    [Fact]
    public async Task CaptureAsync_ReportsNotCaptured_WhenFileZillaWasNeverInstalledOnSource()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var service = new FileZillaMigrationService(fileSystem, new RecordingServiceAdapter());

        var result = await service.CaptureAsync(
            fixture.PathFor("F-sem-filezilla"), fixture.PathFor("package"), CancellationToken.None);

        Assert.False(result.Captured);
    }

    [Fact]
    public async Task RestoreAsync_RebindsSharedDirectory_AndCyclesTheService()
    {
        using var fixture = new TemporaryDirectory();
        var sourceVolumeRoot = fixture.PathFor("F");
        fixture.CreateFileZillaInstall(
            sourceVolumeRoot, ClassicConfigXml.Replace("{PROFILE_ROOT}", @"F:\Users\auxatc"));

        var fileSystem = new WindowsFileSystemAdapter();
        var serviceAdapter = new RecordingServiceAdapter();
        var service = new FileZillaMigrationService(fileSystem, serviceAdapter);
        var packageRoot = fixture.PathFor("package-restore");
        await service.CaptureAsync(sourceVolumeRoot, packageRoot, CancellationToken.None);

        // Destino: FileZilla ja instalado (pendrive padrao da empresa), config vazia por enquanto.
        var destinationVolumeRoot = fixture.PathFor("C");
        fixture.CreateFileZillaInstall(destinationVolumeRoot, "<FileZillaServer />");

        var result = await service.RestoreAsync(
            packageRoot,
            oldProfileRoot: @"F:\Users\auxatc",
            newProfileRoot: @"C:\Users\auxatc.DOMINIO",
            destinationVolumeRoot: destinationVolumeRoot,
            CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(["FileZilla Server"], serviceAdapter.StoppedServices);
        Assert.Equal(["FileZilla Server"], serviceAdapter.StartedServices);

        var appliedConfigPath = Path.Combine(
            destinationVolumeRoot, "Program Files (x86)", "FileZilla Server", "FileZilla Server.xml");
        var appliedXml = await File.ReadAllTextAsync(appliedConfigPath);
        Assert.Contains(@"C:\Users\auxatc.DOMINIO\Desktop\Scan", appliedXml);
        Assert.DoesNotContain(@"F:\Users\auxatc\Desktop\Scan", appliedXml);
    }

    [Fact]
    public async Task RestoreAsync_ReportsFailure_WhenFileZillaIsNotInstalledOnDestination()
    {
        using var fixture = new TemporaryDirectory();
        var sourceVolumeRoot = fixture.PathFor("F");
        fixture.CreateFileZillaInstall(
            sourceVolumeRoot, ClassicConfigXml.Replace("{PROFILE_ROOT}", @"F:\Users\auxatc"));

        var fileSystem = new WindowsFileSystemAdapter();
        var serviceAdapter = new RecordingServiceAdapter();
        var service = new FileZillaMigrationService(fileSystem, serviceAdapter);
        var packageRoot = fixture.PathFor("package-no-dest");
        await service.CaptureAsync(sourceVolumeRoot, packageRoot, CancellationToken.None);

        var result = await service.RestoreAsync(
            packageRoot,
            oldProfileRoot: @"F:\Users\auxatc",
            newProfileRoot: @"C:\Users\auxatc.DOMINIO",
            destinationVolumeRoot: fixture.PathFor("C-sem-filezilla"),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.NotNull(result.DiagnosticMessage);
        Assert.Empty(serviceAdapter.StoppedServices);
    }

    private sealed class RecordingServiceAdapter : IWindowsServiceAdapter
    {
        public List<string> StoppedServices { get; } = [];

        public List<string> StartedServices { get; } = [];

        public Task StopAsync(string serviceName, CancellationToken cancellationToken = default)
        {
            StoppedServices.Add(serviceName);
            return Task.CompletedTask;
        }

        public Task StartAsync(string serviceName, CancellationToken cancellationToken = default)
        {
            StartedServices.Add(serviceName);
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            System.IO.Path.GetTempPath(), "grbkp-filezilla-service", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string PathFor(string relative) =>
            System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void CreateFileZillaInstall(string volumeRoot, string configXml)
        {
            var configDirectory = System.IO.Path.Combine(
                volumeRoot, "Program Files (x86)", "FileZilla Server");
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(
                System.IO.Path.Combine(configDirectory, "FileZilla Server.xml"),
                configXml,
                Encoding.UTF8);
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
