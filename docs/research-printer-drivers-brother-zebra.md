# Pesquisa: drivers de impressora (Brother/Zebra) e filas do Windows

Fonte: documentação oficial Microsoft/Brother/Zebra. Relevante para a Fase 4 (drivers + impressão).

## Conclusão principal — três coisas diferentes

1. **Pacote do driver** (INF + catálogo + binários) → vive no **Driver Store**.
2. **Objetos de impressão do Windows**: fila, nome, porta/IP, compartilhamento, driver associado, preferências.
3. **Aplicativos/configurações do fabricante**: Brother ControlCenter/iPrint&Scan, Zebra Setup Utilities, config gravada na própria impressora.

**Copiar `Program Files`/`Program Files (x86)` não reinstala corretamente nenhum dos três.** Driver → DISM/PnPUtil. Filas/portas → PrintBRM ou PrintManagement. Utilitários → instaladores oficiais.

## Fonte: Windows ainda funcionando (origem com boot)

```powershell
Get-Printer -Full
Get-PrinterDriver
Get-PrinterPort
Get-PrintConfiguration -PrinterName "<fila>"

pnputil /enum-drivers /files /format CSV /output-file drivers.csv
```
`Get-PrintConfiguration` preserva `PrintTicketXml` (papel, cor, duplex etc.). ([Get-Printer](https://learn.microsoft.com/en-us/powershell/module/printmanagement/get-printer), [Get-PrinterDriver](https://learn.microsoft.com/en-us/powershell/module/printmanagement/get-printerdriver), [Get-PrinterPort](https://learn.microsoft.com/en-us/powershell/module/printmanagement/get-printerport), [Set-PrintConfiguration](https://learn.microsoft.com/en-us/powershell/module/printmanagement/set-printconfiguration))

Para alta fidelidade, com `PrintBrm.exe` presente:
```
%windir%\System32\spool\tools\PrintBrm.exe -b -f backup.printerExport
...
PrintBrm.exe -r -f backup.printerExport -o force
```
Inclui drivers, filas, portas, formulários, monitores de linguagem, processadores. Suporta `-nobin`, mapeamento de driver antigo→novo via `BrmConfig.xml`, extração/reempacotamento, omissão de ACLs entre domínios. ([sintaxe PrintBRM](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2012-R2-and-2012/jj134237%28v%3Dws.11%29))

**Recomendação GRBKP:** gerar dois artefatos — `printerExport` completo (máxima fidelidade) + manifesto JSON próprio (só filas Brother/Zebra: portas, IP, driver, arquitetura, versão, `PrintTicketXml`) para validação e reconstrução controlada. O PrintBRM completo pode conter filas irrelevantes — restaurar seletivamente exige extrair/editar/reempacotar ou reconstrução via PowerShell.

## SSD com Windows offline (sem boot)

```powershell
Get-WindowsDriver -Path "E:\"
Export-WindowsDriver -Path "E:\" -Destination "D:\Backup\Drivers"
```
ou `dism /Image:E:\ /Get-Drivers` / `/Export-Driver`. Inventário traz `oem#.inf`, INF original, classe, provedor, catálogo, versão, estado de assinatura. ([Get-WindowsDriver](https://learn.microsoft.com/en-us/powershell/module/dism/get-windowsdriver), [Export-WindowsDriver](https://learn.microsoft.com/en-us/powershell/module/dism/export-windowsdriver), [opções DISM](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-driver-servicing-command-line-options-s14))

Filtragem: Brother → `ProviderName` contendo "Brother" (preservar também classes scanner/imagem, não só Printer — o pacote pode ter WIA/TWAIN); Zebra → `ProviderName` contendo "Zebra"/"ZDesigner". Se ambos existirem, guardar ambos.

**Limitação importante: PrintBRM não aceita a raiz de um Windows offline** — origem precisa ser spooler local ou servidor por nome. De um SSD sem boot dá pra recuperar os pacotes INF, mas **não há fluxo Microsoft oficialmente suportado** para reconstruir com fidelidade total filas/portas/preferências direto dos hives offline.

Salvaguarda forense possível (mas não um backup PrintBRM suportado): copiar `Windows\System32\config\SYSTEM`, `...\SOFTWARE`, `Windows\System32\spool`. Interpretar isso seria rotina própria do GRBKP, sujeita a diferenças de versão.

## Reinstalação automática no destino

```powershell
pnputil /add-driver "D:\Backup\Drivers\*.inf" /subdirs /install
```
Adiciona ao Driver Store e instala/atualiza dispositivos compatíveis. Exige elevação. **Não usar `/reboot`** — acumular necessidade de reinício e fazer um único reboot planejado no fim (drivers + filas + utilitários juntos). **Nunca `/ForceUnsigned` em produção.**

Limitações: `/install` não força driver com classificação inferior ao que o Windows já considera melhor; reenumerar e validar qual driver ficou associado; pacote precisa casar arquitetura/versão do Windows; DISM/PnPUtil trabalham com INF, não MSI/EXE. ([PnPUtil](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax))

Drivers x64 modo kernel exigem assinatura; **rejeitar pacote não assinado ou inválido**, preferir pacote atual do fabricante se o exportado for incompatível. ([assinatura kernel](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-requirements--windows-vista-and-later-))

PrintBRM tem incompatibilidade conhecida x86→x64 (monitores de linguagem precisam mesma arquitetura) — instalar driver da arquitetura de destino e mapear antigo→novo.

## Brother

- Driver INF exportado **não instala** ControlCenter, iPrint&Scan, scanner TWAIN/WIA, PC-FAX.
- Pacote oficial: **"Full Driver & Software Package"** por modelo (ex.: [MFC-L2750DW](https://support.brother.com/g/b/downloadlist.aspx?c=us&lang=en&os=10072&prod=mfcl2750dw_us_eu_as)).
- **Fluxo recomendado:** INF exportado = fallback offline; pacote oficial completo (modelo + Windows/arquitetura) = caminho preferencial (driver + scanner + ControlCenter); depois restaurar fila/porta/config; validar impressão e digitalização.
- Brother oferece "Driver Deployment Wizard" (cria EXE autônomo com mesmas configs) — mas precisa ser criado com a instalação antiga ainda funcional, e não substitui o pacote completo do ControlCenter.
- ControlCenter4 permite exportar/importar "ajustes da máquina" via Remote Setup — mas **não há mecanismo oficial genérico para exportar automaticamente todos os perfis locais do ControlCenter4**. Copiar o AppData ajuda a preservar dados, mas reinstalar o software continua obrigatório; validar destino "Scan to File" (formato/resolução/OCR/botões) após instalar.

## Zebra

- Driver ZDesigner (certificado Microsoft) + Zebra Setup Utilities (ZSU) instalam driver e configuram USB/serial/Ethernet/Wi-Fi/Bluetooth.
- `PrnInst.exe` existe mas **não há CLI silenciosa estável oficialmente documentada** para todas as versões.
- **Recomendação para automação:** (1) instalar INF assinado via PnPUtil; (2) recriar fila e porta; (3) aplicar config da fila; (4) instalar ZSU separadamente só quando necessário.
- Config interna da etiquetadora é separada da fila do Windows: ZSU permite salvar config ao fim do assistente; impressoras Link-OS geram/restauram `.zprofile` via API (`CreateBackup`/`LoadBackup`); impressoras ZPL aceitam arquivo de config reutilizável. **Esses backups exigem comunicação com a impressora durante a captura — não recuperáveis só do SSD offline.**

## Implicações para o GRBKP

- `Program Files` serve só para **detectar** que Brother/Zebra Utilities estavam instalados (e talvez achar instalador original) — não é fonte autoritativa do driver.
- Fonte autoritativa: **Driver Store**, via DISM/PnPUtil.
- Backup online: PrintBRM + manifesto próprio + INF.
- SSD offline: só INF, registrar explicitamente que filas/portas/preferências não têm restauração oficialmente suportada com fidelidade total.
- Instalar automaticamente só pacotes assinados e compatíveis; preferir driver oficial atual do modelo, usar exportado como fallback.
- Instalar Full Driver & Software Package da Brother para ControlCenter/scanner.
- Zebra: preservar separadamente config da fila do Windows e config interna da etiquetadora.
- **Validação obrigatória no fim:** driver efetivamente associado, porta/IP, página de teste, digitalização Brother, etiqueta de teste Zebra.
