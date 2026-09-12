using System.Diagnostics;
using GRBKP.Core.Adapters;

namespace GRBKP.Windows.Commands;

/// <summary>
/// Roda um desinstalador cujo caminho so e conhecido em tempo de execucao (ex.:
/// "OneDriveSetup.exe" — ver <c>CorporateCleanupService</c>). O chamador e responsavel por ja ter
/// confirmado que <paramref name="verifiedExecutablePath"/> existe antes de chamar; este adaptador
/// nunca resolve nada por PATH e nunca passa por um shell — sempre caminho absoluto, argumentos em
/// lista.
/// </summary>
public sealed class WindowsExternalUninstallerAdapter : IExternalUninstallerAdapter
{
    public async Task<WindowsCommandResult> RunUninstallerAsync(
        string verifiedExecutablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedExecutablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(verifiedExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Não foi possível iniciar {verifiedExecutablePath}.");

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);

        return new WindowsCommandResult(process.ExitCode, standardOutput, standardError);
    }
}
