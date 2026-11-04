# home-ia-monitoring

Serviço independente do módulo **Monitoramento** do Home IA. Este repositório iniciou a extração de `T16-C` com contrato versionado, health e read model de status; `T16-C-B-B-B` adiciona o runtime de captura/spool com corte explícito da captura local do monólito.

## Contrato

- `GET /health/live` — liveness sem dependências.
- `GET /health/ready` — prontidão do contrato (`status-ready` quando a captura está desativada; `capture-ready` quando habilitada).
- `GET /api/v1/monitoring/contract` — manifesto `monitoring` (`schemaVersion=1.0`, `contractVersion=1.0`, capabilities).
- `GET /api/v1/monitoring/capabilities` — mesma projeção de capabilities.
- `GET /api/v1/monitoring/cameras/{cameraId}/status` — lê `manifests/` e `gaps/` do spool e reporta `capturing`, `stale`, `unavailable` ou `not_found`; 404 para câmera diferente da configurada.
- `GET /api/v1/monitoring/capture` — configuração efetiva (sem RTSP/segredos), revisão e estado do worker.
- `PUT /api/v1/monitoring/capture/settings` — atualiza `enabled`, `segmentSeconds`, `maximumSegments` e `maximumSegmentBytes` com validação; persiste em `capture-settings.json` no spool e aplica no próximo ciclo.
- `GET /api/v1/monitoring/clips` — lista manifests de clipes por `cameraIds` repetido, período, estado e paginação; sem autenticação própria, o chamador é responsável pelo escopo de câmeras.
- `GET /api/v1/monitoring/clips/{publicId}` — detalhe do clipe.
- `GET /api/v1/monitoring/clips/{publicId}/thumbnail` — thumbnail JPEG validada por SHA-256.
- `GET /api/v1/monitoring/clips/{publicId}/media` — MP4 `faststart` com range, derivado por FFmpeg e cacheado em `clips/playback/`; checksum do original é validado antes.
- `POST /api/v1/monitoring/clips/media` — materializa mídia (`clips/media/<clipId>.ts`) e thumbnail (`clips/thumbnails/<clipId>.jpg`) a partir de `segmentIds` já validados no spool, com SHA-256; a operação é idempotente e o portal mantém o manifesto/estado do clipe.
- `GET /api/v1/monitoring/live/{cameraId}/index.m3u8` — playlist HLS dos últimos 6 segmentos válidos com `MEDIA-SEQUENCE` monotônica e `DISCONTINUITY` por época; URIs relativas `segments/<segmentId>`.
- `GET /api/v1/monitoring/live/{cameraId}/segments/{segmentId}` — segmento MPEG-TS com range, resolvido apenas por manifesto validado da câmera configurada.
- `POST /api/v1/monitoring/events` — recebe envelope `{schemaVersion, eventSha256, event}` já serializado/hasheado pelo chamador; valida identidade, SHA-256 e cota, e grava de forma atômica e idempotente em `events/<camera>/<epoch>/<eventId>.json`.
- `GET /api/v1/monitoring/events` — lista eventos por `cameraIds` repetido, período e limite; a ordenação é por `startedAt` decrescente.
- `GET /api/v1/monitoring/events/recovery` — reconcilia arquivos existentes e informa contagem válida/inválida e bytes.
- `GET /api/v1/monitoring/roster/people` — lista pessoas autorizadas ativas com paginação; registros removidos viram tombstones e não aparecem.
- `POST /api/v1/monitoring/roster/people` — cadastra `{displayName, purpose, authorizationConfirmed}` e gera `PER`+12 dígitos; a autorização do operador e a auditoria continuam no portal.
- `DELETE /api/v1/monitoring/roster/people/{publicId}` — remove nome/finalidade e mantém apenas o tombstone versionado; repetir a remoção é idempotente.
- `GET /api/v1/monitoring/retention/cameras/{cameraId}` — política versionada de retenção por câmera (dias e cota de arquivo).
- `PUT /api/v1/monitoring/retention/cameras/{cameraId}` — atualiza `{expectedVersion, retentionDays, maximumStorageGigabytes}` com concorrência otimista.
- `GET /api/v1/monitoring/retention/preservations?clipPublicIds=...` — lê o estado de preservação em lote por IDs públicos de clipe.
- `PUT /api/v1/monitoring/retention/clips/{clipPublicId}/preservation` — preserva/libera um clipe arquivado com `{expectedVersion, isPreserved}`.
- `GET /api/v1/monitoring/biometrics/enrollments?userPublicId=...` — lista até 20 inscrições biométricas cifradas do usuário, expirando vencidas sob demanda.
- `GET /api/v1/monitoring/biometrics/ciphertexts` — exporta somente envelopes ativos (ciphertext/nonce/tag/`keyVersion`/dimensões/modelo/schema) para o matching no portal; token de serviço, sem chave ou plaintext.
- `GET /api/v1/monitoring/biometrics/enrollments/{publicId}` — lê o resumo de uma inscrição sem devolver ciphertext.
- `POST /api/v1/monitoring/biometrics/enrollments` — recebe apenas `{userPublicId, embeddingCiphertext/Nonce/Tag, keyVersion, dimensions, modelId, modelVersion, embeddingSchemaVersion, adapterId, consentVersion}` e revoga a anterior ativa.
- `DELETE /api/v1/monitoring/biometrics/enrollments/{publicId}?reason=owner_request|administrator_request` — revoga e apaga o payload cifrado, mantendo tombstone versionado.
- Capabilities `biometrics.read|write`, `events.read|write`, `retention.read|write`, `roster.read|write`, `clips.write` e `live.hls.read` são anunciadas no contrato.

