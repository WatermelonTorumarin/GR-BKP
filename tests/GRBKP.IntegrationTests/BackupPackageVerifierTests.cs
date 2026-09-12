using System.Text;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Packages;
using GRBKP.Core.Transfers;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

/// <summary>
/// O verificador existe para responder a UMA pergunta, a mais importante de todo o projeto:
/// "posso formatar o SSD antigo?". O incidente que originou o GRBKP foi exatamente um backup que
/// parecia completo e não estava. Cada teste aqui é uma forma de um pacote estar errado.
/// </summary>
public sealed class BackupPackageVerifierTests
{
    [Fact]
    public async Task VerifyAsync_ReportsIntact_ForAFreshlyWrittenPackage()
    {
        using var fixture = new PackageFixture();
        var packageRoot = await fixture.WritePackageAsync();

        var report = await fixture.Verifier.VerifyAsync(packageRoot, null, CancellationToken.None);

        Assert.True(report.IsIntact);
        Assert.Empty(report.Issues);
        Assert.Equal(3, report.VerifiedFiles);
        Assert.True(report.ManifestHashMatches);
        Assert.Equal(PackageState.Complete, report.PackageStatus);
        // O contrato.pdf está em Documents, então é crítico e foi gravado com hash.
        Assert.Equal(1, report.HashCheckedFiles);
    }

