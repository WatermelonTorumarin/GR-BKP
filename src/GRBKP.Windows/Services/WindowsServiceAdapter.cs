using System.ServiceProcess;
using GRBKP.Core.Adapters;

namespace GRBKP.Windows.Services;

public sealed class WindowsServiceAdapter : IWindowsServiceAdapter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task StopAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        using var controller = new ServiceController(serviceName);
        if (controller.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }

        controller.Stop();
        await Task.Run(
            () => controller.WaitForStatus(ServiceControllerStatus.Stopped, Timeout),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        using var controller = new ServiceController(serviceName);
        if (controller.Status == ServiceControllerStatus.Running)
        {
            return;
        }

        controller.Start();
        await Task.Run(
            () => controller.WaitForStatus(ServiceControllerStatus.Running, Timeout),
            cancellationToken).ConfigureAwait(false);
    }
}
