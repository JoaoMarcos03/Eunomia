# Phase 4: Database Tree

**Scope (27.1 #3, #4; 28):** a metadata engine with a per-session cache and invalidation; the complete tree (optional groups, columns below tables, Size with bars, icons and states, favourites, the 6 tree style options, Expand/Collapse all, F5 and Shift+F5 keeping databases open and the focused object, `popupDB`, drag &amp; drop and double click into the editor); navigation selection → tab, and tab captions and visibility; the Host tab (Databases and Processes; Variables, Status and Command-Statistics visible and disabled, 27.2); the Database tab (object list with configurable columns, multi-selection, rename with F2, Empty, Drop).

**Requirements:** FAM-T01–T09, L04, L08; PR-08 (names ≤ server + 300 ms, statistics in the background), PR-11 (Table filter over 10k nodes ≤ 50 ms).

**Work order:**
1. `src/Engine.Metadata`: a per-session `MetadataCache` (databases, objects per database in two phases: names and then statistics, table definitions), invalidation by scope and a `MetadataChanged` event; tests against the server.
2. Tree: a `TreeModel` with groups, columns, a refresh that keeps the state, per-session favourites (`app.db`), states (database faded until cached, object until selected, the database overlay on `USE`, inactive sessions in grey), engine overlays, size bars; persisted tree style options.
3. `popupDB` and commands (Refresh, Full refresh, Expand/Collapse all, Empty, Drop, Select top 1000 rows, Create database, Disconnect, Connection properties); drag &amp; drop into the editor.
4. Navigation and tabs (FAM-L04/L08): Host (caption = session), "Database: x", "Table: y", Data only for tables and views.
5. `UI.Features.HostTab` (Databases with bars and "Fetch database items"; Processes with Kill and "Process SQL") and `UI.Features.DatabaseTab` (list with visible/hidden columns, status "db: N objects (…)").
6. Headless and integration tests; snapshots of the tree with groups and of the Host/Database tabs.

**Done when:** build and tests green; F5 keeps open databases and focus (integration test); groups, columns, favourites and filters tested; Host and Database tabs with real data; PR-08 measured with 5,000 tables on the local server.
