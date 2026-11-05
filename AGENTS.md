# Instruções do projeto home-ia-monitoring

- Serviço .NET 10 independente do módulo Monitoramento; o portal é autoridade de identidade/autorização e o serviço será dono de câmera/mídia/clips quando a extração avançar.
- `T16-C-B-A` é status-read-only: lê manifests/gaps do spool. `T16-C-B-B-A` adiciona `SpoolWriter` atômico e `T16-C-B-B-B` adiciona hosted capture service, adaptadores `synthetic`/`ffmpeg-rtsp`, gaps, retenção, recuperação e settings persistidos. A captura inicia desativada por `MONITORING_CAPTURE_ENABLED=false`; o corte da captura local da Platform é explícito (`CAPTURE_AUTHORITY=remote`) e nunca deve haver dois writers no mesmo spool.
- HLS e materialização de mídia/thumbnail de clipes já pertencem ao serviço; a política de disparo, o manifesto/estado do clipe e a autorização continuam no portal.
- Não há reconhecimento facial real, NAS, IA de reconhecimento ou banco operacional neste serviço. O producer de observação de identidade (`T09-PRODUCER`) existe com fonte `synthetic`, padrão `IDENTITY_OBSERVATION_PRODUCER_ENABLED=false`, fila em memória e sem extração/pesos; embeddings nunca são gravados em disco. Extração real exige tarefa e decisão de modelo.
- Os endpoints `/api/v1/monitoring/*` usam credencial serviço-a-serviço P28 (token HS256 de curta duração) validada no startup por `MONITORING_SERVICE_AUTH_MODE=required|disabled` e `MONITORING_SERVICE_TOKEN_SECRET` (>=32 bytes). Em `required` (default fora de Development) o serviço falha fechado sem segredo; `/health/*` permanece público. O segredo nunca vai ao frontend/log e o serviço continua interno (loopback/rede interna).
- Validar o contrato e health no startup; não aceitar tráfego de produção, não expor fora do loopback e não incluir segredos.
- Usar os padrões .NET do workspace, dependências fixadas e testes com `WebApplicationFactory<Program>`.
- Verificações pela raiz: `./garage verify-monitoring-service`, `format-monitoring-service`, `lock-monitoring-service`; runtime dev via `./garage monitoring` (não altera a captura da Platform).
- Atualizar documentação compartilhada/backlog e o inventário de migração quando runtime/dados forem movidos.
