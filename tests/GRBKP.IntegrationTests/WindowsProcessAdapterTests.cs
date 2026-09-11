using System.Diagnostics;
using GRBKP.Windows.Processes;

namespace GRBKP.IntegrationTests;

public sealed class WindowsProcessAdapterTests
{
    [Fact]
    public async Task TerminateProcessTreesAsync_KillsMatchingProcess()
    {
        // Usa uma copia com nome unico (guid) de um utilitario inofensivo do Windows que fica
        // esperando entrada — nunca o nome real de um processo do sistema/usuario, para nao
        // correr nenhum risco de encerrar algo que nao foi criado por este teste.
        using var fixture = new UniqueDummyProcess();
        var adapter = new WindowsProcessAdapter();

        Assert.False(fixture.Process.HasExited);

        await adapter.TerminateProcessTreesAsync([fixture.ProcessName], CancellationToken.None);

        fixture.Process.Refresh();
        Assert.True(fixture.Process.HasExited);
    }

    [Fact]
    public async Task TerminateProcessTreesAsync_DoesNothing_WhenNoProcessMatchesTheName()
    {
        var adapter = new WindowsProcessAdapter();

        await adapter.TerminateProcessTreesAsync(
            [$"grbkp-nao-existe-{Guid.NewGuid():N}"],
            CancellationToken.None);

        // Nao lanca excecao — ausencia de processo correspondente e um caso normal, nao uma falha.
    }

    private sealed class UniqueDummyProcess : IDisposable
    {
        private readonly string tempExecutablePath;

        public UniqueDummyProcess()
        {
            ProcessName = $"grbkp-test-{Guid.NewGuid():N}";
            tempExecutablePath = Path.Combine(
                Path.GetTempPath(),
                $"{ProcessName}.exe");
            File.Copy(
                Path.Combine(Environment.SystemDirectory, "choice.exe"),
                tempExecutablePath);

            Process = Process.Start(new ProcessStartInfo(tempExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
        }

        public string ProcessName { get; }

        public Process Process { get; }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    Process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
            }

            Process.Dispose();

            try
            {
                File.Delete(tempExecutablePath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Arquivo ainda pode estar com handle preso por um instante apos o processo
                // encerrar (Windows libera o handle do executavel de forma assincrona); nao e
                // critico para o resultado do teste, que ja validou o encerramento do processo.
            }
        }
    }
}
