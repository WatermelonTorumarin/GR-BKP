# Pesquisa: migração do FileZilla Server (0.9.x → atual)

Fonte: documentação oficial FileZilla/FileZilla Pro e Microsoft. Relevante para a Fase 4 (FileZilla + impressão/scan).

## Compatibilidade e migração do `FileZilla Server.xml`

- FileZilla Server clássico **0.9.x está descontinuado**, sem correções de segurança.
- Desde o **FileZilla Server 1.9**, `Server → Import Configuration` aceita configurações do 0.9.x oficialmente. O `FileZilla Server.xml` recuperado do SSD antigo deve ser **preservado como artefato e importado pela interface atual** — não deve substituir diretamente os XMLs internos da versão nova.
- Usuários, grupos e configurações principais migram. Permissões amplas (leitura/escrita/exclusão) migram, mas **controles granulares antigos por diretório** (ex.: impedir listagem para um usuário específico) podem não ter correspondência exata — revisão pós-importação necessária.
- **A pasta física de scan não está dentro do XML como conteúdo** — precisa ser copiada separadamente, e o caminho nativo precisa existir no computador novo.

Fontes: [export/import 0.9.x→1.9+](https://filezillapro.com/docs/server/advanced-options/how-to-export-and-import-server-configurations/), [migração 0.9.x, diffs de permissão](https://filezillapro.com/docs/server/filezilla-pro-enterprise-server/upgrade-from-filezilla-server-09x-to-pro-enterprise-server/)

## Onde ficam as configurações (versões atuais)

- Serviço como `SYSTEM` (padrão): `%PROGRAMDATA%\filezilla-server`
- Serviço sob outra conta: `%LOCALAPPDATA%\filezilla-server` dessa conta
- Interface administrativa: `%LOCALAPPDATA%\filezilla-server-gui` do usuário
- `--config-dir` pode alterar o diretório do serviço

**O GRBKP não deve assumir só `ProgramData`** — deve consultar a conta e o `ImagePath` do serviço `filezilla-server`, procurando `--config-dir`.

Fontes: [locais oficiais](https://filezillapro.com/docs/server/faq-server/filezilla-servers-configuration-files/), [`--config-dir`](https://filezillapro.com/docs/server/faq-server/how-to-change-configuration-files-are-stored/)

## Instalação silenciosa — lacuna documental real

- Instalação oficial descrita só via assistente interativo.
- Histórico oficial confirma existência de "silent installations" e correções específicas nelas, mas **não publica sintaxe completa e estável dos parâmetros**.
- `/S /user=all` é documentado para o **cliente** FileZilla Pro, não para o Server — não reutilizar como prova para o servidor.

**Conclusão:** fixar uma versão específica do instalador do Server e validar parâmetros em teste de integração antes de automatizar. Até lá, não afirmar que existe CLI silenciosa oficialmente documentada.

Depois de instalado, controle oficial do serviço:
```
net start filezilla-server
net stop filezilla-server
```

Fontes: [instalação oficial](https://filezillapro.com/docs/server/basic-usage-instructions-server/how-to-install-filezilla-server-on-windows/), [histórico de versões](https://filezillapro.com/filezilla-pro-enterprise-server-version-history/), [controle do serviço](https://filezillapro.com/docs/server/basic-usage-instructions-server/starting-stopping-filezilla-server/)

Não há CLI oficialmente documentada para importar o XML antigo de forma não interativa. `SC.exe control filezilla-server paramchange` só recarrega config atual já válida, não converte formato 0.9.x. ([recarregar config](https://filezillapro.com/docs/server/advanced-options/how-to-reload-server-configuration/))

## Usuários, grupos, permissões, pasta de Scan

Após importar, verificar:
- usuário da impressora habilitado;
- associação/ordem dos grupos (**definição do usuário prevalece sobre grupo; entre grupos, prevalece o primeiro na ordem**);
- mount point virtual (normalmente `/`);
- caminho nativo existe no Windows novo;
- conceder só o necessário — `Write Only` para conta exclusiva de scan é preferível a `Read+Write`;
- **permissões NTFS podem restringir além do que o FileZilla concede**.

Fontes: [usuários](https://filezillapro.com/docs/server/advanced-options/filezilla-server-users-panel/), [grupos/ordem](https://filezillapro.com/docs/server/advanced-options/how-to-add-a-user-to-a-group/), [mount points](https://filezillapro.com/docs/server/advanced-options/how-to-edit-mount-points-virtual-native-paths/)

## Porta, IP e firewall (scan-to-FTP local)

- IP local estável (reserva DHCP é suficiente) ou nome DNS estável.
- Listener FTP atual: TCP 21, todos IPv4/IPv6, **exige FTPS explícito por padrão**.
- Se a impressora só suporta FTP simples: habilitar "Explicit FTP over TLS and insecure plain FTP" no listener. **FTP simples envia credenciais sem criptografia.**
- Modo passivo com intervalo fixo de portas (sugestão oficial: 49152–65534; pode reduzir para LAN pequena).
- Habilitar "Use local IP for local connections" para conexões locais.
- Não é preciso IP externo/NAT para scan só na LAN.
- **Não abrir a porta administrativa (14147/14148) para a impressora.**

```powershell
New-NetFirewallRule `
  -Name "GRBKP-FileZilla-FTP-Control" `
  -DisplayName "GRBKP FileZilla FTP - controle" `
  -Direction Inbound -Action Allow -Protocol TCP `
  -LocalPort 21 -RemoteAddress "<IP_DA_IMPRESSORA>" `
  -Profile Domain,Private

New-NetFirewallRule `
  -Name "GRBKP-FileZilla-FTP-Passive" `
  -DisplayName "GRBKP FileZilla FTP - passivo" `
  -Direction Inbound -Action Allow -Protocol TCP `
  -LocalPort "<PORTA_INICIAL>-<PORTA_FINAL>" -RemoteAddress "<IP_DA_IMPRESSORA>" `
  -Profile Domain,Private
```

`RemoteAddress` aceita lista de IPs se houver várias impressoras. Restringir por IP + perfis `Domain,Private` é melhor que liberar para `Any`.

Fontes: [listeners/porta 21/FTPS padrão](https://filezillapro.com/docs/server/advanced-options/setting-up-ftp-listeners-and-connection-security/), [modo passivo](https://filezillapro.com/docs/server/advanced-options/filezilla-server-passive-mode/), [protocolos suportados (FTP simples em claro)](https://filezillapro.com/docs/server/basic-usage-instructions-server/protocols-supported-filezilla-server/), [`New-NetFirewallRule`](https://learn.microsoft.com/en-us/powershell/module/netsecurity/new-netfirewallrule)

## Validação sem expor credenciais

`Server → Test FTP Network Configuration` só verifica rede — não prova autenticação/escrita real.

1. Manter senha/hash importado como dado opaco — nunca logar/exibir.
2. Se precisar trocar senha: gerar aleatória, gravar direto no FileZilla e na impressora — nunca em argumento de linha de comando.
3. Restringir usuário ao IP da impressora + mount point `Write Only`.
4. Mandar a impressora digitalizar página de teste.
5. Confirmar arquivo novo, não vazio, legível no diretório correto.
6. Registrar no relatório só: resultado, horário, IP do dispositivo, fila/perfil, caminho final — **nunca senha, hash, XML ou log completo**.
7. Se FTP simples for inevitável: mitigar com LAN/VLAN confiável, conta exclusiva, permissões mínimas, firewall restrito ao IP.

Log do FileZilla deve ser tratado como sensível (a própria doc aplica permissões restritivas). ([logging](https://filezillapro.com/docs/server/advanced-options/logging-level-filezilla-server/))
