using System.Text;
using System.Text.Json;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Packages;
using GRBKP.Core.Transfers;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

/// <summary>
/// Reproduz o que aconteceu em produção (09/10/2026, perfil marcelo): o SSD foi desconectado no
/// meio do backup. O Windows já havia estendido o files.jsonl, mas os dados ainda estavam em
/// cache e se perderam — o NTFS preenche essas regiões com ZEROS para não vazar conteúdo antigo
/// do disco. Ao retomar, o app lia o arquivo já com os zeros, continuava anexando a partir dali e
/// ainda calculava o hash por cima. O pacote terminava "completo" mas com uma linha impossível de
/// interpretar, e a restauração morria nela.
/// </summary>
public sealed class ResumeAfterCorruptionTests
{
    [Fact]
    public async Task Resume_DiscardsTheCorruptedTail_AndFinishesWithAReadableManifest()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var packageRoot = fixture.PathFor("pacote");
        var sources = fixture.CreateSourceFiles(40);

        // 1) Backup interrompido no meio (o SSD some).
        var interrompido = await BuildEngine(fileSystem).RunAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()),
            ItemsThatFailAfter(sources, 25));
        Assert.Equal(PackageState.Paused, interrompido.State);

        // 2) A região perdida vira zeros no fim do manifesto.
        CorruptManifestTail(packageRoot);
        Assert.Contains((byte)0, File.ReadAllBytes(Path.Combine(packageRoot, "files.jsonl")));

        // 3) Retomada com a lista completa.
        var resumed = await BuildEngine(fileSystem).ResumeAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()),
            AllItems(sources));

        Assert.Equal(PackageState.Complete, resumed.State);

        var manifesto = ReadManifest(packageRoot);
        Assert.DoesNotContain((byte)0, File.ReadAllBytes(Path.Combine(packageRoot, "files.jsonl")));
        Assert.Equal(40, manifesto.Count);
        Assert.Equal(40, manifesto.Select(entry => entry.Sequence).Distinct().Count());
        Assert.Equal(
            Enumerable.Range(1, 40).Select(value => (long)value),
            manifesto.Select(entry => entry.Sequence));

        // Todo arquivo listado precisa existir de verdade dentro do pacote.
        foreach (var entry in manifesto)
        {
            var caminho = Path.Combine(
                packageRoot, entry.PackageRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(caminho), $"ausente no pacote: {entry.PackageRelativePath}");
        }
    }

    /// <summary>
    /// O que de fato importa no fim: um pacote que passou por interrupção + corrupção + retomada
    /// tem que RESTAURAR, com todo arquivo chegando ao destino. Ler o manifesto sem erro é meio
    /// caminho; o pacote só está bom quando serve para o que existe.
    /// </summary>
    [Fact]
    public async Task APackageResumedAfterCorruption_RestoresCompletely()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var packageRoot = fixture.PathFor("pacote-e2e");
        var sources = fixture.CreateSourceFiles(50);

        var interrompido = await BuildEngine(fileSystem).RunAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()),
            ItemsThatFailAfter(sources, 30));
        Assert.Equal(PackageState.Paused, interrompido.State);

        CorruptManifestTail(packageRoot);

        var retomado = await BuildEngine(fileSystem).ResumeAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()),
            AllItems(sources));
        Assert.Equal(PackageState.Complete, retomado.State);

        // Agora a prova real: restaurar.
        var destino = fixture.PathFor(@"destino\Users\teste");
        var clock = new FixedClock();
        var restauracao = await new StreamingRestoreEngine(
            fileSystem, new BackupPackageModule(fileSystem, clock), clock).RunAsync(
            new RestoreRequest(
                Guid.NewGuid(),
                packageRoot,
                destino,
                fixture.PathFor(@"destino\Users\Public"),
                fixture.PathFor("operacao")));

        Assert.Equal(RestoreState.Complete, restauracao.State);
        Assert.Equal(50, restauracao.Counters.CopiedFiles);

        foreach (var origem in sources)
        {
            var restaurado = Path.Combine(destino, "Documents", Path.GetFileName(origem));
            Assert.True(File.Exists(restaurado), $"não restaurou: {Path.GetFileName(origem)}");
            Assert.Equal(File.ReadAllText(origem), File.ReadAllText(restaurado));
        }
    }

    [Fact]
    public async Task Resume_AlsoRepairsAHalfWrittenLastLine()
    {
        // Variação do mesmo problema: a queda cortou a última linha no meio, sem zeros.
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var packageRoot = fixture.PathFor("pacote-cortado");
        var sources = fixture.CreateSourceFiles(30);

        var interrompido = await BuildEngine(fileSystem).RunAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()),
            ItemsThatFailAfter(sources, 20));
        Assert.Equal(PackageState.Paused, interrompido.State);

        var manifestPath = Path.Combine(packageRoot, "files.jsonl");
        var texto = File.ReadAllText(manifestPath, Encoding.UTF8);
        File.WriteAllText(manifestPath, texto + "{\"sequence\":99,\"moduleId\":\"user-fi", Encoding.UTF8);

        var resumed = await BuildEngine(fileSystem).ResumeAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()),
            AllItems(sources));

        Assert.Equal(PackageState.Complete, resumed.State);
        var manifesto = ReadManifest(packageRoot);
        Assert.Equal(30, manifesto.Count);
        Assert.Equal(30, manifesto.Select(entry => entry.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task Resume_DoesNotThrowAwayAnythingFromAHealthyManifest()
    {
        // Trava de segurança: a limpeza só pode remover o que está realmente quebrado. Um
        // manifesto íntegro precisa sair da retomada exatamente como entrou.
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var packageRoot = fixture.PathFor("pacote-saudavel");
        var sources = fixture.CreateSourceFiles(30);

        var interrompido = await BuildEngine(fileSystem).RunAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()),
            ItemsThatFailAfter(sources, 22));
        Assert.Equal(PackageState.Paused, interrompido.State);

        var antes = ReadManifest(packageRoot);
        Assert.NotEmpty(antes);

        var resumed = await BuildEngine(fileSystem).ResumeAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()),
            AllItems(sources));

        Assert.Equal(PackageState.Complete, resumed.State);
        var depois = ReadManifest(packageRoot);
        Assert.Equal(30, depois.Count);
        // As entradas que já existiam continuam lá, na mesma ordem e sem duplicar.
        Assert.Equal(
            antes.Select(entry => entry.Sequence),
            depois.Take(antes.Count).Select(entry => entry.Sequence));
    }

    [Fact]
    public async Task Resume_KeepsTheCountersConsistentWithTheManifest()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var packageRoot = fixture.PathFor("pacote-contadores");
        var sources = fixture.CreateSourceFiles(40);

        var interrompido = await BuildEngine(fileSystem).RunAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()),
            ItemsThatFailAfter(sources, 25));
        Assert.Equal(PackageState.Paused, interrompido.State);
        CorruptManifestTail(packageRoot);

        var resumed = await BuildEngine(fileSystem).ResumeAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()),
            AllItems(sources));

        var manifesto = ReadManifest(packageRoot);
        Assert.Equal(manifesto.Count, resumed.Counters.CopiedFiles);
        Assert.Equal(manifesto.Sum(entry => entry.Length), resumed.Counters.CopiedBytes);
    }

    // ---- apoio ----

    private static void CorruptManifestTail(string packageRoot)
    {
        var manifestPath = Path.Combine(packageRoot, "files.jsonl");
        var bytes = File.ReadAllBytes(manifestPath);
        // Corta no meio de uma linha e preenche com zeros, como o NTFS faz.
        var corte = bytes.Length - (bytes.Length / 4);
        var resultado = new byte[corte + 4096];
        Array.Copy(bytes, resultado, corte);
        File.WriteAllBytes(manifestPath, resultado);
    }

    private static List<FileManifestEntry> ReadManifest(string packageRoot) =>
    [
        .. File.ReadAllLines(Path.Combine(packageRoot, "files.jsonl"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<FileManifestEntry>(
                line, BackupPackageJsonOptions())!)
    ];

    private static JsonSerializerOptions BackupPackageJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static StreamingBackupEngine BuildEngine(IFileSystemAdapter fileSystem)
    {
        var clock = new FixedClock();
        return new StreamingBackupEngine(
            fileSystem, new BackupPackageModule(fileSystem, clock), clock);
    }

    private static async IAsyncEnumerable<TransferItem> AllItems(IReadOnlyList<string> sources)
    {
        for (var index = 0; index < sources.Count; index++)
        {
            yield return BuildItem(index + 1, sources[index]);
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<TransferItem> ItemsThatFailAfter(
        IReadOnlyList<string> sources, int limit)
    {
        for (var index = 0; index < sources.Count; index++)
        {
            if (index == limit)
            {
                throw new StorageDisconnectedException("A mídia de origem foi desconectada.");
            }

            yield return BuildItem(index + 1, sources[index]);
        }

        await Task.CompletedTask;
    }

    private static TransferItem BuildItem(int sequence, string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        return new TransferItem(
            sequence,
            "user-files",
            sourcePath,
            $"data/Users/teste/Documents/{Path.GetFileName(sourcePath)}",
            TransferClassification.PersonalFile,
            SourceRelativePath: Path.GetFileName(sourcePath),
            Metadata: new FileMetadata(
                info.CreationTimeUtc, info.LastWriteTimeUtc, info.Attributes),
            Length: info.Length);
    }

    private static PackageMetadata Metadata() => new(
        "1.0", "1.0.0-retomada", Guid.NewGuid(), PackageState.Created,
        DateTimeOffset.UnixEpoch, null,
        new SourceComputer("RETOMADA", @"F:\", "Windows 11", "x64"),
        new SourceProfile("teste", @"F:\Users\teste", null),
        "teste", [], DurableCounters.Empty,
        new IntegrityPolicy("SHA256", true, "size-and-handle"), IsEncrypted: false);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(), "grbkp-retomada", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string PathFor(string relative) => Path.Combine(root, relative);

        public List<string> CreateSourceFiles(int count)
        {
            var origem = Path.Combine(root, "origem");
            Directory.CreateDirectory(origem);
            var lista = new List<string>(count);
            for (var index = 1; index <= count; index++)
            {
                var caminho = Path.Combine(origem, $"arquivo-{index:D3}.txt");
                File.WriteAllText(caminho, new string('x', 200 + index), Encoding.UTF8);
                lista.Add(caminho);
            }

            return lista;
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
    }
}
