# home-ia-monitoring

Serviço independente do módulo **Monitoramento** do Home IA. Este repositório inicia a extração de `T16-C` com um contrato versionado e health próprio; a captura/mídia ainda permanece no monólito da platform até as fases de migração de runtime/dados.

## Contrato inicial (T16-C-A)

- `GET /health/live` — liveness sem dependências.
- `GET /health/ready` — prontidão do contrato (`contract-ready`), sem alegar captura.
- `GET /api/v1/monitoring/contract` — manifesto `monitoring` (`schemaVersion=1.0`, `contractVersion=1.0`, `executionMode=contract-only`, capabilities).
- `GET /api/v1/monitoring/capabilities` — mesma projeção de capabilities.

O serviço não autentica usuários, não decide autorização e não acessa mídia/spool nesta fase. O portal continuará sendo a autoridade de sessão/permissão; o serviço receberá decisões de escopo quando a captura for movida.

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