O payload de evento é opaco ao serviço: ele valida o hash sobre os bytes canônicos recebidos e preserva o mesmo JSON. Assim, o journal local do monólito continua lendo os arquivos após rollback, sem reescrita nem duplicação.

O painel administrativo do portal consome esses endpoints por proxy autenticado (`/api/v1/admin/monitoring/capture`) e mantém RTSP/credenciais fora da tela. A edição só é liberada no modo remoto (cutover).

O serviço não autentica usuários e não decide autorização. Ele lê/escreve o spool configurado em `MONITORING_SPOOL_DIRECTORY` (default `/spool`) e usa `MONITORING_STATUS_STALE_SECONDS` (default 20) para classificar status. A exposição direta do serviço é apenas para a rede interna; o portal é a autoridade de sessão/permissão.

## Captura (`T16-C-B-B-B`)

- `MONITORING_CAPTURE_ENABLED=false` é o default de bootstrap. A captura só inicia quando habilitada pelo ambiente ou pela API de settings.
- Perfis: `synthetic` (test pattern via FFmpeg) e `ffmpeg-rtsp` (main stream RTSP, TCP, cópia H.264).
- `MONITORING_CAPTURE_PROFILE`, `MONITORING_CAPTURE_RTSP_URL` (somente no perfil RTSP), `MONITORING_CAPTURE_FFMPEG_PATH`, `MONITORING_CAPTURE_FFPROBE_PATH`, `MONITORING_CAPTURE_AUDIO_ENABLED` e `MONITORING_CAPTURE_SEGMENT_TIMEOUT_SECONDS`.
- Escrita atômica de segmento + manifesto SHA-256, recuperação de temporários/órfãos no startup e retenção limitada por `maximumSegments`, preservando segmentos fixados por clipes em `clips/manifests`.
- Falhas geram `gaps/*.json`, retry com backoff limitado e status observável; reconexão não apaga mídia válida.
- Alterações de settings cancelam a tentativa atual e reaplicam o perfil/limites sem reiniciar o processo.

## Eventos (`T16-C-C-B-A`)

