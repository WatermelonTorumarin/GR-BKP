namespace GRBKP.Core.Adapters;

public interface IFileSystemAdapter
{
    IAsyncEnumerable<FileSystemEntry> EnumerateFilesAsync(
        string rootPath,
        CancellationToken cancellationToken = default);

    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);

    ValueTask<Stream> OpenWriteAsync(
        string path,
        FileWriteMode mode,
        CancellationToken cancellationToken = default);

    ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    ValueTask<PathEntryInfo?> GetInfoAsync(string path, CancellationToken cancellationToken = default);

    ValueTask MoveAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        string path,
        bool recursive,
        CancellationToken cancellationToken = default);

    ValueTask<FileSystemCapabilities> GetCapabilitiesAsync(
        string path,
        CancellationToken cancellationToken = default);

    ValueTask SetMetadataAsync(
        string path,
        FileMetadata metadata,
        CancellationToken cancellationToken = default);
}

public sealed record FileSystemEntry(
    string FullPath,
    long Length,
    DateTimeOffset CreationTimeUtc,
    DateTimeOffset LastWriteTimeUtc,
    FileAttributes Attributes,
    bool IsCloudPlaceholder = false,
    bool IsReparsePoint = false);

public sealed record PathEntryInfo(
    string FullPath,
    PathEntryKind Kind,
    long Length,
    bool IsReparsePoint = false);

public sealed record FileMetadata(
    DateTimeOffset CreationTimeUtc,
    DateTimeOffset LastWriteTimeUtc,
    FileAttributes Attributes);

public sealed record FileSystemCapabilities(
    string Format,
    bool SupportsLargeFiles,
    bool SupportsAtomicRename,
    bool IsRemote = false);

public enum PathEntryKind
{
    File,
    Directory
}

public enum FileWriteMode
{
    CreateNew,
    CreateOrOverwrite,
    Append
}
