# Fase 6: SQL Editor

**Âmbito (27.1 #10–12 no editor, #18, #19, #24; 28):** query tabs completas: Run (F9), Run selection (Ctrl+F9), Run current query (Ctrl+Shift+F9), "Send queries one by one" e "Send batch in one go", Stop on errors, `DELIMITER`; um tab de resultado por result set (até 10, "nome (Nr × Mc)", `-- name:`/`-- names:`, Alt+←/→, ordenação local numa coluna); mensagens no SQL log no formato do HeidiSQL; cancel do próprio tab (D2); aviso de UPDATE/DELETE sem WHERE; linha de erro a rosa e cursor no erro; autocomplete A sobre a cache de metadata; sessão alvo na tab e "fixar tab à sessão" (D9); restauro de tabs com journal (PR-21); ficheiros (Load com encoding, Save, Save as, Save selection, recentes, alteração externa, > 5 MiB "Run directly"); histórico por sessão e snippets; Query helpers (Columns, SQL functions, keywords, Snippets, History); Find/Replace no editor e no grid; Un/comment, Ctrl+U/L.

**Requisitos:** FAM-Q01–Q05, Q07, Q08, Q10, Q11 (Q06 e Q09 sem bind parameters e query profile, 27.2); PR-10, PR-20, PR-21; D2, D9. Fora: Explain, Reformat SQL, bind parameters, query profile (27.2).

**Ordem de trabalho:**
1. `App/Query`: execução de um batch (one by one / batch in one go até `max_allowed_packet`), recolha de até 10 result sets, mensagens do log, classificação de instruções (unsafe, `USE`), linha do erro; testes contra o servidor.
2. Result tabs na query tab (captions, nomes por comentário, Alt+←/→, ordenação local) e cancel por tab; status "Executing query #n of m".
3. Editor: linha de erro, cor da sessão no gutter, sessão alvo e fixar à sessão, autocomplete A (aliases, `db.tabela.`, `@@SESSION.`), Find/Replace, comentar, maiúsculas/minúsculas.
4. Journal das tabs (≤ 2 s) e restauro após crash; ficheiros; histórico (`app.db`) e snippets; painel de helpers.
5. Testes de integração e headless; PR-10 (10.000 `SELECT 1`), PR-20 (cancel), PR-21 (kill durante a escrita).

**Feito quando:** build e testes verdes; FAM-Q01–Q05, Q07, Q08, Q11 com testes; tabs restauradas depois de matar o processo; PR-10, PR-20 e PR-21 medidos.