- `MONITORING_EVENT_JOURNAL_MAX_EVENTS` (default 10000) e `MONITORING_EVENT_JOURNAL_MAX_BYTES` (default 67108864) limitam o journal.
- O journal é append-only, com SHA-256 por ocorrência e escrita atômica; um evento repetido com o mesmo ID/hash é idempotente e um ID com conteúdo diferente é conflito.
- A recuperação remove temporários, valida cada envelope e reconcilia a contagem no startup; eventos inválidos são apenas contados e não interrompem a captura.
- Em `MONITORING_EVENTS_SOURCE=remote`, o portal deixa de escrever localmente e usa estes endpoints; `local` mantém o journal no processo atual.

## Análise (`T16-C-C-B-B-A/B`)

- `GET/PUT /api/v1/monitoring/analysis/policies/{cameraId}` — política `always|large_motion|manual_only|never` por câmera, versionada; atualização exige `expectedVersion`.
- `POST /api/v1/monitoring/analysis/jobs` — enfileira job idempotente por `clipId`+`idempotencyKey` e deduplica por `clipId`+`analysisVersion`; respeita política e capacidade.
- `POST /api/v1/monitoring/analysis/jobs/claim` — reivindica o job pendente mais antigo com lease; leases expirados voltam a pendente na reconciliação.
- `GET /api/v1/monitoring/analysis/recovery` — remove temporários e informa políticas/jobs válidos, inválidos e bytes.
- `GET /api/v1/monitoring/analysis/jobs/{publicId}` e `GET /api/v1/monitoring/analysis/clips?clipPublicIds=...` — leitura de status; `GET /api/v1/monitoring/analysis/results/{clipPublicId}` — resultado versionado `analysis-result.v1`.
- `MONITORING_ANALYSIS_QUEUE_CAPACITY` (1..64, default 8), `MONITORING_ANALYSIS_LEASE_SECONDS` (30..300, default 60) e `MONITORING_ANALYSIS_DEFAULT_VERSION` (default `analysis-v1`).
- Políticas/jobs são gravados atomicamente em `analysis/policies/` e `analysis/jobs/`; resultados em `analysis/results/`. O serviço é instância única.
- **Execução de inferência (`MONITORING_ANALYSIS_ENABLED=true`):** o worker reivindica um job, lê o MP4 de playback validado do clipe, amostra frames com FFmpeg (`MONITORING_ANALYSIS_SAMPLE_FPS`, `MONITORING_ANALYSIS_MAX_FRAMES`), executa o ONNX e grava o resultado com SHA-256. Falhas são retentadas até `MONITORING_ANALYSIS_MAX_ATTEMPTS` e então registradas como `failed`; lease perdido gera `analysis_lease_lost`. Desativado por padrão.
- Detector: `MONITORING_DETECTOR_ONNX_MODEL_PATH` (absoluto, montado no contêiner), `_MODEL_VERSION`, `_INPUT_NAME`, `_OUTPUT_NAME`, `_INPUT_WIDTH/HEIGHT`, `_SCORE_THRESHOLD`, `_LABELS` (separadas por vírgula), `_INTRA_OP_THREADS`, `_INTER_OP_THREADS`. Sem caminho/etiquetas válidos o runtime não inicia.
- Eventos de regra continuam no portal nesta fase; o serviço persiste o resultado da análise e não publica eventos, evitando duplicidade.
- Em `MONITORING_ANALYSIS_SOURCE=remote`, o portal usa estes endpoints, exibe o status vindo do serviço e mantém autorização/auditoria; `local` mantém o store SQLite do monólito.

## Retenção e arquivo (`T16-C-D-B`)

