using GRBKP.Core.Adapters;

namespace GRBKP.Core.Modules.CorporateCleanup;

public enum OneDriveInstallScope
{
    PerMachine,
    PerUser
}

public sealed record OneDriveInstallation(OneDriveInstallScope Scope, string SetupExecutablePath);

public sealed record CorporateCleanupResult(
    OneDriveInstallation? OneDrive,
    bool OneDriveUninstallSucceeded,
    bool CopilotRemovalSucceeded,
    IReadOnlyList<string> Messages);

/// <summary>
/// Limpeza corporativa pos-restauracao: desinstala o OneDrive e remove o app Copilot do
/// computador de DESTINO (nao tem nada a ver com o que foi capturado do computador antigo — por
/// isso, diferente de FileZilla/drivers, nao ha etapa de captura, so aplicacao direta no destino).
/// Executado por escolha do tecnico (caixa de marcacao na tela de restauracao, ligada por padrao —
/// decisao da empresa) apos a restauracao do perfil terminar com sucesso.
///
/// Por decisao explicita da pesquisa (ver docs/research-onedrive-copilot-removal.md): NUNCA apaga
/// pastas de dados residuais do OneDrive automaticamente (podem ter arquivos "somente online"
/// ainda nao baixados, ou Area de Trabalho/Documentos redirecionados pra dentro do OneDrive) — só
/// desinstala o programa em si. Remove o Copilot dos perfis existentes e do provisionamento futuro
/// (Copilot no Edge/Office fica de fora — decisao Q28, experiencias distintas do app independente).
/// </summary>
public sealed class CorporateCleanupService(
    IFileSystemAdapter fileSystem,
    IExternalUninstallerAdapter uninstallerAdapter,
    IWindowsCommandAdapter commandAdapter)
{
    private static readonly string[] PerMachineProgramFilesSegments =
        ["Program Files", "Microsoft OneDrive", "OneDriveSetup.exe"];
    private static readonly string[] PerMachineProgramFilesX86Segments =
        ["Program Files (x86)", "Microsoft OneDrive", "OneDriveSetup.exe"];
    private static readonly string[] PerUserSegments =
        ["AppData", "Local", "Microsoft", "OneDrive", "OneDriveSetup.exe"];

    // Script fixo, sem nenhuma interpolacao de entrada externa — o mesmo raciocinio de seguranca
    // de "so binarios/scripts conhecidos do proprio codigo" de WindowsCommandAdapter se aplica.
    private const string CopilotRemovalScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        Get-AppxPackage -AllUsers -Name 'Microsoft.Copilot' | Remove-AppxPackage -AllUsers
        Get-AppxProvisionedPackage -Online |
            Where-Object { $_.DisplayName -eq 'Microsoft.Copilot' } |
            Remove-AppxProvisionedPackage -Online
        """;

    public async Task<OneDriveInstallation?> DetectOneDriveAsync(
        string destinationVolumeRoot,
        string destinationProfileRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationVolumeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationProfileRoot);

        // Por-computador tem prioridade sobre por-usuario: se ambos existirem (reinstalacao
        // anterior deixou os dois), o instalador por-computador e o que realmente controla.
        foreach (var segments in (string[][])
            [PerMachineProgramFilesSegments, PerMachineProgramFilesX86Segments])
        {
            var candidatePath = Path.Combine([destinationVolumeRoot, .. segments]);
            if (await fileSystem.GetInfoAsync(candidatePath, cancellationToken).ConfigureAwait(false)
                is not null)
            {
                return new OneDriveInstallation(OneDriveInstallScope.PerMachine, candidatePath);
            }
        }

        var perUserPath = Path.Combine([destinationProfileRoot, .. PerUserSegments]);
        if (await fileSystem.GetInfoAsync(perUserPath, cancellationToken).ConfigureAwait(false)
            is not null)
        {
            return new OneDriveInstallation(OneDriveInstallScope.PerUser, perUserPath);
        }

        return null;
    }

    public async Task<CorporateCleanupResult> ApplyAsync(
        string destinationVolumeRoot,
        string destinationProfileRoot,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();

        var oneDrive = await DetectOneDriveAsync(
            destinationVolumeRoot, destinationProfileRoot, cancellationToken).ConfigureAwait(false);
        var oneDriveSucceeded = false;
        if (oneDrive is not null)
        {
            var uninstallResult = await uninstallerAdapter.RunUninstallerAsync(
                oneDrive.SetupExecutablePath, ["/uninstall"], cancellationToken).ConfigureAwait(false);
            oneDriveSucceeded = uninstallResult.ExitCode == 0;
            messages.Add(oneDriveSucceeded
                ? $"OneDrive ({oneDrive.Scope}) desinstalado."
                : $"Falha ao desinstalar OneDrive ({oneDrive.Scope}): código {uninstallResult.ExitCode}.");
        }
        else
        {
            messages.Add("OneDrive não estava instalado neste computador — nenhuma ação necessária.");
        }

        var copilotResult = await commandAdapter.ExecuteApprovedAsync(
            new WindowsCommandRequest(
                ApprovedWindowsCommand.PowerShell,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", CopilotRemovalScript]),
            cancellationToken).ConfigureAwait(false);
        var copilotSucceeded = copilotResult.ExitCode == 0;
        messages.Add(copilotSucceeded
            ? "Copilot removido dos perfis existentes e do provisionamento futuro."
            : $"Falha ao remover o Copilot: código {copilotResult.ExitCode}.");

        return new CorporateCleanupResult(oneDrive, oneDriveSucceeded, copilotSucceeded, messages);
    }
}
