# Inventário de migração — T16-C

Este inventário orienta a extração do runtime de Monitoramento do monólito `garage-watch-platform` para `home-ia-monitoring`. O serviço já possui contrato, status de câmera, `SpoolWriter`, runtime de captura segmentada, leitura aditiva de clipes/eventos, roster/retenção/biometria e, em `T16-E`, HLS e materialização de mídia/thumbnail de clipes; política de disparo, manifesto/estado do clipe e IA embarcada ainda pertencem ao monólito.

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
- Spool: `segments/`, `clips/manifests/`, `clips/media/`, `clips/playback/`, `events/` (journal e `outbox/` de auditoria), `analysis/`, `gaps/`.
- Arquivo: `archive/<camera>/<clip>/` com `archive-manifest.json`.
- Modelos/artefatos: `face-models` (read-only) e configuração de captura por ambiente.
- Contratos: capture segment/gap, clip manifest, archived clip manifest, detection result, event rules.

## Fases

1. **T16-C-A:** contrato HTTP/health, service skeleton, root commands e inventário. Monólito continua autoridade.
2. **T16-C-B-A:** read model de status de câmera lendo manifests/gaps do spool; portal usa `MONITORING_STATUS_SOURCE=local|remote`; default local. O writer ainda é o monólito.
3. **T16-C-B-B-A:** extrair `SpoolWriter` com escrita atômica de segmento/manifesto e limites, desativado por `MONITORING_CAPTURE_ENABLED=false`; sem hosted capture service/adaptador. O monólito continua writer ativo.
4. **T16-C-B-B-B (esta):** hosted capture service com adaptadores `synthetic`/`ffmpeg-rtsp`, gaps, retenção, recuperação e settings persistidos; Platform corta a captura local por `CAPTURE_AUTHORITY=remote` e lê o mesmo spool/HLS. Rollback restaura `local`.
5. **T16-C-C-A (concluída no escopo local):** leitura aditiva de clipes no serviço — `GET /api/v1/monitoring/clips` (filtros/paginação/escopo de câmeras), detalhe, thumbnail e mídia validados por SHA-256; portal usa `MONITORING_CLIPS_SOURCE=local|remote`, mantém autorização `clips.read` e faz proxy do stream. Escrita de clipes, eventos, análise e jobs seguem no monólito (escrita resolvida em `T16-E`).
6. **T16-C-C-B-A (concluída no escopo local):** journal de eventos no serviço — `POST/GET /api/v1/monitoring/events`, recovery e capabilities `events.read|write`; envelope e SHA-256 são validados e preservados, o portal usa `MONITORING_EVENTS_SOURCE=local|remote`. A reconciliação é o próprio journal append-only compartilhado: rollback volta ao `LocalEventJournal` sem cópia nem reescrita.
7. **T16-C-C-B-B-A (concluída no escopo local):** store operacional de análise no serviço — política versionada, jobs idempotentes com capacidade limitada, claim com lease e reconciliação de leases expirados; portal usa `MONITORING_ANALYSIS_SOURCE=local|remote`, mantém autorização/auditoria e faz proxy das operações. A inferência ainda não roda no serviço.
8. **T16-C-C-B-B-B (concluída no escopo local):** worker de inferência no serviço reivindica jobs, amostra frames do playback validado via FFmpeg, executa ONNX, grava `analysis-result.v1` em `analysis/results/` e conclui o job por lease; falhas têm retry limitado. Desativado por `MONITORING_ANALYSIS_ENABLED=false`. Eventos de regra e promoção de modelo permanecem no portal/decisão do operador.
9. **T16-C-D-A (concluída no escopo local):** roster de pessoas autorizadas movido para o serviço — `GET/POST /api/v1/monitoring/roster/people` e `DELETE .../{publicId}`, capabilities `roster.read|write`, tombstones versionados em `roster/people/`; o portal mantém identidade, autorização e auditoria e usa `MONITORING_ROSTER_SOURCE=local|remote` (default `local`).
10. **T16-C-D-B (concluída no escopo local):** retenção, preservação e arquivo movidos para o serviço — políticas e preservações versionadas em `retention/`, `ClipArchiveService` com validação SHA-256 e cota, worker de expiração protegendo clipes preservados/em análise, endpoints `retention.*` e capabilities `retention.read|write`; o portal mantém catálogo de câmeras, autorização e auditoria, usa `MONITORING_RETENTION_SOURCE=local|remote` (default `local`) para política/preservação/proxy de enriquecimento e desativa os workers locais em `remote`. Não há migração automática de `ClipRetentionPolicies`/`ClipPreservations` nem transação distribuída de auditoria.
11. **T16-C-D-C (concluída no escopo local, fatia A):** registro operacional de inscrições biométricas cifradas no serviço — `GET/POST /api/v1/monitoring/biometrics/enrollments`, detalhe e `DELETE .../{publicId}`; guarda somente ciphertext/nonce/tag em `biometrics/enrollments/`, revoga a inscrição anterior, expira em 365 dias e mantém tombstone. O portal mantém identidade, consentimento, autorização `biometrics.manage`, extração facial, chave/cifra e auditoria IAM, e usa `MONITORING_BIOMETRICS_SOURCE=local|remote` (default `local`). Não há migração automática das inscrições SQLite nem transação distribuída de auditoria; o serviço não descriptografa nem reconhece.
12. **T16-C-D (parcial):** resta o corte final do monólito com validação serviço-a-serviço; a extração/cifra biométrica ainda roda no portal e o serviço não executa reconhecimento.
13. **T16-E (concluída no escopo local para HLS e escrita de clipes):** o serviço serve `GET /api/v1/monitoring/live/{cameraId}/index.m3u8` e `.../segments/{segmentId}` a partir de manifests validados, e `POST /api/v1/monitoring/clips/media` materializa mídia/thumbnail idempotentes com SHA-256 (`clips.write`, `live.hls.read`). O portal mantém trigger, autorização, manifesto/estado do clipe e usa `MONITORING_LIVE_SOURCE=local|remote` e `MONITORING_CLIPS_WRITE_SOURCE=local|remote` (default `local`). Como o manifesto e os arquivos continuam no mesmo formato/spool, o rollback é apenas de configuração. O corte final `T16-E` ainda exige validação de spool compartilhado em execução contínua e câmera real.