- `MONITORING_ARCHIVE_ENABLED=false` mantém arquivo/retenção desativados; com `true`, o serviço arquiva clipes `ready` em `MONITORING_ARCHIVE_DIRECTORY` (absoluto, fora do spool).
- `MONITORING_RETENTION_WORKER_ENABLED` (default `true`) controla o worker de expiração; políticas e preservações ficam em `retention/policies/` e `retention/preservations/`.
- Expiração respeita idade (`retentionDays`), cota (`maximumStorageGigabytes`) e preserva clipes fixados ou com análise ativa; a cópia arquivada é validada por SHA-256 antes de qualquer remoção.
- `MONITORING_ARCHIVE_POLL_SECONDS` (2..3600), `MONITORING_ARCHIVE_MAX_CLIPS`, `MONITORING_ARCHIVE_MAX_BYTES` e `MONITORING_ARCHIVE_MAX_CLIP_BYTES` limitam o worker.
- Em `MONITORING_RETENTION_SOURCE=remote`, o portal usa estes endpoints e desativa seus workers locais; autorização, auditoria IAM e catálogo de câmeras continuam no portal. O serviço não autentica usuários.

## Biometria cifrada (`T16-C-D-C`)

- O serviço é dono do registro operacional de inscrições em `biometrics/enrollments/<BIO...>.json`; guarda somente ciphertext, nonce e tag, nunca imagem, embedding em claro ou nome.
- `MONITORING_BIOMETRICS_SOURCE=remote` no portal delega lista/enroll/revoke, mas identidade, consentimento, autorização, extração facial, chave de criptografia e auditoria IAM permanecem no portal.
- A inscrição expira em 365 dias (`biometric-consent.v1`); o worker de expiração limpa o payload e mantém tombstone. O serviço não descriptografa, não reconhece e não decide autorização.
- Ativar `remote` exige exportação/importação explícita das inscrições SQLite existentes; não há migração automática nem transação distribuída com a auditoria do portal.

## Corte e rollback

O corte é explícito no portal, não automático:

1. Serviço ativo com spool compartilhado e `MONITORING_CAPTURE_ENABLED=true`.
2. Platform com `PLATFORM_CAPTURE_AUTHORITY=remote`, `PLATFORM_MONITORING_STATUS_SOURCE=remote` e `PLATFORM_MONITORING_EXECUTION_MODE=remote`.
3. `./garage restart-active-api` recria a API; a Platform deixa de escrever no spool e passa a ler o mesmo spool/HLS.
4. Rollback: voltar as três chaves de captura/status/execução para `local`/`local`/`in-process`, `MONITORING_LIVE_SOURCE=local`, `MONITORING_CLIPS_WRITE_SOURCE=local`, `MONITORING_CLIPS_SOURCE=local`, `MONITORING_EVENTS_SOURCE=local`, `MONITORING_ROSTER_SOURCE=local`, `MONITORING_RETENTION_SOURCE=local` e `MONITORING_BIOMETRICS_SOURCE=local`, e reiniciar a API. O serviço pode ser parado sem remover spool/dados; o journal de eventos usa o mesmo formato e é reconciliado na volta. Os manifests de clipe continuam no mesmo formato e o rollback apenas devolve a materialização de mídia/thumbnail ao monólito.

Nunca execute os dois writers no mesmo spool. A autoridade remota exige status remoto para evitar que o portal reporte captura local falsa.

## Verificações

A partir da raiz do contexto:

```bash
./garage verify-monitoring-service
./garage format-monitoring-service
./garage lock-monitoring-service
./garage monitoring
./garage logs monitoring
```

`./garage monitoring` inicia somente o serviço e **não** altera a captura local da Platform.

## Limites

- Não há NAS, reconhecimento facial ou banco operacional neste serviço ainda; roster, retenção, HLS e inscrições biométricas cifradas usam arquivos JSON atômicos e o spool configurado.
- HLS e materialização de clipe são servidos apenas para a câmera configurada e nunca aceitam caminho arbitrário; a política de disparo, a autorização e o estado do manifesto de clipe continuam no portal.
- Arquivo/retenção e expiração biométrica são instâncias únicas, sem lease distribuído; a auditoria de expiração permanece no worker local do portal e não há migração automática das políticas/preservações/inscrições SQLite existentes.
- A captura é single-camera e single-host; não há concorrência de câmeras, autenticação serviço-a-serviço ou validação física/24×7.
- Testes locais usam adaptadores contratuais e test pattern; não homologam câmera, RTSP real, CPU/RAM, NAS ou estabilidade.
