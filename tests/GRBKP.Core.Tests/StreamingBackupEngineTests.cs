using System.Text;
using System.Text.Json;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Packages;
using GRBKP.Core.Transfers;

namespace GRBKP.Core.Tests;

public sealed class StreamingBackupEngineTests
{
    [Fact]
    public async Task T07_OneHundredThousandFilesAnd40GiB_StreamWithBoundedBackpressure_AndVerifyDoesNotRereadData()
    {
        const int fileCount = 100_000;
        const long totalBytes = 40L * 1024 * 1024 * 1024;
        var fileSystem = new VirtualBackupFileSystem(maximumAllowedAhead: 8);
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var packageModule = new BackupPackageModule(fileSystem, clock);
        var engine = new StreamingBackupEngine(
            fileSystem,
            packageModule,
            clock,
            new StreamingBackupOptions(ChannelCapacity: 4, BufferSize: 1024 * 1024));
        var backupId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var operationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var packageRoot = @"X:\GRBKP_Ana\2026-09-11";
        var metadata = CreateMetadata(backupId, clock.UtcNow);

        var result = await engine.RunAsync(
            new BackupRequest(operationId, packageRoot, metadata),
            GenerateItems(fileSystem, fileCount, totalBytes));

        Assert.Equal(PackageState.Complete, result.State);
        Assert.Equal(fileCount, result.Counters.CopiedFiles);
        Assert.Equal(totalBytes, result.Counters.CopiedBytes);
        Assert.Equal(fileCount, fileSystem.SourceOpenCount);
        Assert.Equal(0, fileSystem.PackageDataReadAttempts);
        Assert.InRange(fileSystem.MaximumObservedAhead, 1, 8);
        Assert.Equal(fileCount, fileSystem.CommittedDataFiles);

        using var packageJson = JsonDocument.Parse(fileSystem.ReadText(
            Path.Combine(packageRoot, "package.json")));
        Assert.Equal("complete", packageJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(fileCount, packageJson.RootElement
            .GetProperty("counters")
            .GetProperty("copiedFiles")
            .GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(packageJson.RootElement
            .GetProperty("filesManifestSha256")
            .GetString()));
    }

    [Fact]
    public async Task T10_FileLargerThan4GiB_Uses64BitLengthOnExFat_AndFat32IsRejectedBeforeSourceOpen()
    {
        const long largeLength = 4_294_967_297;
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 11, 12, 30, 0, TimeSpan.Zero));
        var backupId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var operationId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        const string relativePath = "data/Users/Ana/Documents/large.bin";

        var exFat = new VirtualBackupFileSystem(
            maximumAllowedAhead: 8,
            new FileSystemCapabilities("exFAT", true, true));
        var exFatEngine = new StreamingBackupEngine(
            exFat,
            new BackupPackageModule(exFat, clock),
            clock,
            new StreamingBackupOptions(ChannelCapacity: 2, BufferSize: 1024 * 1024));
        var item = new TransferItem(
            1,
            "user-files",
            $"virtual-source://1/{largeLength}",
            relativePath,
            TransferClassification.PersonalFile);

        var exFatResult = await exFatEngine.RunAsync(
            new BackupRequest(operationId, @"X:\ExFatBackup", CreateMetadata(backupId, clock.UtcNow)),
            Single(item));

        Assert.Equal(PackageState.Complete, exFatResult.State);
        Assert.Equal(largeLength, exFatResult.Counters.CopiedBytes);
        Assert.Equal(largeLength, exFat.GetVirtualLength(
            Path.Combine(@"X:\ExFatBackup", relativePath.Replace('/', Path.DirectorySeparatorChar))));

        var fat32 = new VirtualBackupFileSystem(
            maximumAllowedAhead: 8,
            new FileSystemCapabilities("FAT32", false, false));
        var fat32Engine = new StreamingBackupEngine(
            fat32,
            new BackupPackageModule(fat32, clock),
            clock);

        var fat32Result = await fat32Engine.RunAsync(
            new BackupRequest(operationId, @"F:\Fat32Backup", CreateMetadata(backupId, clock.UtcNow)),
            Single(item));

