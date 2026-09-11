using GRBKP.Core.Modules;
using GRBKP.Core.Operations;

namespace GRBKP.Core.Packages;

public sealed record PackageMetadata(
    string FormatVersion,
    string ApplicationVersion,
    Guid BackupId,
    PackageState Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    SourceComputer Source,
    SourceProfile Profile,
    string SelectionRule,
    IReadOnlyList<string> Modules,
    DurableCounters Counters,
    IntegrityPolicy Integrity,
    bool IsEncrypted,
    string? FilesManifestSha256 = null);

public sealed record SourceComputer(
    string MachineName,
    string Volume,
    string WindowsVersion,
    string Architecture);

public sealed record SourceProfile(string Name, string OriginalPath, string? Sid);

public sealed record IntegrityPolicy(
    string Algorithm,
    bool HashCriticalFiles,
    string CommonFileValidation);

public sealed record FileManifestEntry(
    long Sequence,
    string ModuleId,
    TransferClassification Classification,
    string SourceRelativePath,
    string PackageRelativePath,
    long Length,
    DateTimeOffset CreationTimeUtc,
    DateTimeOffset LastWriteTimeUtc,
    FileAttributes Attributes,
    FileTransferState State,
    string? Sha256);

public enum FileTransferState
{
    Copied,
    Ignored,
    Failed
}

public sealed record JournalEntry(
    DateTimeOffset TimestampUtc,
    Guid OperationId,
    string EventType,
    string Outcome,
    long? Sequence = null,
    string? ModuleId = null,
    string? RelativePath = null,
    string? EvidenceCode = null,
    string? Message = null);
