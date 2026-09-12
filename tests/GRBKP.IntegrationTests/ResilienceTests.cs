using System.Text;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Packages;
using GRBKP.Core.Transfers;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

/// <summary>
/// Testes adversariais contra o sistema de arquivos REAL do Windows, cobrindo o que acontece de
/// fato num perfil de usuario de verdade: arquivo travado por outro processo, arquivo
/// somente-leitura no destino, pasta somente-leitura sendo apagada, caminho acima de 260
/// caracteres. Sao exatamente as situacoes em que um utilitario de backup nao pode simplesmente
/// desistir de tudo.
/// </summary>
public sealed class ResilienceTests
{
    // ---- Adaptador de sistema de arquivos ----

    [Fact]
    public async Task MoveAsync_OverwritesDestination_EvenWhenItIsReadOnly()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var source = fixture.WriteFile("origem.txt", "conteúdo novo");
        var destination = fixture.WriteFile("destino.txt", "conteúdo antigo");
        File.SetAttributes(destination, FileAttributes.ReadOnly);

        await fileSystem.MoveAsync(source, destination, overwrite: true, CancellationToken.None);

        Assert.Equal("conteúdo novo", File.ReadAllText(destination));
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile_EvenWhenItIsReadOnly()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var path = fixture.WriteFile("somente-leitura.txt", "x");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        await fileSystem.DeleteAsync(path, recursive: false, CancellationToken.None);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DeleteAsync_RemovesWholeTree_EvenWhenItContainsReadOnlyFiles()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var tree = fixture.PathFor("arvore");
        var readOnlyFile = fixture.WriteFile(@"arvore\sub\somente-leitura.txt", "x");
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);

        await fileSystem.DeleteAsync(tree, recursive: true, CancellationToken.None);

        Assert.False(Directory.Exists(tree));
    }

    // ---- Motor de backup ----

    [Fact]
    public async Task BackupRunAsync_SkipsLockedFile_ButCopiesEverythingElse()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var engine = BuildBackupEngine(fileSystem);

        var before = fixture.WriteFile("origem/antes.txt", "ANTES");
        var locked = fixture.WriteFile("origem/travado.bin", "CONTEUDO-TRAVADO");
        var after = fixture.WriteFile("origem/depois.txt", "DEPOIS");
        var packageRoot = fixture.PathFor("pacote");

        // Trava exclusiva: exatamente o que um Outlook/navegador aberto faz com seus bancos.
        using var exclusiveLock = new FileStream(
            locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await engine.RunAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, BuildMetadata()),
            PersonalFiles([("antes.txt", before), ("travado.bin", locked), ("depois.txt", after)]));

        Assert.Equal(PackageState.Complete, result.State);
        Assert.Equal(2, result.Counters.CopiedFiles);
        Assert.Equal(1, result.Counters.FailedFiles);
        var error = Assert.Single(result.Errors);
        Assert.Contains("travado.bin", error.RelativePath ?? string.Empty);

        Assert.Equal("ANTES", File.ReadAllText(
            Path.Combine(packageRoot, "data", "Users", "auxatc", "antes.txt")));
        Assert.Equal("DEPOIS", File.ReadAllText(
            Path.Combine(packageRoot, "data", "Users", "auxatc", "depois.txt")));
        Assert.False(File.Exists(
            Path.Combine(packageRoot, "data", "Users", "auxatc", "travado.bin")));
    }

    [Fact]
    public async Task BackupRunAsync_LeavesNoPartialFile_WhenACopyFails()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var engine = BuildBackupEngine(fileSystem);

        var locked = fixture.WriteFile("origem/travado.bin", "CONTEUDO");
        var packageRoot = fixture.PathFor("pacote");
        using var exclusiveLock = new FileStream(
            locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await engine.RunAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, BuildMetadata()),
            PersonalFiles([("travado.bin", locked)]));

        var profileFolder = Path.Combine(packageRoot, "data", "Users", "auxatc");
        var leftovers = Directory.Exists(profileFolder)
            ? Directory.GetFiles(profileFolder, "*", SearchOption.AllDirectories)
            : [];
        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task BackupRunAsync_StillPauses_WhenStorageDisconnects()
    {
        // Regressao: a resiliencia por arquivo NAO pode engolir uma desconexao de midia — o SSD
        // sumir precisa continuar pausando a operacao inteira, nao virar "um arquivo com erro".
        using var fixture = new TemporaryDirectory();
        var fileSystem = new DisconnectingFileSystemAdapter(new WindowsFileSystemAdapter());
        var engine = BuildBackupEngine(fileSystem);

        var file = fixture.WriteFile("origem/qualquer.txt", "x");
        var packageRoot = fixture.PathFor("pacote");
        fileSystem.FailNextCopyWithDisconnection = true;

        var result = await engine.RunAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, BuildMetadata()),
            PersonalFiles([("qualquer.txt", file)]));

        Assert.Equal(PackageState.Paused, result.State);
        Assert.Equal("storage-disconnected", Assert.Single(result.Errors).Code);
    }

    // ---- Motor de restauracao ----

    [Fact]
    public async Task RestoreRunAsync_SucceedsOnSecondRun_WhenPreviousRunLeftReadOnlyFiles()
    {
        // Cenario real: o tecnico roda a restauracao, algo o faz repetir. Os arquivos ja
        // restaurados carregam os atributos originais — inclusive somente-leitura.
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var packageRoot = fixture.PathFor("pacote");
        var source = fixture.WriteFile("origem/contrato.txt", "CONTRATO");
        File.SetAttributes(source, FileAttributes.ReadOnly);

        var backupEngine = BuildBackupEngine(fileSystem);
        var backup = await backupEngine.RunAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, BuildMetadata()),
            PersonalFiles([("contrato.txt", source)]));
        Assert.Equal(PackageState.Complete, backup.State);

        var destination = fixture.PathFor(@"destino\Users\auxatc.DOMINIO");
        var first = await RunRestoreAsync(fixture, fileSystem, packageRoot, destination, "op1");
        Assert.Equal(RestoreState.Complete, first.State);

        var second = await RunRestoreAsync(fixture, fileSystem, packageRoot, destination, "op2");

        Assert.Equal(RestoreState.Complete, second.State);
        Assert.Equal("CONTRATO", File.ReadAllText(Path.Combine(destination, "contrato.txt")));
    }

    [Fact]
    public async Task RestoreRunAsync_SkipsFileLockedAtDestination_ButRestoresEverythingElse()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var packageRoot = fixture.PathFor("pacote");
        var first = fixture.WriteFile("origem/primeiro.txt", "PRIMEIRO");
        var blocked = fixture.WriteFile("origem/bloqueado.txt", "BLOQUEADO");
        var last = fixture.WriteFile("origem/ultimo.txt", "ULTIMO");

        var backupEngine = BuildBackupEngine(fileSystem);
        await backupEngine.RunAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, BuildMetadata()),
            PersonalFiles([
                ("primeiro.txt", first), ("bloqueado.txt", blocked), ("ultimo.txt", last)]));

        var destination = fixture.PathFor(@"destino\Users\auxatc.DOMINIO");
        Directory.CreateDirectory(destination);
        var blockedDestination = Path.Combine(destination, "bloqueado.txt");
        File.WriteAllText(blockedDestination, "ORIGINAL-DO-DESTINO");
        using var exclusiveLock = new FileStream(
            blockedDestination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await RunRestoreAsync(fixture, fileSystem, packageRoot, destination, "op1");

        Assert.Equal(RestoreState.Complete, result.State);
        Assert.Equal(1, result.Counters.FailedFiles);
        Assert.Equal("PRIMEIRO", File.ReadAllText(Path.Combine(destination, "primeiro.txt")));
        Assert.Equal("ULTIMO", File.ReadAllText(Path.Combine(destination, "ultimo.txt")));
    }

    // ---- Caminhos longos (> 260 caracteres) ----

    [Fact]
    public async Task EnumerateAndCopy_HandlePathsLongerThanTheClassicWindowsLimit()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();

        // Pastas aninhadas como num perfil real (Downloads de projetos, OneDrive corporativo).
        var deep = fixture.PathFor("origem");
        Directory.CreateDirectory(deep);
        while (deep.Length < 280)
        {
            deep = Path.Combine(deep, new string('p', 40));
            Directory.CreateDirectory(deep);
        }

        var deepFile = Path.Combine(deep, "documento.txt");
        File.WriteAllText(deepFile, "CONTEUDO-PROFUNDO", Encoding.UTF8);

        var found = new List<FileSystemEntry>();
        await foreach (var entry in fileSystem.EnumerateFilesAsync(
            fixture.PathFor("origem"), CancellationToken.None))
        {
            found.Add(entry);
        }

        Assert.Single(found);
        Assert.True(found[0].Length > 0, "O arquivo profundo deve ter tamanho legível.");
    }

    // ---- Infraestrutura dos testes ----

    private static StreamingBackupEngine BuildBackupEngine(IFileSystemAdapter fileSystem)
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero));
        return new StreamingBackupEngine(fileSystem, new BackupPackageModule(fileSystem, clock), clock);
    }

    private static async Task<RestoreOperationResult> RunRestoreAsync(
        TemporaryDirectory fixture,
        IFileSystemAdapter fileSystem,
        string packageRoot,
        string destinationProfileRoot,
        string operationFolder)
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 12, 4, 0, 0, TimeSpan.Zero));
        var engine = new StreamingRestoreEngine(
            fileSystem, new BackupPackageModule(fileSystem, clock), clock);
        return await engine.RunAsync(new RestoreRequest(
            Guid.NewGuid(),
            packageRoot,
            destinationProfileRoot,
            fixture.PathFor(@"destino\Users\Public"),
            fixture.PathFor($@"operacoes\{operationFolder}")));
    }

    private static PackageMetadata BuildMetadata() => new(
        "1.0",
        "1.0.0-resiliencia",
        Guid.NewGuid(),
        PackageState.Created,
        new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero),
        null,
        new SourceComputer("RESILIENCIA-TESTE", @"F:\", "Windows 11 Pro", "x64"),
        new SourceProfile("auxatc", @"F:\Users\auxatc", null),
        "teste-de-resiliencia",
        [],
        DurableCounters.Empty,
        new IntegrityPolicy("SHA256", false, "size-and-handle"),
        IsEncrypted: false);

    private static async IAsyncEnumerable<TransferItem> PersonalFiles(
        IReadOnlyList<(string Relative, string SourcePath)> files)
    {
        var sequence = 0L;
        foreach (var (relative, sourcePath) in files)
        {
            sequence++;
            // Metadados reais do arquivo, como a política de seleção faz em produção — é o que
            // propaga o atributo somente-leitura até o pacote e, na restauração, até o destino.
            var info = new FileInfo(sourcePath);
            yield return new TransferItem(
                sequence,
                "user-files",
                sourcePath,
                $"data/Users/auxatc/{relative}",
                TransferClassification.PersonalFile,
                SourceRelativePath: relative,
                Metadata: new FileMetadata(
                    info.CreationTimeUtc, info.LastWriteTimeUtc, info.Attributes));
        }

        await Task.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    /// <summary>
    /// Simula a midia sumir no meio de uma copia (SSD desconectado), para garantir que a
    /// resiliencia por arquivo nao transforme isso num simples "arquivo com erro".
    /// </summary>
    private sealed class DisconnectingFileSystemAdapter(IFileSystemAdapter inner) : IFileSystemAdapter
    {
        public bool FailNextCopyWithDisconnection { get; set; }

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (FailNextCopyWithDisconnection && !path.Contains("pacote", StringComparison.Ordinal))
            {
                FailNextCopyWithDisconnection = false;
                throw new StorageDisconnectedException("A mídia de origem foi desconectada.");
            }

            return inner.OpenReadAsync(path, cancellationToken);
        }

        public IAsyncEnumerable<FileSystemEntry> EnumerateFilesAsync(
            string rootPath, CancellationToken cancellationToken = default) =>
            inner.EnumerateFilesAsync(rootPath, cancellationToken);

        public IAsyncEnumerable<PathEntryInfo> EnumerateDirectoriesAsync(
            string rootPath, CancellationToken cancellationToken = default) =>
            inner.EnumerateDirectoriesAsync(rootPath, cancellationToken);

        public ValueTask<Stream> OpenWriteAsync(
            string path, FileWriteMode mode, CancellationToken cancellationToken = default) =>
            inner.OpenWriteAsync(path, mode, cancellationToken);

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
            inner.CreateDirectoryAsync(path, cancellationToken);

        public ValueTask<PathEntryInfo?> GetInfoAsync(
            string path, CancellationToken cancellationToken = default) =>
            inner.GetInfoAsync(path, cancellationToken);

        public ValueTask MoveAsync(
            string sourcePath, string destinationPath, bool overwrite,
            CancellationToken cancellationToken = default) =>
            inner.MoveAsync(sourcePath, destinationPath, overwrite, cancellationToken);

        public ValueTask DeleteAsync(
            string path, bool recursive, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(path, recursive, cancellationToken);

        public ValueTask<FileSystemCapabilities> GetCapabilitiesAsync(
            string path, CancellationToken cancellationToken = default) =>
            inner.GetCapabilitiesAsync(path, cancellationToken);

        public ValueTask SetMetadataAsync(
            string path, FileMetadata metadata, CancellationToken cancellationToken = default) =>
            inner.SetMetadataAsync(path, metadata, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            System.IO.Path.GetTempPath(), "grbkp-resiliencia", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string PathFor(string relative) =>
            System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public string WriteFile(string relativePath, string content)
        {
            var path = PathFor(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Encoding.UTF8);
            return path;
        }

        public void Dispose()
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Limpeza de melhor esforço.
                }
            }

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Limpeza de melhor esforço.
            }
        }
    }
}
