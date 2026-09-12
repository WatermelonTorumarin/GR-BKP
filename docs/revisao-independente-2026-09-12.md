# Revisão independente do GRBKP v1.0 — 12/09/2026

Varredura completa do projeto feita de madrugada, sem nenhuma ação externa necessária.
Modelo diferente do que escreveu a maior parte do código (Opus 5 revisando trabalho do Sonnet 5),
justamente para trazer outro par de olhos.

**Resultado:** 6 bugs reais encontrados e corrigidos, 1 gargalo medido e reduzido pela metade,
20 testes novos. De 86 para 107 testes, todos passando.

---

## 1. Bugs encontrados e corrigidos

### 1.1 — CRÍTICO: a leitura de SID nunca funcionou

`RegistryHiveOnDemand` (a biblioteca que lê o registro do SSD antigo) devolve as subchaves de
`key.SubKeys` **rasas**: só com o nome, sempre com a lista de valores vazia. O adaptador lia
`subKey.Values` direto, então **todo valor pedido voltava nulo**, em qualquer hive e qualquer
chave.

Consequência: o resolvedor de SID, que lê `ProfileImagePath` de cada perfil em `ProfileList`,
nunca resolvia um único SID. A conferência de usuário da tela de restauração sempre diria
*"SID do backup original não disponível"* e nunca compararia nada. Toda a entrega de SID da
Fase 2 era silenciosamente inoperante.

Provado contra hive real (cópia de `C:\Users\Default\NTUSER.DAT`, sem elevação):
`GetKey` pelo caminho completo traz os 20 valores de *User Shell Folders*; a **mesma chave**
alcançada via `SubKeys` do pai traz **zero**.

Por que passou despercebido: o teste do adaptador real pedia uma lista **vazia** de nomes de valor
e só checava que a lista de subchaves não era vazia; o teste do resolvedor usava um leitor falso
que devolvia valores. Nenhum dos dois exercitava a única coisa que o adaptador existe para fazer.

### 1.2 — ALTO: um arquivo ilegível derrubava o backup inteiro

Um único arquivo travado por outro processo (base do Outlook/navegador aberto) ou sem permissão
de leitura — **exatamente o caso de ler um SSD antigo cujas ACLs apontam para o SID de outra
máquina** — abortava o backup completo com exceção.

