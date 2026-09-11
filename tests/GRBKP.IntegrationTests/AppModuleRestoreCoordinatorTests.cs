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

    [Fact]
    public async Task RestoreAsync_StopsAtFirstFailure_WithoutRunningLaterModules()
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
            ["thunderbird", "firefox"]);

        var thunderbird = new RecordingModule("thunderbird", ModuleOutcome.Failed);
        var firefox = new RecordingModule("firefox", ModuleOutcome.Applied);
        var coordinator = new AppModuleRestoreCoordinator(
            packageModule,
            new Dictionary<string, IMigrationModule>
            {
                ["thunderbird"] = thunderbird,
                ["firefox"] = firefox
            });

        var context = new RestoreContext(
            Guid.NewGuid(),
            packageRoot,
            fixture.PathFor("destination-failure"),
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

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, thunderbird.CallCount);
        Assert.Equal(0, firefox.CallCount);
        Assert.Equal(ModuleOutcome.Failed, Assert.Single(outcome.Results).Outcome);
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
