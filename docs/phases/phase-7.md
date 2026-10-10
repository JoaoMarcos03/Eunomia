# Phase 7: Editing

**Scope (27.1 #14–17 and #20; 28):** editing in the Data tab and in editable results, saved to the server; editors per type (text with a popup, ENUM, SET, date/time with arrows per segment, hex, FK dropdown, function); Insert row, Duplicate (with and without keys), Delete with "Delete N row(s)?"; Insert value ▸; Ctrl+X/Ctrl+V in the grid; the table editor (Basic, Options, Columns, Indexes, Foreign keys, Check constraints, CREATE code, ALTER code); create/drop/empty/rename of tables.

**Requirements:** FAM-G07–G10, E01–E03; D3 (checking the affected rows); TR-17 (per-row edit buffer, `src/Engine.Execution/Editing`). Appendix D B15–B17; appendix C §5.

**Work order:**
1. `Engine.Execution/Editing`: the row key (PK → UNIQUE NOT NULL → every column), INSERT/UPDATE/DELETE with literals per type (NULL, function, numbers, BIT, binary in hex, dates), `LIMIT 1` without a key, checking the affected rows, reading the saved row back; a round trip per type against the server.
2. Data tab: saving on row change and Ctrl+Enter, Esc, triangles, an error keeps the row; Insert/Duplicate/Delete; Ctrl+X/V; Insert value ▸; a "not enough key" warning.
3. Editors per type and the text popup; FK dropdown and function mode.
4. Table editor (model, ALTER/CREATE, UI with the 8 tabs) and create table; a `SHOW CREATE TABLE` diff.
5. Editable results in query tabs (one table with a key); tests, closing the phase, push.

**Done when:** build and tests green; editing with a round trip per type on the 4 CI versions; the generated ALTER has the same effect (equal `SHOW CREATE TABLE`).