A enumeração já tinha sido endurecida quando apareceu o bug das junctions em pt-BR ("Meus
Vídeos"), mas a camada de **cópia** continuava frágil. O mesmo valia na restauração: um arquivo
travado no destino abortava tudo e deixava o perfil pela metade.

Agora falha de um arquivo é falha **daquele** arquivo: contabilizada, registrada no journal do
pacote, e a operação segue. O campo `FailedFiles` já existia no modelo e o relatório já o
consumia — nenhum motor jamais o incrementava.

### 1.3 — ALTO: sobrescrever arquivo somente-leitura falha no Windows

`File.Move(overwrite: true)` sobre um arquivo somente-leitura lança `UnauthorizedAccessException`,
mesmo com o app elevado. Como o backup preserva os atributos originais, **repetir uma restauração
quebrava**: o destino já tinha os arquivos somente-leitura da execução anterior.

Pior: `Directory.Delete(recursive: true)` falha no primeiro arquivo somente-leitura que encontra —
o que quebraria a limpeza da pasta de proteção pré-migração **depois** de uma troca de árvore
bem-sucedida (Thunderbird, Office etc.), transformando sucesso em falha.

Corrigido no adaptador: limpa o atributo antes de sobrescrever e antes de apagar.

### 1.4 — ALTO: pastas pessoais redirecionadas saíam do backup em silêncio

A varredura procura literalmente em `<perfil>\Documents`, `<perfil>\Desktop` etc. Quando essas
pastas estão redirecionadas — **Backup de Pastas do OneDrive** (que a empresa usa, tanto que a
Fase 5 existe para removê-lo) ou redirecionamento corporativo para a rede — o caminho padrão fica
vazio e o backup terminaria **com sucesso e sem os documentos do funcionário**.

Perda silenciosa é o pior tipo de falha num utilitário de backup: só se descobre na restauração,
com o SSD antigo já formatado. E foi mais ou menos assim que este projeto começou.

Agora o app lê *User Shell Folders* do `NTUSER.DAT` do perfil (offline) antes de copiar qualquer
coisa e, se achar pasta fora do lugar, mostra um aviso com os caminhos reais e pede confirmação.
Duas travas: se o hive não puder ser lido, não acusa nada (nunca impede um backup); e um teste
contra o `NTUSER.DAT` real garante ausência de falso positivo — aviso errado em toda operação
treinaria o técnico a ignorá-lo justamente quando for verdadeiro.

### 1.5 — MÉDIO: disco cheio viraria "backup concluído"

Consequência da própria correção 1.2: com resiliência por arquivo, um SSD de destino lotado seria
classificado como "um arquivo com problema" — e como todo arquivo seguinte falharia igual, o
resultado seria *"Backup concluído"* com dezenas de milhares de arquivos não copiados.

Disco cheio agora é reconhecido como falha da operação inteira, nos dois motores.

### 1.6 — MÉDIO: omissões que o técnico nunca via

- A regra que ignora **Imagens com menos de 21 arquivos** ficava só no resumo interno: o backup
  terminava "concluído" sem avisar que as imagens ficaram de fora. Agora aparece na tela.
- Arquivos que falharam agora aparecem com exemplos de caminho. Resiliência sem visibilidade
  seria perda silenciosa — pior que falhar alto.
- Títulos de resultado mostravam o nome do estado em inglês ("Backup paused") num app em
  português, e a tela de falha de restauração não exibia a mensagem do motor.
- Clique duplo em "Iniciar backup" disparava duas operações concorrentes.

---

## 2. Gargalo medido e reduzido

Medição com 2.000 arquivos de 4 KB (perfil de usuário é dominado por arquivos pequenos):

| | tempo | throughput |
|---|---|---|
| cópia crua do Windows | 1.966 ms | 1.017 arq/s |
| motor GRBKP **antes** | 10.032 ms | 199 arq/s |
| motor GRBKP **depois** | 5.251 ms | **381 arq/s** |

**Causa isolada:** os dois motores regravavam `operation.json` a cada arquivo copiado, e cada
regravação serializa + grava temporário + flush + renomeia — quatro operações de disco por arquivo
do usuário. Sozinhas custavam **3.471 ms** dos 10.032 ms. Para comparação, acrescentar uma linha
ao manifesto e ao journal custou **65 ms no total**.

Agora o checkpoint é gravado a cada 64 arquivos ou 2 segundos. Numa queda de energia a retomada
recomeça no máximo 64 arquivos atrás — todos já dentro do pacote. Transições de estado, pausa e
conclusão sempre gravam na hora.

**Impacto prático:** num perfil com 200 mil arquivos, isso é a diferença entre ~17 e ~9 minutos
só de pedágio de checkpoint.

**Outra medição:** `AppData\Local` deste perfil tem **16,6 GB**, e a varredura genérica copiava
tudo — inclusive `Temp` (389 MB), dumps de falha, cache de miniaturas do Explorer (79 MB) e
WebCache (28 MB), que o Windows recria sozinho. Adicionada uma lista curta e explícita de pastas
efêmeras a pular. Deliberadamente **sem** regra genérica por nome (tipo "exclua qualquer pasta
chamada Cache"): aplicativos guardam dado real em pastas assim.

**Verificado e NÃO é problema:** caminhos acima de 260 caracteres funcionam normalmente no
.NET 10, mesmo com `LongPathsEnabled=0` no registro e sem `longPathAware` no manifesto. (Minha
primeira medição via PowerShell dizia o contrário — mas PowerShell 5.1 roda em .NET Framework,
que tem comportamento diferente. Por isso medir na plataforma certa importa.)

---

## 3. Recomendações — em ordem de valor

### 3.1 — Botão "Conferir backup" (ALTO valor, esforço baixo)

Hoje não existe forma de verificar um pacote **antes** de formatar o SSD antigo. O incidente que
originou este projeto foi exatamente um backup que parecia completo e não estava.

A infraestrutura já existe: o pacote tem `files.jsonl` com tamanho de cada arquivo e
`FilesManifestSha256` do manifesto inteiro. Um botão que relê o pacote, confere se todo arquivo
do manifesto existe com o tamanho certo e mostra "X arquivos, Y GB, manifesto íntegro" daria ao
técnico a confiança que hoje falta. **É a recomendação que eu priorizaria.**

### 3.2 — Decidir a política de integridade (ALTO valor, precisa da sua decisão)

Hoje **nenhum arquivo é verificado por hash**: `RequiresSha256` é `false` para todos, e a política
gravada no pacote diz `HashCriticalFiles: false`, validação `"size-and-handle"`. Isso detecta
truncamento, mas **não** corrupção silenciosa (setor ruim no SSD velho, bit trocado).

Medi o custo aqui: **SHA-256 a 281 MB/s** nesta máquina. É mais lento do que eu esperava — ligar
hash para *todos* os arquivos poderia reduzir bastante a velocidade em arquivos grandes (um PST de
10 GB, por exemplo). Mas o hash é calculado **no mesmo buffer da cópia**, sem I/O extra.

Sugestão: ligar hash apenas para arquivos **críticos** (PST/OST do Outlook, Documentos) — que é
exatamente o que o campo `HashCriticalFiles` foi desenhado para significar e nunca foi usado.
Essa é uma decisão sua, não minha: envolve trocar velocidade por garantia.

### 3.3 — Tornar a retomada alcançável pela interface (MÉDIO)

Os motores sabem retomar um backup interrompido, mas a interface sempre cria uma pasta nova com
data e hora — então a retomada **nunca é acionada na prática**. Se o SSD desconectar em 80% de um
backup de 200 GB, hoje se recomeça do zero. Ofereço detectar pacote incompleto do mesmo
funcionário e perguntar "retomar ou começar de novo?".

### 3.4 — Cópia paralela (MÉDIO valor, esforço médio-alto)

O motor copia um arquivo por vez. Em SSD externo por USB, 3–4 cópias simultâneas normalmente
rendem 2–3x em arquivos pequenos, porque escondem a latência por arquivo. O que sobra de
diferença para a cópia crua (2,7x) é inerente ao que o motor faz por arquivo: temporário +
renomeação atômica, três chamadas de metadados e as linhas de manifesto. Mexer nisso exige
cuidado com ordem de checkpoint — não é mudança para fazer às pressas.

### 3.5 — Decidir o que fazer com pastas redirecionadas (MÉDIO)

Hoje eu apenas **aviso**. O passo natural seria oferecer incluir a pasta redirecionada no backup
(lendo o caminho real do registro). Não implementei porque muda o que entra no pacote e onde é
restaurado — decisão de produto, não de madrugada.

### 3.6 — Journal cresce sem limite (BAIXO)

Uma linha por arquivo: ~57 MB de journal para 200 mil arquivos. Não é problema hoje (o custo de
escrita é irrisório), mas vale saber que existe.

---

## 4. O que continua pendente de hardware real (Fase 6)

Nada disso pode ser verificado sem os computadores da empresa:

1. **Nome do serviço do FileZilla** — o código usa `"FileZilla Server"`, nunca confirmado numa
   instalação real. É a única suposição não verificada que sobrou.
2. **Instalação real de driver** Brother/Zebra via `pnputil`.
3. **Remoção real do OneDrive/Copilot** — só validada com adaptadores falsos.
4. **Reinício e retomada pós-boot** — a criação/remoção de tarefa agendada foi validada contra o
   Agendador de Tarefas real; o `shutdown /r` em si nunca foi executado (por segurança).
5. **Comportamento em perfis grandes de verdade** — os números de desempenho acima vêm de um
   teste sintético nesta máquina.
