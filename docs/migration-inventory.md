# Inventário de migração — T16-C

Este inventário orienta a extração do runtime de Monitoramento do monólito `garage-watch-platform` para `home-ia-monitoring`. O serviço já possui contrato, status de câmera, `SpoolWriter` e runtime de captura segmentada; HLS, clipes, eventos, IA e dados ainda pertencem ao monólito.

## Propriedade alvo

| Dado/componente | Dono atual | Dono alvo | Observação |
|---|---|---|---|
| Câmera/captura/RTSP, spool, HLS, clipes, thumbnails, arquivo | platform | Monitoramento | Arquivos e manifests permanecem no serviço; portal não lê mídia |
| Eventos/journal local, regras temporais, análise IA | platform | Monitoramento | Resultados e jobs pertencem ao domínio de monitoramento |
| `CameraContexts`, `CameraCatalogEntry` (configuração operacional) | SQLite IAM | Monitoramento | Portal mantém projeção de recursos para autorização |
| `ClipAnalysisDispatchPolicy`, `ClipAnalysisJob` | SQLite IAM | Monitoramento | Migração aditiva com reconciliação |
| `ClipRetentionPolicy`, `ClipPreservation` | SQLite IAM | Monitoramento | Operação de mídia/arquivo |
| `KnownPeople`, `BiometricEnrollments`, `FaceExtractor*` | SQLite IAM | Monitoramento | Biometria cifrada não vai ao portal; referência por `userPublicId` |
| Contas, sessões, perfis, permissões, auditoria central, módulos/menus/preferências | SQLite IAM | Portal | Não duplicar no serviço |

## Tabelas e arquivos a inventariar antes de mover

- Tabelas SQLite: `CameraContexts`, `CameraCatalog`, `ClipAnalysisDispatchPolicies`, `ClipAnalysisJobs`, `KnownPeople`, `BiometricEnrollments`, `FaceExtractorCandidates`, `FaceExtractorSelections`, `ClipRetentionPolicies`, `ClipPreservations`.
- Spool: `segments/`, `clips/manifests/`, `clips/media/`, `clips/playback/`, `events/`, `gaps/`.
- Arquivo: `archive/<camera>/<clip>/` com `archive-manifest.json`.
- Modelos/artefatos: `face-models` (read-only) e configuração de captura por ambiente.
- Contratos: capture segment/gap, clip manifest, archived clip manifest, detection result, event rules.

## Fases

1. **T16-C-A:** contrato HTTP/health, service skeleton, root commands e inventário. Monólito continua autoridade.
2. **T16-C-B-A:** read model de status de câmera lendo manifests/gaps do spool; portal usa `MONITORING_STATUS_SOURCE=local|remote`; default local. O writer ainda é o monólito.
3. **T16-C-B-B-A:** extrair `SpoolWriter` com escrita atômica de segmento/manifesto e limites, desativado por `MONITORING_CAPTURE_ENABLED=false`; sem hosted capture service/adaptador. O monólito continua writer ativo.
4. **T16-C-B-B-B (esta):** hosted capture service com adaptadores `synthetic`/`ffmpeg-rtsp`, gaps, retenção, recuperação e settings persistidos; Platform corta a captura local por `CAPTURE_AUTHORITY=remote` e lê o mesmo spool/HLS. Rollback restaura `local`.
5. **T16-C-C:** mover clipes/thumbnails/eventos/análise e jobs, com migração aditiva e reconciliação.
6. **T16-C-D:** mover retenção/arquivo/roster/biometria, validar autorização serviço-a-serviço e executar corte final.

## Regras de migração e rollback

- Nenhuma fase move dados por cópia destrutiva: inventariar, copiar/validar, reconciliar e só então cortar.
- IDs, timestamps, checksums, estados de mídia, epochs e histórico de auditoria são preservados.
- O monólito permanece fallback até o aceite da fase; rollback desliga o modo `remote` e restaura a autoridade anterior.
- Não executar dual-write sem reconciliador explícito e idempotente; não alternar autoridades silenciosamente.
- O portal não recebe mídia, embeddings, credenciais RTSP/NAS ou escrita ampla no serviço.
- Validação física/CT02/NAS/24×7 não é alegada por testes de contrato.
