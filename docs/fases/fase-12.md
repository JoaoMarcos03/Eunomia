# Fase 12: Hardening (MVP)

**Âmbito (secção 28; critérios 27.3):** segurança (segredos nos logs), log de diagnóstico, crash recovery, stalls da UI thread, soak, stress B14, acessibilidade, pacote portable, documentação de utilizador e o estado dos critérios de saída. Fora do alcance desta máquina: soak de 24 h, instalador e assinatura (certificados), beta com utilizadores, medições na máquina de referência (vão para "Precisa do Marcos").

**Requisitos:** secção 21.5 (dois logs, redação, stalls), 23.2 (máscara de `IDENTIFIED BY` / `SET PASSWORD`, connection strings nunca registadas, nada sai da máquina), 24–26 (B14), 27.3.

**Ordem de trabalho:**
1. SQL log com os literais de passwords mascarados (`IDENTIFIED BY`, `SET PASSWORD`, `PASSWORD()`); teste que procura a password de teste em logs, settings, journals e ficheiros do perfil depois de ligar, falhar e correr comandos.
2. `Infra.Observability`: log de diagnóstico em JSON lines com níveis, rotação e redação; exceções não tratadas (UI thread, tarefas, domínio) num crash report sem SQL nem valores, diálogo e continuação quando possível; monitor de stalls da UI thread (> 250 ms) no log.
3. Soak (`EUNOMIA_SOAK_MINUTES`) e stress B14 ao nível do motor, com memória e latência; corrida curta aqui.
4. Acessibilidade das peças novas (nomes de automação, teclado), pacote portable (`build/`), documentação de utilizador em `docs/utilizador/`, e `docs/mvp-criterios.md` com o estado de cada critério do 27.3.

**Feito quando:** build e testes verdes; critérios automáticos do 27.3 com estado registado; push.
