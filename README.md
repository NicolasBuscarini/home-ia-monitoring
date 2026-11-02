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

O serviço não autentica usuários e não decide autorização. Ele lê/escreve o spool configurado em `MONITORING_SPOOL_DIRECTORY` (default `/spool`) e usa `MONITORING_STATUS_STALE_SECONDS` (default 20) para classificar status. A exposição direta do serviço é apenas para a rede interna; o portal é a autoridade de sessão/permissão.

## Captura (`T16-C-B-B-B`)

- `MONITORING_CAPTURE_ENABLED=false` é o default de bootstrap. A captura só inicia quando habilitada pelo ambiente ou pela API de settings.
- Perfis: `synthetic` (test pattern via FFmpeg) e `ffmpeg-rtsp` (main stream RTSP, TCP, cópia H.264).
- `MONITORING_CAPTURE_PROFILE`, `MONITORING_CAPTURE_RTSP_URL` (somente no perfil RTSP), `MONITORING_CAPTURE_FFMPEG_PATH`, `MONITORING_CAPTURE_FFPROBE_PATH`, `MONITORING_CAPTURE_AUDIO_ENABLED` e `MONITORING_CAPTURE_SEGMENT_TIMEOUT_SECONDS`.
- Escrita atômica de segmento + manifesto SHA-256, recuperação de temporários/órfãos no startup e retenção limitada por `maximumSegments`, preservando segmentos fixados por clipes em `clips/manifests`.
- Falhas geram `gaps/*.json`, retry com backoff limitado e status observável; reconexão não apaga mídia válida.
- Alterações de settings cancelam a tentativa atual e reaplicam o perfil/limites sem reiniciar o processo.

## Corte e rollback

O corte é explícito no portal, não automático:

1. Serviço ativo com spool compartilhado e `MONITORING_CAPTURE_ENABLED=true`.
2. Platform com `PLATFORM_CAPTURE_AUTHORITY=remote`, `PLATFORM_MONITORING_STATUS_SOURCE=remote` e `PLATFORM_MONITORING_EXECUTION_MODE=remote`.
3. `./garage restart-active-api` recria a API; a Platform deixa de escrever no spool e passa a ler o mesmo spool/HLS.
4. Rollback: voltar as três chaves para `local`/`local`/`in-process` e reiniciar a API. O serviço pode ser parado sem remover spool/dados.

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

- Não há HLS, clipes, eventos, IA, NAS, biometria ou banco operacional neste serviço ainda.
- A captura é single-camera e single-host; não há concorrência de câmeras, autenticação serviço-a-serviço ou validação física/24×7.
- Testes locais usam adaptadores contratuais e test pattern; não homologam câmera, RTSP real, CPU/RAM, NAS ou estabilidade.
