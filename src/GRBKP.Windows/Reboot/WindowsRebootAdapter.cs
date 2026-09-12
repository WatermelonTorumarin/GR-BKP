using GRBKP.Core.Adapters;

namespace GRBKP.Windows.Reboot;

/// <summary>
/// Implementacao real do mecanismo de reinicio automatico + retomada pos-boot (RestoreState
/// RebootPending → PostBootValidation, ver RestoreStateMachine). NAO esta conectado ao fluxo
/// principal do app hoje — por decisao do responsavel, a Fase 5 desta passada usa reinicio manual
/// (o tecnico reinicia quando quiser, sem retomada automatica). Esta classe existe como a
/// alternativa ja pronta pra ligar no futuro, se a empresa decidir que vale o risco/complexidade
/// de automatizar o reboot.
///
/// ScheduleResumeAsync/RemoveResumeTaskAsync sao verificados de verdade contra o Agendador de
/// Tarefas real do Windows (ver WindowsRebootAdapterTests) — criar/remover uma tarefa agendada e
/// seguro e reversivel, sem nenhum efeito ate um logon acontecer. RequestRebootAsync (que executa
/// "shutdown /r") NUNCA e testado contra a implementacao real — chamar de verdade reiniciaria a
/// maquina que estiver rodando o teste. So e verificado com um fake de IWindowsCommandAdapter, que
/// confirma o comando e os argumentos exatos sem executa-los.
/// </summary>
public sealed class WindowsRebootAdapter(IWindowsCommandAdapter commandAdapter) : IRebootAdapter
{
    private const string ResumeArgument = "--resume-restore";
    private const int RebootDelaySeconds = 60;

    public static string TaskNameFor(Guid operationId) => $"GRBKP-Resume-{operationId:N}";

    public async Task ScheduleResumeAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Não foi possível determinar o executável atual.");

        var result = await commandAdapter.ExecuteApprovedAsync(
            new WindowsCommandRequest(
                ApprovedWindowsCommand.SchTasks,
                [
                    "/Create", "/TN", TaskNameFor(operationId),
                    "/TR", $"\"{exePath}\" {ResumeArgument} {operationId:N}",
                    "/SC", "ONLOGON", "/RL", "HIGHEST", "/F"
                ]),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Não foi possível agendar a retomada pós-boot (schtasks código {result.ExitCode}): " +
                result.StandardError);
        }
    }

    public async Task RequestRebootAsync(CancellationToken cancellationToken = default)
    {
        var result = await commandAdapter.ExecuteApprovedAsync(
            new WindowsCommandRequest(
                ApprovedWindowsCommand.Shutdown,
                [
                    "/r", "/t", RebootDelaySeconds.ToString(),
                    "/c", "GRBKP: reiniciando para concluir a migração"
                ]),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Não foi possível agendar o reinício (shutdown código {result.ExitCode}): " +
                result.StandardError);
        }
    }

    public async Task RemoveResumeTaskAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        // Melhor esforco e idempotente: se a tarefa ja nao existir, ou schtasks falhar por
        // qualquer motivo, nao interrompe a finalizacao da restauracao.
        await commandAdapter.ExecuteApprovedAsync(
            new WindowsCommandRequest(
                ApprovedWindowsCommand.SchTasks,
                ["/Delete", "/TN", TaskNameFor(operationId), "/F"]),
            cancellationToken).ConfigureAwait(false);
    }
}
