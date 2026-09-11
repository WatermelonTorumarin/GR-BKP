# Notas de aceite da Fase 1 (auditoria cruzada com subagentes do Codex)

Registro do que foi verificado contra o código real em 2026-09-11, cruzando o acceptance map detalhado que o Codex produziu via subagentes antes de ficar sem créditos.

## Gap de arquitetura identificado: `MigrationOrchestrator` ausente

O subagente "Phase1 test review" apontou que o prompt da Fase 1 não fixava uma interface pública mínima de orquestração (iniciar backup/restauração, retomar por `operationId`, consultar status), e que sem isso os testes tendem a exercitar classes internas do `Core` diretamente em vez de um ponto de entrada único.

Confirmado: o código não tinha `MigrationOrchestrator` (nem qualquer classe com esse nome). Os testes chamavam `StreamingBackupEngine`, `ApplicationTreeRestorer`, `PostBootResumeModule`, `ReportModule` isoladamente. Isso não violava o critério mínimo do prompt (T02–T10, T34–T38), mas era dívida real: a Fase 2 ("UI de backup/restauração básica") precisaria de uma fachada única.

**Resolvido em 2026-09-11**: implementado `GRBKP.Core.Orchestration.MigrationOrchestrator`, cobrindo detecção automática de retomada (resume vs. run) e consulta de status via checkpoint — ver `src/GRBKP.Core/Orchestration/MigrationOrchestrator.cs`.

## Invariantes verificadas contra o código (todas confirmadas)

| Invariante exigida | Verificação |
|---|---|
| `GRBKP.Core` sem WPF/API Windows concreta | `GRBKP.Core.csproj` → `net10.0` puro; `GRBKP.App`/`GRBKP.Windows` → `net10.0-windows` |
| `operation.json`/`package.json` por temporário + rename atômico | `BackupPackageWriter.WriteJsonAtomicallyAsync` |
| `files.jsonl` escrito incrementalmente, nunca carregado inteiro | `AppendCommittedFileAsync` grava+flush linha a linha; `ReadFilesAsync` usa `ReadLineAsync` em loop, streaming |
| Checksum de `files.jsonl` fora do próprio arquivo (sem autorreferência) | Vai para `package.json.FilesManifestSha256`, calculado via `IncrementalHash` durante a escrita |
| Sem chamada de rede em `src/` | `grep -rn "HttpClient\|WebRequest\|Socket"` → vazio |
| Nenhuma execução de binário recuperado | `grep -rn "Process.Start\|ProcessStartInfo"` → vazio |

## Pendência não resolvida — investigar antes de fechar a Fase 1 de vez

O acceptance map (seção 8.7) levanta a política para a **última linha truncada de `files.jsonl` após uma desconexão no meio de uma escrita**. `BackupPackageModule.ReadFilesAsync` usa `StreamReader.ReadLineAsync`, que trata uma última linha sem quebra final como linha completa — isso pode virar `InvalidDataException` em vez de ser descartado silenciosamente na retomada. Não há teste dedicado a esse cenário específico ainda (os testes T08/T09 cobrem desconexão, mas não confirmadamente uma linha JSON parcial gravada no meio do byte-write).

**Ação recomendada:** escrever um teste que trunca deliberadamente a última linha de `files.jsonl` no meio (simulando crash durante `WriteAsync`) e confirmar que a retomada lida com isso corretamente — hoje incerto se o comportamento é o esperado.

## Referência: fontes de pesquisa complementares desta Fase

- `research-legacy-runtime-flash-air-java.md` — Fases 3/5
- `research-filezilla-server.md` — Fase 4
- `research-printer-drivers-brother-zebra.md` — Fase 4
- `research-onedrive-copilot-removal.md` — Fase 5
