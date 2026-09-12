using System.Text;
using GRBKP.Core.Adapters;
using GRBKP.Core.Operations;
using GRBKP.Core.Packages;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

/// <summary>
/// Sem isto, os motores sabiam retomar um backup interrompido mas a interface nunca acionava a
/// retomada: cada execução criava uma pasta nova com data e hora. Se o SSD desconectasse em 80%
/// de um backup de 200 GB, recomeçava do zero.
/// </summary>
public sealed class IncompletePackageFinderTests
{
    [Fact]
    public async Task FindMostRecentAsync_FindsNothing_WhenEveryPackageIsComplete()
    {
        using var fixture = new TemporaryDirectory();
        var personFolder = fixture.PathFor("BackUp[auxatc]");
        fixture.WritePackage(personFolder, "2026-09-10_08-00", PackageState.Complete, hours: 8);
        fixture.WritePackage(personFolder, "2026-09-11_09-00", PackageState.Complete, hours: 9);

        var found = await fixture.Finder.FindMostRecentAsync(personFolder, CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task FindMostRecentAsync_FindsTheNewestUnfinishedPackage()
    {
        using var fixture = new TemporaryDirectory();
        var personFolder = fixture.PathFor("BackUp[auxatc]");
        fixture.WritePackage(personFolder, "2026-09-10_08-00", PackageState.Paused, hours: 8);
        fixture.WritePackage(personFolder, "2026-09-11_09-00", PackageState.Complete, hours: 9);
        fixture.WritePackage(personFolder, "2026-09-12_10-00", PackageState.Copying, hours: 10);

        var found = await fixture.Finder.FindMostRecentAsync(personFolder, CancellationToken.None);

        Assert.NotNull(found);
        Assert.EndsWith("2026-09-12_10-00", found.PackageRoot, StringComparison.Ordinal);
        Assert.Equal(PackageState.Copying, found.Status);
    }

    [Fact]
    public async Task FindMostRecentAsync_IgnoresFoldersThatAreNotPackages()
    {
        using var fixture = new TemporaryDirectory();
        var personFolder = fixture.PathFor("BackUp[auxatc]");
        Directory.CreateDirectory(Path.Combine(personFolder, "pasta-qualquer"));
        File.WriteAllText(
            Path.Combine(personFolder, "pasta-qualquer", "leia-me.txt"), "nada a ver");
        fixture.WritePackage(personFolder, "2026-09-12_10-00", PackageState.Paused, hours: 10);

        var found = await fixture.Finder.FindMostRecentAsync(personFolder, CancellationToken.None);

        Assert.NotNull(found);
        Assert.EndsWith("2026-09-12_10-00", found.PackageRoot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindMostRecentAsync_IgnoresAPackageWithCorruptedMetadata()
    {
        // Um package.json ilegível não pode derrubar a tela de backup: só significa que aquele
        // pacote não é retomável.
        using var fixture = new TemporaryDirectory();
        var personFolder = fixture.PathFor("BackUp[auxatc]");
        var broken = Path.Combine(personFolder, "2026-09-12_11-00");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "package.json"), "{ isso não é json válido", Encoding.UTF8);
        fixture.WritePackage(personFolder, "2026-09-12_10-00", PackageState.Paused, hours: 10);

        var found = await fixture.Finder.FindMostRecentAsync(personFolder, CancellationToken.None);

        Assert.NotNull(found);
        Assert.EndsWith("2026-09-12_10-00", found.PackageRoot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindMostRecentAsync_ReturnsNull_WhenThePersonNeverHadABackup()
    {
        using var fixture = new TemporaryDirectory();

        var found = await fixture.Finder.FindMostRecentAsync(
            fixture.PathFor("BackUp[ninguem]"), CancellationToken.None);

        Assert.Null(found);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(), "grbkp-retomada", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(root);
            var fileSystem = new WindowsFileSystemAdapter();
            Finder = new IncompletePackageFinder(
                fileSystem, new BackupPackageModule(fileSystem, new FixedClock()));
        }

        public IncompletePackageFinder Finder { get; }

        public string PathFor(string relative) => Path.Combine(root, relative);

        public void WritePackage(string personFolder, string stamp, PackageState status, int hours)
        {
            var packageRoot = Path.Combine(personFolder, stamp);
            Directory.CreateDirectory(packageRoot);
            var metadata = new PackageMetadata(
                "1.0", "1.0.0", Guid.NewGuid(), status,
                new DateTimeOffset(2026, 9, 12, hours, 0, 0, TimeSpan.Zero), null,
                new SourceComputer("PC", @"F:\", "Windows 11", "x64"),
                new SourceProfile("auxatc", @"F:\Users\auxatc", null),
                "teste", [], DurableCounters.Empty,
                new IntegrityPolicy("SHA256", true, "size-and-handle"), IsEncrypted: false);
            File.WriteAllText(
                Path.Combine(packageRoot, "package.json"),
                System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    Converters =
                    {
                        new System.Text.Json.Serialization.JsonStringEnumConverter(
                            System.Text.Json.JsonNamingPolicy.CamelCase)
                    }
                }),
                Encoding.UTF8);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // limpeza de melhor esforço
            }
        }

        private sealed class FixedClock : IClock
        {
            public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        }
    }
}