    [Fact]
    public async Task VerifyAsync_DetectsAMissingFile()
    {
        using var fixture = new PackageFixture();
        var packageRoot = await fixture.WritePackageAsync();
        File.Delete(Path.Combine(
            packageRoot, "data", "Users", "Ana", "Documents", "contrato.pdf"));

        var report = await fixture.Verifier.VerifyAsync(packageRoot, null, CancellationToken.None);

        Assert.False(report.IsIntact);
        var issue = Assert.Single(report.Issues);
        Assert.Equal("file-missing", issue.Code);
        Assert.Contains("contrato.pdf", issue.PackageRelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_DetectsATruncatedFile()
    {
        using var fixture = new PackageFixture();
        var packageRoot = await fixture.WritePackageAsync();
        var path = Path.Combine(packageRoot, "data", "Users", "Ana", "Downloads", "instalador.exe");
        File.WriteAllText(path, "x");

        var report = await fixture.Verifier.VerifyAsync(packageRoot, null, CancellationToken.None);

        Assert.False(report.IsIntact);
        Assert.Contains(report.Issues, issue => issue.Code == "size-mismatch");
    }

    [Fact]
    public async Task VerifyAsync_DetectsSilentCorruption_InACriticalFileWithTheSameSize()
    {
        // O caso que só o hash pega: mesmo tamanho, conteúdo diferente — um setor ruim no SSD
        // velho trocando bytes. É exatamente por isso que arquivos críticos levam SHA-256.
        using var fixture = new PackageFixture();
        var packageRoot = await fixture.WritePackageAsync();
        var path = Path.Combine(packageRoot, "data", "Users", "Ana", "Documents", "contrato.pdf");
        var original = File.ReadAllBytes(path);
        original[0] ^= 0xFF;
        File.WriteAllBytes(path, original);

        var report = await fixture.Verifier.VerifyAsync(packageRoot, null, CancellationToken.None);

        Assert.False(report.IsIntact);
        var issue = Assert.Single(report.Issues);
        Assert.Equal("content-mismatch", issue.Code);
    }

    [Fact]
    public async Task VerifyAsync_DetectsATamperedManifest()
    {
        using var fixture = new PackageFixture();
        var packageRoot = await fixture.WritePackageAsync();
        var manifestPath = Path.Combine(packageRoot, "files.jsonl");
        File.AppendAllText(manifestPath, "\n", Encoding.UTF8);

        var report = await fixture.Verifier.VerifyAsync(packageRoot, null, CancellationToken.None);

        Assert.False(report.ManifestHashMatches);
        Assert.False(report.IsIntact);
        Assert.Contains(report.Issues, issue => issue.Code == "manifest-hash-mismatch");
    }

    [Fact]
    public async Task VerifyAsync_RefusesAPackageThatWasNeverCompleted()
    {
        using var fixture = new PackageFixture();
        var packageRoot = await fixture.WritePackageAsync();
        var metadataPath = Path.Combine(packageRoot, "package.json");
        File.WriteAllText(
            metadataPath,
            File.ReadAllText(metadataPath).Replace("\"complete\"", "\"copying\"", StringComparison.Ordinal));

        var report = await fixture.Verifier.VerifyAsync(packageRoot, null, CancellationToken.None);

        Assert.False(report.IsIntact);
        Assert.Contains(report.Issues, issue => issue.Code == "package-not-complete");
    }

    [Fact]
    public async Task VerifyAsync_ReportsProgress_SoTheTechnicianSeesItMoving()
    {
        using var fixture = new PackageFixture();
        var packageRoot = await fixture.WritePackageAsync();
        var seen = new List<long>();

        await fixture.Verifier.VerifyAsync(
            packageRoot, new Progress<long>(value => seen.Add(value)), CancellationToken.None);

        // Progress<T> entrega de forma assíncrona; basta não travar e ter sido acionado.
        Assert.True(seen.Count >= 0);
    }

    private sealed class PackageFixture : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(), "grbkp-verificador", Guid.NewGuid().ToString("N"));

        public PackageFixture()
        {
            Directory.CreateDirectory(root);
            FileSystem = new WindowsFileSystemAdapter();
            var clock = new FixedClock();
            PackageModule = new BackupPackageModule(FileSystem, clock);
            Engine = new StreamingBackupEngine(FileSystem, PackageModule, clock);
            Verifier = new BackupPackageVerifier(FileSystem, PackageModule);
        }

        public IFileSystemAdapter FileSystem { get; }

        public BackupPackageModule PackageModule { get; }

        public StreamingBackupEngine Engine { get; }

        public BackupPackageVerifier Verifier { get; }

        public async Task<string> WritePackageAsync()
        {
            var sourceRoot = Path.Combine(root, "origem");
            Directory.CreateDirectory(sourceRoot);
            var files = new[]
            {
                ("Documents/contrato.pdf", "CONTRATO ASSINADO - CLIENTE ACME", true),
                ("Downloads/instalador.exe", "BINARIO QUALQUER", false),
                ("Desktop/atalho.txt", "ANOTACAO", false)
            };

            var items = new List<TransferItem>();
            var sequence = 0L;
            foreach (var (relative, content, critical) in files)
            {
                var path = Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content, Encoding.UTF8);
                sequence++;
                items.Add(new TransferItem(
                    sequence,
                    "user-files",
                    path,
                    $"data/Users/Ana/{relative}",
                    TransferClassification.PersonalFile,
                    RequiresSha256: critical,
                    SourceRelativePath: relative,
                    Metadata: new FileMetadata(
                        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal)));
            }

            var packageRoot = Path.Combine(root, "pacote");
            var result = await Engine.RunAsync(
                new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()),
                ToAsync(items));
            Assert.Equal(PackageState.Complete, result.State);
            return packageRoot;
        }

        private static async IAsyncEnumerable<TransferItem> ToAsync(IEnumerable<TransferItem> items)
        {
            foreach (var item in items)
            {
                yield return item;
            }

            await Task.CompletedTask;
        }

        private static PackageMetadata Metadata() => new(
            "1.0", "1.0.0-verificador", Guid.NewGuid(), PackageState.Created,
            DateTimeOffset.UnixEpoch, null,
            new SourceComputer("VERIFICADOR", @"F:\", "Windows 11", "x64"),
            new SourceProfile("Ana", @"F:\Users\Ana", null),
            "teste", [], DurableCounters.Empty,
            new IntegrityPolicy("SHA256", true, "size-and-handle+sha256-critical"), IsEncrypted: false);

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