        Assert.Equal(PackageState.Failed, fat32Result.State);
        Assert.Contains(fat32Result.Errors, error =>
            error.Code == "destination-filesystem-large-files-unsupported");
        Assert.Equal(0, fat32.SourceOpenCount);
    }

    [Fact]
    public async Task T08_DisconnectedBackup_IsPausedAndResumesWithoutCommittingPartialOrDuplicatingItems()
    {
        const int fileCount = 7;
        const long totalBytes = 70;
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 11, 13, 0, 0, TimeSpan.Zero));
        var backupId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var operationId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var packageRoot = @"X:\InterruptedBackup";
        var fileSystem = new VirtualBackupFileSystem(int.MaxValue)
        {
            DisconnectWhenCommittedFileCountIs = 3
        };
        var firstEngine = new StreamingBackupEngine(
            fileSystem,
            new BackupPackageModule(fileSystem, clock),
            clock,
            new StreamingBackupOptions(ChannelCapacity: 2, BufferSize: 16));
        var request = new BackupRequest(
            operationId,
            packageRoot,
            CreateMetadata(backupId, clock.UtcNow));

        var interrupted = await firstEngine.RunAsync(
            request,
            GenerateItems(fileSystem, fileCount, totalBytes));

        Assert.Equal(PackageState.Paused, interrupted.State);
        Assert.Equal(3, interrupted.Counters.CopiedFiles);
        Assert.DoesNotContain(interrupted.Errors, error => error.Code == "complete");
        Assert.False(fileSystem.HasVirtualFile(Path.Combine(
            packageRoot,
            "data",
            "Users",
            "Ana",
            "AppData",
            "Local",
            "file-000004.bin")));

        fileSystem.Reconnect();
        var resumedEngine = new StreamingBackupEngine(
            fileSystem,
            new BackupPackageModule(fileSystem, clock),
            clock,
            new StreamingBackupOptions(ChannelCapacity: 2, BufferSize: 16));
        var completed = await resumedEngine.ResumeAsync(
            request,
            GenerateItems(fileSystem, fileCount, totalBytes));

        Assert.Equal(PackageState.Complete, completed.State);
        Assert.Equal(fileCount, completed.Counters.CopiedFiles);
        Assert.Equal(totalBytes, completed.Counters.CopiedBytes);

        // O que precisa continuar exato: o manifesto tem uma linha por arquivo, sem duplicar
        // nada. É ele que a restauração e a conferência percorrem.
        Assert.Equal(fileCount, fileSystem.ReadText(Path.Combine(packageRoot, "files.jsonl"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Length);

        // Já o número de GRAVAÇÕES físicas pode passar de fileCount: com cópia paralela, no
        // instante da desconexão havia outras cópias em voo que chegaram a terminar sem serem
        // registradas (e foram apagadas do pacote no aborto). A retomada as refaz. O desperdício
        // é limitado à janela de concorrência — é o preço de sobrepor cópias, e vale a pena.
        Assert.InRange(
            fileSystem.CommittedDataFiles,
            fileCount,
            fileCount + new StreamingBackupOptions().ValidatedMaxConcurrentCopies);

        // Retomar um pacote JÁ concluído não pode copiar nem mais um arquivo: o contador precisa
        // ficar exatamente onde estava antes desta chamada.
        var writesBeforeRepeat = fileSystem.CommittedDataFiles;
        var repeated = await resumedEngine.ResumeAsync(
            request,
            GenerateItems(fileSystem, fileCount, totalBytes));
        Assert.Equal(PackageState.Complete, repeated.State);
        Assert.Equal(writesBeforeRepeat, fileSystem.CommittedDataFiles);
    }

    [Fact]
    public async Task T09_DisconnectedRestore_KeepsFinalFileIntactAndResumesFromDurableCheckpoint()
    {
        const int fileCount = 3;
        const long totalBytes = 30;
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 11, 14, 0, 0, TimeSpan.Zero));
        var backupId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var backupOperationId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var restoreOperationId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var packageRoot = @"X:\RestoreSource";
        var operationRoot = @"C:\ProgramData\GRBKP\operations\99999999";
        var destinationRoot = @"C:\Users\Ana";
        var fileSystem = new VirtualBackupFileSystem(int.MaxValue);
        var packageModule = new BackupPackageModule(fileSystem, clock);
        var backupEngine = new StreamingBackupEngine(fileSystem, packageModule, clock);
        var backupRequest = new BackupRequest(
            backupOperationId,
            packageRoot,
            CreateMetadata(backupId, clock.UtcNow));
        var backup = await backupEngine.RunAsync(
            backupRequest,
            GeneratePersonalItems(fileSystem, fileCount, totalBytes));
        Assert.Equal(PackageState.Complete, backup.State);

        fileSystem.AllowPackageDataReads = true;
        fileSystem.DisconnectOnPackageDataOpenNumber = 2;
        var restoreRequest = new RestoreRequest(
            restoreOperationId,
            packageRoot,
            destinationRoot,
            @"C:\Users\Public",
            operationRoot);
        var firstRestoreEngine = new StreamingRestoreEngine(fileSystem, packageModule, clock);

        var paused = await firstRestoreEngine.RunAsync(restoreRequest);

        Assert.Equal(RestoreState.Paused, paused.State);
        Assert.Equal(1, paused.Counters.CopiedFiles);
        Assert.True(fileSystem.HasFile(Path.Combine(
            destinationRoot,
            "Documents",
            "file-000001.bin")));
        Assert.False(fileSystem.HasFile(Path.Combine(
            destinationRoot,
            "Documents",
            "file-000002.bin")));

        fileSystem.Reconnect();
        var resumedRestoreEngine = new StreamingRestoreEngine(
            fileSystem,
            new BackupPackageModule(fileSystem, clock),
            clock);
        var completed = await resumedRestoreEngine.ResumeAsync(restoreRequest);

        Assert.Equal(RestoreState.Complete, completed.State);
        Assert.Equal(fileCount, completed.Counters.CopiedFiles);
        Assert.Equal(totalBytes, completed.Counters.CopiedBytes);
        Assert.Equal(fileCount, fileSystem.RestoredFiles);

        var repeated = await resumedRestoreEngine.ResumeAsync(restoreRequest);
        Assert.Equal(RestoreState.Complete, repeated.State);
        Assert.Equal(fileCount, fileSystem.RestoredFiles);
    }

    [Fact]
    public async Task T06_UnknownAppData_IsPreservedInPackage_NotRestored_AndReported()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 11, 15, 0, 0, TimeSpan.Zero));
        var backupId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var backupOperationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var restoreOperationId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var packageRoot = @"X:\UnknownAppDataBackup";
        var operationRoot = @"C:\ProgramData\GRBKP\operations\cccccccc";
        var destinationRoot = @"C:\Users\Ana";
        var sourcePath = @"E:\Users\Ana\AppData\Roaming\UnknownVendor\state.db";
        var destinationPath = Path.Combine(
            destinationRoot,
            "AppData",
            "Roaming",
            "UnknownVendor",
            "state.db");
        var fileSystem = new VirtualBackupFileSystem(int.MaxValue);
        fileSystem.SeedFile(sourcePath, Encoding.UTF8.GetBytes("OLD-SSD"));
        fileSystem.SeedFile(destinationPath, Encoding.UTF8.GetBytes("NEW-SSD"));
        var packageModule = new BackupPackageModule(fileSystem, clock);
        var backupEngine = new StreamingBackupEngine(fileSystem, packageModule, clock);
        var item = new TransferItem(
            1,
            "unclassified-appdata",
            sourcePath,
            "data/Users/Ana/AppData/Roaming/UnknownVendor/state.db",
            TransferClassification.UnclassifiedApplicationData,
            SourceRelativePath: "AppData/Roaming/UnknownVendor/state.db");

        var backup = await backupEngine.RunAsync(
            new BackupRequest(
                backupOperationId,
                packageRoot,
                CreateMetadata(backupId, clock.UtcNow)),
            Single(item));
        Assert.Equal(PackageState.Complete, backup.State);

        fileSystem.AllowPackageDataReads = true;
        var restore = await new StreamingRestoreEngine(fileSystem, packageModule, clock).RunAsync(
            new RestoreRequest(
                restoreOperationId,
                packageRoot,
                destinationRoot,
                @"C:\Users\Public",
                operationRoot));

        Assert.Equal(RestoreState.Complete, restore.State);
        Assert.Equal(0, restore.Counters.CopiedFiles);
        Assert.Equal(1, restore.Counters.IgnoredFiles);
        var preserved = Assert.Single(restore.PreservedItems);
        Assert.Equal("preserved-not-reintegrated", preserved.ReasonCode);
        Assert.Equal("NEW-SSD", Encoding.UTF8.GetString(fileSystem.ReadBytes(destinationPath)));
        Assert.True(fileSystem.HasVirtualFile(PackagePath.ResolveInside(
            packageRoot,
            item.PackageRelativePath)));

        using var report = JsonDocument.Parse(fileSystem.ReadText(Path.Combine(
            operationRoot,
            "reports",
            "report.json")));
        Assert.Equal("complete", report.RootElement.GetProperty("state").GetString());
        var pending = Assert.Single(report.RootElement.GetProperty("pendingItems").EnumerateArray());
        Assert.Equal("preserved-not-reintegrated", pending.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task T36_DocumentsCollision_IsOverwrittenWhileDestinationOnlyFileIsPreserved()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 11, 15, 30, 0, TimeSpan.Zero));
        var fileSystem = new VirtualBackupFileSystem(int.MaxValue)
        {
            StorePackageDataContent = true
        };
        var packageRoot = @"X:\DocumentsBackup";
        var destinationRoot = @"C:\Users\Ana";
        var operationRoot = @"C:\ProgramData\GRBKP\operations\dddddddd";
        var collisionSource = @"E:\Users\Ana\Documents\collision.txt";
        var sourceOnlySource = @"E:\Users\Ana\Documents\source-only.txt";
        var collisionDestination = Path.Combine(destinationRoot, "Documents", "collision.txt");
        var destinationOnly = Path.Combine(destinationRoot, "Documents", "destination-only.txt");
        var sourceOnlyDestination = Path.Combine(destinationRoot, "Documents", "source-only.txt");
        fileSystem.SeedFile(collisionSource, Encoding.UTF8.GetBytes("FROM-OLD-SSD"));
        fileSystem.SeedFile(sourceOnlySource, Encoding.UTF8.GetBytes("SOURCE-ONLY"));
        fileSystem.SeedFile(collisionDestination, Encoding.UTF8.GetBytes("FROM-NEW-SSD"));
        fileSystem.SeedFile(destinationOnly, Encoding.UTF8.GetBytes("KEEP-ME"));
        var packageModule = new BackupPackageModule(fileSystem, clock);
        var backupEngine = new StreamingBackupEngine(fileSystem, packageModule, clock);
        var items = Items(
            new TransferItem(
                1,
                "user-files",
                collisionSource,
                "data/Users/Ana/Documents/collision.txt",
                TransferClassification.PersonalFile),
            new TransferItem(
                2,
                "user-files",
                sourceOnlySource,
                "data/Users/Ana/Documents/source-only.txt",
                TransferClassification.PersonalFile));
        var backup = await backupEngine.RunAsync(
            new BackupRequest(
                Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                packageRoot,
                CreateMetadata(
                    Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                    clock.UtcNow)),
            items);
        Assert.Equal(PackageState.Complete, backup.State);

        fileSystem.AllowPackageDataReads = true;
        var restore = await new StreamingRestoreEngine(fileSystem, packageModule, clock).RunAsync(
            new RestoreRequest(
                Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                packageRoot,
                destinationRoot,
                @"C:\Users\Public",
                operationRoot));

        Assert.Equal(RestoreState.Complete, restore.State);
        Assert.Equal("FROM-OLD-SSD", Encoding.UTF8.GetString(
            fileSystem.ReadBytes(collisionDestination)));
        Assert.Equal("KEEP-ME", Encoding.UTF8.GetString(fileSystem.ReadBytes(destinationOnly)));
        Assert.Equal("SOURCE-ONLY", Encoding.UTF8.GetString(
            fileSystem.ReadBytes(sourceOnlyDestination)));
    }

    [Fact]
    public async Task T34_MaliciousManifestPaths_AreRejectedBeforeAnyDestinationWrite()
    {
        var maliciousPaths = new[]
        {
            "../escape.txt",
            "data/Users/Ana/Documents/../../../../escape.txt",
            @"C:\escape.txt",
            @"C:escape.txt",
            @"\\server\share\escape.txt",
            @"\\?\C:\escape.txt",
            @"/rooted/escape.txt",
            @"data/Users/Ana/Documents\..\escape.txt",
            "data/Users/Ana/Documents/file.txt:alternate"
        };

        foreach (var maliciousPath in maliciousPaths)
        {
            var fixture = await CreateManualManifestFixtureAsync(maliciousPath);
            var sentinelPath = @"C:\outside-sentinel.txt";
            fixture.FileSystem.SeedFile(sentinelPath, Encoding.UTF8.GetBytes("UNCHANGED"));

            var result = await new StreamingRestoreEngine(
                fixture.FileSystem,
                fixture.PackageModule,
                fixture.Clock).RunAsync(fixture.RestoreRequest);

            Assert.Equal(RestoreState.Failed, result.State);
            Assert.Contains(result.Errors, error => error.Code.StartsWith(
                "path-",
                StringComparison.Ordinal));
            Assert.Equal("UNCHANGED", Encoding.UTF8.GetString(
                fixture.FileSystem.ReadBytes(sentinelPath)));
            Assert.Equal(0, fixture.FileSystem.RestoredFiles);
        }

        var reparseFixture = await CreateManualManifestFixtureAsync(
            "data/Users/Ana/Documents/link/escape.txt");
        reparseFixture.FileSystem.MarkReparseDirectory(
            @"C:\Users\Ana\Documents\link");

        var reparseResult = await new StreamingRestoreEngine(
            reparseFixture.FileSystem,
            reparseFixture.PackageModule,
            reparseFixture.Clock).RunAsync(reparseFixture.RestoreRequest);

        Assert.Equal(RestoreState.Failed, reparseResult.State);
        Assert.Contains(reparseResult.Errors, error => error.Code == "path-reparse-parent");
        Assert.Equal(0, reparseFixture.FileSystem.RestoredFiles);
    }

    private static async Task<ManualManifestFixture> CreateManualManifestFixtureAsync(
        string packageRelativePath)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 11, 16, 0, 0, TimeSpan.Zero));
        var fileSystem = new VirtualBackupFileSystem(int.MaxValue);
        var packageModule = new BackupPackageModule(fileSystem, clock);
        var packageRoot = $@"X:\Hostile\{suffix}";
        var backupId = Guid.NewGuid();
        var emptyBackup = await new StreamingBackupEngine(fileSystem, packageModule, clock).RunAsync(
            new BackupRequest(Guid.NewGuid(), packageRoot, CreateMetadata(backupId, clock.UtcNow)),
            Items());
        Assert.Equal(PackageState.Complete, emptyBackup.State);

        var rawManifest = $$"""
            {"sequence":1,"moduleId":"user-files","classification":"personalFile","sourceRelativePath":"Documents/escape.txt","packageRelativePath":"{{packageRelativePath.Replace("\\", "\\\\")}}","length":4,"creationTimeUtc":"1970-01-01T00:00:00+00:00","lastWriteTimeUtc":"1970-01-01T00:00:00+00:00","attributes":"normal","state":"copied","sha256":null}
            """ + "\n";
        fileSystem.SeedText(Path.Combine(packageRoot, "files.jsonl"), rawManifest);
        fileSystem.AllowPackageDataReads = true;
        var operationId = Guid.NewGuid();
        var request = new RestoreRequest(
            operationId,
            packageRoot,
            @"C:\Users\Ana",
            @"C:\Users\Public",
            $@"C:\ProgramData\GRBKP\operations\{operationId:N}");
        return new ManualManifestFixture(fileSystem, packageModule, clock, request);
    }

    private sealed record ManualManifestFixture(
        VirtualBackupFileSystem FileSystem,
        BackupPackageModule PackageModule,
        FixedClock Clock,
        RestoreRequest RestoreRequest);

    private static async IAsyncEnumerable<TransferItem> Items(params TransferItem[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<TransferItem> Single(TransferItem item)
    {
        yield return item;
        await Task.CompletedTask;
    }

    private static PackageMetadata CreateMetadata(Guid backupId, DateTimeOffset now) =>
        new(
            "1.0",
            "1.0.0-phase1",
            backupId,
            PackageState.Created,
            now,
            null,
            new SourceComputer("PC-ANTIGO", "E:", "Windows 11 Pro", "x64"),
            new SourceProfile("Ana", @"E:\Users\Ana", "S-1-5-21-test"),
            "standard-v1",
            ["unclassified-appdata"],
            DurableCounters.Empty,
            new IntegrityPolicy("SHA-256", true, "open-handle-length"),
            IsEncrypted: false);

    private static async IAsyncEnumerable<TransferItem> GenerateItems(
        VirtualBackupFileSystem fileSystem,
        int fileCount,
        long totalBytes)
    {
        var baseLength = totalBytes / fileCount;
        var remainder = totalBytes % fileCount;

        for (var index = 1; index <= fileCount; index++)
        {
            fileSystem.MarkItemEnumerated();
            var length = baseLength + (index <= remainder ? 1 : 0);
            yield return new TransferItem(
                index,
                "unclassified-appdata",
                $"virtual-source://{index}/{length}",
                $"data/Users/Ana/AppData/Local/file-{index:D6}.bin",
                TransferClassification.UnclassifiedApplicationData);

            if ((index & 1023) == 0)
            {
                await Task.Yield();
            }
        }
    }

    private static async IAsyncEnumerable<TransferItem> GeneratePersonalItems(
        VirtualBackupFileSystem fileSystem,
        int fileCount,
        long totalBytes)
    {
        var baseLength = totalBytes / fileCount;
        var remainder = totalBytes % fileCount;

        for (var index = 1; index <= fileCount; index++)
        {
            fileSystem.MarkItemEnumerated();
            var length = baseLength + (index <= remainder ? 1 : 0);
            yield return new TransferItem(
                index,
                "user-files",
                $"virtual-source://{index}/{length}",
                $"data/Users/Ana/Documents/file-{index:D6}.bin",
                TransferClassification.PersonalFile);
            await Task.Yield();
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class VirtualBackupFileSystem(
        int maximumAllowedAhead,
        FileSystemCapabilities? configuredCapabilities = null) : IFileSystemAdapter
    {
        private readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> virtualFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> reparseDirectories = new(StringComparer.OrdinalIgnoreCase);
        private long enumeratedItems;
        private readonly FileSystemCapabilities capabilities = configuredCapabilities ??
            new FileSystemCapabilities("NTFS", true, true);
        private bool disconnectTriggered;
        private int packageDataOpenCount;

        public long SourceOpenCount { get; private set; }

        public int PackageDataReadAttempts { get; private set; }

        public long CommittedDataFiles { get; private set; }

        public long MaximumObservedAhead { get; private set; }

        public long? DisconnectWhenCommittedFileCountIs { get; set; }

        public bool AllowPackageDataReads { get; set; }

        public bool StorePackageDataContent { get; set; }

        public int? DisconnectOnPackageDataOpenNumber { get; set; }

        public long RestoredFiles { get; private set; }

        public void MarkItemEnumerated()
        {
            enumeratedItems++;
            var ahead = enumeratedItems - CommittedDataFiles;
            MaximumObservedAhead = Math.Max(MaximumObservedAhead, ahead);
            if (ahead > maximumAllowedAhead)
            {
                throw new InvalidOperationException(
                    $"A enumeração avançou {ahead} itens sem backpressure.");
            }
        }

        public string ReadText(string path) => Encoding.UTF8.GetString(files[path]);

        public byte[] ReadBytes(string path) => files[path];

        public void SeedFile(string path, byte[] content) => files[path] = content.ToArray();

        public void SeedText(string path, string content) =>
            files[path] = Encoding.UTF8.GetBytes(content);

        public void MarkReparseDirectory(string path) => reparseDirectories.Add(path);

        public long GetVirtualLength(string path) => virtualFiles[path];

        public bool HasVirtualFile(string path) => virtualFiles.ContainsKey(path);

        public bool HasFile(string path) => files.ContainsKey(path) || virtualFiles.ContainsKey(path);

        public void Reconnect()
        {
            DisconnectWhenCommittedFileCountIs = null;
            DisconnectOnPackageDataOpenNumber = null;
            disconnectTriggered = false;
            packageDataOpenCount = 0;
        }

        public async IAsyncEnumerable<FileSystemEntry> EnumerateFilesAsync(
            string rootPath,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public IAsyncEnumerable<PathEntryInfo> EnumerateDirectoriesAsync(
            string rootPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Stream> OpenReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.StartsWith("virtual-source://", StringComparison.Ordinal))
            {
                SourceOpenCount++;
                var length = long.Parse(path[(path.LastIndexOf('/') + 1)..],
                    System.Globalization.CultureInfo.InvariantCulture);
                return ValueTask.FromResult<Stream>(new GeneratedReadStream(length));
            }

            if (IsPackageDataPath(path))
            {
                if (!AllowPackageDataReads)
                {
                    PackageDataReadAttempts++;
                    throw new InvalidOperationException("VERIFYING tentou reler conteúdo do pacote.");
                }

                packageDataOpenCount++;
                if (files.TryGetValue(path, out var storedContent))
                {
                    return ValueTask.FromResult<Stream>(new MemoryStream(
                        storedContent,
                        writable: false));
                }

                var length = virtualFiles[path];
                if (DisconnectOnPackageDataOpenNumber == packageDataOpenCount)
                {
                    return ValueTask.FromResult<Stream>(new DisconnectingReadStream(length));
                }

                return ValueTask.FromResult<Stream>(new GeneratedReadStream(length));
            }

            return ValueTask.FromResult<Stream>(new MemoryStream(files[path], writable: false));
        }

        public ValueTask<Stream> OpenWriteAsync(
            string path,
            FileWriteMode mode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPackageDataPath(path))
            {
                if (StorePackageDataContent)
                {
                    files.TryGetValue(path, out var storedContent);
                    var storedInitial = mode == FileWriteMode.Append && storedContent is not null
                        ? storedContent
                        : [];
                    return ValueTask.FromResult<Stream>(new CommitMemoryStream(
                        storedInitial,
                        mode == FileWriteMode.Append,
                        bytes => files[path] = bytes));
                }

                virtualFiles.TryGetValue(path, out var initialLength);
                if (!disconnectTriggered &&
                    DisconnectWhenCommittedFileCountIs == CommittedDataFiles)
                {
                    disconnectTriggered = true;
                    return ValueTask.FromResult<Stream>(new DisconnectingWriteStream(
                        length => virtualFiles[path] = length));
                }

                return ValueTask.FromResult<Stream>(new CountingWriteStream(
                    mode == FileWriteMode.Append ? initialLength : 0,
                    length => virtualFiles[path] = length));
            }

            files.TryGetValue(path, out var existing);
            var initial = mode == FileWriteMode.Append && existing is not null ? existing : [];
            return ValueTask.FromResult<Stream>(new CommitMemoryStream(
                initial,
                mode == FileWriteMode.Append,
                bytes => files[path] = bytes));
        }

        public ValueTask CreateDirectoryAsync(
            string path,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<PathEntryInfo?> GetInfoAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            if (reparseDirectories.Contains(path))
            {
                return ValueTask.FromResult<PathEntryInfo?>(new PathEntryInfo(
                    path,
                    PathEntryKind.Directory,
                    0,
                    IsReparsePoint: true));
            }

            if (files.TryGetValue(path, out var bytes))
            {
                return ValueTask.FromResult<PathEntryInfo?>(new PathEntryInfo(
                    path,
                    PathEntryKind.File,
                    bytes.LongLength));
            }

            if (virtualFiles.TryGetValue(path, out var length))
            {
                return ValueTask.FromResult<PathEntryInfo?>(new PathEntryInfo(
                    path,
                    PathEntryKind.File,
                    length));
            }

            return ValueTask.FromResult<PathEntryInfo?>(null);
        }

        public ValueTask MoveAsync(
            string sourcePath,
            string destinationPath,
            bool overwrite,
            CancellationToken cancellationToken = default)
        {
            if (virtualFiles.Remove(sourcePath, out var length))
            {
                virtualFiles[destinationPath] = length;
                if (IsPackageDataPath(destinationPath) &&
                    !destinationPath.Contains(".grbkp.partial.", StringComparison.Ordinal))
                {
                    CommittedDataFiles++;
                }

                return ValueTask.CompletedTask;
            }

            if (files.Remove(sourcePath, out var bytes))
            {
                files[destinationPath] = bytes;
                if (IsPackageDataPath(destinationPath) &&
                    !destinationPath.Contains(".grbkp.partial.", StringComparison.Ordinal))
                {
                    CommittedDataFiles++;
                }

                if (IsRestoredDestination(destinationPath) &&
                    !destinationPath.Contains(".grbkp.partial.", StringComparison.Ordinal))
                {
                    RestoredFiles++;
                }

                return ValueTask.CompletedTask;
            }

            throw new FileNotFoundException("Origem da movimentação não existe.", sourcePath);
        }

        public ValueTask DeleteAsync(
            string path,
            bool recursive,
            CancellationToken cancellationToken = default)
        {
            files.Remove(path);
            virtualFiles.Remove(path);
            return ValueTask.CompletedTask;
        }

        public ValueTask<FileSystemCapabilities> GetCapabilitiesAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(capabilities);

        public ValueTask SetMetadataAsync(
            string path,
            FileMetadata metadata,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        private static bool IsPackageDataPath(string path) =>
            path.Contains("\\data\\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/data/", StringComparison.OrdinalIgnoreCase);

        private static bool IsRestoredDestination(string path) =>
            path.StartsWith("C:\\Users\\", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DisconnectingReadStream(long length) : Stream
    {
        private long position;
        private bool failed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get; } = length;
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failed)
            {
                throw new StorageDisconnectedException("O pacote foi desconectado durante a leitura.");
            }

            var read = (int)Math.Min(Math.Max(1, buffer.Length / 2), Length - position);
            position += read;
            failed = true;
            return ValueTask.FromResult(read);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DisconnectingWriteStream(Action<long> commit) : Stream
    {
        private long length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position
        {
            get => length;
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            length += Math.Max(1, buffer.Length / 2);
            throw new StorageDisconnectedException("A mídia de destino foi desconectada.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                commit(length);
            }

            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => length = value;
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new StorageDisconnectedException("A mídia de destino foi desconectada.");
    }

    private sealed class GeneratedReadStream(long length) : Stream
    {
        private long position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get; } = length;
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, Length - position);
            position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, Length - position);
            position += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingWriteStream(long initialLength, Action<long> commit) : Stream
    {
        private long length = initialLength;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position
        {
            get => length;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => length += count;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            length += buffer.Length;
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                commit(length);
            }

            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => length = value;
    }

    private sealed class CommitMemoryStream : MemoryStream
    {
        public CommitMemoryStream(byte[] initial, bool append, Action<byte[]> commit)
        {
            this.commit = commit;
            Write(initial);
            if (append)
            {
                Position = Length;
            }
            else
            {
                Position = 0;
                SetLength(0);
            }
        }

        private readonly Action<byte[]> commit;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                commit(ToArray());
            }

            base.Dispose(disposing);
        }
    }
}
