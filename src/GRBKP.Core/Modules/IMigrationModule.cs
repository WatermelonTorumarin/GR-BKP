namespace GRBKP.Core.Modules;

public interface IMigrationModule
{
    string Id { get; }

    Task<ModuleDiscovery> DiscoverAsync(MigrationContext context, CancellationToken ct);

    IAsyncEnumerable<TransferItem> EnumerateBackupAsync(
        ModuleDiscovery discovery,
        CancellationToken ct);

    Task<ModuleResult> RestoreAsync(RestoreContext context, CancellationToken ct);

    Task<ValidationResult> ValidateAsync(ValidationContext context, CancellationToken ct);
}
