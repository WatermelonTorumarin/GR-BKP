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

/// <summary>
/// Le uma chave e suas subchaves diretamente do ARQUIVO de um hive de registro — nunca monta
/// nada no registro ao vivo do Windows (sem RegLoadKey/RegUnLoadKey). Existe especificamente
/// para ler o registro de um SSD antigo desmontado (ex.: <c>ProfileList</c> do
/// <c>SOFTWARE</c> hive em <c>F:\Windows\System32\config\SOFTWARE</c>), onde nao ha registro
/// ao vivo pra consultar.
/// </summary>
public interface IOfflineRegistryHiveReader
{
    Task<IReadOnlyList<OfflineRegistrySubKey>> EnumerateSubKeysAsync(
        string hiveFilePath,
        string keyPath,
        IReadOnlyCollection<string> valueNames,
        CancellationToken cancellationToken = default);
}

public sealed record OfflineRegistrySubKey(
    string Name,
    IReadOnlyDictionary<string, string?> Values);

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
