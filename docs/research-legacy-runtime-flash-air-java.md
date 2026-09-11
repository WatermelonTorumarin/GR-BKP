# Pesquisa: Flash, Adobe AIR e Java legados (baseline Windows 11 Pro)

Fonte: pesquisa em documentação oficial (Adobe, Microsoft, Oracle, HARMAN/Wipro). Relevante para as Fases 3/5 (migração de aplicativos legados encontrados no SSD antigo).

## Conclusão executiva

| Componente legado | Decisão recomendada |
|---|---|
| `flash.exe` / Adobe Flash Player | Proibido instalar ou executar automaticamente. Preservar apenas como artefato em quarentena, se necessário para auditoria. |
| Instalador antigo do Adobe AIR | Não instalar por padrão. Substituir por runtime atual mantido pela HARMAN/Wipro, somente quando uma aplicação aprovada realmente depender dele. |
| Java/JRE antigo | Não reinstalar a cópia antiga. Descobrir a versão principal exigida pela aplicação e instalar a atualização de segurança mais recente e certificada dessa linha, respeitando licenciamento. |

Um instalador antigo ainda abrir ou retornar sucesso no Windows 11 **não** o torna suportado, seguro ou funcional.

## Adobe Flash Player

- Fim de vida: Adobe encerrou suporte em 31/12/2020, bloqueou execução de conteúdo a partir de 12/01/2021, retirou downloads e recomenda desinstalar. Cópias fora dos canais oficiais são fonte comum de malware. ([Adobe — EOL do Flash](https://www.adobe.com/products/flashplayer/end-of-life-alternative.html))
- Microsoft removeu o Flash dos navegadores e do Windows; políticas de Flash no Edge são obsoletas desde as versões 87/88. ([Microsoft — ciclo de vida do Flash](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies))
- Windows 11 nunca fez parte da lista de sistemas certificados (lançado depois do fim do produto).
- `flash.exe` não identifica com segurança o arquivo (pode ser projetor Flash, app renomeado, ou malicioso). Sempre inspecionar `ProductName`/`OriginalFilename`/versão/assinatura Authenticode/SHA-256 **sem executar**.
- Se houver dependência de negócio real, a única opção mantida é comercial: soluções isoladas com navegador empacotado da HARMAN/Wipro — projeto de exceção, não função genérica do GRBKP. ([HARMAN/Wipro — FAQ AIR](https://airsdk.harman.com/))

## Adobe AIR

- Suporte transferido da Adobe para HARMAN após dez/2020. Documentação atual da Adobe aponta para o SDK novo da HARMAN.
- `AdobeAIRInstaller.exe` antigo: sem suporte, não certificado para Windows 11, pode ter falhas já corrigidas, pode sofrer bloqueio de assinatura/reputação.
- Recomendação atual: empacotar com runtime cativo (bundle); pacotes que dependem de baixar o shared runtime são "strongly discouraged" (instaladores AIR 33+ não baixam mais runtimes automaticamente).
- Toolchain atual suporta Windows 7+ (32/64 bits) — existe linha atual usável no Windows 11, mas não garante compatibilidade de cada app/extensão nativa (ANE) antiga.

**Regra para o GRBKP:**
1. Detectar qual aplicativo depende do AIR.
2. Tentar runtime compartilhado atual da HARMAN somente se o fornecedor suportar.
3. Preferir versão do app empacotada pelo fornecedor com runtime cativo.
4. Testar app completo (WebView2, gráficos, impressão, ANE).
5. Manter instalador antigo só como evidência/quarentena, nunca como fallback automático.
6. Confirmar licença/redistribuição antes de incluir runtime no pacote do GRBKP.

## Java / JRE antigo

Diferente do Flash: ainda existem linhas mantidas (inclusive Java 8 LTS). O problema é reinstalar uma build antiga e vulnerável.

- Oracle recomenda remover versões antigas e testar primeiro com a versão atual; se a app exigir versão velha, pedir atualização ao fornecedor.
- Certificação Windows 11 x64: Oracle JDK/JRE 8 só a partir do **8u311**; JDK 17 a partir do **17.0.1**.
- Linhas LTS: 8, 11, 17, 21, 25 — cada app deve ser validada na linha indicada pelo fornecedor. Não instalar "o mais novo" cegamente (JavaFX, Web Start, componentes removidos podem quebrar app antigo).
- Applets: sem suporte desde abril/2019; Edge não suporta ActiveX/plug-in Java; IE Mode classifica só como "known-to-work", não suportado como plataforma moderna.
- **Licenciamento**: Oracle JDK/JRE 8, 11 e updates recentes do 17 exigem licença paga para uso comercial interno (uso pessoal é diferente). Confirmar assinatura Oracle, direito do fornecedor do app, ou usar OpenJDK suportado.
- Recomendação Oracle: aplicações modernas devem embarcar runtime customizado próprio, não depender de JRE global na máquina.

## Bloqueios gerais do Windows 11 Pro

- Apps Win32 de 32 bits funcionam via WOW64; componentes de **16 bits não são suportados** (alguns instaladores muito antigos).
- Mesmo um EXE x86 compatível pode ser bloqueado por: Smart App Control, SmartScreen, App Control for Business (EXE/MSI/scripts/DLL), UAC (elevação exigida).
- **O GRBKP não deve desativar Defender, Smart App Control, UAC ou políticas corporativas** para forçar instalador legado.

## Baseline de validação recomendada (antes de qualquer execução automática)

1. Nunca executar diretamente um EXE encontrado no backup ou em `Program Files`.
2. Obter produto/versão/arquitetura/editor sem iniciar o arquivo.
3. Calcular SHA-256 e comparar com catálogo interno aprovado (`Get-FileHash`).
4. Exigir assinatura Authenticode válida e editor exato da allowlist (`Get-AuthenticodeSignature`) — nome do arquivo não é evidência.
5. Preferir download atual do fabricante, não a cópia herdada.
6. Confirmar arquitetura, versão mínima certificada, licença e parâmetros silenciosos oficialmente documentados.
7. Testar primeiro em **Windows Sandbox** (Windows 11 Pro tem nativo), de preferência sem rede/clipboard/pastas do host.
8. Após instalar: verificar versão efetiva + smoke test da app dependente (estar em "Aplicativos instalados" não prova funcionamento).
9. Gerar resultado explícito: instalado / já compatível / bloqueado por política / assinatura inválida / não certificado / licença não confirmada / requer intervenção.
10. Nunca guardar ou enviar automaticamente instaladores EOL para o computador novo.

**Política resumida:** Flash = denylist absoluta. AIR = instalação condicional (só runtime HARMAN atual ou pacote cativo aprovado). Java = instalação condicional por aplicativo, linha principal fixada, patch atual, assinatura/hash verificados, licença confirmada.
