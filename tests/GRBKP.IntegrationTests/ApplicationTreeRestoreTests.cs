using System.Text;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Restore;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class ApplicationTreeRestoreTests
{
    [Fact]
    public async Task T37_ModuleTreeSwap_DeletesPreMigrationOnSuccess_AndRestoresItOnValidationFailure()
    {
        var fileSystem = new WindowsFileSystemAdapter();
        var sut = new ApplicationTreeRestorer(fileSystem);
        using var fixture = new TemporaryDirectory();

        var successfulDestination = fixture.CreateTree(
            "success/Thunderbird",
            ("new-only.txt", "NEW-PROFILE"));
        var successfulStaging = fixture.CreateTree(
            "success/staging",
            ("profile.marker", "VALID-OLD-PROFILE"));
        var successOperationId = Guid.Parse("10101010-1010-1010-1010-101010101010");
        var module = new MarkerValidationModule("thunderbird", "VALID-OLD-PROFILE");

        var success = await sut.RestoreAsync(
            new ModuleTreeRestoreRequest(
                successOperationId,
                module.Id,
                successfulStaging,
                successfulDestination),
            module);

        Assert.Equal(ModuleOutcome.Applied, success.ModuleResult.Outcome);
        Assert.Equal("VALID-OLD-PROFILE", File.ReadAllText(Path.Combine(
            successfulDestination,
            "profile.marker")));
        Assert.False(File.Exists(Path.Combine(successfulDestination, "new-only.txt")));
        Assert.False(Directory.Exists(success.PreMigrationPath));
        Assert.Null(success.FailedRestorePath);

        var failedDestination = fixture.CreateTree(
            "failure/Chrome",
            ("new-only.txt", "FUNCTIONAL-NEW-PROFILE"));
        var failedStaging = fixture.CreateTree(
            "failure/staging",
            ("profile.marker", "BROKEN-OLD-PROFILE"));
        var failedOperationId = Guid.Parse("20202020-2020-2020-2020-202020202020");
        var failedModule = new MarkerValidationModule("chrome", "VALID-OLD-PROFILE");

        var failure = await sut.RestoreAsync(
            new ModuleTreeRestoreRequest(
                failedOperationId,
                failedModule.Id,
                failedStaging,
                failedDestination),
            failedModule);

        Assert.Equal(ModuleOutcome.Failed, failure.ModuleResult.Outcome);
        Assert.Equal("FUNCTIONAL-NEW-PROFILE", File.ReadAllText(Path.Combine(
            failedDestination,
            "new-only.txt")));
        Assert.False(File.Exists(Path.Combine(failedDestination, "profile.marker")));
        Assert.False(Directory.Exists(failure.PreMigrationPath));
        Assert.NotNull(failure.FailedRestorePath);
        Assert.Equal("BROKEN-OLD-PROFILE", File.ReadAllText(Path.Combine(
            failure.FailedRestorePath!,
            "profile.marker")));
        Assert.Equal(failure.FailedRestorePath, failure.ModuleResult.DiagnosticPath);
    }

    private sealed class MarkerValidationModule(string id, string expectedMarker) : IMigrationModule
    {
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

        public Task<ModuleResult> RestoreAsync(RestoreContext context, CancellationToken ct) =>
            Task.FromResult(new ModuleResult(Id, ModuleOutcome.Applied, []));

        public async Task<ValidationResult> ValidateAsync(
            ValidationContext context,
            CancellationToken ct)
        {
            var markerPath = Path.Combine(context.DestinationPath, "profile.marker");
            var info = await context.FileSystem.GetInfoAsync(markerPath, ct);
            if (info is null)
            {
                return new ValidationResult(
                    false,
                    [new MigrationEvidence("marker-missing", "Marcador ausente.", EvidenceSeverity.Error)],
                    "marker-missing");
            }

            await using var stream = await context.FileSystem.OpenReadAsync(markerPath, ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var marker = await reader.ReadToEndAsync(ct);
            var valid = marker == expectedMarker;
            return new ValidationResult(
                valid,
                [new MigrationEvidence(
                    valid ? "marker-valid" : "marker-invalid",
                    valid ? "Marcador válido." : "Marcador inválido.",
                    valid ? EvidenceSeverity.Information : EvidenceSeverity.Error)],
                valid ? null : "marker-invalid");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "grbkp-phase1", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateTree(string relativePath, params (string Name, string Content)[] files)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(path);
            foreach (var (name, content) in files)
            {
                File.WriteAllText(Path.Combine(path, name), content, Encoding.UTF8);
            }

            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
