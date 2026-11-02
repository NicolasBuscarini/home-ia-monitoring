# home-ia-monitoring

Serviço independente do módulo **Monitoramento** do Home IA. Este repositório inicia a extração de `T16-C` com um contrato versionado e health próprio; a captura/mídia ainda permanece no monólito da platform até as fases de migração de runtime/dados.

## Contrato (T16-C-B-A)

- `GET /health/live` — liveness sem dependências.
- `GET /health/ready` — prontidão do read model (`status-ready`), sem alegar captura.
- `GET /api/v1/monitoring/contract` — manifesto `monitoring` (`schemaVersion=1.0`, `contractVersion=1.0`, `executionMode=status-read-only`, capabilities).
- `GET /api/v1/monitoring/capabilities` — mesma projeção de capabilities.
- `GET /api/v1/monitoring/cameras/{cameraId}/status` — lê `manifests/` e `gaps/` do spool e reporta `capturing`, `stale`, `unavailable` ou `not_found`; 404 para câmera diferente da configurada.

O serviço não autentica usuários, não decide autorização e não escreve mídia nem inicia captura nesta fase. Ele lê o spool configurado em `MONITORING_SPOOL_DIRECTORY` (default `/spool`) e usa `MONITORING_STATUS_STALE_SECONDS` (default 20) para classificar status. O portal continua sendo a autoridade de sessão/permissão; o serviço receberá decisões de escopo quando o writer for movido.

## Verificações

A partir da raiz do contexto:

```bash
./garage verify-monitoring-service
./garage format-monitoring-service
./garage lock-monitoring-service
```

## Limites

- `contract-only`: não inicia captura, não lê câmera/NAS, não serve HLS/clipes.
- O monólito da platform continua sendo a fonte de comportamento até a fase de extração de runtime.
- Não publicar na LAN/Internet nem usar como produção sem ADR, autenticação serviço-a-serviço e migração de dados.
