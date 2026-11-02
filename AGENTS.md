# Instruções do projeto home-ia-monitoring

- Serviço .NET 10 independente do módulo Monitoramento; o portal é autoridade de identidade/autorização e o serviço será dono de câmera/mídia/clips quando a extração avançar.
- `T16-C-A` é contract-only: não implementar captura, HLS, IA, NAS, biometria ou banco operacional neste incremento sem tarefa e migração explícitas.
- Validar o contrato e health no startup; não aceitar tráfego de produção, não expor fora do loopback e não incluir segredos.
- Usar os padrões .NET do workspace, dependências fixadas e testes com `WebApplicationFactory<Program>`.
- Verificações pela raiz: `./garage verify-monitoring-service`, `format-monitoring-service`, `lock-monitoring-service`.
- Atualizar documentação compartilhada/backlog e o inventário de migração quando runtime/dados forem movidos.
