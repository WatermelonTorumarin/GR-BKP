using System.Text;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Packages;
using GRBKP.Core.Transfers;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

/// <summary>
/// Escolher QUAIS pastas pessoais restaurar. Vale só na restauração: no backup a seleção já é
/// resolvida pela política, e restringir lá seria jogar dados fora na origem — aqui é o oposto,
/// o pacote continua inteiro e o técnico decide o que devolve para a máquina.
/// </summary>
public sealed class RestoreFolderSelectionTests
{
    [Fact]
    public async Task RestoreAsync_RestoresOnlyTheChosenFolders()
    {
        using var fixture = new PackageFixture();
        var packageRoot = await fixture.WritePackageAsync();
        var destino = fixture.PathFor(@"destino\Users\ana");

        var resultado = await fixture.RestoreAsync(
            packageRoot, destino, "op1", ["Documents", "Desktop"]);

        Assert.Equal(RestoreState.Complete, resultado.State);
        Assert.True(File.Exists(Path.Combine(destino, "Documents", "contrato.txt")));
        Assert.True(File.Exists(Path.Combine(destino, "Desktop", "atalho.txt")));
        Assert.False(File.Exists(Path.Combine(destino, "Downloads", "instalador.txt")));
        Assert.False(File.Exists(Path.Combine(destino, "Pictures", "foto.txt")));
    }

    [Fact]
    public async Task RestoreAsync_RestoresEverything_WhenNoFolderFilterIsGiven()
    {
        using var fixture = new PackageFixture();
        var packageRoot = await fixture.WritePackageAsync();
        var destino = fixture.PathFor(@"destino-tudo\Users\ana");

        var resultado = await fixture.RestoreAsync(packageRoot, destino, "op2", null);

        Assert.Equal(RestoreState.Complete, resultado.State);
        foreach (var (pasta, arquivo) in new[]
        {
            ("Documents", "contrato.txt"), ("Desktop", "atalho.txt"),
            ("Downloads", "instalador.txt"), ("Pictures", "foto.txt")
        })
        {
            Assert.True(
                File.Exists(Path.Combine(destino, pasta, arquivo)),
                $"deveria ter restaurado {pasta}/{arquivo}");
        }
    }

    [Fact]
    public async Task RestoreAsync_CountsTheSkippedFolders_AsIgnoredNotCopied()
    {
        using var fixture = new PackageFixture();
        var packageRoot = await fixture.WritePackageAsync();
        var destino = fixture.PathFor(@"destino-contagem\Users\ana");

        var resultado = await fixture.RestoreAsync(packageRoot, destino, "op3", ["Documents"]);

        Assert.Equal(1, resultado.Counters.CopiedFiles);
        Assert.Equal(3, resultado.Counters.IgnoredFiles);
    }

    [Fact]
    public async Task RestoreAsync_TreatsFolderNamesWithoutCaseSensitivity()
    {
        using var fixture = new PackageFixture();
        var packageRoot = await fixture.WritePackageAsync();
        var destino = fixture.PathFor(@"destino-caixa\Users\ana");

        var resultado = await fixture.RestoreAsync(packageRoot, destino, "op4", ["documents"]);

        Assert.Equal(RestoreState.Complete, resultado.State);
        Assert.True(File.Exists(Path.Combine(destino, "Documents", "contrato.txt")));
    }

    private sealed class PackageFixture : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(), "grbkp-pastas", Guid.NewGuid().ToString("N"));
        private readonly IFileSystemAdapter fileSystem = new WindowsFileSystemAdapter();
        private readonly FixedClock clock = new();

        public PackageFixture() => Directory.CreateDirectory(root);

        public string PathFor(string relative) => Path.Combine(root, relative);

        public async Task<string> WritePackageAsync()
        {
            var origem = Path.Combine(root, "origem");
            var arquivos = new[]
            {
                ("Documents", "contrato.txt", "CONTRATO"),
                ("Desktop", "atalho.txt", "ATALHO"),
                ("Downloads", "instalador.txt", "INSTALADOR"),
                ("Pictures", "foto.txt", "FOTO")
            };

            var itens = new List<TransferItem>();
            var sequencia = 0L;
            foreach (var (pasta, nome, conteudo) in arquivos)
            {
                var caminho = Path.Combine(origem, pasta, nome);
                Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
                File.WriteAllText(caminho, conteudo, Encoding.UTF8);
                sequencia++;
                itens.Add(new TransferItem(
                    sequencia,
                    "user-files",
                    caminho,
                    $"data/Users/ana/{pasta}/{nome}",
                    TransferClassification.PersonalFile,
                    SourceRelativePath: $"{pasta}/{nome}",
                    Metadata: new FileMetadata(
                        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal),
                    Length: new FileInfo(caminho).Length));
            }

            var packageRoot = Path.Combine(root, "pacote");
            var engine = new StreamingBackupEngine(
                fileSystem, new BackupPackageModule(fileSystem, clock), clock);
            var resultado = await engine.RunAsync(
                new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()), ToAsync(itens));
            Assert.Equal(PackageState.Complete, resultado.State);
            return packageRoot;
        }

        public async Task<RestoreOperationResult> RestoreAsync(
            string packageRoot,
            string destino,
            string operacao,
            IReadOnlyCollection<string>? pastas)
        {
            var engine = new StreamingRestoreEngine(
                fileSystem, new BackupPackageModule(fileSystem, clock), clock);
            return await engine.RunAsync(new RestoreRequest(
                Guid.NewGuid(),
                packageRoot,
                destino,
                PathFor(@"destino\Users\Public"),
                PathFor($@"operacoes\{operacao}"),
                pastas));
        }

        private static async IAsyncEnumerable<TransferItem> ToAsync(IEnumerable<TransferItem> itens)
        {
            foreach (var item in itens)
            {
                yield return item;
            }

            await Task.CompletedTask;
        }

        private static PackageMetadata Metadata() => new(
            "1.0", "1.0.0-pastas", Guid.NewGuid(), PackageState.Created,
            DateTimeOffset.UnixEpoch, null,
            new SourceComputer("PASTAS", @"F:\", "Windows 11", "x64"),
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
