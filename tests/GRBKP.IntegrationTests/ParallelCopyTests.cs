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
/// Cópia paralela (recomendação 3.4): em SSD externo por USB, copiar 3–4 arquivos ao mesmo tempo
/// esconde a latência por arquivo. O risco de mexer aqui é a ORDEM: a restauração percorre o
/// manifesto e pula tudo com sequência menor ou igual ao último confirmado — se o manifesto ficar
/// fora de ordem, arquivos seriam pulados na restauração e isso é perda de dados silenciosa.
/// Por isso o primeiro teste aqui é justamente o da ordem, com as cópias terminando de propósito
/// na ordem errada.
/// </summary>
public sealed class ParallelCopyTests
{
    [Fact]
    public async Task ManifestStaysInStrictSequenceOrder_EvenWhenCopiesFinishOutOfOrder()
    {
        using var fixture = new TemporaryDirectory();
        // Quanto MAIOR a sequência, mais rápido termina: sem reordenação, o manifesto sairia
        // praticamente invertido.
        var fileSystem = new ReorderingFileSystemAdapter(new WindowsFileSystemAdapter());
        var engine = BuildEngine(fileSystem, maxConcurrentCopies: 4);

        var sources = new List<(string Relative, string Path)>();
        for (var index = 1; index <= 12; index++)
        {
            sources.Add(($"arquivo-{index:D2}.txt",
                fixture.WriteFile($"origem/arquivo-{index:D2}.txt", $"CONTEUDO {index}")));
        }

        var packageRoot = fixture.PathFor("pacote");
        var result = await engine.RunAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()), Items(sources));

        Assert.Equal(PackageState.Complete, result.State);

        var sequences = ReadManifestSequences(packageRoot);
        Assert.Equal(12, sequences.Count);
        Assert.Equal(sequences.OrderBy(value => value).ToList(), sequences);
    }

    [Fact]
    public async Task ConcurrentCopies_ProduceAPackageThatVerifiesAsIntact()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var engine = BuildEngine(fileSystem, maxConcurrentCopies: 4);

        var sources = new List<(string Relative, string Path)>();
        for (var index = 1; index <= 60; index++)
        {
            sources.Add(($"arquivo-{index:D3}.txt",
                fixture.WriteFile($"origem/arquivo-{index:D3}.txt", $"CONTEUDO REAL {index}")));
        }

        var packageRoot = fixture.PathFor("pacote");
        var result = await engine.RunAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()), Items(sources));

        Assert.Equal(PackageState.Complete, result.State);
        Assert.Equal(60, result.Counters.CopiedFiles);

        var verifier = new BackupPackageVerifier(
            fileSystem, new BackupPackageModule(fileSystem, new FixedClock()));
        var report = await verifier.VerifyAsync(packageRoot, null, CancellationToken.None);
        Assert.True(report.IsIntact);
        Assert.Equal(60, report.VerifiedFiles);

        foreach (var (relative, _) in sources)
        {
            var copied = Path.Combine(packageRoot, "data", "Users", "auxatc", relative);
            Assert.Equal(
                $"CONTEUDO REAL {int.Parse(relative[8..11])}", File.ReadAllText(copied));
        }
    }

    [Fact]
    public async Task ConcurrentCopies_StillIsolateASingleUnreadableFile()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var engine = BuildEngine(fileSystem, maxConcurrentCopies: 4);

        var sources = new List<(string Relative, string Path)>();
        for (var index = 1; index <= 8; index++)
        {
            sources.Add(($"arquivo-{index:D2}.txt",
                fixture.WriteFile($"origem/arquivo-{index:D2}.txt", $"CONTEUDO {index}")));
        }

        using var exclusiveLock = new FileStream(
            sources[3].Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var packageRoot = fixture.PathFor("pacote");
        var result = await engine.RunAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()), Items(sources));

        Assert.Equal(PackageState.Complete, result.State);
        Assert.Equal(7, result.Counters.CopiedFiles);
        Assert.Equal(1, result.Counters.FailedFiles);
        var sequences = ReadManifestSequences(packageRoot);
        Assert.Equal(sequences.OrderBy(value => value).ToList(), sequences);
    }

    [Fact]
    public async Task ConcurrentCopies_AbortWhenTheDestinationDiskFills()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new DiskFullAfterFileSystemAdapter(new WindowsFileSystemAdapter())
        {
            FailWritesAfter = 2
        };
        var engine = BuildEngine(fileSystem, maxConcurrentCopies: 4);

        var sources = new List<(string Relative, string Path)>();
        for (var index = 1; index <= 20; index++)
        {
            sources.Add(($"arquivo-{index:D2}.txt",
                fixture.WriteFile($"origem/arquivo-{index:D2}.txt", $"CONTEUDO {index}")));
        }

        var result = await engine.RunAsync(
            new BackupRequest(Guid.NewGuid(), fixture.PathFor("pacote"), Metadata()), Items(sources));

        Assert.NotEqual(PackageState.Complete, result.State);
        Assert.Contains(result.Errors, error => error.Code == "destination-disk-full");
    }

    [Fact]
    public async Task ResumingAConcurrentBackup_DoesNotRecopyWhatIsAlreadyInThePackage()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var packageRoot = fixture.PathFor("pacote");

        var sources = new List<(string Relative, string Path)>();
        for (var index = 1; index <= 30; index++)
        {
            sources.Add(($"arquivo-{index:D2}.txt",
                fixture.WriteFile($"origem/arquivo-{index:D2}.txt", $"CONTEUDO {index}")));
        }

        var first = await BuildEngine(fileSystem, maxConcurrentCopies: 4).RunAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()), Items(sources));
        Assert.Equal(PackageState.Complete, first.State);

        // Retomar um pacote já concluído devolve o resultado gravado, sem recopiar nada.
        var resumed = await BuildEngine(fileSystem, maxConcurrentCopies: 4).ResumeAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, Metadata()), Items(sources));

        Assert.Equal(PackageState.Complete, resumed.State);
        Assert.Equal(30, resumed.Counters.CopiedFiles);
        Assert.Equal(30, ReadManifestSequences(packageRoot).Count);
    }

    private static List<long> ReadManifestSequences(string packageRoot) =>
        [.. File.ReadAllLines(Path.Combine(packageRoot, "files.jsonl"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("sequence").GetInt64())];

    private static StreamingBackupEngine BuildEngine(
        IFileSystemAdapter fileSystem, int maxConcurrentCopies)
    {
        var clock = new FixedClock();
        return new StreamingBackupEngine(
            fileSystem,
            new BackupPackageModule(fileSystem, clock),
            clock,
            new StreamingBackupOptions(MaxConcurrentCopies: maxConcurrentCopies));
    }

    private static async IAsyncEnumerable<TransferItem> Items(
        IReadOnlyList<(string Relative, string Path)> sources)
    {
        var sequence = 0L;
        foreach (var (relative, path) in sources)
        {
            sequence++;
            yield return new TransferItem(
                sequence,
                "user-files",
                path,
                $"data/Users/auxatc/{relative}",
                TransferClassification.PersonalFile,
                SourceRelativePath: relative,
                Metadata: new FileMetadata(
                    DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal));
        }

        await Task.CompletedTask;
    }

    private static PackageMetadata Metadata() => new(
        "1.0", "1.0.0-paralelo", Guid.NewGuid(), PackageState.Created,
        DateTimeOffset.UnixEpoch, null,
        new SourceComputer("PARALELO", @"F:\", "Windows 11", "x64"),
        new SourceProfile("auxatc", @"F:\Users\auxatc", null),
        "teste-paralelo", [], DurableCounters.Empty,
        new IntegrityPolicy("SHA256", true, "size-and-handle+sha256-critical"), IsEncrypted: false);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    /// <summary>
    /// Faz as cópias terminarem fora de ordem de propósito: quanto maior a sequência do arquivo,
    /// menor o atraso na leitura.
    /// </summary>
    private sealed class ReorderingFileSystemAdapter(IFileSystemAdapter inner) : DelegatingFileSystem(inner)
    {
        public override async ValueTask<Stream> OpenReadAsync(
            string path, CancellationToken cancellationToken = default)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("arquivo-", StringComparison.Ordinal) &&
                int.TryParse(name.AsSpan(8), out var index))
            {
                await Task.Delay(40 - index * 3, cancellationToken);
            }

            return await Inner.OpenReadAsync(path, cancellationToken);
        }
    }

    private sealed class DiskFullAfterFileSystemAdapter(IFileSystemAdapter inner)
        : DelegatingFileSystem(inner)
    {
        private int writes;

        public int FailWritesAfter { get; set; } = int.MaxValue;

        public override ValueTask<Stream> OpenWriteAsync(
            string path, FileWriteMode mode, CancellationToken cancellationToken = default)
        {
            if (path.Contains("data", StringComparison.Ordinal) &&
                Interlocked.Increment(ref writes) > FailWritesAfter)
            {
                throw new IOException("Não há espaço suficiente no disco.", unchecked((int)0x80070070));
            }

            return Inner.OpenWriteAsync(path, mode, cancellationToken);
        }
    }

    private abstract class DelegatingFileSystem(IFileSystemAdapter inner) : IFileSystemAdapter
    {
        protected IFileSystemAdapter Inner { get; } = inner;

        public virtual ValueTask<Stream> OpenReadAsync(
            string path, CancellationToken cancellationToken = default) =>
            Inner.OpenReadAsync(path, cancellationToken);

        public virtual ValueTask<Stream> OpenWriteAsync(
            string path, FileWriteMode mode, CancellationToken cancellationToken = default) =>
            Inner.OpenWriteAsync(path, mode, cancellationToken);

        public IAsyncEnumerable<FileSystemEntry> EnumerateFilesAsync(
            string rootPath, CancellationToken cancellationToken = default) =>
            Inner.EnumerateFilesAsync(rootPath, cancellationToken);

        public IAsyncEnumerable<PathEntryInfo> EnumerateDirectoriesAsync(
            string rootPath, CancellationToken cancellationToken = default) =>
            Inner.EnumerateDirectoriesAsync(rootPath, cancellationToken);

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
            Inner.CreateDirectoryAsync(path, cancellationToken);

        public ValueTask<PathEntryInfo?> GetInfoAsync(
            string path, CancellationToken cancellationToken = default) =>
            Inner.GetInfoAsync(path, cancellationToken);

        public ValueTask MoveAsync(
            string sourcePath, string destinationPath, bool overwrite,
            CancellationToken cancellationToken = default) =>
            Inner.MoveAsync(sourcePath, destinationPath, overwrite, cancellationToken);

        public ValueTask DeleteAsync(
            string path, bool recursive, CancellationToken cancellationToken = default) =>
            Inner.DeleteAsync(path, recursive, cancellationToken);

        public ValueTask<FileSystemCapabilities> GetCapabilitiesAsync(
            string path, CancellationToken cancellationToken = default) =>
            Inner.GetCapabilitiesAsync(path, cancellationToken);

        public ValueTask SetMetadataAsync(
            string path, FileMetadata metadata, CancellationToken cancellationToken = default) =>
            Inner.SetMetadataAsync(path, metadata, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(), "grbkp-paralelo", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string PathFor(string relative) =>
            Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        public string WriteFile(string relativePath, string content)
        {
            var path = PathFor(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Encoding.UTF8);
            return path;
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
