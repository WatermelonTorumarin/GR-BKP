using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Packages;
using GRBKP.Core.Restore;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class AppModuleRestoreCoordinatorTests
{
    [Fact]
    public async Task RestoreAsync_InvokesOnlyRegisteredModules_SkippingUnimplementedOnes()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 11, 16, 0, 0, TimeSpan.Zero));
        var packageModule = new BackupPackageModule(fileSystem, clock);
        var packageRoot = fixture.PathFor("package");

        await CreateCompletePackageAsync(
            packageModule,
            packageRoot,
            clock.UtcNow,
            ["thunderbird", "some-future-module"]);

        var thunderbird = new RecordingModule("thunderbird", ModuleOutcome.Applied);
        var coordinator = new AppModuleRestoreCoordinator(
            packageModule,
            new Dictionary<string, IMigrationModule> { ["thunderbird"] = thunderbird });

        var context = new RestoreContext(
            Guid.NewGuid(),
            packageRoot,
            fixture.PathFor("destination"),
            fileSystem,
            new OperationCheckpoint(
                Guid.NewGuid(),
                Guid.NewGuid(),
                OperationKind.Restore,
                RestoreState.RestoringAppModules.ToString(),
                null,
                0,
                null,
                DurableCounters.Empty));

        var outcome = await coordinator.RestoreAsync(context);

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, thunderbird.CallCount);
        Assert.Single(outcome.Results);
        Assert.Equal(ModuleOutcome.Applied, outcome.Results[0].Outcome);
    }

    /// <summary>
    /// Cenário REAL que custou os dados dos navegadores de um funcionário (09/10/2026): o
    /// Thunderbird é o primeiro da lista de Modules do pacote e falhou; com a parada no primeiro
    /// erro, Chrome e Edge sequer chegaram a ser TENTADOS, e a restauração terminou sem eles.
    ///
    /// Cada Module é uma troca de árvore independente, com reversão própria — a falha de um não
    /// contamina os outros. Por isso todos precisam ser tentados; o resultado geral continua
    /// sendo falha, mas o técnico leva de volta tudo o que era possível recuperar.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_KeepsGoingAfterAFailure_SoOneBrokenAppDoesNotBlockTheOthers()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 11, 16, 30, 0, TimeSpan.Zero));
        var packageModule = new BackupPackageModule(fileSystem, clock);
        var packageRoot = fixture.PathFor("package-with-failure");

        await CreateCompletePackageAsync(
            packageModule,
            packageRoot,
            clock.UtcNow,
            ["thunderbird", "chrome", "edge"]);

        var thunderbird = new RecordingModule("thunderbird", ModuleOutcome.Failed);
        var chrome = new RecordingModule("chrome", ModuleOutcome.Applied);
        var edge = new RecordingModule("edge", ModuleOutcome.Applied);
        var coordinator = new AppModuleRestoreCoordinator(
            packageModule,
            new Dictionary<string, IMigrationModule>
            {
                ["thunderbird"] = thunderbird,
                ["chrome"] = chrome,
                ["edge"] = edge
            });

        var outcome = await coordinator.RestoreAsync(
            BuildContext(fixture, packageRoot, "destination-failure", fileSystem));

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, thunderbird.CallCount);
        Assert.Equal(1, chrome.CallCount);
        Assert.Equal(1, edge.CallCount);
        Assert.Equal(3, outcome.Results.Count);
        Assert.Equal(
            ModuleOutcome.Applied,
            outcome.Results.Single(r => r.ModuleId == "chrome").Outcome);
    }

    /// <summary>
    /// Um Module que lança exceção (sem espaço em disco, arquivo travado no destino) derrubava a
    /// restauração de TODOS os aplicativos seguintes. Vira falha daquele Module e só.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_TurnsAThrowingModuleIntoItsOwnFailure_WithoutAbortingTheRest()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 11, 17, 0, 0, TimeSpan.Zero));
        var packageModule = new BackupPackageModule(fileSystem, clock);
        var packageRoot = fixture.PathFor("package-throwing");

        await CreateCompletePackageAsync(
            packageModule, packageRoot, clock.UtcNow, ["thunderbird", "chrome"]);

        var chrome = new RecordingModule("chrome", ModuleOutcome.Applied);
        var coordinator = new AppModuleRestoreCoordinator(
            packageModule,
            new Dictionary<string, IMigrationModule>
            {
                ["thunderbird"] = new ThrowingModule("thunderbird"),
                ["chrome"] = chrome
            });

        var outcome = await coordinator.RestoreAsync(
            BuildContext(fixture, packageRoot, "destination-throwing", fileSystem));

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, chrome.CallCount);
        Assert.Equal(
            ModuleOutcome.Failed,
            outcome.Results.Single(r => r.ModuleId == "thunderbird").Outcome);
        Assert.Equal(
            ModuleOutcome.Applied,
            outcome.Results.Single(r => r.ModuleId == "chrome").Outcome);
    }

    /// <summary>
    /// Restaurar só alguns aplicativos: quando uma restauração já foi feita e um aplicativo ficou
    /// para trás, repetir os 50 GB de arquivos pessoais só para recuperar o navegador é perda de
    /// tempo (e risco). O técnico escolhe o que refazer.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_RestoresOnlyTheChosenModules_WhenAFilterIsGiven()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 11, 18, 0, 0, TimeSpan.Zero));
        var packageModule = new BackupPackageModule(fileSystem, clock);
        var packageRoot = fixture.PathFor("package-filtrado");

        await CreateCompletePackageAsync(
            packageModule, packageRoot, clock.UtcNow, ["thunderbird", "chrome", "edge"]);

        var thunderbird = new RecordingModule("thunderbird", ModuleOutcome.Applied);
        var chrome = new RecordingModule("chrome", ModuleOutcome.Applied);
        var edge = new RecordingModule("edge", ModuleOutcome.Applied);
        var coordinator = new AppModuleRestoreCoordinator(
            packageModule,
            new Dictionary<string, IMigrationModule>
            {
                ["thunderbird"] = thunderbird,
                ["chrome"] = chrome,
                ["edge"] = edge
            });

        var outcome = await coordinator.RestoreAsync(
            BuildContext(fixture, packageRoot, "destination-filtrado", fileSystem),
            ["chrome", "edge"]);

        Assert.True(outcome.Succeeded);
        Assert.Equal(0, thunderbird.CallCount);
        Assert.Equal(1, chrome.CallCount);
        Assert.Equal(1, edge.CallCount);
        Assert.Equal(2, outcome.Results.Count);
    }

    private static RestoreContext BuildContext(
        TemporaryDirectory fixture,
        string packageRoot,
        string destinationFolder,
        WindowsFileSystemAdapter fileSystem) =>
        new(
            Guid.NewGuid(),
            packageRoot,
            fixture.PathFor(destinationFolder),
            fileSystem,
            new OperationCheckpoint(
                Guid.NewGuid(),
                Guid.NewGuid(),
                OperationKind.Restore,
                RestoreState.RestoringAppModules.ToString(),
                null,
                0,
                null,
                DurableCounters.Empty));

    private sealed class ThrowingModule(string id) : IMigrationModule
    {
        public string Id { get; } = id;

        public Task<ModuleDiscovery> DiscoverAsync(MigrationContext context, CancellationToken ct) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TransferItem> EnumerateBackupAsync(
            ModuleDiscovery discovery, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ModuleResult> RestoreAsync(RestoreContext context, CancellationToken ct) =>
            throw new IOException("Não há espaço suficiente no disco.");

        public Task<ValidationResult> ValidateAsync(ValidationContext context, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static async Task CreateCompletePackageAsync(
        BackupPackageModule packageModule,
        string packageRoot,
        DateTimeOffset now,
        IReadOnlyList<string> moduleIds)
    {
        var metadata = new PackageMetadata(
            "1.0",
            "1.0.0-fase3",
            Guid.NewGuid(),
            PackageState.Created,
            now,
            null,
            new SourceComputer("COORD-TEST", "F:", "Windows 11 Pro", "x64"),
            new SourceProfile("Ana", @"F:\Users\Ana", null),
            "coordinator-test",
            moduleIds,
            DurableCounters.Empty,
            new IntegrityPolicy("SHA256", false, "size-and-handle"),
            IsEncrypted: false);

        await using var writer = await packageModule.CreateWriterAsync(
            packageRoot,
            Guid.NewGuid(),
            metadata,
            CancellationToken.None);
        await writer.CompleteAsync(DurableCounters.Empty, CancellationToken.None);
    }

    private sealed class RecordingModule(string id, ModuleOutcome outcomeToReturn) : IMigrationModule
    {
        public int CallCount { get; private set; }

        public string Id { get; } = id;

        public Task<ModuleDiscovery> DiscoverAsync(MigrationContext context, CancellationToken ct) =>
            Task.FromResult(new ModuleDiscovery(Id, true, new Dictionary<string, string?>()));

        public async IAsyncEnumerable<TransferItem> EnumerateBackupAsync(
            ModuleDiscovery discovery,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<ModuleResult> RestoreAsync(RestoreContext context, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new ModuleResult(Id, outcomeToReturn, []));
        }

        public Task<ValidationResult> ValidateAsync(ValidationContext context, CancellationToken ct) =>
            Task.FromResult(new ValidationResult(true, []));
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            System.IO.Path.GetTempPath(),
            "grbkp-module-coordinator",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string PathFor(string relative) =>
            System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
