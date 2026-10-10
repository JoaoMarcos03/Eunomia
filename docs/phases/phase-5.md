# Phase 5: Data Grid

**Scope (27.1 #5–#13; 28):** the complete Data tab over real tables: the generated SELECT (truncation to 256 with a key, `*` when possible), keyset/offset sources with Next and Show all (configurable and visible limit, D5), a `BlockStore` with a budget and spill, the label with the count variants, accumulated server-side sorting and the Sorting dialog, Columns (v/t), the WHERE editor with recent filters, the multi column filter, Quick Filter, the regex filter panel (grid and lists), per-table persistence in `app.db`; a grid with "no. name" headers, key icons, PK in bold, automatic width ≤ 300 px; cell Ctrl+C/X/V with the full value and NULL; Copy as in the 16 formats; Export grid rows.

**Requirements:** FAM-G01–G06, G11–G16 (G15 partial: Open URL, Follow Foreign Key; BLOBs and images are 27.2), C01–C06; PR-04, PR-05, PR-09, PR-17; D5, D6, D10.

**Work order:**
1. `App/Data`: the Data tab SELECT generator (columns, truncation, WHERE, ORDER BY, keyset/offset, count) and per-table state (`HiddenColumns`, `Filter`, `Sort`, recent filters) in `app.db`; tests.
2. `Engine.Results`: `TableBrowseSource` (keyset with a key, offset without a key, Next, Show all up to the limit), memory budget and spill in the `BlockStore`; the full value on demand; tests against the server.
3. Data tab (`UI.Features.DataTab`): label, Next, Show all, Sorting (n), Columns (v/t), Filter; headers with the number, key, ▲▼ with the position; click and Shift+click; Reset sorting (Alt+S).
4. Filters: the WHERE panel (F9, Ctrl+W, recent filters, Insert function), the multi column filter (F7), Quick Filter with SQL in the captions and Ctrl+Shift+1…6, the regex filter panel (500 ms, "x out of y matching. z hidden.") in the grid and in lists.
5. Clipboard and export: Ctrl+C/X/V (cell, full value, NULL), Copy column names, the 16 Copy as formats (writers in `App`, D6 fixed), the Export grid rows dialog.
6. Keyboard and selection tests (FAM-G05/G06), snapshots, PR-09 (1,000 rows ≤ server + 100 ms), PR-04/PR-05/PR-17 with the existing benchmarks.

**Done when:** build and tests green; the Data tab opens real tables with Next/Show all, sorting, filters and columns persisted per table; the 16 formats tested against the 4.6 examples (HeidiSQL golden files are left for the author); PR-09 measured.
