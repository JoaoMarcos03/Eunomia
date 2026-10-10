# Phase 6: SQL Editor

**Scope (27.1 #10–12 in the editor, #18, #19, #24; 28):** complete query tabs: Run (F9), Run selection (Ctrl+F9), Run current query (Ctrl+Shift+F9), "Send queries one by one" and "Send batch in one go", Stop on errors, `DELIMITER`; one result tab per result set (up to 10, "name (Nr × Mc)", `-- name:`/`-- names:`, Alt+←/→, local sorting on one column); messages in the SQL log in HeidiSQL's format; cancelling the tab itself (D2); a warning for UPDATE/DELETE without WHERE; the error line in pink and the cursor on the error; autocomplete A over the metadata cache; the target session on the tab and "pin tab to session" (D9); tab restore with a journal (PR-21); files (Load with encoding, Save, Save as, Save selection, recent files, external change, > 5 MiB "Run directly"); per-session history and snippets; Query helpers (Columns, SQL functions, keywords, Snippets, History); Find/Replace in the editor and the grid; Un/comment, Ctrl+U/L.

**Requirements:** FAM-Q01–Q05, Q07, Q08, Q10, Q11 (Q06 and Q09 without bind parameters and query profile, 27.2); PR-10, PR-20, PR-21; D2, D9. Out: Explain, Reformat SQL, bind parameters, query profile (27.2).

**Work order:**
1. `App/Query`: running a batch (one by one / batch in one go up to `max_allowed_packet`), collecting up to 10 result sets, log messages, statement classification (unsafe, `USE`), the error line; tests against the server.
2. Result tabs in the query tab (captions, names from comments, Alt+←/→, local sorting) and cancellation per tab; the status "Executing query #n of m".
3. Editor: error line, the session colour in the gutter, target session and pinning to a session, autocomplete A (aliases, `db.table.`, `@@SESSION.`), Find/Replace, commenting, upper/lower case.
4. Tab journal (≤ 2 s) and restore after a crash; files; history (`app.db`) and snippets; the helpers panel.
5. Integration and headless tests; PR-10 (10,000 `SELECT 1`), PR-20 (cancel), PR-21 (kill during a write).

**Done when:** build and tests green; FAM-Q01–Q05, Q07, Q08, Q11 with tests; tabs restored after killing the process; PR-10, PR-20 and PR-21 measured.
