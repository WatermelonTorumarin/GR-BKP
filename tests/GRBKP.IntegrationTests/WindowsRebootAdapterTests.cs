using GRBKP.Core.Adapters;
using GRBKP.Windows.Commands;
using GRBKP.Windows.Reboot;

namespace GRBKP.IntegrationTests;

/// <summary>
/// WindowsRebootAdapter e a trilha alternativa (nao conectada ao fluxo principal do app ainda —
/// ver comentario na classe) de reinicio automatico + retomada pos-boot. ScheduleResumeAsync e
/// RemoveResumeTaskAsync sao seguros de verificar contra o Agendador de Tarefas REAL do Windows
/// (criar/consultar/remover uma tarefa nao tem nenhum efeito ate um logon acontecer, e o teste
/// remove a tarefa antes de terminar). RequestRebootAsync NUNCA e chamado contra a implementacao
/// real aqui — reiniciaria de verdade a maquina rodando o teste — e so e verificado com um fake de
/// IWindowsCommandAdapter.
/// </summary>
public sealed class WindowsRebootAdapterTests
{
    /// <summary>
    /// "/RL HIGHEST" exige elevacao — o processo de teste (dotnet test) nao roda elevado, so o
    /// GRBKP.exe real (via app.manifest) roda. Igual ao teste do DISM (WindowsCommandAdapterTests):
    /// aceitamos "acesso negado" como prova de que o adaptador genuinamente tentou o comando certo
    /// contra o schtasks.exe real; a criacao/consulta/remocao de fato so e verificada quando o
    /// processo de teste por acaso estiver elevado.
    /// </summary>
    [Fact]
    public async Task ScheduleResumeAsync_CreatesRealScheduledTask_ThenRemoveResumeTaskAsync_DeletesIt()
    {
        var operationId = Guid.NewGuid();
        var taskName = WindowsRebootAdapter.TaskNameFor(operationId);
        var adapter = new WindowsRebootAdapter(new WindowsCommandAdapter());
        var queryAdapter = new WindowsCommandAdapter();

        try
        {
            await adapter.ScheduleResumeAsync(operationId, CancellationToken.None);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains(
            "Acesso negado", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            // "/FO LIST" evita o formato de tabela padrao do schtasks, que TRUNCA a coluna de
            // nome da tarefa (o codigo de producao nunca depende desse parsing — so este teste).
            var queryAfterCreate = await queryAdapter.ExecuteApprovedAsync(
                new WindowsCommandRequest(
                    ApprovedWindowsCommand.SchTasks, ["/Query", "/TN", taskName, "/FO", "LIST"]),
                CancellationToken.None);
            Assert.Equal(0, queryAfterCreate.ExitCode);
            Assert.Contains(taskName, queryAfterCreate.StandardOutput);
        }
        finally
        {
            await adapter.RemoveResumeTaskAsync(operationId, CancellationToken.None);
        }

        var queryAfterDelete = await queryAdapter.ExecuteApprovedAsync(
            new WindowsCommandRequest(ApprovedWindowsCommand.SchTasks, ["/Query", "/TN", taskName]),
            CancellationToken.None);
        Assert.NotEqual(0, queryAfterDelete.ExitCode);
    }

    [Fact]
    public async Task RequestRebootAsync_RequestsShutdownWithDelayAndReason_ButNeverRunsForReal()
    {
        var commandAdapter = new RecordingCommandAdapter();
        var adapter = new WindowsRebootAdapter(commandAdapter);

        await adapter.RequestRebootAsync(CancellationToken.None);

        var request = Assert.Single(commandAdapter.Requests);
        Assert.Equal(ApprovedWindowsCommand.Shutdown, request.Command);
        Assert.Equal("/r", request.Arguments[0]);
        Assert.Equal("/t", request.Arguments[1]);
        Assert.Equal("60", request.Arguments[2]);
        Assert.Equal("/c", request.Arguments[3]);
    }

    [Fact]
    public async Task ScheduleResumeAsync_RequestsOnLogonTaskAtHighestPrivilege_ViaFake()
    {
        var commandAdapter = new RecordingCommandAdapter();
        var adapter = new WindowsRebootAdapter(commandAdapter);
        var operationId = Guid.NewGuid();

        await adapter.ScheduleResumeAsync(operationId, CancellationToken.None);

        var request = Assert.Single(commandAdapter.Requests);
        Assert.Equal(ApprovedWindowsCommand.SchTasks, request.Command);
        Assert.Equal("/Create", request.Arguments[0]);
        Assert.Contains(WindowsRebootAdapter.TaskNameFor(operationId), request.Arguments);
        Assert.Contains("ONLOGON", request.Arguments);
        Assert.Contains("HIGHEST", request.Arguments);
        var launchCommand = Assert.Single(
            request.Arguments, argument => argument.Contains("--resume-restore", StringComparison.Ordinal));
        Assert.Contains(operationId.ToString("N"), launchCommand);
    }

    private sealed class RecordingCommandAdapter : IWindowsCommandAdapter
    {
        public List<WindowsCommandRequest> Requests { get; } = [];

        public Task<WindowsCommandResult> ExecuteApprovedAsync(
            WindowsCommandRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new WindowsCommandResult(0, "ok", string.Empty));
        }
    }
}
