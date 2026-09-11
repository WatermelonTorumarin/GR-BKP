namespace GRBKP.Core.Operations;

public enum OperationKind
{
    Backup,
    Restore
}

public enum PackageState
{
    Created,
    Inspecting,
    Copying,
    Verifying,
    Complete,
    Incomplete,
    Paused,
    Failed
}

public enum RestoreState
{
    Preflight,
    Confirmed,
    ForceClosing,
    RestoringUserData,
    RestoringAppModules,
    RestoringFileZilla,
    RestoringPrinters,
    ApplyingMachineSettings,
    CoreMigrationApplied,
    AwaitingCleanupChoice,
    ApplyingCorporateCleanup,
    CleanupSkipped,
    RebootPending,
    PostBootValidation,
    Complete,
    PhysicalValidationPending,
    Failed,
    Paused
}

public sealed record DurableCounters(
    long CopiedFiles,
    long CopiedBytes,
    long FailedFiles,
    long IgnoredFiles)
{
    public static DurableCounters Empty { get; } = new(0, 0, 0, 0);
}

public sealed record OperationCheckpoint(
    Guid OperationId,
    Guid BackupId,
    OperationKind Kind,
    string State,
    string? ResumeState,
    long LastConfirmedSequence,
    string? LastConfirmedPath,
    DurableCounters Counters,
    string? PendingModuleId = null,
    string? PendingModulePhase = null,
    bool ResumeTaskPending = false,
    DateTimeOffset? UpdatedAtUtc = null);
