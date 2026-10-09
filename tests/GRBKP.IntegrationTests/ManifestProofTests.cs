using System.Text;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Packages;
using GRBKP.Core.Transfers;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

/// <summary>
/// Um manifesto precisa se PROVAR antes de ser usado. Até aqui, uma linha ilegível só aparecia
/// durante a restauração — que morria no meio, com metade dos arquivos já gravados no perfil do
/// funcionário e uma mensagem que não dizia nada (aconteceu em 09/10/2026, perfil marcelo).
///
/// A prova tem que acontecer ANTES de tocar no destino, e uma linha quebrada no meio não pode
/// condenar as outras cento e tantas mil que estão perfeitas.
/// </summary>
public sealed class ManifestProofTests
{
    [Fact]
    public async Task InspectManifestAsync_ProvesAHealthyManifest()
    {
        using var fixture = new PackageFixture();
        var packageRoot = await fixture.WritePackageAsync(20);

        var inspecao = await fixture.PackageModule.InspectManifestAsync(packageRoot);

        Assert.True(inspecao.IsIntact);
        Assert.Equal(20, inspecao.ValidEntries);
        Assert.Empty(inspecao.UnreadableLines);
    }

    [Fact]
    public async Task InspectManifestAsync_FindsABrokenLine_WithoutCondemningTheGoodOnes()
    {
        using var fixture = new PackageFixture();
        var packageRoot = await fixture.WritePackageAsync(20);
        CorruptOneLineInTheMiddle(packageRoot, lineNumber: 8);

        var inspecao = await fixture.PackageModule.InspectManifestAsync(packageRoot);

        Assert.False(inspecao.IsIntact);
        Assert.Equal(8, Assert.Single(inspecao.UnreadableLines));
        Assert.Equal(19, inspecao.ValidEntries);
    }

    [Fact]
    public async Task RestoreAsync_RestoresEverythingReadable_WhenOneLineIsBroken()
    {
        // Exatamente o caso do marcelo: uma linha destruída no meio do manifesto. Antes, a
        // restauração morria ali. Agora segue e devolve tudo que dá para ler.
        using var fixture = new PackageFixture();
        var packageRoot = await fixture.WritePackageAsync(20);
        CorruptOneLineInTheMiddle(packageRoot, lineNumber: 8);
        var destino = fixture.PathFor(@"destino\Users\ana");

        var resultado = await fixture.RestoreAsync(packageRoot, destino, "op-quebrado");

        Assert.Equal(RestoreState.Complete, resultado.State);
        Assert.Equal(19, resultado.Counters.CopiedFiles);
        Assert.Contains(resultado.Errors, erro => erro.Code == "manifest-line-unreadable");

        // Os 19 legíveis chegaram ao destino; só o da linha destruída ficou de fora.
        var restaurados = Directory.GetFiles(Path.Combine(destino, "Documents")).Length;
        Assert.Equal(19, restaurados);
    }

    [Fact]
    public async Task VerifyAsync_ReportsABrokenManifestAsAFinding_InsteadOfCrashing()
    {
        using var fixture = new PackageFixture();
        var packageRoot = await fixture.WritePackageAsync(20);
        CorruptOneLineInTheMiddle(packageRoot, lineNumber: 8);

        var verificador = new BackupPackageVerifier(fixture.FileSystem, fixture.PackageModule);
        var relatorio = await verificador.VerifyAsync(packageRoot, null, CancellationToken.None);

        Assert.False(relatorio.IsIntact);
        Assert.Contains(relatorio.Issues, issue => issue.Code == "manifest-line-unreadable");
    }

    private static void CorruptOneLineInTheMiddle(string packageRoot, int lineNumber)
    {
        var manifestPath = Path.Combine(packageRoot, "files.jsonl");
        var linhas = File.ReadAllLines(manifestPath);
        // Zeros no meio de uma linha, como o NTFS deixa depois de uma queda.
        linhas[lineNumber - 1] = new string('\0', 120);
        File.WriteAllLines(manifestPath, linhas, new UTF8Encoding(false));
    }

    private sealed class PackageFixture : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(), "grbkp-prova", Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new();

        public PackageFixture()
        {
            Directory.CreateDirectory(root);
            FileSystem = new WindowsFileSystemAdapter();
            PackageModule = new BackupPackageModule(FileSystem, clock);
        }

        public IFileSystemAdapter FileSystem { get; }

        public BackupPackageModule PackageModule { get; }

        public string PathFor(string relative) => Path.Combine(root, relative);

        public async Task<string> WritePackageAsync(int count)
        {
            var origem = Path.Combine(root, "origem");
            Directory.CreateDirectory(origem);
            var itens = new List<TransferItem>();
            for (var index = 1; index <= count; index++)
            {
                var caminho = Path.Combine(origem, $"arquivo-{index:D3}.txt");
                File.WriteAllText(caminho, $"CONTEUDO {index}", Encoding.UTF8);
                itens.Add(new TransferItem(
                    index,
                    "user-files",
                    caminho,
                    $"data/Users/ana/Documents/arquivo-{index:D3}.txt",
                    TransferClassification.PersonalFile,
                    SourceRelativePath: $"arquivo-{index:D3}.txt",
                    Metadata: new FileMetadata(
                        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal),
                    Length: new FileInfo(caminho).Length));
            }

            var packageRoot = Path.Combine(root, "pacote");
            var resultado = await new StreamingBackupEngine(FileSystem, PackageModule, clock)
                .RunAsync(new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()), ToAsync(itens));
            Assert.Equal(PackageState.Complete, resultado.State);
            return packageRoot;
        }

        public async Task<RestoreOperationResult> RestoreAsync(
            string packageRoot, string destino, string operacao) =>
            await new StreamingRestoreEngine(FileSystem, PackageModule, clock).RunAsync(
                new RestoreRequest(
                    Guid.NewGuid(), packageRoot, destino,
                    PathFor(@"destino\Users\Public"), PathFor($@"operacoes\{operacao}")));

        private static async IAsyncEnumerable<TransferItem> ToAsync(IEnumerable<TransferItem> itens)
        {
            foreach (var item in itens)
            {
                yield return item;
            }

            await Task.CompletedTask;
        }

        private static PackageMetadata Metadata() => new(
            "1.0", "1.0.0-prova", Guid.NewGuid(), PackageState.Created,
            DateTimeOffset.UnixEpoch, null,
            new SourceComputer("PROVA", @"F:\", "Windows 11", "x64"),
            new SourceProfile("ana", @"F:\Users\ana", null),
            "teste", [], DurableCounters.Empty,
            new IntegrityPolicy("SHA256", true, "size-and-handle"), IsEncrypted: false);

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
