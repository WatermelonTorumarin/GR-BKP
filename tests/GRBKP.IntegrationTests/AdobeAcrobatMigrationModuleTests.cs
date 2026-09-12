using System.Text;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Modules.Adobe;
using GRBKP.Core.Operations;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class AdobeAcrobatMigrationModuleTests
{
    [Fact]
    public async Task DiscoverAsync_ReportsNotApplicable_WhenAcrobatFolderIsAbsent()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var sourceRoot = fixture.CreateTree("no-acrobat/Users/auxatc");
        var module = new AdobeAcrobatMigrationModule(fileSystem, new RecordingProcessAdapter());

        var discovery = await module.DiscoverAsync(
            new MigrationContext(sourceRoot, "auxatc", fileSystem), CancellationToken.None);

        Assert.False(discovery.IsApplicable);
    }

    [Fact]
    public async Task RestoreAsync_ReplacesAcrobatSettings_AndClosesReaderProcesses()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        const string marker = "CONFIGURACAO-BASICA-DO-ACROBAT-READER";

        var sourceRoot = fixture.PathFor("source/Users/auxatc");
        fixture.WriteFile(
            "source/Users/auxatc/AppData/Roaming/Adobe/Acrobat/DC/Preferences.xml", marker);

        var destinationRoot = fixture.PathFor("destination/Users/auxatc.DOMINIO");
        fixture.WriteFile(
            "destination/Users/auxatc.DOMINIO/AppData/Roaming/Adobe/Acrobat/DC/Preferences.xml",
            "CONFIGURACAO-ANTIGA-DO-PERFIL-NOVO");

        var processAdapter = new RecordingProcessAdapter();
        var module = new AdobeAcrobatMigrationModule(fileSystem, processAdapter);
        var discovery = await module.DiscoverAsync(
            new MigrationContext(sourceRoot, "auxatc", fileSystem), CancellationToken.None);
        Assert.True(discovery.IsApplicable);

        var packageRoot = fixture.PathFor("package");
        await foreach (var item in module.EnumerateBackupAsync(discovery, CancellationToken.None))
        {
            var destinationItemPath = Path.Combine(
                packageRoot, item.PackageRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationItemPath)!);
            File.Copy(item.SourcePath, destinationItemPath);
        }

        var operationId = Guid.NewGuid();
        var restoreContext = new RestoreContext(
            operationId,
            packageRoot,
            destinationRoot,
            fileSystem,
            new OperationCheckpoint(
                operationId, Guid.NewGuid(), OperationKind.Restore,
                RestoreState.RestoringAppModules.ToString(), null, 0, null, DurableCounters.Empty));

        var result = await module.RestoreAsync(restoreContext, CancellationToken.None);

        Assert.Equal(ModuleOutcome.Applied, result.Outcome);
        Assert.Contains("AcroRd32", processAdapter.TerminatedProcessNames);
        Assert.Contains("Acrobat", processAdapter.TerminatedProcessNames);
        Assert.Equal(
            marker,
            File.ReadAllText(Path.Combine(
                destinationRoot, "AppData", "Roaming", "Adobe", "Acrobat", "DC", "Preferences.xml")));
    }

    [Fact]
    public async Task RestoreAsync_RevertsAndFails_WhenCapturedFileIsEmpty()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();

        var sourceRoot = fixture.PathFor("source-corrupt/Users/auxatc");
        fixture.WriteEmptyFile(
            "source-corrupt/Users/auxatc/AppData/Roaming/Adobe/Acrobat/DC/Preferences.xml");

        var destinationRoot = fixture.PathFor("destination-corrupt/Users/auxatc.DOMINIO");
        fixture.WriteFile(
            "destination-corrupt/Users/auxatc.DOMINIO/AppData/Roaming/Adobe/Acrobat/DC/Preferences.xml",
            "CONFIGURACAO-QUE-JA-EXISTIA");

        var module = new AdobeAcrobatMigrationModule(fileSystem, new RecordingProcessAdapter());
        var discovery = await module.DiscoverAsync(
            new MigrationContext(sourceRoot, "auxatc", fileSystem), CancellationToken.None);

        var packageRoot = fixture.PathFor("package-corrupt");
        await foreach (var item in module.EnumerateBackupAsync(discovery, CancellationToken.None))
        {
            var destinationItemPath = Path.Combine(
                packageRoot, item.PackageRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationItemPath)!);
            File.Copy(item.SourcePath, destinationItemPath);
        }

        var operationId = Guid.NewGuid();
        var restoreContext = new RestoreContext(
            operationId,
            packageRoot,
            destinationRoot,
            fileSystem,
            new OperationCheckpoint(
                operationId, Guid.NewGuid(), OperationKind.Restore,
                RestoreState.RestoringAppModules.ToString(), null, 0, null, DurableCounters.Empty));

        var result = await module.RestoreAsync(restoreContext, CancellationToken.None);

        Assert.Equal(ModuleOutcome.Failed, result.Outcome);
        Assert.Equal(
            "CONFIGURACAO-QUE-JA-EXISTIA",
            File.ReadAllText(Path.Combine(
                destinationRoot, "AppData", "Roaming", "Adobe", "Acrobat", "DC", "Preferences.xml")));
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
            System.IO.Path.GetTempPath(), "grbkp-adobe-phase3", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string PathFor(string relative) =>
            System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public string CreateTree(string relativePath)
        {
            var path = PathFor(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public void WriteFile(string relativePath, string content)
        {
            var path = PathFor(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Encoding.UTF8);
        }

        public void WriteEmptyFile(string relativePath)
        {
            var path = PathFor(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
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
