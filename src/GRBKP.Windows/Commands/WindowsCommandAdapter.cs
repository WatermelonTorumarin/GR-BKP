using System.Diagnostics;
using GRBKP.Core.Adapters;

namespace GRBKP.Windows.Commands;

/// <summary>
/// Executa apenas os quatro comandos aprovados de <see cref="ApprovedWindowsCommand"/>, sempre
/// pelo caminho absoluto em System32 (nunca resolvido via PATH) e sempre com argumentos passados
/// como lista, nunca como string de shell — nao ha superficie para injecao de comando, e nunca
/// executa nada que nao seja um destes quatro binarios do proprio Windows.
/// </summary>
public sealed class WindowsCommandAdapter : IWindowsCommandAdapter
{
    public async Task<WindowsCommandResult> ExecuteApprovedAsync(
        WindowsCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var executablePath = ResolveExecutablePath(request.Command);
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Não foi possível iniciar {executablePath}.");

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);

        return new WindowsCommandResult(process.ExitCode, standardOutput, standardError);
    }

    private static string ResolveExecutablePath(ApprovedWindowsCommand command)
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return command switch
        {
            ApprovedWindowsCommand.Dism => Path.Combine(system32, "Dism.exe"),
            ApprovedWindowsCommand.PnpUtil => Path.Combine(system32, "pnputil.exe"),
            ApprovedWindowsCommand.PrintBrm => Path.Combine(system32, "spool", "tools", "PrintBrm.exe"),
            ApprovedWindowsCommand.PowerCfg => Path.Combine(system32, "powercfg.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
        };
    }
}
