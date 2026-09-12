using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GRBKP.Core.Adapters;
using GRBKP.Core.Operations;

namespace GRBKP.Core.Packages;

public sealed class BackupPackageModule(IFileSystemAdapter fileSystem, IClock clock)
{
    internal static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();
    internal static JsonSerializerOptions JsonLineOptions { get; } = CreateJsonLineOptions();

    public async Task<BackupPackageWriter> CreateWriterAsync(
        string packageRoot,
        Guid operationId,
        PackageMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        ArgumentNullException.ThrowIfNull(metadata);

        await fileSystem.CreateDirectoryAsync(packageRoot, cancellationToken).ConfigureAwait(false);
        foreach (var directory in new[] { "data", "modules", "reports", "resume" })
        {
            await fileSystem.CreateDirectoryAsync(
                Path.Combine(packageRoot, directory),
                cancellationToken).ConfigureAwait(false);
        }

        var filesStream = await fileSystem.OpenWriteAsync(
            Path.Combine(packageRoot, "files.jsonl"),
            FileWriteMode.CreateOrOverwrite,
            cancellationToken).ConfigureAwait(false);
        var journalStream = await fileSystem.OpenWriteAsync(
            Path.Combine(packageRoot, "journal.jsonl"),
            FileWriteMode.CreateOrOverwrite,
            cancellationToken).ConfigureAwait(false);

        var checkpoint = new OperationCheckpoint(
            operationId,
            metadata.BackupId,
            OperationKind.Backup,
            PackageState.Created.ToString(),
            null,
            0,
            null,
            DurableCounters.Empty,
            UpdatedAtUtc: clock.UtcNow);

        var writer = new BackupPackageWriter(
            fileSystem,
            clock,
            packageRoot,
            metadata,
            checkpoint,
            filesStream,
            journalStream,
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256));
        await writer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return writer;
    }

    public async Task<BackupPackageWriter> OpenWriterAsync(
        string packageRoot,
        CancellationToken cancellationToken = default)
    {
        var metadata = await ReadMetadataAsync(packageRoot, cancellationToken).ConfigureAwait(false);
        var checkpoint = await ReadOperationAsync(packageRoot, cancellationToken).ConfigureAwait(false);
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var manifestPath = Path.Combine(packageRoot, "files.jsonl");
        await using (var existingManifest = await fileSystem.OpenReadAsync(
            manifestPath,
            cancellationToken).ConfigureAwait(false))
        {
            var buffer = new byte[128 * 1024];
            while (true)
            {
                var read = await existingManifest.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }
        }

        var filesStream = await fileSystem.OpenWriteAsync(
            manifestPath,
            FileWriteMode.Append,
            cancellationToken).ConfigureAwait(false);
        var journalStream = await fileSystem.OpenWriteAsync(
            Path.Combine(packageRoot, "journal.jsonl"),
            FileWriteMode.Append,
            cancellationToken).ConfigureAwait(false);
        var writer = new BackupPackageWriter(
            fileSystem,
            clock,
            packageRoot,
            metadata,
            checkpoint,
            filesStream,
            journalStream,
            hash);
        await writer.AppendJournalAsync(new JournalEntry(
            clock.UtcNow,
            checkpoint.OperationId,
            "operation-resumed",
            "success"), cancellationToken).ConfigureAwait(false);
        return writer;
    }

    public async Task<PackageMetadata> ReadMetadataAsync(
        string packageRoot,
        CancellationToken cancellationToken = default) =>
        await ReadJsonAsync<PackageMetadata>(
            Path.Combine(packageRoot, "package.json"),
            cancellationToken).ConfigureAwait(false);

    public async Task<OperationCheckpoint> ReadOperationAsync(
        string packageRoot,
        CancellationToken cancellationToken = default) =>
        await ReadJsonAsync<OperationCheckpoint>(
            Path.Combine(packageRoot, "operation.json"),
            cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<FileManifestEntry> ReadFilesAsync(
        string packageRoot,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await using var stream = await fileSystem.OpenReadAsync(
            Path.Combine(packageRoot, "files.jsonl"),
            cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            FileManifestEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<FileManifestEntry>(line, JsonLineOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Linha inválida em files.jsonl.", exception);
            }

            if (entry is null)
            {
                throw new InvalidDataException("Entrada nula em files.jsonl.");
            }

            PackagePath.ValidateRelative(entry.PackageRelativePath);
            yield return entry;
        }
    }

    private async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = await fileSystem.OpenReadAsync(path, cancellationToken)
            .ConfigureAwait(false);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return value ?? throw new InvalidDataException($"JSON vazio ou inválido: {path}");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static JsonSerializerOptions CreateJsonLineOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed class BackupPackageWriter : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly IFileSystemAdapter fileSystem;
    private readonly IClock clock;
    private readonly string packageRoot;
    private readonly Stream filesStream;
    private readonly Stream journalStream;
    private readonly IncrementalHash filesHash;
    private PackageMetadata metadata;
    private OperationCheckpoint checkpoint;
    private bool disposed;

    internal BackupPackageWriter(
        IFileSystemAdapter fileSystem,
        IClock clock,
        string packageRoot,
        PackageMetadata metadata,
        OperationCheckpoint checkpoint,
        Stream filesStream,
        Stream journalStream,
        IncrementalHash filesHash)
    {
        this.fileSystem = fileSystem;
        this.clock = clock;
        this.packageRoot = packageRoot;
        this.metadata = metadata;
        this.checkpoint = checkpoint;
        this.filesStream = filesStream;
        this.journalStream = journalStream;
        this.filesHash = filesHash;
    }

    public PackageMetadata Metadata => metadata;

    public OperationCheckpoint Checkpoint => checkpoint;

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await WriteMetadataAsync(cancellationToken).ConfigureAwait(false);
        await WriteOperationAsync(cancellationToken).ConfigureAwait(false);
        await AppendJournalAsync(new JournalEntry(
            clock.UtcNow,
            checkpoint.OperationId,
            "operation-created",
            "success"), cancellationToken).ConfigureAwait(false);
    }

    public async Task TransitionAsync(
        PackageState state,
        CancellationToken cancellationToken = default)
    {
        metadata = metadata with { Status = state };
        checkpoint = checkpoint with
        {
            State = state.ToString(),
            UpdatedAtUtc = clock.UtcNow
        };
        await WriteMetadataAsync(cancellationToken).ConfigureAwait(false);
        await WriteOperationAsync(cancellationToken).ConfigureAwait(false);
        await AppendJournalAsync(new JournalEntry(
            clock.UtcNow,
            checkpoint.OperationId,
            "state-changed",
            state.ToString().ToLowerInvariant()), cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendCommittedFileAsync(
        FileManifestEntry entry,
        DurableCounters counters,
        CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(entry, BackupPackageModule.JsonLineOptions) + "\n";
        var bytes = Utf8.GetBytes(line);
        await filesStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        filesHash.AppendData(bytes);
        await filesStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        checkpoint = checkpoint with
        {
            LastConfirmedSequence = entry.Sequence,
            LastConfirmedPath = entry.PackageRelativePath,
            Counters = counters,
            UpdatedAtUtc = clock.UtcNow
        };
        metadata = metadata with { Counters = counters };
        await WriteOperationAsync(cancellationToken).ConfigureAwait(false);
        await AppendJournalAsync(new JournalEntry(
            clock.UtcNow,
            checkpoint.OperationId,
            "file-committed",
            "success",
            entry.Sequence,
            entry.ModuleId,
            entry.PackageRelativePath), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Registra de forma durável que um arquivo NÃO pôde ser copiado, sem escrevê-lo em
    /// files.jsonl — o manifesto precisa listar apenas o que realmente existe dentro do pacote,
    /// senão a restauração tentaria copiar um arquivo ausente. A sequência avança mesmo assim
    /// para que uma retomada não fique repetindo o mesmo arquivo problemático indefinidamente.
    /// </summary>
    public async Task AppendFailedFileAsync(
        long sequence,
        string moduleId,
        string packageRelativePath,
        string reason,
        DurableCounters counters,
        CancellationToken cancellationToken = default)
    {
        checkpoint = checkpoint with
        {
            LastConfirmedSequence = sequence,
            LastConfirmedPath = packageRelativePath,
            Counters = counters,
            UpdatedAtUtc = clock.UtcNow
        };
        metadata = metadata with { Counters = counters };
        await WriteOperationAsync(cancellationToken).ConfigureAwait(false);
        await AppendJournalAsync(new JournalEntry(
            clock.UtcNow,
            checkpoint.OperationId,
            "file-failed",
            reason,
            sequence,
            moduleId,
            packageRelativePath), cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendJournalAsync(
        JournalEntry entry,
        CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(entry, BackupPackageModule.JsonLineOptions) + "\n";
        var bytes = Utf8.GetBytes(line);
        await journalStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await journalStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync(
        DurableCounters counters,
        CancellationToken cancellationToken = default)
    {
        await TransitionAsync(PackageState.Verifying, cancellationToken).ConfigureAwait(false);
        await filesStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await journalStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var hash = Convert.ToHexString(filesHash.GetHashAndReset());
        metadata = metadata with
        {
            Status = PackageState.Complete,
            FinishedAtUtc = clock.UtcNow,
            Counters = counters,
            FilesManifestSha256 = hash
        };
        checkpoint = checkpoint with
        {
            State = PackageState.Complete.ToString(),
            Counters = counters,
            UpdatedAtUtc = clock.UtcNow
        };
        await WriteMetadataAsync(cancellationToken).ConfigureAwait(false);
        await WriteOperationAsync(cancellationToken).ConfigureAwait(false);
        await AppendJournalAsync(new JournalEntry(
            clock.UtcNow,
            checkpoint.OperationId,
            "state-changed",
            "complete"), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        filesHash.Dispose();
        await filesStream.DisposeAsync().ConfigureAwait(false);
        await journalStream.DisposeAsync().ConfigureAwait(false);
    }

    private Task WriteMetadataAsync(CancellationToken cancellationToken) =>
        WriteJsonAtomicallyAsync(
            Path.Combine(packageRoot, "package.json"),
            metadata,
            cancellationToken);

    private Task WriteOperationAsync(CancellationToken cancellationToken) =>
        WriteJsonAtomicallyAsync(
            Path.Combine(packageRoot, "operation.json"),
            checkpoint,
            cancellationToken);

    private async Task WriteJsonAtomicallyAsync<T>(
        string destinationPath,
        T value,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + $".grbkp.partial.{checkpoint.OperationId:N}";
        await using (var stream = await fileSystem.OpenWriteAsync(
            temporaryPath,
            FileWriteMode.CreateOrOverwrite,
            cancellationToken).ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                value,
                BackupPackageModule.JsonOptions,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await fileSystem.MoveAsync(
            temporaryPath,
            destinationPath,
            overwrite: true,
            cancellationToken).ConfigureAwait(false);
    }
}
