using System.Text;
using GRBKP.Core.Adapters;
using GRBKP.Core.Discovery;
using GRBKP.Core.Modules;
using GRBKP.Core.Modules.FileZilla;
using GRBKP.Core.Modules.Printing;
using GRBKP.Core.Operations;
using GRBKP.Core.Orchestration;
using GRBKP.Core.Packages;
using GRBKP.Core.Restore;
using GRBKP.Core.Transfers;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

/// <summary>
/// Prova de integracao real do fluxo da Fase 4: FileZilla e drivers de impressora sao servicos de
/// NIVEL DE MAQUINA (dependem da raiz do volume, nao da raiz do perfil), entao nao se encaixam no
/// contrato IMigrationModule (que so conhece raiz de perfil) e nao passam pelo
/// AppModuleRestoreCoordinator. Este teste reproduz exatamente a sequencia que o app real usa
/// (MainWindow): captura-los para dentro do MESMO packageRoot que o MigrationOrchestrator usa
/// para o perfil, e restaura-los logo apos a restauracao de perfil terminar — provando que as
/// duas trilhas coexistem no mesmo pacote sem colisao de caminho e sem que uma interfira na outra.
/// </summary>
public sealed class MachineLevelServicesWiringTests
{
    [Fact]
    public async Task CaptureAndRestore_CoexistInSamePackage_AlongsideProfileLevelOrchestration()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero));
        var packageModule = new BackupPackageModule(fileSystem, clock);
        var backupEngine = new StreamingBackupEngine(fileSystem, packageModule, clock);

        const string documentContent = "CONTRATO ASSINADO - CLIENTE ACME";

        // --- Origem: volume com um perfil (arquivo pessoal), FileZilla Server e um driver Brother ---
        var sourceVolumeRoot = fixture.PathFor("F");
        var sourceProfileRoot = Path.Combine(sourceVolumeRoot, "Users", "auxatc");
        var documentSourcePath = fixture.WriteFile(
            Path.Combine(sourceProfileRoot, "Documents", "contrato.txt"), documentContent);
        fixture.CreateFileZillaInstall(
            sourceVolumeRoot,
            $"""
            <FileZillaServer>
              <Users>
                <User Name="1">
                  <Permissions>
                    <Permission Dir="{sourceProfileRoot}\Desktop\Scan">
                      <IsHome>1</IsHome>
                    </Permission>
                  </Permissions>
                </User>
              </Users>
            </FileZillaServer>
            """);
        fixture.CreatePrinterDriverPackage(
            sourceVolumeRoot,
            "brprm15a.inf_amd64_7036458cd876f71d",
            "oem23.inf",
            "[Version]\nProvider=Brother Industries, Ltd.\n");

        // --- Destino: SSD novo, FileZilla ja instalado (pendrive padrao da empresa) ---
        var destinationProfileRoot = fixture.PathFor(@"destino\Users\auxatc.DOMINIO");
        var destinationVolumeRoot = fixture.PathFor("destino");
        fixture.CreateFileZillaInstall(destinationVolumeRoot, "<FileZillaServer />");

        var fileZillaService = new FileZillaMigrationService(fileSystem, new RecordingServiceAdapter());
        var commandAdapter = new RecordingCommandAdapter();
        var printerDriverService = new PrinterDriverMigrationService(
            fileSystem, commandAdapter, new PrinterDriverDiscovery(fileSystem));

        var packageRoot = fixture.PathFor("package");

        // --- Backup: arquivo pessoal via MigrationOrchestrator + captura direta dos dois servicos
        // de nivel de maquina, tudo no mesmo packageRoot (sem nenhum Module de app neste teste — a
        // coexistencia com Thunderbird/Office/etc ja e provada em MigrationModuleCatalogEndToEndTests). ---
        var backupOperationId = Guid.NewGuid();
        var metadata = new PackageMetadata(
            "1.0",
            "1.0.0-fase4",
            Guid.NewGuid(),
            PackageState.Created,
            clock.UtcNow,
            null,
            new SourceComputer("MACHINE-LEVEL-E2E-TEST", sourceVolumeRoot, "Windows 11 Pro", "x64"),
            new SourceProfile("auxatc", sourceProfileRoot, null),
            "fase4-machine-level-e2e-test",
            [],
            DurableCounters.Empty,
            new IntegrityPolicy("SHA256", false, "size-and-handle"),
            IsEncrypted: false);

        var backupResult = await backupEngine.RunAsync(
            new BackupRequest(backupOperationId, packageRoot, metadata),
            PersonalFileOnly(documentSourcePath));
        Assert.Equal(PackageState.Complete, backupResult.State);

        var fileZillaCapture = await fileZillaService.CaptureAsync(
            sourceVolumeRoot, packageRoot, CancellationToken.None);
        Assert.True(fileZillaCapture.Captured);

        var printerCapture = await printerDriverService.CaptureAsync(
            sourceVolumeRoot, packageRoot, CancellationToken.None);
        Assert.Single(printerCapture.Drivers);

        // --- Restauracao: MigrationOrchestrator para o arquivo pessoal, seguido dos dois servicos
        // de nivel de maquina — exatamente a sequencia que ConfirmRestore_Click usa. ---
        var restoreEngine = new StreamingRestoreEngine(fileSystem, packageModule, clock);
        var moduleCoordinator = new AppModuleRestoreCoordinator(
            packageModule, new Dictionary<string, IMigrationModule>());
        var orchestrator = new MigrationOrchestrator(
            fileSystem, packageModule, clock, backupEngine, restoreEngine, moduleCoordinator);

        var restoreOperationId = Guid.NewGuid();
        var restoreRequest = new RestoreRequest(
            restoreOperationId,
            packageRoot,
            destinationProfileRoot,
            fixture.PathFor(@"destino\Users\Public"),
            fixture.PathFor(@"operations\machine-level"));

        var restoreResult = await orchestrator.RunOrResumeRestoreAsync(restoreRequest);
        Assert.Equal(RestoreState.Complete, restoreResult.OverallState);

        var fileZillaRestore = await fileZillaService.RestoreAsync(
            packageRoot,
            oldProfileRoot: sourceProfileRoot,
            newProfileRoot: destinationProfileRoot,
            destinationVolumeRoot: destinationVolumeRoot,
            CancellationToken.None);
        var printerOutcomes = await printerDriverService.RestoreAsync(packageRoot, CancellationToken.None);

        // Efeito 1: arquivo pessoal restaurado pelo orquestrador, sem interferencia dos servicos
        // de nivel de maquina (que escrevem em pastas completamente diferentes do pacote).
        Assert.Equal(
            documentContent,
            File.ReadAllText(Path.Combine(destinationProfileRoot, "Documents", "contrato.txt")));

        // Efeito 2: FileZilla — config revinculada para o novo perfil, servico reciclado.
        Assert.True(fileZillaRestore.Applied);
        var appliedConfigPath = Path.Combine(
            destinationVolumeRoot, "Program Files (x86)", "FileZilla Server", "FileZilla Server.xml");
        Assert.Contains(
            $@"{destinationProfileRoot}\Desktop\Scan", await File.ReadAllTextAsync(appliedConfigPath));

        // Efeito 3: driver de impressora — instalado via pnputil, exatamente o pacote capturado.
        var outcome = Assert.Single(printerOutcomes);
        Assert.True(outcome.Succeeded);
        Assert.Single(commandAdapter.Requests);
    }

    private static async IAsyncEnumerable<TransferItem> PersonalFileOnly(string documentSourcePath)
    {
        yield return new TransferItem(
            1,
            "user-files",
            documentSourcePath,
            "data/Users/auxatc/Documents/contrato.txt",
            TransferClassification.PersonalFile,
            SourceRelativePath: "Documents/contrato.txt");
        await Task.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingServiceAdapter : IWindowsServiceAdapter
    {
        public Task StopAsync(string serviceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StartAsync(string serviceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingCommandAdapter : IWindowsCommandAdapter
    {
        public List<WindowsCommandRequest> Requests { get; } = [];

        public Task<WindowsCommandResult> ExecuteApprovedAsync(
            WindowsCommandRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new WindowsCommandResult(0, "ok", string.Empty));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            System.IO.Path.GetTempPath(), "grbkp-machine-level-e2e", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string PathFor(string relative) =>
            System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public string WriteFile(string absolutePath, string content)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(absolutePath)!);
            File.WriteAllText(absolutePath, content, Encoding.UTF8);
            return absolutePath;
        }

        public void CreateFileZillaInstall(string volumeRoot, string configXml)
        {
            var configDirectory = System.IO.Path.Combine(
                volumeRoot, "Program Files (x86)", "FileZilla Server");
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(
                System.IO.Path.Combine(configDirectory, "FileZilla Server.xml"), configXml, Encoding.UTF8);
        }

        public void CreatePrinterDriverPackage(
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
