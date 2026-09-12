using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Selection;

namespace GRBKP.Core.Tests;

public sealed class SelectionPolicyTests
{
    [Fact]
    public async Task T02_PicturesWith20LocalFiles_AreIgnoredAndDecisionIsRecorded()
    {
        var fileSystem = new SelectionFileSystemFake();
        fileSystem.FailOnSecondEnumeration = true;
        var profileRoot = @"E:\Users\Ana";
        fileSystem.AddRegularFiles(Path.Combine(profileRoot, "Pictures"), 20);
        var sut = new SelectionPolicyModule(fileSystem);

        var run = sut.Select(new ProfileSelection("Ana", profileRoot, null));
        var items = await CollectAsync(run.Items);

        Assert.DoesNotContain(items, item =>
            item.PackageRelativePath.Contains("/Pictures/", StringComparison.Ordinal));
        var decision = Assert.Single(run.Summary.Decisions, item =>
            item.LogicalFolder == LogicalFolder.Pictures);
        Assert.Equal(SelectionDecisionKind.IgnoredBelowThreshold, decision.Kind);
        Assert.Equal(20, decision.LocalFileCount);
        Assert.Equal(21, decision.RequiredFileCount);
    }

    [Fact]
    public async Task T03_PicturesWith21OrMoreLocalFiles_AreAllIncludedInOnePass()
    {
        var fileSystem = new SelectionFileSystemFake();
        fileSystem.FailOnSecondEnumeration = true;
        var profileRoot = @"E:\Users\Ana";
        fileSystem.AddRegularFiles(Path.Combine(profileRoot, "Pictures"), 23);
        var sut = new SelectionPolicyModule(fileSystem);

        var run = sut.Select(new ProfileSelection("Ana", profileRoot, null));
        var items = await CollectAsync(run.Items);

        Assert.Equal(23, items.Count);
        Assert.All(items, item => Assert.StartsWith(
            "data/Users/Ana/Pictures/",
            item.PackageRelativePath,
            StringComparison.Ordinal));
        var decision = Assert.Single(run.Summary.Decisions, item =>
            item.LogicalFolder == LogicalFolder.Pictures);
        Assert.Equal(SelectionDecisionKind.Included, decision.Kind);
        Assert.Equal(23, decision.LocalFileCount);
    }

    [Fact]
    public async Task AppDataSweep_SkipsEphemeralFolders_ThatOnlyWasteSpaceInThePackage()
    {
        // Medido num perfil real: AppData\Local tinha 16,6 GB, dos quais centenas de MB eram
        // apenas Temp, dumps de falha e caches de miniatura — conteúdo que o Windows recria
        // sozinho, mas que hoje entraria inteiro no pacote e no SSD de backup.
        var fileSystem = new SelectionFileSystemFake();
        var profileRoot = @"E:\Users\Ana";
        var appDataLocal = Path.Combine(profileRoot, "AppData", "Local");
        fileSystem.AddRegularFile(appDataLocal, @"Temp\instalador-qualquer.tmp");
        fileSystem.AddRegularFile(appDataLocal, @"CrashDumps\app.12345.dmp");
        fileSystem.AddRegularFile(appDataLocal, @"Microsoft\Windows\Explorer\thumbcache_256.db");
        fileSystem.AddRegularFile(appDataLocal, @"Microsoft\Windows\INetCache\IE\index.dat");
        fileSystem.AddRegularFile(appDataLocal, @"Microsoft\Windows\WebCache\WebCacheV01.dat");
        var sut = new SelectionPolicyModule(fileSystem);

        var run = sut.Select(new ProfileSelection("Ana", profileRoot, null));
        var items = await CollectAsync(run.Items);

        Assert.Empty(items);
    }