## Producer de observação de identidade (T09-PRODUCER, 05/11/2026)

- `IDENTITY_OBSERVATION_PRODUCER_ENABLED=false` por padrão; `..._SOURCE=disabled|synthetic` (extração real ainda não existe).
- Varre clipes `ready` mais recentes, deduplica por `clipPublicId` e limita uma observação por câmera a cada `..._MIN_INTERVAL_SECONDS` (padrão 60).
- Fila limitada em memória (`..._QUEUE_CAPACITY`, padrão 64): embeddings não são gravados em disco; reinício perde pendências e o descarte é observável.
- Usa `MONITORING_SERVICE_TOKEN_SECRET` e envia `face-observation.v1` a `IDENTITY_OBSERVATION_PRODUCER_PORTAL_URL/api/v1/monitoring/identity-observations` com escopo `identity.observations.write`.
- Retry para rede/`408`/`429`/`5xx`; `4xx` definitivo é descartado. Status em `GET /api/v1/monitoring/identity-observations/status`; métricas `home_ia_identity_observations_*` em `/metrics`.
- Rollback: `..._ENABLED=false` e reinício do serviço; nenhum dado persistido no serviço precisa ser removido.

## Regras de migração e rollback

- Nenhuma fase move dados por cópia destrutiva: inventariar, copiar/validar, reconciliar e só então cortar.
- IDs, timestamps, checksums, estados de mídia, epochs e histórico de auditoria são preservados.
- O monólito permanece fallback até o aceite da fase; rollback desliga o modo `remote` e restaura a autoridade anterior.
- Não executar dual-write sem reconciliador explícito e idempotente; não alternar autoridades silenciosamente.
- O portal não recebe persistência de mídia, embeddings, credenciais RTSP/NAS ou escrita ampla no serviço; ele apenas faz proxy autenticado do stream já autorizado. Em `T16-E`, a materialização de mídia/thumbnail de clipe ocorre no serviço sob demanda do portal, mantendo o manifesto e o estado do clipe no portal.
- Em `T16-C-C-B-B-A`, política/job pertencem ao store do serviço; o portal mantém a autorização e a auditoria, mas a transação não é distribuída entre HTTP e SQLite — falha de auditoria após o commit remoto é registrada como limitação conhecida.
- Em `T16-C-C-B-B-B`, o resultado da análise é do serviço; o portal apenas consulta status/resultado. Ativar inferência no serviço e deixar o detector do monólito ligado pode duplicar custo de CPU (não eventos, que continuam só no portal). Não há validação de acurácia do modelo nem homologação de carga.
- Validação física/CT02/NAS/24×7 não é alegada por testes de contrato.
