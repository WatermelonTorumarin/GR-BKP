using System.Text;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Modules.Office;
using GRBKP.Core.Operations;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class OfficeMigrationModuleTests
{
    [Fact]
    public async Task DiscoverAsync_ReportsNotApplicable_WhenNoKnownTreeExists()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var sourceRoot = fixture.CreateTree("no-office/Users/auxatc");
        var module = new OfficeMigrationModule(fileSystem, new RecordingProcessAdapter());

        var discovery = await module.DiscoverAsync(
            new MigrationContext(sourceRoot, "auxatc", fileSystem), CancellationToken.None);

        Assert.False(discovery.IsApplicable);
    }

    [Fact]
    public async Task RestoreAsync_ReplacesEachKnownSubtreeIndependently_AndClosesOfficeProcesses()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        const string signatureMarker = "ASSINATURA-DO-FUNCIONARIO";
        const string pstMarker = "CONTEUDO-DO-PST-DO-OUTLOOK";

        var sourceRoot = fixture.PathFor("source/Users/auxatc");
        fixture.WriteFile(
            "source/Users/auxatc/AppData/Roaming/Microsoft/Signatures/Padrao.htm", signatureMarker);
        fixture.WriteFile(
            "source/Users/auxatc/AppData/Local/Microsoft/Outlook/auxatc.pst", pstMarker);
        // "Templates" e "UProof" nao existem na origem — nao devem ser tocados na restauracao.

        var destinationRoot = fixture.PathFor("destination/Users/auxatc.DOMINIO");
        fixture.WriteFile(
            "destination/Users/auxatc.DOMINIO/AppData/Roaming/Microsoft/Signatures/Antiga.htm",
            "ASSINATURA-VAZIA-DO-PERFIL-NOVO");
        fixture.WriteFile(
            "destination/Users/auxatc.DOMINIO/AppData/Roaming/Microsoft/Templates/Normal.dotm",
            "MODELO-PADRAO-DO-PERFIL-NOVO");

        var processAdapter = new RecordingProcessAdapter();
        var module = new OfficeMigrationModule(fileSystem, processAdapter);
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
        Assert.Contains("OUTLOOK", processAdapter.TerminatedProcessNames);
        Assert.Contains("WINWORD", processAdapter.TerminatedProcessNames);

        // Assinaturas: substituida pela da origem (a antiga do perfil novo nao deve sobrar).
        Assert.Equal(
            signatureMarker,
            File.ReadAllText(Path.Combine(
                destinationRoot, "AppData", "Roaming", "Microsoft", "Signatures", "Padrao.htm")));
        Assert.False(File.Exists(Path.Combine(
            destinationRoot, "AppData", "Roaming", "Microsoft", "Signatures", "Antiga.htm")));

        // Outlook: PST recuperado presente.
        Assert.Equal(
            pstMarker,
            File.ReadAllText(Path.Combine(
                destinationRoot, "AppData", "Local", "Microsoft", "Outlook", "auxatc.pst")));

        // Templates: nao existia na origem, entao o que ja estava no destino NAO deve ser mexido.
        Assert.Equal(
            "MODELO-PADRAO-DO-PERFIL-NOVO",
            File.ReadAllText(Path.Combine(
                destinationRoot, "AppData", "Roaming", "Microsoft", "Templates", "Normal.dotm")));
    }

    [Fact]
    public async Task RestoreAsync_RevertsOnlyTheFailedSubtree_WhenItsCapturedFileIsEmpty()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();

        var sourceRoot = fixture.PathFor("source-corrupt/Users/auxatc");
        // A assinatura capturada existe mas esta com 0 bytes — evidencia de copia
        // truncada/corrompida, igual ao padrao ja usado para prefs.js/Local State.
        fixture.WriteEmptyFile(
            "source-corrupt/Users/auxatc/AppData/Roaming/Microsoft/Signatures/Padrao.htm");
        fixture.WriteFile(
            "source-corrupt/Users/auxatc/AppData/Local/Microsoft/Outlook/auxatc.pst", "dados-ok");

        var destinationRoot = fixture.PathFor("destination-corrupt/Users/auxatc.DOMINIO");
        fixture.WriteFile(
            "destination-corrupt/Users/auxatc.DOMINIO/AppData/Roaming/Microsoft/Signatures/Existente.htm",
            "ASSINATURA-QUE-JA-EXISTIA");

        var module = new OfficeMigrationModule(fileSystem, new RecordingProcessAdapter());
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
        // A assinatura que ja existia no destino precisa ter voltado, intacta.
        Assert.Equal(
            "ASSINATURA-QUE-JA-EXISTIA",
            File.ReadAllText(Path.Combine(
                destinationRoot, "AppData", "Roaming", "Microsoft", "Signatures", "Existente.htm")));
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
            System.IO.Path.GetTempPath(), "grbkp-office-phase3", Guid.NewGuid().ToString("N"));

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
