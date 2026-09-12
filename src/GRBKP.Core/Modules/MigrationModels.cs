using GRBKP.Core.Adapters;
using GRBKP.Core.Operations;
using GRBKP.Core.Packages;

namespace GRBKP.Core.Modules;

public sealed record MigrationContext(
    string SourceRoot,
    string ProfileName,
    IFileSystemAdapter FileSystem);

public sealed record ModuleDiscovery(
    string ModuleId,
    bool IsApplicable,
    IReadOnlyDictionary<string, string?> Facts);

public sealed record TransferItem(
    long Sequence,
    string ModuleId,
    string SourcePath,
    string PackageRelativePath,
    TransferClassification Classification,
    bool RequiresSha256 = false,
    string? SourceRelativePath = null,
    FileMetadata? Metadata = null,
    /// <summary>
    /// Tamanho conhecido na hora da descoberta. Serve para somar o total ANTES de copiar e assim
    /// mostrar uma barra de progresso de verdade ("3,4 GB de 8,9 GB") em vez de só um indicador
    /// girando sem fim.
    /// </summary>
    long Length = 0);

public enum TransferClassification
{
    PersonalFile,
    PublicFile,
    ApplicationData,
    UnclassifiedApplicationData,
    ModuleData
}

public sealed record RestoreContext(
    Guid OperationId,
    string PackageRoot,
    string DestinationProfileRoot,
    IFileSystemAdapter FileSystem,
    OperationCheckpoint Checkpoint);

public sealed record ModuleResult(
    string ModuleId,
    ModuleOutcome Outcome,
    IReadOnlyList<MigrationEvidence> Evidence,
    string? DiagnosticPath = null);

public enum ModuleOutcome
{
    Applied,
    PreservedNotRestored,
    NotApplicable,
    Failed
}

public sealed record ValidationContext(
    Guid OperationId,
    string DestinationPath,
    IFileSystemAdapter FileSystem);

public sealed record ValidationResult(
    bool IsValid,
    IReadOnlyList<MigrationEvidence> Evidence,
    string? ErrorCode = null);

public sealed record MigrationEvidence(string Code, string Message, EvidenceSeverity Severity);

public enum EvidenceSeverity
{
    Information,
    Warning,
    Error
}
