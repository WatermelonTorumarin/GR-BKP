using GRBKP.Core.Operations;

namespace GRBKP.Core.Adapters;

public interface IProcessAdapter
{
    Task TerminateProcessTreesAsync(
        IReadOnlyCollection<string> processNames,
        CancellationToken cancellationToken = default);
}

public interface IWindowsServiceAdapter
{
    Task StopAsync(string serviceName, CancellationToken cancellationToken = default);

    Task StartAsync(string serviceName, CancellationToken cancellationToken = default);
}

public interface IRegistryAdapter
{
    Task<IReadOnlyDictionary<string, string?>> ReadValuesAsync(
        RegistryReadRequest request,
        CancellationToken cancellationToken = default);
}

public interface IWindowsCommandAdapter
{
    Task<WindowsCommandResult> ExecuteApprovedAsync(
        WindowsCommandRequest request,
        CancellationToken cancellationToken = default);
}

public interface IPrinterAdapter
{
    Task<IReadOnlyList<PrinterDescriptor>> DiscoverAsync(
        CancellationToken cancellationToken = default);
}

public interface IRebootAdapter
{
    Task ScheduleResumeAsync(Guid operationId, CancellationToken cancellationToken = default);

    Task RequestRebootAsync(CancellationToken cancellationToken = default);

    Task RemoveResumeTaskAsync(Guid operationId, CancellationToken cancellationToken = default);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed record RegistryReadRequest(
    RegistryHiveKind Hive,
    string KeyPath,
    IReadOnlyCollection<string> ValueNames);

public enum RegistryHiveKind
{
    CurrentUser,
    LocalMachine,
    OfflineUser,
    OfflineLocalMachine
}

public sealed record WindowsCommandRequest(
    ApprovedWindowsCommand Command,
    IReadOnlyList<string> Arguments);

public enum ApprovedWindowsCommand
{
    Dism,
    PnpUtil,
    PrintBrm,
    PowerCfg
}

public sealed record WindowsCommandResult(int ExitCode, string StandardOutput, string StandardError);

public sealed record PrinterDescriptor(string Name, string DriverName, string? PortName);
