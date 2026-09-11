# Pesquisa: remoção do OneDrive e do app Copilot

Fonte: documentação oficial Microsoft atual. Relevante para a Fase 5 (aba "Deseja desinstalar OneDrive e Copilot?").

## OneDrive

Remoção automática suportada por:
```powershell
winget uninstall --id Microsoft.OneDrive --exact --silent `
  --disable-interactivity --source winget --accept-source-agreements
```
ou `OneDriveSetup.exe /uninstall`. O instalador pode ser **por usuário** (`%LocalAppData%`) ou **por computador** (`Program Files`) — o GRBKP precisa detectar o escopo antes de agir.

Fontes: [desinstalação](https://support.microsoft.com/en-us/onedrive/turn-off-disable-or-uninstall-onedrive), [comando do instalador](https://learn.microsoft.com/en-us/troubleshoot/sharepoint/installation-and-setup/how-to-block-onedrive-from-being-advertised-after-install-office-2016), [WinGet uninstall](https://learn.microsoft.com/en-us/windows/package-manager/winget/uninstall), [instalação por computador](https://learn.microsoft.com/en-us/sharepoint/per-machine-installation)

Para manter desativado: policy `DisableFileSyncNGSC=1` em `HKLM\SOFTWARE\Policies\Microsoft\Windows\OneDrive`. Interrompe sync, esconde do Explorador, bloqueia acesso por apps. Documentado para Windows 10 1703+ e Windows 11 Pro/Enterprise/Education/IoT Enterprise. ([policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-system#disableonedrivefilesync))

**Cuidado antes de remover:** arquivos "somente online" são placeholders sem conteúdo local; Área de Trabalho/Documentos/Imagens podem estar redirecionadas para dentro do OneDrive; pode haver arquivos locais ainda não sincronizados. **O GRBKP não deve apagar automaticamente nenhuma pasta residual do OneDrive.** ([Files On-Demand](https://learn.microsoft.com/en-us/sharepoint/files-on-demand-windows), [Known Folder Move](https://learn.microsoft.com/en-us/sharepoint/use-group-policy#prevent-users-from-moving-their-windows-known-folders-to-onedrive))

## Copilot (app independente)

Remoção do usuário atual:
```powershell
$package = Get-AppxPackage -Name 'Microsoft.Copilot'
$package | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName }
```
Para todos os perfis: `Get-AppxPackage -AllUsers` + `Remove-AppxPackage -AllUsers` (requer admin). Para impedir instalação em perfis **futuros**, remover também o pacote provisionado: `Remove-AppxProvisionedPackage` — **isso não remove o app de perfis já existentes**, é uma operação separada.

Fontes: [gerenciamento atual do Copilot](https://learn.microsoft.com/en-us/windows/client-management/manage-windows-copilot#remove-or-prevent-installation-of-the-microsoft-copilot-app), [Remove-AppxPackage](https://learn.microsoft.com/en-us/powershell/module/appx/remove-appxpackage), [Remove-AppxProvisionedPackage](https://learn.microsoft.com/en-us/powershell/module/dism/remove-appxprovisionedpackage)

A policy antiga `TurnOffWindowsCopilot` está **depreciada**, não controla a experiência nova. Para impedir reinstalação: AppLocker com o pacote `MICROSOFT.COPILOT` (aplicável em Windows 10 2004+ com KB5024351 e todas edições do Windows 11). ([policy antiga](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-windowsai#turnoffwindowscopilot), [requisitos AppLocker](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/requirements-to-use-applocker))

Em domínio, via Edge Update (Windows 10 22H2 / Windows 11 23H2+):
```
HKLM\SOFTWARE\Policies\Microsoft\EdgeUpdate
Install{C50565E9-CCCF-44B4-BA15-5AC5C6569197}   = 0
Uninstall{C50565E9-CCCF-44B4-BA15-5AC5C6569197} = 1   (2 também apaga dados do app)
```
Executa na próxima verificação do Edge Update — **não é imediato**. ([policy](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-update-policies#uninstall-copilot))

**Importante:** app independente Copilot, Copilot no Edge, e Copilot no Office/M365 são experiências **distintas** (em unificação, mas ainda separadas hoje). Remover `Microsoft.Copilot` **não** remove o Copilot do Edge ou do Office — confirma a decisão já tomada no projeto (Q28: só o app, Edge/Office ficam de fora).

## Sequência recomendada para o GRBKP

1. Verificar backup e possíveis redirecionamentos/placeholders do OneDrive.
2. Remover Copilot dos perfis existentes + provisionamento futuro.
3. Aplicar política preventiva (AppLocker) se aplicável.
4. Desinstalar OneDrive conforme escopo (usuário vs. computador).
5. Aplicar `DisableFileSyncNGSC`.
6. **Nunca excluir pastas de dados residuais automaticamente.**

Isso corresponde ao estado `AwaitingCleanupChoice → ApplyingCorporateCleanup` já modelado em `RestoreStateMachine` — falta só a implementação real (Fase 5).