    [Fact]
    public async Task AppDataSweep_StillCapturesRealApplicationData_NextToTheEphemeralFolders()
    {
        // Trava de segurança da exclusão acima: só o que é descartável pode sair. Um programa
        // com "Temp" no meio do próprio nome, ou dados dentro de uma pasta de aplicativo, ficam.
        var fileSystem = new SelectionFileSystemFake();
        var profileRoot = @"E:\Users\Ana";
        var appDataLocal = Path.Combine(profileRoot, "AppData", "Local");
        fileSystem.AddRegularFile(appDataLocal, @"SistemaDaConcessionaria\config.xml");
        fileSystem.AddRegularFile(appDataLocal, @"TempoReal\banco.db");
        fileSystem.AddRegularFile(appDataLocal, @"Microsoft\Windows\Temas\meu-tema.theme");
        var sut = new SelectionPolicyModule(fileSystem);

        var run = sut.Select(new ProfileSelection("Ana", profileRoot, null));
        var items = await CollectAsync(run.Items);

        Assert.Equal(3, items.Count);
        Assert.Contains(items, item => item.PackageRelativePath.EndsWith(
            "AppData/Local/SistemaDaConcessionaria/config.xml", StringComparison.Ordinal));
        Assert.Contains(items, item => item.PackageRelativePath.EndsWith(
            "AppData/Local/TempoReal/banco.db", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CriticalFiles_AreMarkedForHashing_WhileBulkFilesAreNot()
    {
        // Política de integridade: verificar por hash TUDO custaria caro (SHA-256 medido a
        // 281 MB/s nesta máquina), mas os arquivos insubstituíveis merecem a garantia contra
        // corrupção silenciosa — setor ruim no SSD velho, bit trocado. Critério: documentos do
        // funcionário e bases do Outlook (.pst/.ost), onde ninguém percebe a corrupção até
        // precisar do arquivo, já com o SSD de origem formatado.
        var fileSystem = new SelectionFileSystemFake();
        var profileRoot = @"E:\Users\Ana";
        fileSystem.AddRegularFile(Path.Combine(profileRoot, "Documents"), "contrato.pdf");
        fileSystem.AddRegularFile(Path.Combine(profileRoot, "Downloads"), "instalador.exe");
        fileSystem.AddRegularFile(
            Path.Combine(profileRoot, "AppData", "Local"), @"Microsoft\Outlook\ana@ravel.pst");
        var sut = new SelectionPolicyModule(fileSystem);

        var run = sut.Select(new ProfileSelection("Ana", profileRoot, null));
        var items = await CollectAsync(run.Items);

        Assert.True(
            items.Single(item => item.PackageRelativePath.EndsWith(
                "contrato.pdf", StringComparison.Ordinal)).RequiresSha256,
            "Documento do funcionário precisa de verificação por hash.");
        Assert.True(
            items.Single(item => item.PackageRelativePath.EndsWith(
                ".pst", StringComparison.Ordinal)).RequiresSha256,
            "Base do Outlook precisa de verificação por hash.");
        Assert.False(
            items.Single(item => item.PackageRelativePath.EndsWith(
                "instalador.exe", StringComparison.Ordinal)).RequiresSha256,
            "Download reinstalável não justifica o custo do hash.");
    }

    [Fact]
    public async Task T04_PublicAndProfileFilesWithSameName_KeepDistinctNamespaces()
    {
        var fileSystem = new SelectionFileSystemFake();
        var profileRoot = @"E:\Users\Ana";
        var publicRoot = @"E:\Users\Public";
        fileSystem.AddRegularFile(Path.Combine(profileRoot, "Documents"), "igual.txt");
        fileSystem.AddRegularFile(Path.Combine(publicRoot, "Documents"), "igual.txt");
        var sut = new SelectionPolicyModule(fileSystem);

        var run = sut.Select(new ProfileSelection("Ana", profileRoot, publicRoot));
        var items = await CollectAsync(run.Items);

        Assert.Contains(items, item =>
            item.PackageRelativePath == "data/Users/Ana/Documents/igual.txt" &&
            item.Classification == TransferClassification.PersonalFile);
        Assert.Contains(items, item =>
            item.PackageRelativePath == "data/Users/Public/Documents/igual.txt" &&
            item.Classification == TransferClassification.PublicFile);
        Assert.Equal(2, items.Count(item => item.PackageRelativePath.EndsWith(
            "/Documents/igual.txt",
            StringComparison.Ordinal)));
    }

    [Fact]
    public async Task T05_AppDataTrees_AreCapturedLazilyWithoutACollectionProportionalToTotal()
    {
        var fileSystem = new SelectionFileSystemFake();
        var profileRoot = @"E:\Users\Ana";
        fileSystem.AddGeneratedFiles(Path.Combine(profileRoot, "AppData", "Local"), 10_000);
        fileSystem.AddGeneratedFiles(Path.Combine(profileRoot, "AppData", "LocalLow"), 10_000);
        fileSystem.AddGeneratedFiles(Path.Combine(profileRoot, "AppData", "Roaming"), 10_000);
        var sut = new SelectionPolicyModule(fileSystem);

        var run = sut.Select(new ProfileSelection("Ana", profileRoot, null));
        long count = 0;
        string? firstPath = null;
        string? lastPath = null;
        await foreach (var item in run.Items)
        {
            if (item.Classification != TransferClassification.UnclassifiedApplicationData)
            {
                continue;
            }

            firstPath ??= item.PackageRelativePath;
            lastPath = item.PackageRelativePath;
            count++;
        }

        Assert.Equal(30_000, count);
        Assert.Equal("data/Users/Ana/AppData/Local/file-00000.bin", firstPath);
        Assert.Equal("data/Users/Ana/AppData/Roaming/file-09999.bin", lastPath);
        Assert.Equal(30_000, fileSystem.GeneratedEntriesYielded);
    }

    [Fact]
    public async Task AppDataScan_ExcludesKnownAppFootprints_ToAvoidDuplicatingModuleData()
    {
        // O SelectionPolicyModule varre AppData inteiro como fallback generico, mas apps que
        // ja tem um IMigrationModule real (Thunderbird, Firefox, Chrome, Edge) capturam sua
        // propria subarvore separadamente com classificacao ModuleData. Sem essa exclusao, os
        // mesmos arquivos entrariam duas vezes no pacote — uma pelo Module, outra por aqui.
        var fileSystem = new SelectionFileSystemFake();
        var profileRoot = @"E:\Users\Ana";
        fileSystem.AddRegularFile(
            Path.Combine(profileRoot, "AppData", "Roaming"),
            "Thunderbird/profiles.ini");
        fileSystem.AddRegularFile(
            Path.Combine(profileRoot, "AppData", "Roaming"),
            "Thunderbird/Profiles/abc.default/prefs.js");
        fileSystem.AddRegularFile(
            Path.Combine(profileRoot, "AppData", "Roaming"),
            "OutroApp/config.dat");
        var sut = new SelectionPolicyModule(
            fileSystem,
            excludedAppDataRelativePaths: ["AppData/Roaming/Thunderbird"]);

        var run = sut.Select(new ProfileSelection("Ana", profileRoot, null));
        var items = await CollectAsync(run.Items);
        var appDataItems = items
            .Where(item => item.Classification == TransferClassification.UnclassifiedApplicationData)
            .ToList();

        Assert.DoesNotContain(appDataItems, item =>
            item.PackageRelativePath.Contains("/Thunderbird/", StringComparison.Ordinal));
        Assert.Contains(appDataItems, item =>
            item.PackageRelativePath.EndsWith("/OutroApp/config.dat", StringComparison.Ordinal));
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class SelectionFileSystemFake : IFileSystemAdapter
    {
        private readonly Dictionary<string, List<FileSystemEntry>> entries =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, int> generatedEntries =
            new(StringComparer.OrdinalIgnoreCase);

        public bool FailOnSecondEnumeration { get; set; }

        public long GeneratedEntriesYielded { get; private set; }

        public void AddRegularFiles(string root, int count)
        {
            entries[root] = Enumerable.Range(1, count)
                .Select(index => new FileSystemEntry(
                    Path.Combine(root, $"image-{index:D2}.jpg"),
                    index,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    FileAttributes.Normal))
                .ToList();
        }

        public void AddRegularFile(string root, string relativePath)
        {
            if (!entries.TryGetValue(root, out var found))
            {
                found = [];
                entries[root] = found;
            }

            found.Add(new FileSystemEntry(
                Path.Combine(root, relativePath),
                1,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                FileAttributes.Normal));
        }

        public void AddGeneratedFiles(string root, int count) => generatedEntries[root] = count;

        private readonly Dictionary<string, int> enumerationCounts =
            new(StringComparer.OrdinalIgnoreCase);

        public IAsyncEnumerable<PathEntryInfo> EnumerateDirectoriesAsync(
            string rootPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<FileSystemEntry> EnumerateFilesAsync(
            string rootPath,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            enumerationCounts[rootPath] = enumerationCounts.GetValueOrDefault(rootPath) + 1;
            if (FailOnSecondEnumeration && enumerationCounts[rootPath] > 1)
            {
                throw new InvalidOperationException($"A origem foi enumerada novamente: {rootPath}");
            }

            if (!entries.TryGetValue(rootPath, out var found))
            {
                if (!generatedEntries.TryGetValue(rootPath, out var generatedCount))
                {
                    yield break;
                }

                for (var index = 0; index < generatedCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    GeneratedEntriesYielded++;
                    yield return new FileSystemEntry(
                        Path.Combine(rootPath, $"file-{index:D5}.bin"),
                        index,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch,
                        FileAttributes.Normal);
                }

                yield break;
            }

            foreach (var entry in found)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
                await Task.Yield();
            }
        }

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Stream> OpenWriteAsync(
            string path,
            FileWriteMode mode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<PathEntryInfo?> GetInfoAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PathEntryInfo?>(null);

        public ValueTask MoveAsync(
            string sourcePath,
            string destinationPath,
            bool overwrite,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteAsync(
            string path,
            bool recursive,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileSystemCapabilities> GetCapabilitiesAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FileSystemCapabilities("NTFS", true, true));

        public ValueTask SetMetadataAsync(
            string path,
            FileMetadata metadata,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
