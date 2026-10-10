# Fase 15: roadmap sem decisões do autor

**Objetivo (pedido do utilizador a 9 out):** implementar do `docs/roadmap.md` o que não precisa de decisões do autor (licença, certificados, canais, ferramentas novas) e não arrisca o projeto. Fora: refactors grandes (3.2–3.4), analyzers em massa (2.3), CI com ferramentas novas (2.5–2.11), PostgreSQL. Sem pacotes novos.

**Ordem de trabalho:**
1. 3.1 + 2.12: nomes inseridos no editor pela camada de linguagem (`Engine.Sql`) e pelo `IDialect`; teste de arquitetura sem `MySqlIdentifier` nem tipos `MySql*` fora do driver e do `Engine.Sql`.
2. 2.4: testes headless com timeout por teste e diálogo modal inesperado a falhar o teste.
3. 4.4: teste que percorre as janelas e exige `AutomationProperties.Name` nos controlos interativos.
4. 2.2: cobertura de código no CI (coletor do Test SDK), relatório por projeto e limiar que só sobe.
5. 4.3 e 4.5: modelo de ameaças documentado; contraste dos tokens dos temas (WCAG 2.1 AA) verificado por teste.
6. Funcionalidades 1.x de leitura ou de baixo risco, por ordem de uso: Table tools › Maintenance; Save BLOB e Image preview; Query profile; code folding; histórico pesquisável; paleta de comandos; outras se o tempo der.

**Feito quando:** cada item com testes, build e testes verdes, `docs/progress.md`, `CHANGELOG.md`, `docs/roadmap.md` e guia atualizados; push para um branch neutro.

**Estado (9 out):** concluída. Feito: 1–5 (4.5 sem os snapshots a 150% e 200%) e, das 1.x, Maintenance, Copy table, Save BLOB to file, Image preview, code folding e a paleta de comandos; a revisão a olho dos snapshots apanhou o `_` perdido nos menus e caixas (corrigido) e o guarda dos diálogos apanhou um erro espúrio depois de um Drop (corrigido). Detalhe em `docs/progress.md`.
