using System.ComponentModel;
using System.Diagnostics;
using GRBKP.Core.Adapters;

namespace GRBKP.Windows.Processes;

/// <summary>
/// Encerra a força, sem tentar fechamento gracioso primeiro (decisão Q64 do levantamento com
/// o responsável: "feche o processo à força já na primeira etapa antes de solicitar normalmente").
/// O SSD de destino é sempre uma máquina limpa preparada pela empresa — não há trabalho do
/// usuário em aberto que precise ser salvo antes da restauração sobrescrever o perfil do app.
/// </summary>
public sealed class WindowsProcessAdapter : IProcessAdapter
{
    public Task<IReadOnlyList<string>> GetRunningProcessesAsync(
        IReadOnlyCollection<string> processNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processNames);
        cancellationToken.ThrowIfCancellationRequested();

        var running = new List<string>();
        foreach (var processName in processNames)
        {
            var alive = false;
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        // Um processo recém-encerrado ainda aparece na lista do Windows por um
                        // instante. Avisar o técnico de que "o Outlook está aberto" quando ele
                        // acabou de fechar seria ruído — e, pior, faria a checagem parecer errada.
                        alive |= !process.HasExited;
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException or Win32Exception)
                    {
                        // Sem permissão para inspecionar (processo de outro usuário/sistema):
                        // ele existe, então conta como aberto.
                        alive = true;
                    }
                }
            }

            if (alive)
            {
                running.Add(processName);
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(running);
    }

    public async Task TerminateProcessTreesAsync(
        IReadOnlyCollection<string> processNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processNames);

        foreach (var processName in processNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        // O processo ja tinha terminado entre a listagem e a tentativa de encerrar.
                    }
                    catch (Win32Exception)
                    {
                        // Sem permissao para encerrar. Nao mascarar o problema: a copia seguinte
                        // vai falhar de forma visivel ao tentar sobrescrever um arquivo travado,
                        // em vez de este Adapter mentir que o processo foi fechado.
                    }
                }
            }
        }
    }
}
