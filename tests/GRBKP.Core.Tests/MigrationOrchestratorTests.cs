using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Orchestration;
using GRBKP.Core.Packages;
using GRBKP.Core.Transfers;

namespace GRBKP.Core.Tests;

public sealed class MigrationOrchestratorTests
{
    [Fact]
    public async Task StartsFreshBackup_ThenAutoDetectsAndResumesAfterDisconnection_WithoutCallerTrackingState()
    {
        var fileSystem = new InMemoryOrchestratorFileSystem();
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var packageModule = new BackupPackageModule(fileSystem, clock);
        var packageRoot = @"X:\GRBKP_Orchestrator\2026-09-11";
        var backupId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var metadata = CreateMetadata(backupId, clock.UtcNow);

        // Primeira chamada: nao existe package.json ainda -> orquestrador deve iniciar do zero.
        fileSystem.DisconnectAfterDataWrites = 2;
        var firstOrchestrator = new MigrationOrchestrator(
            fileSystem,
            packageModule,
            new StreamingBackupEngine(fileSystem, packageModule, clock));
        var firstOperationId = Guid.Parse("66666666-6666-6666-6666-666666666666");

        var first = await firstOrchestrator.RunOrResumeBackupAsync(
            new BackupRequest(firstOperationId, packageRoot, metadata),
            Items(startSequence: 1, count: 4));

        Assert.Equal(OrchestratedBackupOutcome.Started, first.Outcome);
        Assert.Equal(PackageState.Paused, first.Result.State);
        Assert.Equal(2, first.Result.Counters.CopiedFiles);

        // Segunda chamada: instancia nova (nenhum estado em memoria compartilhado), package.json ja existe
        // no destino -> o orquestrador precisa detectar sozinho que deve retomar, sem o chamador
        // precisar saber disso.
        fileSystem.DisconnectAfterDataWrites = null;
        var secondOrchestrator = new MigrationOrchestrator(
            fileSystem,
            new BackupPackageModule(fileSystem, clock),
            new StreamingBackupEngine(fileSystem, new BackupPackageModule(fileSystem, clock), clock));

        var second = await secondOrchestrator.RunOrResumeBackupAsync(
            new BackupRequest(firstOperationId, packageRoot, metadata),
            Items(startSequence: 1, count: 4));

        Assert.Equal(OrchestratedBackupOutcome.Resumed, second.Outcome);
        Assert.Equal(PackageState.Complete, second.Result.State);
        Assert.Equal(4, second.Result.Counters.CopiedFiles);

        var status = await secondOrchestrator.GetBackupStatusAsync(packageRoot);
        Assert.Equal(PackageState.Complete.ToString(), status.State);
        Assert.Equal(4, status.Counters.CopiedFiles);
    }

    private static async IAsyncEnumerable<TransferItem> Items(long startSequence, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var sequence = startSequence + i;
            var content = $"conteudo-{sequence}";
            yield return new TransferItem(
                sequence,
                "user-files",
                $"virtual-source://{sequence}?content={content}",
                $"data/Users/Ana/Documents/file{sequence}.txt",
                TransferClassification.PersonalFile);
            await Task.CompletedTask;
        }
    }

    private static PackageMetadata CreateMetadata(Guid backupId, DateTimeOffset now) => new(
        "1.0",
        "1.0.0-fase1",
        backupId,
        PackageState.Created,
        now,
        null,
        new SourceComputer("ORCH-TEST", "F:", "Windows 11 Pro", "x64"),
        new SourceProfile("Ana", @"F:\Users\Ana", null),
        "orchestrator-test",
        ["user-files"],
        DurableCounters.Empty,
        new IntegrityPolicy("SHA256", false, "size-and-handle"),
        IsEncrypted: false);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class InMemoryOrchestratorFileSystem : IFileSystemAdapter
    {
        private readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
        private int dataWritesCompleted;

        public int? DisconnectAfterDataWrites { get; set; }

        public IAsyncEnumerable<FileSystemEntry> EnumerateFilesAsync(
            string rootPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Stream> OpenReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            if (path.StartsWith("virtual-source://", StringComparison.Ordinal))
            {
                var contentMarker = "?content=";
                var index = path.IndexOf(contentMarker, StringComparison.Ordinal);
                var content = index >= 0 ? path[(index + contentMarker.Length)..] : string.Empty;
                return ValueTask.FromResult<Stream>(new MemoryStream(
                    System.Text.Encoding.UTF8.GetBytes(content),
                    writable: false));
            }

            if (!files.TryGetValue(path, out var bytes))
            {
                throw new FileNotFoundException(path);
            }

            return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }

        public ValueTask<Stream> OpenWriteAsync(
            string path,
            FileWriteMode mode,
            CancellationToken cancellationToken = default)
        {
            var isDataFile = path.Contains(
                Path.Combine("data", ""),
                StringComparison.OrdinalIgnoreCase) &&
                path.Contains(".grbkp.partial.", StringComparison.Ordinal);
            if (isDataFile && DisconnectAfterDataWrites is { } limit && dataWritesCompleted >= limit)
            {
                throw new StorageDisconnectedException("Mídia removida durante a cópia (simulado).");
            }

            var existing = mode == FileWriteMode.Append && files.TryGetValue(path, out var current)
                ? current
                : [];
            return ValueTask.FromResult<Stream>(
                new CommittingWriteStream(existing, committed =>
                {
                    files[path] = committed;
                    if (isDataFile)
                    {
                        dataWritesCompleted++;
                    }
                }));
        }

        public ValueTask CreateDirectoryAsync(
            string path,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<PathEntryInfo?> GetInfoAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(files.ContainsKey(path)
                ? new PathEntryInfo(path, PathEntryKind.File, files[path].Length)
                : null);

        public ValueTask MoveAsync(
            string sourcePath,
            string destinationPath,
            bool overwrite,
            CancellationToken cancellationToken = default)
        {
            if (!files.TryGetValue(sourcePath, out var bytes))
            {
                throw new FileNotFoundException(sourcePath);
            }

            files[destinationPath] = bytes;
            files.Remove(sourcePath);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(
            string path,
            bool recursive,
            CancellationToken cancellationToken = default)
        {
            files.Remove(path);
            return ValueTask.CompletedTask;
        }

        public ValueTask<FileSystemCapabilities> GetCapabilitiesAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FileSystemCapabilities("NTFS", true, true));

        public ValueTask SetMetadataAsync(
            string path,
            FileMetadata metadata,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        private sealed class CommittingWriteStream : MemoryStream
        {
            private readonly Action<byte[]> onCommitted;
            private bool committed;

            public CommittingWriteStream(byte[] initial, Action<byte[]> onCommitted)
            {
                this.onCommitted = onCommitted;
                if (initial.Length > 0)
                {
                    Write(initial, 0, initial.Length);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !committed)
                {
                    committed = true;
                    onCommitted(ToArray());
                }

                base.Dispose(disposing);
            }
        }
    }
}
