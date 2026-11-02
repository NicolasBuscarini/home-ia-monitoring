# Instruções do projeto home-ia-monitoring

- Serviço .NET 10 independente do módulo Monitoramento; o portal é autoridade de identidade/autorização e o serviço será dono de câmera/mídia/clips quando a extração avançar.
- `T16-C-B-A` é status-read-only: lê manifests/gaps do spool. `T16-C-B-B-A` adiciona `SpoolWriter` (escrita atômica segmento/manifesto) desativado por `MONITORING_CAPTURE_ENABLED=false`; não há hosted capture service, adaptador FFmpeg, HLS, IA, NAS, biometria ou banco operacional ativo sem tarefa/migração explícitas.
- Validar o contrato e health no startup; não aceitar tráfego de produção, não expor fora do loopback e não incluir segredos.
- Usar os padrões .NET do workspace, dependências fixadas e testes com `WebApplicationFactory<Program>`.
- Verificações pela raiz: `./garage verify-monitoring-service`, `format-monitoring-service`, `lock-monitoring-service`.
- Atualizar documentação compartilhada/backlog e o inventário de migração quando runtime/dados forem movidos.
