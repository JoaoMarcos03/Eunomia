# 01 — Connections, Session Manager, connection layer, settings persistence

Source: HeidiSQL `master` @ `c123ccdb9e3f58d4390a05deb31f6e6effa33875` (2026-09-22). All links point at that commit.

**Tag legend:** **[CODE]** confirmed in Pascal source (linked) · **[DFM]** confirmed in a form definition (linked) · **[INFERENCE]** my reading of the code, not stated directly · **[UNVERIFIED]** plausible but not confirmed in this repo.

---

## 0. Key findings (read this first)

| # | Finding | Why it matters for the successor |
|---|---|---|
| K1 | **Every query first sends a ping** (`mysql_ping`, `PQsendQuery('')`, ADO `SELECT 1`) and silently reconnects if it fails: `TDBConnection.Query` → `Ping(True)` [CODE] [dbconnection.pas#L3738-L3754](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3738-L3754), [dbconnection.pas#L3598-L3622](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3598-L3622). | That's one extra round trip per statement, which doubles latency on slow links. Reconnects are also invisible: session state, temp tables and open transactions are lost without a warning [INFERENCE]. |
| K2 | **Almost all DB work runs on the UI thread.** Only the SQL editor's batch execution runs in a worker (`TQueryThread`) [CODE] [apphelpers.pas#L3301-L3383](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3301-L3383). Metadata, data grid fetches [main.pas#L6160-L6164](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6160-L6164), connecting, SSH setup and keep-alive pings all run on the main thread. | A slow server or a dead network freezes the whole app, including every other open session [INFERENCE]. |
| K3 | If the main thread needs a connection while a worker is using it, it **blocks in `WaitFor`** until the worker finishes [CODE] [dbconnection.pas#L3740-L3747](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3740-L3747). | Clicking the tree of a session with a long query running freezes the UI [INFERENCE]. |
| K4 | **Cancel = open a second "helper" connection and send `KILL QUERY <id>`**, taking the ID from the *currently active* session, not from the tab's session [CODE] [main.pas#L14330-L14362](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14330-L14362). MSSQL uses `KILL %d`, which drops the whole session. SQLite's template is `KILL %d`, which is invalid SQL there. | For SSH sessions, cancelling spawns a second tunnel [INFERENCE]. There is no native cancel (`mysql_kill`, `PQcancel`, `sqlite3_interrupt` are not used [CODE: grep]). |
| K5 | All MySQL results are **fully buffered** with `mysql_store_result`. There is no `use_result` or streaming anywhere [CODE] [dbconnection.pas#L3796](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3796). PG, SQLite and FireDAC also buffer everything [dbconnection.pas#L3990-L4007](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3990-L4007), [dbconnection.pas#L4041-L4042](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4041-L4042). | Memory grows with result size. The data grid limits this with LIMIT paging instead (`DatagridRowsPerStep`=1000, max 100000) [apphelpers.pas#L3886-L3887](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3886-L3887). |
| K6 | "Query timeout" means different things per engine. **MySQL: only the *connect* timeout** (`MYSQL_OPT_CONNECT_TIMEOUT`) [dbconnection.pas#L2568](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2568). PG: server-side `statement_timeout` [dbconnection.pas#L2957](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2957). MSSQL: ADO `CommandTimeout` [dbconnection.pas#L2800](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2800). SQLite: `busy_timeout` [dbconnection.pas#L3074](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3074). Default is **30 s** [apphelpers.pas#L3956](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3956). | With defaults, PG and MSSQL user queries are cut off after 30 s, while MySQL queries never time out [INFERENCE]. |
| K7 | Passwords use **reversible obfuscation**: each char code gets a random salt of 1..9 added, then it is hex-encoded, with the salt digit appended [CODE] [apphelpers.pas#L626-L685](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L626-L685). | Anyone who can read the registry or an export file can recover the password. There is no DPAPI or Credential Manager. |
| K8 | **Portable mode still goes through the registry.** The settings file is loaded into `HKCU\Software\HeidiSQL Portable <PID>\` at startup and written back only in the destructor [CODE] [apphelpers.pas#L3831-L3843](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3831-L3843), [apphelpers.pas#L4186-L4219](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4186-L4219). | If the app crashes, all changes since startup are lost. A later instance then deletes the orphaned key [INFERENCE]. |
| K9 | **Settings import never overwrites**: a value that already exists is skipped [CODE] [apphelpers.pas#L4644-L4645](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4644-L4645). Separately, the `.reg` branch compares `ExtractFileExt()` (which returns `.reg`) against `'reg'`, so it never fires [CODE] [main.pas#L3209](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3209). | This is non-obvious behaviour to either keep or fix on purpose. |
| K10 | **The SSH tunnel is an external `plink.exe` or `ssh.exe` process.** The password goes on the command line (`-pw`). The code always waits the full SSH timeout before connecting ("Todo: Find a way to wait only until connection is established") [CODE] [dbconnection.pas#L1139-L1142](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1139-L1142), [dbconnection.pas#L1183-L1191](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1183-L1191). OpenSSH gets `-o StrictHostKeyChecking=no` [dbconnection.pas#L1147-L1148](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1147-L1148). | Every SSH connect costs at least the timeout (default **4 s** [apphelpers.pas#L3944](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3944)). The password is visible in the process list, and host keys are not checked when OpenSSH is used [INFERENCE]. |
| K11 | The capability system is thin. There are 12 `frXXX` flags and they are **only implemented for the MySQL family**; every other engine returns False [CODE] [dbconnection.pas#L6780-L6804](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L6780-L6804). The rest of the dialect handling is ~70 `TQueryId` templates per provider plus about 255 ad-hoc `IsAnyXXX` / `NetTypeGroup` / `ServerVersionInt` checks across the source [CODE: grep count, 139 of them in dbconnection.pas]. | A successor needs a real capability model. See §12. |

---

## 1. Session Manager (`connections.pas` / `connections.dfm`)

### 1.1 Window layout
- Modal form titled "Session manager". Design size is 749×506, with a minimum of 640×470 [DFM] [connections.dfm#L5-L10](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L5-L10). At runtime the caption is `HeidiSQL <ver> - Session manager: <folder\session path>` [CODE] [connections.pas#L271-L276](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L271-L276).
- The title bar has a Help "?" button that opens the online help anchor `connecting` [CODE] [connections.pas#L261-L268](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L261-L268).
- **Left panel** (`pnlLeft`, min width 200, persisted width `SessionManager_ListWidth`=220) [DFM] [connections.dfm#L942-L955](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L942-L955):
  - Filter box `editSearch` at the top: search icon on the left, clear "x" button on the right, placeholder text "Filter" [connections.dfm#L1037-L1056](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L1037-L1056).
  - Session tree `ListSessions` (a VirtualStringTree) below it [connections.dfm#L956-L1036](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L956-L1036).
- A splitter sits between the two panels [connections.dfm#L26-L39](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L26-L39).
- **Right side:** `PageControlDetails` with tabs **Start / Settings / SSH tunnel / Advanced / SSL / Statistics** [DFM] [connections.dfm#L104-L929](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L104-L929).
  - Selecting a **session** shows every tab except Start.
  - Selecting a **folder or nothing** shows only Start [CODE] [connections.pas#L1008-L1013](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1008-L1013).
- **Start tab:** help text plus an "Import settings file ..." button [connections.dfm#L119-L147](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L119-L147).
  - With zero sessions the text is: *"New here? In order to connect to a server, you have to create a so called "session" at first. Just click the "New" button…"*.
  - Otherwise it says *"Please click a session on the left list to edit parameters, doubleclick to open it."* [CODE] [connections.pas#L1020-L1027](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1020-L1027).
- **Bottom-left buttons:** **New** (split button, drop-down `popupNew`), **Save**, **Delete**. They share the left panel's width equally [DFM] [connections.dfm#L40-L103](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L40-L103); resizing logic in [connections.pas#L1660-L1671](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1660-L1671).
- **Bottom-right buttons:** **Open** (the default button; Enter), **Cancel** (Esc, `ModalResult=2`), **More** (split button). Each is a third of the right panel's width, clamped to 50..100 px [CODE] [connections.pas#L1673-L1683](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1673-L1683).
- Window position and size, list width and the column setup are saved on close and restored on show. The window is also moved back on-screen if its monitor was unplugged (`MakeFullyVisible`) [CODE] [connections.pas#L386-L416](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L386-L416).
- On show, the last active session (`LastActiveSession`, falling back to the first entry of `LastSessions`) gets the focus [CODE] [connections.pas#L420-L432](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L420-L432).

### 1.2 Session tree columns [DFM] [connections.dfm#L992-L1035](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L992-L1035), text in [connections.pas#L795-L827](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L795-L827)

| Pos | Column | Visible by default | Content |
|---|---|---|---|
| 0 | Session name | yes | Session name. When the focused session has unsaved edits a ` *` suffix is added, plus a pencil overlay icon (#162) [CODE] [connections.pas#L780-L782](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L780-L782), [connections.pas#L810-L814](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L810-L814) |
| 1 | Host | yes | Hostname |
| 2 | Port | yes | Port |
| 3 | User | no | Username |
| 4 | Version | no | Full server version string from the last connect (`ServerVersionFull`) [CODE] [dbconnection.pas#L1530](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1530) |
| 5 | Last connect | yes | `DateTimeToStr(LastConnect)` or empty |
| 6 | Counter | no (right-aligned) | **Number of successful connects** (`ConnectCount`, incremented in `InitConnection`) [CODE] [main.pas#L4411-L4417](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4411-L4417) |
| 7 | Comment | yes | Session comment |

- Folders show only their name; every other column is empty [CODE] [connections.pas#L803-L807](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L803-L807).
- Headers can be resized, dragged to reorder, auto-resized, and clicked to sort (sort glyphs shown). The header context menu is `MainForm.popupListHeader`, used to show/hide columns [DFM] [connections.dfm#L965-L967](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L965-L967).
- Widths, visibility, positions and sort are saved through `SaveListSetup` / `RestoreListSetup`, under settings `ColWidths_%s`, `ColsVisible_%s`, `ColPositions_%s`, `ColSort_%s` [CODE] [connections.pas#L396](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L396), [apphelpers.pas#L4138-L4141](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4138-L4141).
- **Icon per row** comes from the network type plus the vendor sniffed from the last `ServerVersion` string [CODE] [dbconnection.pas#L1864-L1895](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1864-L1895):
  - Folder=174.
  - MySQL family: MariaDB 166, MySQL 164, Percona 169, TokuDB 171, InfiniDB 172, Infobright 173, MemSQL 194, ProxySQL 197, RDS 205.
  - MSSQL 123 (Azure 188); PG 187 (Redshift 195); SQLite 196; Interbase 203; Firebird 204.

### 1.3 Folders, sorting, drag & drop, rename
- **Folders** are registry keys under `Servers\` that contain a value named `Folder`. Nesting depth is unlimited, and the session path is written as `Folder\Sub\Session` [CODE] [apphelpers.pas#L4578-L4600](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4578-L4600), [dbconnection.pas#L1472-L1474](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1472-L1474).
- **Sorting:**
  - The tree auto-sorts (`toAutoSort`) on the header sort column, default 0 [DFM] [connections.dfm#L967](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L967), [connections.dfm#L973](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L973).
  - Comparison is `CompareAnyNode` on the cell text.
  - **"Folders at top"** is a context-menu checkbox, default **on** (`SessionManager_ListFoldersAtTop`). It keeps folders above sessions whatever the sort direction [CODE] [connections.pas#L871-L888](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L871-L888), [apphelpers.pas#L4045](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4045).
  - Because of auto-sort there is no manual ordering [INFERENCE].
- **Drag & drop** (`DragMode=dmAutomatic`, `toFullRowDrag`) [DFM] [connections.dfm#L962](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L962), [connections.dfm#L974](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L974):
  - Dropping *on a folder* moves the node in as its first child.
  - Dropping above or below a node moves it into that node's parent.
  - Rejected drops: onto its own current parent, reorders within the same folder, into itself, or into its own descendants [CODE] [connections.pas#L952-L987](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L952-L987).
  - A drop runs `TRegistry.MoveKey`. If the target already has a node with the same name it shows "Session "%s" already exists!" [CODE] [connections.pas#L898-L949](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L898-L949).
- **Rename:**
  - Triggered by **F2** (context menu, ShortCut 113) [DFM] [connections.dfm#L1062-L1069](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L1062-L1069) or by in-place editing (`toEditable`) with a custom editor [CODE] [connections.pas#L890-L895](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L890-L895).
  - `\` in the new name is replaced with `-` (issue #682).
  - A duplicate sibling name is rejected. This also means a **case-only rename is refused**: the message tells the user to rename twice.
  - The registry key is moved, and the `SessionPath` of any open connection with that session is updated too [CODE] [connections.pas#L1150-L1186](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1150-L1186).
- **Double-click** opens the session, but only when the click lands on the label or icon, not on the expand button (#820) [CODE] [connections.pas#L1189-L1204](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1189-L1204). On a folder, a double-click toggles expansion (`toToggleOnDblClick`) [DFM] [connections.dfm#L974](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L974).
- **Incremental search** runs across all columns (`IncrementalSearch = isAll`) [DFM] [connections.dfm#L970](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L970).

### 1.4 Filter box
- The box has a 500 ms debounce timer [DFM] [connections.dfm#L1211-L1217](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L1211-L1217), [CODE] [connections.pas#L1755-L1767](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1755-L1767).
- The filter text is used as a **case-insensitive regular expression**. An invalid regex behaves like an empty filter.
- It is tested against **every column's text, including hidden columns**, of leaf nodes. Nodes that don't match are hidden, and the parent folders of nodes that do match are kept visible [CODE] [extra_controls.pas#L381-L425](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/extra_controls.pas#L381-L425).
- **Ctrl+F** ("Filter ...", ShortCut 16454) focuses the box [DFM] [connections.dfm#L1204-L1209](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L1204-L1209).

### 1.5 Buttons and menus

| Control | Items / behaviour | Source |
|---|---|---|
| **New** (split button) | Plain click: new session inside the focused folder, or inside the focused session's folder. Drop-down: *Session in root folder*, *Session in selected folder*, *Folder in root folder*, *Folder in selected folder*. The "in selected folder" items are disabled unless the selection is inside a folder. The new node is named `Unnamed`, `Unnamed-1`, …, **saved to the registry immediately**, and put straight into rename mode. | [DFM] [connections.dfm#L1124-L1152](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L1124-L1152); [CODE] [connections.pas#L609-L656](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L609-L656), [connections.pas#L1015-L1016](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1015-L1016) |
| **Save** / Ctrl+S | Enabled only when the focused session has modifications. Writes every field. Also pushes `SessionColor` live to already-open connections and repaints the main DB tree. | [connections.pas#L488-L549](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L488-L549), [connections.pas#L1649](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1649) |
| **Delete** / Del | Confirms with "Delete session "%s"?" and deletes the registry key. **Deleting a folder deletes all of its subkeys, and so every session inside it** (per the comment in `DeleteCurrentKey`). | [connections.pas#L664-L685](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L664-L685), [apphelpers.pas#L4330-L4344](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4330-L4344) |
| **Duplicate / save as ...** / F12 | InputQuery for a new name, which must be unique among siblings. Saves the *current form values* as a new session next to the original. | [DFM] [connections.dfm#L1077-L1083](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L1077-L1083); [connections.pas#L561-L590](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L561-L590) |
| **Open** (default button) | Builds parameters from the *unsaved form values* (`CurrentParams`), shows a small dot animation on the button (200 ms timer), and calls `MainForm.InitConnection`. The form closes with `mrOK` on success; on failure it stays open and the statistics refresh. | [connections.pas#L461-L485](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L461-L485), [connections.pas#L701-L762](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L701-L762) |
| **Cancel** / Esc | `ModalResult=mrCancel`. At app start, cancelling this dialog quits the app. | [DFM] [connections.dfm#L65-L75](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L65-L75); [main.pas#L2363-L2366](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2363-L2366) |
| **More** (split button, and a plain click also opens the menu) | Preferences, Check for updates, Import settings file ..., Export settings file ..., General help, About | [DFM] [connections.dfm#L1153-L1175](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L1153-L1175); [connections.pas#L552-L558](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L552-L558) |
| Context menu | Rename (F2), Save (Ctrl+S), Duplicate/save as (F12), Delete (Del), New session, New folder, Filter ... (Ctrl+F), Folders at top (check) | [DFM] [connections.dfm#L1058-L1111](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L1058-L1111) |

**Dirty-state handling:**
- `Modification` compares every control with the stored values [CODE] [connections.pas#L1450-L1513](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1450-L1513).
- Changing focus, clicking New, or closing prompts "Save modifications?" with Yes/No/Cancel [connections.pas#L1516-L1531](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1516-L1531).
- **Exception: if only the password changed, there is no prompt.** A password typed but never saved is used for Open and then thrown away [CODE] [connections.pas#L1499-L1501](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1499-L1501), [connections.pas#L1518](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1518). I read this as deliberate ("type the password each time") [INFERENCE].

### 1.6 Fields per tab (defaults are from `InitSetting` / `TConnectionParameters.Create`)
Defaults for a new session come from [dbconnection.pas#L1404-L1456](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1404-L1456) and [apphelpers.pas#L3926-L3960](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3926-L3960). Registry value names are shown in `code`.

**Settings tab** (top to bottom) [DFM] [connections.dfm#L148-L352](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L148-L352)

| Field | Control | Default | Registry value | Enabled when [CODE] [connections.pas#L1549-L1657](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1549-L1657) |
|---|---|---|---|---|
| Network type | image combo, list ordered by DB family | MariaDB or MySQL (TCP/IP) (`NetType`=0) | `NetType` (int = enum ordinal) | always |
| Library | drop-down list (no free text) | per family, see §2.2 | `Library` | always |
| Hostname / IP | edit + drop-down button (SQLite only: "Find database files..." / "Add database files...") | `127.0.0.1`; empty for SQLite | `Host` | always |
| Prompt for credentials | checkbox | off | `LoginPrompt` | MySQL, MSSQL, PG. Mutually exclusive with Windows auth [connections.pas#L1245-L1256](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1245-L1256) |
| Use Windows authentication | checkbox | off | `WindowsAuth` | MSSQL and MySQL |
| User | edit (SQLite-encrypted: a drop-down of cipher names read from `sqlite3mc`) | `root` / `sa` / `postgres` / `sysdba` / '' | `User` | MySQL, MSSQL, PG, IB/FB when neither prompt nor win-auth is on; also SQLite-encrypted |
| Password | masked edit | '' | `Password` (obfuscated, §7) | same as User |
| Port | edit + updown | 3306; 6032 for ProxySQL; 0 for MSSQL ("autodetection by driver"); 5432; 3050 | `Port` (string) | MySQL TCP/SSH/ProxySQL/RDS, MSSQL TCP, PG TCP/SSH, IB/FB TCP |
| Compressed client/server protocol | checkbox | off | `Compressed` | MySQL family only |
| Databases | edit + drop-down, placeholder "Separated by semicolon" | '' | `Databases` | MySQL, MSSQL, PG, IB/FB, SQLite-encrypted |
| Comment | multiline memo | '' | `Comment` | always |

**Relabelling by network type** [CODE] [connections.pas#L1563-L1583](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1563-L1583):
- Named pipe: Host becomes "Socket name:".
- PG: "Database:" with placeholder "Single database name".
- SQLite:
  - Host becomes "Database filename(s):". Double-clicking the field or its button opens a multi-select file dialog; multiple files are joined with `|`. Files in the app folder are stored without a path [connections.pas#L1706-L1752](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1706-L1752).
  - User becomes "Cipher:", Password becomes "Key:".
  - Databases becomes "Encryption parameters:" with placeholder `kdf_iter=4000;legacy=1;...`.
- **Changing the network type between families** resets port, username, hide-DB pattern and host to the new family's defaults *unless the user has edited them*. It also sets "Use SSH tunnel" to the family default and reloads the Library list with the default library preselected [CODE] [connections.pas#L1411-L1447](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1411-L1447).

**SSH tunnel tab** [DFM] [connections.dfm#L353-L528](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L353-L528)

| Field | Default | Registry value | Notes |
|---|---|---|---|
| Use SSH tunnel | on for `MySQL (SSH tunnel)`, `MySQL on RDS`, `PostgreSQL (SSH tunnel)` | `SSHtunnelActive` | Checkbox enabled only for types where `SshSupport` is true: those three plus **MSSQL TCP/IP** [CODE] [dbconnection.pas#L1733-L1736](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1733-L1736), [dbconnection.pas#L1970-L1973](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1970-L1973) |
| SSH executable | editable combo. Lists `*.exe` in the app folder matching `([pk]link\|putty)`, then `ssh.exe` | '' (legacy global `PlinkExecutable`=`plink.exe`) | `SshExecutable` [connections.pas#L327-L336](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L327-L336), [dbconnection.pas#L1536-L1540](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1536-L1540) |
| SSH host + port | '' + **22** | `SSHtunnelHost`, `SSHtunnelHostPort` | When empty, the DB host is used; the placeholder shows the DB host [connections.pas#L1216-L1220](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1216-L1220) |
| Username / Password | '' | `SSHtunnelUser`, `SSHtunnelPassword` (obfuscated) | |
| SSH timeout (seconds) | **4** (DFM updown min 1) | `SSHtunnelTimeout` | Clamped to at least 1 on load [dbconnection.pas#L1507-L1509](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1507-L1509) |
| Private key file | '' | `SSHtunnelPrivateKey` | file picker, all files |
| Local port | **DB port + 1** when the session is created | `SSHtunnelPort` | [dbconnection.pas#L1433](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1433) |

**Advanced tab** [DFM] [connections.dfm#L529-L737](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L529-L737) — effects are in §5.

| Field (top to bottom) | Default | Registry value | Enabled for |
|---|---|---|---|
| Startup script | '' | `StartupScriptFilename` | all |
| Query timeout (s) | **30** | `QueryTimeout` | all |
| Ping every X seconds | **20** (0 disables; max 86400) | `KeepAlive` | all |
| Use own client time zone | off | `LocalTimeZone` | MySQL |
| Get full table status | **on** | `FullTableStatus` | MySQL (not ProxySQL), PG, SQLite |
| Enable cleartext authentication | off | `CleartextPluginEnabled` | MySQL |
| Force Unicode (disable on old servers only) | **on** | `ForceUnicode` | MySQL |
| Background color | clNone | `TreeBackground` | all |
| Hide database pattern (placeholder "Regular expression") | ''; `^pg_temp_\d` for PG | `IgnoreDatabasePattern` | all |
| Log queries to file: path + "DDL queries (CREATE, ALTER, ...)" + "DML queries (INSERT, UPDATE, ...)" | path `<AppData>\HeidiSQL\Logs\%session\%db\%y%m%d.sql`; both boxes off | `LogFilePath`, `LogFileDdl`, `LogFileDml` | path enabled only if DDL or DML is checked |

The **Background color** picker offers standard, extended and system colours, *None*, *Default* and *Custom...*. It also adds the colours used by other sessions as "Custom color: #RRGGBB" entries [DFM] [connections.dfm#L669-L682](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L669-L682), [CODE] [connections.pas#L1266-L1285](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1266-L1285).

**SSL tab** [DFM] [connections.dfm#L738-L867](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L738-L867)

| Field | Default | Registry value |
|---|---|---|
| Use SSL | off (auto-on for old sessions that have an SSL key set) | `SSL_Active` [dbconnection.pas#L1515](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1515) |
| SSL private key / CA certificate / certificate | '' (file pickers, `.pem` / `.crt` filter) | `SSL_Key`, `SSL_CA`, `SSL_Cert` |
| SSL cipher | '' (placeholder "List of permissible ciphers…") | `SSL_Cipher` |
| Certificate verification | **index 2 = "Verify CA and host name identity (may fail with self-signed certs and wildcard cn)"**. 0 = "No verification (insecure)", 1 = "Verify CA (insecure)" | `SSL_Verification` [apphelpers.pas#L3951](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3951) |

Enabled only for MySQL TCP/SSH/ProxySQL/RDS and PG TCP/SSH. **MSSQL has no SSL UI** [CODE] [connections.pas#L1631](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1631).

**Statistics tab** [DFM] [connections.dfm#L868-L927](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L868-L927), refreshed every 60 s and on focus change [CODE] [connections.pas#L1100-L1136](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1100-L1136):
- *Created:* from `SessionCreated`, which is written the first time a session is saved [dbconnection.pas#L1562-L1563](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1562-L1563).
- *Last connect:* shown as a friendly "ago" text with the exact time as a tooltip, or "unknown or never".
- *Successful connects:* `ConnectCount`.
- *Unsuccessful connects:* `RefusedCount`, incremented when `InitConnection` catches an `EDbError` [main.pas#L4457-L4466](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4457-L4466).

### 1.7 Session colour rendering
The colour is painted wherever it differs from the default (`clNone`):
- Every cell of the session-manager row [CODE] [connections.pas#L855-L869](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L855-L869).
- **Every node of that connection in the main DB tree** (session, databases, tables, columns) [main.pas#L14124-L14142](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14124-L14142).
- The query editor gutter [main.pas#L6771-L6776](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6771-L6776).
- The result tabset (selected colour, plus a lighter shade for unselected tabs) [main.pas#L3383-L3391](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3383-L3391).
- The data grid's row-number (`#`) column [main.pas#L11593-L11594](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11593-L11594).

### 1.8 Import/export of settings
- The More menu and the Start tab both have *Import settings file ...* and *Export settings file ...* [DFM] [connections.dfm#L1163-L1168](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L1163-L1168).
- Export writes the **whole HeidiSQL registry subtree**, sessions included, to a UTF-8 (no BOM) text file [CODE] [main.pas#L3179-L3197](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3179-L3197), [apphelpers.pas#L4666-L4713](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4666-L4713).
- **Line format:** `<subkey path>\<ValueName><|||><TRegDataType ordinal><|||><value>`.
  - CR and LF inside values are encoded as `<{{{>` and `<}}}>` [const.inc#L21-L24](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/const.inc#L21-L24).
  - Only string and integer values are supported (`rdString`=1, `rdInteger`=3 [INFERENCE: Delphi's `TRegDataType` ordinals]).
- **Import is add-only**: existing values are left alone [apphelpers.pas#L4644-L4645](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4644-L4645).
- After an import, the session manager re-reads the tree every timer tick for up to 10 s. If nothing shows up it warns "Imported sessions could not be detected. Restarting may solve that" [connections.pas#L593-L606](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L593-L606).

---

## 2. Network types and client libraries

### 2.1 `TNetType` enum [CODE] [dbstructures.pas#L15-L35](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.pas#L15-L35), group mapping [dbconnection.pas#L1710-L1730](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1710-L1730), display names [dbconnection.pas#L1649-L1707](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1649-L1707)
The ordinal is what gets persisted, so it must stay stable for import compatibility.

| Ord | Enum | UI name | Group → connection class | Transport detail |
|---|---|---|---|---|
| 0 | ntMySQL_TCPIP | MariaDB or MySQL (TCP/IP) | ngMySQL → `TMySQLConnection` (libmysql/libmariadb C API) | host:port |
| 1 | ntMySQL_NamedPipe | … (named pipe) | ngMySQL | host `.`, socket = Hostname field [dbconnection.pas#L2512-L2515](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2512-L2515) |
| 2 | ntMySQL_SSHtunnel | … (SSH tunnel) | ngMySQL | plink/ssh → 127.0.0.1:localport |
| 3 | ntMSSQL_NamedPipe | Microsoft SQL Server (named pipe) | ngMSSQL → `TAdoDBConnection` (ADO / OLE DB) | `Network Library=DBNMPNTW` [dbconnection.pas#L2745-L2752](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2745-L2752) |
| 4 | ntMSSQL_TCPIP | … (TCP/IP) | ngMSSQL | `DBMSSOCN`, `Data Source=host,port` if port≠0; optional SSH |
| 5 | ntMSSQL_SPX | … (SPX/IPX) | ngMSSQL | `DBMSSPXN` |
| 6 | ntMSSQL_VINES | … (Banyan VINES) | ngMSSQL | `DBMSVINN` |
| 7 | ntMSSQL_RPC | … (Windows RPC) | ngMSSQL | `DBMSRPCN` |
| 8 | ntPgSQL_TCPIP | PostgreSQL (TCP/IP) | ngPgSQL → `TPgConnection` (libpq) | |
| 9 | ntPgSQL_SSHtunnel | PostgreSQL (SSH tunnel) | ngPgSQL | |
| 10 | ntSQLite | SQLite | ngSQLite → `TSQLiteConnection` (sqlite3 C API) | file(s) |
| 11 | ntMySQL_ProxySQLAdmin | ProxySQL Admin (Experimental) | ngMySQL | default port 6032; special-cased queries throughout |
| 12 | ntInterbase_TCPIP | Interbase (TCP/IP, experimental) | ngInterbase → `TInterbaseConnection` (FireDAC) | `Protocol=ipTCPIP` [dbconnection.pas#L3169-L3179](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3169-L3179) |
| 13 | ntInterbase_Local | Interbase (Local, experimental) | ngInterbase | `ipLocal` |
| 14 | ntFirebird_TCPIP | Firebird (TCP/IP, experimental) | ngInterbase | |
| 15 | ntFirebird_Local | Firebird (Local, experimental) | ngInterbase | |
| 16 | ntMySQL_RDS | MySQL on RDS | ngMySQL | SSH tunnel on by default; kill uses `mysql.rds_kill_query` [dbstructures.mysql.pas#L3280-L3293](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L3280-L3293) |
| 17 | ntSQLiteEncrypted | SQLite (Encrypted) | ngSQLite | SQLite3 Multiple Ciphers (`sqlite3mc.dll`) |

- An unknown `NetType` read from the registry falls back to MySQL TCP/IP with an error dialog [CODE] [dbconnection.pas#L1476-L1483](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1476-L1483).
- **Vendor sub-detection** is plain substring matching on the stored server version string: `-mariadb`, `percona server`, `tokudb`, `infinidb`, `infobright`, `memsql`, `azure`, `redshift` [CODE] [dbconnection.pas#L1769-L1849](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1769-L1849).
- **"Strict MySQL"** means the version contains `mysql`, or the connection is RDS, or the major version is one of 3, 4, 5, 8 [dbconnection.pas#L1775-L1784](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1775-L1784).

### 2.2 Library selection [CODE] [dbconnection.pas#L1927-L1947](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1927-L1947), [dbconnection.pas#L2025-L2088](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2025-L2088)
- **Defaults:**
  - MySQL: `libmariadb.dll`
  - MSSQL: OLE DB provider `MSOLEDBSQL`
  - PG: `libpq.dll`
  - SQLite: `sqlite3.dll`; encrypted: `sqlite3mc.dll`
  - Interbase: `ibclient64.dll` (64-bit) or `gds32.dll`
  - Firebird: `fbclient.dll`
- **The drop-down lists files in the application folder** (`GetLibDir` = the exe folder [apphelpers.pas#L1318-L1327](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L1318-L1327)) that match:
  - `^lib(mysql|mariadb).*\.dll$`
  - `^libpq.*\.dll$`
  - `^sqlite.*\.dll$`, or `^sqlite3mc.*\.dll$` for encrypted
  - `^(gds32|ibclient|fbclient).*\.dll$`
- For MSSQL it lists the installed OLE DB providers matching `^(MSOLEDBSQL|SQLOLEDB)`. Every provider is listed if the hidden registry value `AllProviders` is true [apphelpers.pas#L3935](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3935).
- The list is cached per NetType for the life of the process (a class var).
- If the stored library file doesn't exist, it is silently replaced by the default on load [CODE] [dbconnection.pas#L1495-L1497](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1495-L1497). That check is `FileExists(GetLibDir+name)` whatever the family, so for MSSQL (provider names, not files) the stored provider **always reverts to `MSOLEDBSQL`** [INFERENCE: probable bug].
- DLLs are loaded with `LoadLibrary` per connection (`TDbLib.Create`). Failure hints suggest trying the default library [dbstructures.pas#L258-L290](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.pas#L258-L290).
- Detecting libmariadb vs libmysql:
  - libmariadb is recognised purely by **file name prefix** `libmariadb` [dbstructures.mysql.pas#L3204-L3208](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L3204-L3208).
  - For libmysql ≥ 8, the `mysql_options` constant offsets are remapped [dbstructures.mysql.pas#L3155-L3202](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L3155-L3202).
- MySQL client plugins are loaded from `<appdir>\plugins\` (`MYSQL_PLUGIN_DIR`) [dbconnection.pas#L2550-L2554](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2550-L2554).
- The exported `mysql_authentication_dialog_ask` callback shows a GUI prompt for the "dialog" auth plugin, e.g. PAM or 2FA [dbconnection.pas#L1016-L1025](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1016-L1025).
- MSSQL:
  - Using `SQLOLEDB` triggers a one-off "insecure TLS 1.0" warning [dbconnection.pas#L2734-L2743](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2734-L2743).
  - `MSOLEDBSQL*` gets `DataTypeCompatibility=80` [dbconnection.pas#L2773-L2779](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2773-L2779).

---

## 3. SSH tunnel [CODE] [dbconnection.pas#L1099-L1276](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1099-L1276)

- **Model:** one external process per connection (`TSecureShellCmd`), started in `StartSSHTunnel` before the DB connect. The DB connection then goes to `127.0.0.1:<SSHLocalPort>` [dbconnection.pas#L3386-L3395](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3386-L3395).
  - Used by the MySQL (SSH/RDS), PG and MSSQL `SetActive` [dbconnection.pas#L2517-L2519](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2517-L2519), [dbconnection.pas#L2719](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2719), [dbconnection.pas#L2891](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2891).
- **Local port selection:**
  - Starts from the configured `SSHLocalPort`.
  - `PortOpen()` tries a TCP connect to 127.0.0.1:port; a refused connect counts as "free".
  - If the port is busy, it increments up to 20 times, logging "Port #%d in use. Checking if #%d is available...", and otherwise fails with "Port %d already in use" [dbconnection.pas#L1112-L1120](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1112-L1120), [apphelpers.pas#L3006-L3026](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3006-L3026).
  - The incremented port is written back into the in-memory parameters only.
  - `PortOpen` never calls `closesocket` [INFERENCE: handle leak].
- **Command line** (plink syntax shown): `plink -ssh [-legacy-stdio-prompts] user@sshhost -pw "pass" -P 22 -i "key" -N -L local:DBHOST:DBPORT` [dbconnection.pas#L1122-L1149](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1122-L1149).
  - `-legacy-stdio-prompts` is added for plink ≥ 0.82, detected from the exe's version resource.
  - OpenSSH gets `-p` instead of `-P` and adds `-o StrictHostKeyChecking=no`.
  - **The `-pw` switch is also appended for `ssh.exe`**, which OpenSSH doesn't support [INFERENCE: using a password with ssh.exe likely breaks the command].
  - The forward target `DBHOST` is resolved **on the SSH server**.
  - Double quotes in the password are escaped with a backslash (#261). The password is masked as `******` in the log [dbconnection.pas#L1150-L1154](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1150-L1154).
- **Process spawning:** `CreateProcess` with `CREATE_NEW_CONSOLE` and `SW_HIDE`. stdin, stdout and stderr are redirected to anonymous pipes with an 8 KB buffer [dbconnection.pas#L1156-L1181](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1156-L1181), [dbconnection.pas#L1042-L1062](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1042-L1062). `ssh.exe` is resolved through PATH [INFERENCE: `CreateProcess(nil, cmd…)`].
- **Handshake loop** (runs on the UI thread and blocks it):
  - It **waits the full `SSHTimeout`** in 10 slices of timeout×100 ms. There is a workaround for Wine's non-blocking `WaitForSingleObject` (#1771) [dbconnection.pas#L1183-L1200](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1183-L1200).
  - If the process exits early: "SSH exited unexpected".
  - It parses the pipe output:
    - `login as:` → InputBox asking for the username.
    - `password:` or `Passphrase for key "…":` → masked InputBox. Whatever is typed is written to stdin [dbconnection.pas#L1219-L1226](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1219-L1226).
    - Any other stdout text → information dialog.
    - A `(y/n …)` question on stderr (e.g. the host-key cache prompt) → Yes/No/Cancel dialog [dbconnection.pas#L1238-L1251](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1238-L1251).
    - "Using username", "Pre-authentication banner" and "Access granted…" → a newline is sent back.
    - "Access denied" → fatal error.
    - Other stderr text → error dialog, then the loop continues [dbconnection.pas#L1252-L1269](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1252-L1269).
- **Teardown:** `EndSSHTunnel` → `TerminateProcess` on every disconnect and on connect failure [dbconnection.pas#L1086-L1096](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1086-L1096), [dbconnection.pas#L3398-L3404](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3398-L3404).
- **When the tunnel dies:**
  - Nothing watches the process.
  - Its death is only noticed indirectly, when the next ping or query fails. `Ping(True)` then calls `Active:=False` (which kills the dead tunnel) and `Active:=True`, which **spawns a new tunnel and waits the full timeout again** [INFERENCE from [dbconnection.pas#L3612-L3617](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3612-L3617)].
  - If HeidiSQL itself crashes, the plink processes are left orphaned [INFERENCE].

---

## 4. SSL options → client libraries

| Engine | Mapping | Source |
|---|---|---|
| MySQL, **libmysql** | Sets `MYSQL_OPT_TLS_VERSION='TLSv1,TLSv1.1,TLSv1.2,TLSv1.3'`, `MYSQL_OPT_SSL_KEY/CERT/CA/CIPHER` (only when non-empty), `CLIENT_SSL` flag. `MYSQL_OPT_SSL_MODE`: 0→`SSL_MODE_PREFERRED`, 1→`VERIFY_CA`, 2→`VERIFY_IDENTITY` | [CODE] [dbconnection.pas#L2463-L2490](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2463-L2490), [dbconnection.pas#L2547-L2548](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2547-L2548) |
| MySQL, **libmariadb** | Same TLS version and file options, plus `MARIADB_OPT_TLS_VERSION`. `MYSQL_OPT_SSL_VERIFY_SERVER_CERT`: 0→false, 1 and 2→true (so libmariadb has no separate "CA only" level). **When SSL is off, verification is explicitly disabled** because libmariadb ≥ 3.4 enables it by default | [CODE] [dbconnection.pas#L2491-L2506](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2491-L2506) |
| PostgreSQL | libpq conninfo `sslmode`: `disable` (SSL off), then `require` / `verify-ca` / `verify-full` for 0/1/2. Plus `sslkey`, `sslcert`, `sslrootcert`. **The cipher setting is ignored** (commented out) | [CODE] [dbconnection.pas#L2896-L2919](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2896-L2919) |
| MSSQL / SQLite / IB | Not offered in the UI | [connections.pas#L1631](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1631) |

- After connecting, MySQL checks `SHOW GLOBAL STATUS` for `ssl_cipher` [dbconnection.pas#L2669-L2677](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2669-L2677) and PG checks `SHOW ssl` [dbconnection.pas#L2965-L2969](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2965-L2969).
- If SSL was requested but is not in use, a "SSL not used." warning appears, with a "don't ask again" setting (`SSL_WarnUnused`) [main.pas#L4440-L4447](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4440-L4447).
- Error hints: "certificate verif" suggests lowering the verification level; `SEC_E_ALGORITHM_MISMATCH` suggests a newer libmysql [dbconnection.pas#L2586-L2593](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2586-L2593).

---

## 5. Advanced-tab options: effect in code

| Option | Effect | Source |
|---|---|---|
| Startup script | After every (re)connect, the file is read, split with `TSQLBatch`, and each statement run through `Query()`. **Errors are swallowed** (they only appear in the SQL log). A missing file is logged | [CODE] [dbconnection.pas#L3514-L3530](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3514-L3530) |
| Query timeout | MySQL: `MYSQL_OPT_CONNECT_TIMEOUT` only. MSSQL: `CommandTimeout`. PG: `SET statement_timeout TO n*1000`. SQLite: `PRAGMA busy_timeout=n*1000`. IB/FB: FireDAC `CmdExecTimeout := n` (FireDAC documents this value in milliseconds [UNVERIFIED in repo] → possibly 30 ms) | [dbconnection.pas#L2568](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2568), [dbconnection.pas#L2800](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2800), [dbconnection.pas#L2957](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2957), [dbconnection.pas#L3074](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3074), [dbconnection.pas#L4045](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4045) |
| Ping every X s (keep-alive) | A per-connection VCL `TTimer` (main thread) with interval n×1000 ms calls `Ping(False)` when the connection is not locked by a thread. A dead connection is **disconnected, not reconnected**; the next query reconnects. Every ping restarts the timer. 0 means no handler is assigned | [dbconnection.pas#L3469-L3472](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3469-L3472), [dbconnection.pas#L3727-L3732](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3727-L3732) |
| Use own client time zone | MySQL only (template `qSetTimezone`, ≥ 4.1.3): `SET time_zone='±HH:MM'`, computed from the Windows bias. This is a static offset, not a named zone, so DST changes are not followed [INFERENCE] | [dbconnection.pas#L3495-L3512](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3495-L3512), [dbstructures.mysql.pas#L3363-L3367](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L3363-L3367) |
| Get full table status | MySQL on: `SHOW TABLE STATUS FROM db`. Off: a cheap `information_schema.TABLES` query that returns NULL for rows and sizes. PG on: `pg_table_size` / `pg_relation_size`. **SQLite on: runs `SELECT COUNT(*)` for every table in one UNION** (expensive). Shift+F5 "Full status refresh" temporarily forces it on | [dbconnection.pas#L7186-L7213](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7186-L7213), [dbconnection.pas#L7430-L7438](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7430-L7438), [dbconnection.pas#L7551-L7560](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7551-L7560), [main.pas#L1639-L1650](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1639-L1650), [DFM] [main.dfm#L2541-L2550](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2541-L2550) |
| Enable cleartext authentication | `MYSQL_ENABLE_CLEARTEXT_PLUGIN=true` | [dbconnection.pas#L2557-L2558](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2557-L2558) |
| Force Unicode | MySQL: when off, the internal `IsUnicode` follows the connection charset (utf*), which switches SQL encoding and result decoding to ANSI | [dbconnection.pas#L4632-L4636](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4632-L4636), [dbconnection.pas#L7744-L7750](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7744-L7750) |
| Background color | UI only (§1.7) | |
| Hide database pattern | A regex is applied to the database list; matching names are removed. A bad regex is logged | [dbconnection.pas#L5006-L5022](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L5006-L5022) |
| Log queries to file | In `TDBConnection.Log`, SQL whose text *starts with* CREATE/ALTER/DROP/TRUNCATE/COMMENT/RENAME (DDL) or INSERT/UPDATE/DELETE/UPSERT (DML) is appended with `;` to a file whose name is expanded from placeholders (`%session %host %u %db %date %d %m %y %h %i %s`; `%db` becomes `nodb` when empty). **The file is opened and closed for every statement.** On an I/O error, logging is disabled for the session | [dbconnection.pas#L5096-L5154](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L5096-L5154), [apphelpers.pas#L2904-L2919](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L2904-L2919) |

**Fixed MySQL connect options** (not configurable) [CODE] [dbconnection.pas#L2539-L2565](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2539-L2565):
- `CLIENT_LOCAL_FILES | CLIENT_INTERACTIVE | CLIENT_PROTOCOL_41 | CLIENT_MULTI_STATEMENTS | CLIENT_CAN_HANDLE_EXPIRED_PASSWORDS | CLIENT_PLUGIN_AUTH_LENENC_CLIENT_DATA`, plus `MYSQL_OPT_LOCAL_INFILE=1`.
  - Having LOCAL INFILE always on lets a malicious server request client files [INFERENCE].
- Connect attribute `program_name=HeidiSQL`.
- Right after connecting:
  - A ping, to catch a broken `init_connect` ("Connection closed immediately…") [dbconnection.pas#L2605-L2615](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2605-L2615).
  - `ER_MUST_CHANGE_PASSWORD` opens a change-password dialog and runs `ALTER USER` or `SET PASSWORD` [dbconnection.pas#L2618-L2637](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2618-L2637).
  - Charset: try `utf8mb4`, then `utf8`, then `SET NAMES utf8` [dbconnection.pas#L2647-L2666](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2647-L2666).
  - Reads uptime, SSL status, `NOW()`, `version_compile_os`, `hostname`, `lower_case_table_names` [dbconnection.pas#L2667-L2681](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2667-L2681).

---

## 6. "Databases", "Prompt for credentials", "Windows authentication", "Compressed"

### Databases field — semantics differ by engine [CODE]

| Engine | Meaning of `AllDatabasesStr` |
|---|---|
| MySQL, MSSQL | **Whitelist.** When not empty, it is split on `;` (trimmed, de-duplicated, **not a regex**) [dbconnection.pas#L2103-L2123](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2103-L2123) and **replaces** the server's database list; `SHOW DATABASES` / `sys.databases` is never queried [dbconnection.pas#L4891-L4902](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4891-L4902). "Hide database pattern" is still applied on top. For MSSQL, a single name with no `;` is also sent as `Database=` in the connection string [dbconnection.pas#L2781-L2783](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2781-L2783) |
| PostgreSQL | **The single database to connect to** (`dbname=` in the conninfo) [dbconnection.pas#L2903-L2904](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2903-L2904). The tree then lists **schemas** (`pg_namespace`), which PG treats as "databases" [dbconnection.pas#L4942-L4966](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4942-L4966). When empty, libpq's default applies (dbname = user name) [UNVERIFIED — libpq behaviour] |
| Interbase/Firebird | The database file or alias (`Params['Database']`) [dbconnection.pas#L3181](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3181) |
| SQLite (encrypted) | `;`-separated `name=int` cipher parameters passed to `sqlite3mc_config_cipher`. Ignored for the database list [dbconnection.pas#L3032-L3056](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3032-L3056), [dbconnection.pas#L4896](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4896) |

- **The drop-down button** opens a temporary connection built from the current form values, with the list cleared [CODE] [connections.pas#L1288-L1338](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1288-L1338).
  - MySQL/MSSQL/IB: a menu of all server databases with **checkable items**. Ticking or unticking rewrites the field as a `;`-joined list [connections.pas#L1341-L1355](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1341-L1355).
  - PG: queries `SELECT datname FROM pg_database WHERE datistemplate=FALSE` and shows **radio items** (single choice).
  - Connection errors are silenced; the menu is cached until host, user, password, port or type changes [connections.pas#L1502-L1508](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.pas#L1502-L1508).

### Prompt for credentials
- In `DoBeforeConnect`, a modal `TfrmLogin` (Username/Password, prefilled) is shown **every time a connection is activated, including automatic reconnects and cancel helper connections** [CODE] [dbconnection.pas#L3264-L3280](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3264-L3280) / [INFERENCE for the reconnect case].
- Cancel raises "Login cancelled". The values entered are not saved to the session.

### Use Windows authentication
- MSSQL: `Integrated Security=SSPI` with SQLOLEDB, `Trusted_Connection=yes` otherwise [dbconnection.pas#L2785-L2790](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2785-L2790).
- MySQL: sends the **Windows user name from `GetUserName` with an empty password** (#991) [dbconnection.pas#L2523-L2531](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2523-L2531); presumably for server-side Windows or named-pipe auth plugins [INFERENCE].

### Compressed client/server protocol
- Adds `CLIENT_COMPRESS` to the MySQL client flags [dbconnection.pas#L2545-L2546](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2545-L2546), and `--compress` to external CLI arguments [dbconnection.pas#L2015-L2016](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2015-L2016).
- Only available for the MySQL family.

---

## 7. Password storage [CODE] [apphelpers.pas#L566-L685](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L566-L685)

- The session password and the SSH password are saved as `encrypt(pw)` [dbconnection.pas#L1572](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1572), [dbconnection.pas#L1591](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1591) and read back with `decrypt` [dbconnection.pas#L1487](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1487), [dbconnection.pas#L1506](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1506).
- **ANSI variant:**
  - `salt = random(9)+1`.
  - Each char becomes `(ord+salt)` wrapped at 255, written as 2 hex digits.
  - The single salt digit is appended.
- **Unicode variant**, used if any char is > 255: 4 hex digits per char, `(ord+salt) mod 65536`, then the salt digit, then `0` as a Unicode flag.
- Strength: **none, cryptographically.** It is a Caesar shift with a key in 1..9 that is stored next to the ciphertext, and the password length can be read from the output. No DPAPI, no master password, no OS credential store.
- Values live in `HKCU\Software\HeidiSQL\Servers\<path>\Password` and `SSHtunnelPassword`, and are carried unchanged into export files and `portable_settings.txt`.
- SSL key paths are stored in plain text.

---

## 8. Settings persistence (`TAppSettings`) [CODE] [apphelpers.pas#L250-L319](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L250-L319), [apphelpers.pas#L3795-L4174](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3795-L4174)

- **Normal mode:** registry `\Software\HeidiSQL\` under the current user. `TRegistry` is created with its default root [apphelpers.pas#L3803](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3803), [apphelpers.pas#L3845](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3845); HKCU is Delphi's default [INFERENCE].
  - Sessions: `\Software\HeidiSQL\Servers\<Folder>\…\<Session>` [const.inc#L17](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/const.inc#L17).
  - Per-table UI state (hidden columns, filter, sort, timestamp columns) lives in sub-keys named `<db>|<table>` under the session key [main.pas#L11783-L11791](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11783-L11791).
  - Query history lives under `<session>\QueryHistory` [main.pas#L8902](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8902).
- **Portable mode** is on if `portable.lock` exists next to the exe. For backwards compatibility, an existing `portable_settings.txt` makes the app create the lock file.
  - The command-line switch `--psettings=<file>` (or `-psettings=`) chooses another file [apphelpers.pas#L3807-L3847](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3807-L3847).
  - The file is imported into `\Software\HeidiSQL Portable <PID>\`, exported back and deleted in the destructor. Keys left by dead PIDs are removed [apphelpers.pas#L4177-L4226](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4177-L4226).
  - If the export fails, the app switches to read-only mode with a message pointing at `--psettings` [apphelpers.pas#L4716-L4732](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4716-L4732).
  - AppData and Documents paths resolve to the exe folder [apphelpers.pas#L4735-L4760](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4735-L4760).
- **Read/write mechanics:**
  - Each setting has a name, defaults, and a `Session` flag. Session settings require `SessionPath` to be set, and global settings clear it (an exception is raised otherwise) [apphelpers.pas#L4409-L4453](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4409-L4453).
  - Only global values without a format argument are cached in memory (`Synced`).
  - Writes go straight to the registry and are skipped when unchanged [apphelpers.pas#L4497-L4543](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4497-L4543).
  - `SessionPath` switches the open registry key, which is shared global mutable state [INFERENCE: not thread-safe].
- **Startup side effects** in the dpr [heidisql.dpr#L79-L91](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/packages/Delphi12.3/heidisql.dpr#L79-L91):
  - The date format is forced to `yyyy-mm-dd hh:nn:ss`, and dates such as `LastConnect` are stored as strings in that format.
  - Single-instance mode is optional (`AllowMultipleInstances`, default true). When it is off, a second instance forwards its command line through `WM_COPYDATA` [main.pas#L14198-L14215](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14198-L14215).
- **Opt-in usage statistics** (`DoUsageStatistics`) send the app version, theme and *NetType plus server version of recently used sessions* to heidisql.com every 30 days [main.pas#L2273-L2307](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2273-L2307).

**UX-relevant keys (registry name = default)** [apphelpers.pas#L3851-L4169](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3851-L4169)

| Area | Keys |
|---|---|
| Sessions/startup | `LastSessions` (pipe-separated), `LastActiveSession`, `AutoReconnect`=false (*"Automatically reconnect to previously opened sessions on startup"*), `RestoreLastUsedDB`=true, per session `lastUsedDB`, `SessionManager_ListWidth`=220, `SessionManager_WindowWidth/Height`=700/490, `…Left/Top`=50, `SessionManager_ListFoldersAtTop`=true, `AllowMultipleInstances`=true, `RestoreTabs`=true (Vista+), `Updatecheck`=false/`UpdatecheckInterval`=3 |
| Per-session meta | `ServerVersion` (int), `ServerVersionFull`, `LastConnect`, `ConnectCount`, `RefusedCount`, `SessionCreated`, `Folder`, `FavoriteObjects` |
| Tree | `TreeBackground`=clNone (session colour), `GroupTreeObjects`=false, `TreeShowColumns`=false, `DisplayObjectSizeColumn`=true, `AutoExpand`=false, `DoubleClickInsertsNodeText`=true, `DisplayTreeFilters`=true, `dbtreewidth`=270 |
| Data grid | `DatagridRowsPerStep`=1000, `DatagridMaximumRows`=100000, `MaxColWidth`=300, `GridRowLineCount`=1, `DataFontName`=Tahoma/`DataFontSize`=8, `DataLocalNumberFormat`=true, `ShowRowId`=true, `ForeignDropDown`=true, `FieldEditor_*`=true, `Field_NullBackground`/`RowBackgroundEven/Odd`=clNone, `FieldColor_*` per type category, `DisplayBLOBsAsText`=true, `LowercaseHex`=true, `RealTrailingZeros`=1, `QueryGridLongSortRowNum`=10000, `ColumnHeaderClick`=true, `WarnUnsafeUpdates`=true, `DataPreviewEnabled`=false |
| SQL editor / query | `FontName`=Consolas (else Courier New), `FontSize`=9, `TabWidth`=3, `TabsToSpaces`=false, `WrapLongLines`=false, `CodeFolding`=true, `CompletionProposal`=true, `CompletionProposalInterval`=500, `AutoUppercase`=true, `Delimiter`=`;`, `MaxQueryResults`=10 (result tabs per batch), `StopOnErrorsInBatchMode`=true, `SingleQueries`=true, `QueryHistory`=true, `QueryHistoryKeeypDays`=30 (sic), `PromptSaveFileOnTabClose`=true, `TabCloseOnDoubleClick/MiddleClick`=true, `TabsInMultipleLines`=true |
| SQL log panel | `logsqlnum`=300 lines, `logsqlwidth`=2000 chars, `LogErrors/LogUserSQL/LogSQL/LogInfos`=true, `LogDebug/LogScript/LogTimestamp`=false, `LogToFile`=false, `SessionLogsDirectory`=`<AppData>\Sessionlogs\`, `DisplayLogPanel`=true |
| Export (SQL / grid) | `ExportSQL_*` (`DataInsertSize`=1024, `ExportSQLAddComments`=true, `ExportSQLRemoveDefiner`=true, …), `GridExportEncoding`=4, `GridExportSeparator`=`;`, `GridExportTerminator`=`\r\n`, `GridExportNull`=`\N`, clipboard variants `GridExportClp*`, `CSV*` import defaults (`;`, `"`, `\r\n`) |
| Look & feel | `Theme`=Windows, `IconPack`=Icons8, `GUIFontName`=''/`GUIFontSize`=8, `Language`, `WheelZoom`=true, shortcut overrides `Shortcut1_%s`/`Shortcut2_%s`, highlighter `SQL Attr %s …` |

---

## 9. Command-line parameters [CODE] [apphelpers.pas#L2678-L2852](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L2678-L2852)

Syntax is `-x=value`, `--long=value`, `-x value`, or `-x"value"`. A value may not start with `-` [apphelpers.pas#L2687-L2699](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L2687-L2699).

| Short / long | Meaning |
|---|---|
| `-d` / `--description` | Load a stored session by path (e.g. `-d="Folder\Sess"`). The Win7 jump list uses this form [main.pas#L1795-L1797](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1795-L1797) |
| `-n` / `--nettype` | NetType ordinal. 0 is indistinguishable from "not given" |
| `-h` `--host`, `-P` `--port`, `-u` `--user`, `-p` `--password`, `-l` `--library`, `-S` `--socket` (forces named pipe), `-db` `--databases`, `-W` `--winauth` (0/1), `-cte` `--cleartextenabled` (0/1) | Connection overrides |
| `-ssl` `--ssl` (0/1), `-sslpk` `--sslprivatekey`, `-sslca` `--sslcacertificate`, `-sslcert` `--sslcertificate`, `-sslcip` `--sslcipher`, `-sslvrf` `--sslverification` | SSL |
| `-sh` `--ssh-host` (turns SSH on), `-se` `--ssh-executable`, `-sP` `--ssh-port`, `-sLP` `--ssh-local-port`, `-su` `--ssh-user`, `-sp` `--ssh-password`, `-sk` `--ssh-key`, `-st` `--ssh-timeout` | SSH |
| `-rf` / `--runfrom=scheduler` | Internal, used after self-update |
| `--psettings=<file>` | Portable settings file (parsed in `TAppSettings`) |
| `*.sql` file names (quoted or not) | Opened in query tabs. Missing files produce an error dialog |

- If any of host, user, password, port, socket or databases is given, an ad-hoc parameter set is built on top of the `-d` session, or the defaults.
- Its name is `<host>-<4 random chars>` and it is flagged `DeleteAfterUse`, so its registry key (created implicitly by session-scoped writes) is removed on destroy [apphelpers.pas#L2774-L2841](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L2774-L2841), [dbconnection.pas#L1544-L1552](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1544-L1552).
- The startup script is deliberately not supported from the command line.

---

## 10. Connection layer (`dbconnection.pas`)

### 10.1 Class hierarchy [CODE] [dbconnection.pas#L444-L1011](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L444-L1011), factory [dbconnection.pas#L1610-L1646](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L1610-L1646)

```
TConnectionParameters   (one per session; factory: CreateConnection / CreateQuery by NetTypeGroup)
TDBConnection (TComponent, abstract)          TDBQuery (TComponent, abstract)
 ├─ TMySQLConnection   – libmysql/libmariadb   ├─ TMySQLQuery    (PMYSQL_RES list)
 ├─ TAdoDBConnection   – ADO / OLE DB (MSSQL)  ├─ TAdoDBQuery    (TAdoQuery over _Recordset)
 ├─ TPgConnection      – libpq                 ├─ TPGQuery       (PPGresult)
 ├─ TSQLiteConnection  – sqlite3 / sqlite3mc   ├─ TSQLiteQuery   (TSQLiteGridRows, materialised)
 └─ TInterbaseConnection – FireDAC IB/FB       └─ TInterbaseQuery (TFDQuery, FetchOptions.Mode=fmAll)
TSqlProvider → TMySqlProvider / TMsSqlProvider / TPostgreSQLProvider / TSQLiteProvider / TInterbaseProvider
```

- The SQL provider is created in `DoBeforeConnect` [dbconnection.pas#L3297-L3311](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3297-L3311). It learns `ServerVersion` in `DoAfterConnect` [dbconnection.pas#L3422](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3422).
- Datatype tables are per engine (`MySQLDatatypes`, `MSSQLDatatypes`, …). Quote characters: MySQL `` ` ``, others `"`, MSSQL also `[]` [dbconnection.pas#L2155-L2240](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2155-L2240).
- PG's `FIdentCharsNoQuote` excludes upper-case letters, so mixed-case identifiers always get quoted (#1072).

### 10.2 Query execution
- **Base `Query`:** wait if another thread holds the connection → **`Ping(True)`** → log the SQL → reset counters [dbconnection.pas#L3738-L3754](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3738-L3754).
- **MySQL:**
  - `mysql_real_query`, then `mysql_store_result` for every result of the multi-statement loop (`mysql_next_result`) [dbconnection.pas#L3768-L3845](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3768-L3845).
  - Results are kept only when `DoStoreResult` is true; otherwise they are freed straight away.
  - The first error stops the batch. Errors are formatted `SQL Error (%d): %s`, or `… in statement #%d` for multi-statement batches, with an `perror`-style extra message for `errno` [dbconnection.pas#L4698-L4725](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4698-L4725).
  - Timing is split into `LastQueryDuration` (execution) and `LastQueryNetworkDuration` (store_result).
- **PG:** `PQsendQuery` + `PQgetResult` loop (so still fully buffered). Errors are detected via `PQerrorMessage` after each result, and the remaining results are drained [dbconnection.pas#L3893-L3954](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3893-L3954).
- **MSSQL:** `ConnectionObject.Execute(SQL)` + `NextRecordset` [dbconnection.pas#L3848-L3890](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3848-L3890).
- **SQLite:** `sqlite3_prepare_v3` loop over the tail. All rows are copied into `TGridRow`s up front [dbconnection.pas#L3957-L4026](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3957-L4026).
- **IB/FB:** a new `TFDQuery` for every call, with `OpenOrExecute` [dbconnection.pas#L4029-L4068](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4029-L4068).
- `TDBQuery.Execute` either runs the SQL or takes over one of the connection's last raw results (`UseRawResult`), which is how multi-result batches turn into result tabs [dbconnection.pas#L8218-L8306](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L8218-L8306), [dbconnection.pas#L4071-L4083](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4071-L4083).
- **Result cache:** `GetResults` first checks `FPrefetchResults`, filled by `PrefetchResults(batchSQL)` (used for batch `SHOW CREATE` prefetch) [dbconnection.pas#L5025-L5074](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L5025-L5074).
- **USE detection:** a regex built from the `qUSEQuery` template watches executed SQL. When it sees a database switch it updates `Database` and fires `OnDatabaseChanged` [dbconnection.pas#L4511-L4535](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4511-L4535).
- **Setting `Database`:**
  - Runs `USE x` (PG: `SET search_path TO x, '$user', public`) [dbconnection.pas#L4466-L4508](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4466-L4508).
  - Stores `lastUsedDB` in the session.
  - Invalidates the cached session variables.

### 10.3 Threading and locking [CODE]
- `TQueryThread` (one per query-tab execution, `FreeOnTerminate`) calls `SetLockedByThread(Self)` around each statement or packet. For MySQL this also calls `mysql_thread_init` / `mysql_thread_end` [dbconnection.pas#L2413-L2427](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2413-L2427), [apphelpers.pas#L3301-L3383](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3301-L3383).
- **Batch in one go:** statements are joined up to `max_allowed_packet` [apphelpers.pas#L3318-L3346](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3318-L3346).
- Result handling (`AfterQueryExecution`, which calls `GetLastResults` and builds the grids) runs through `Synchronize` on the UI thread [main.pas#L3366-L3457](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3366-L3457).
- **Logging from the worker** is marshalled with `TThread.Queue` [dbconnection.pas#L5117-L5122](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L5117-L5122), [apphelpers.pas#L3386-L3389](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3386-L3389).
- **The lock is an ownership pointer, not a mutex.** Any other thread entering `Query` does `FLockedByThread.WaitFor`, i.e. waits for the *whole thread* to terminate [dbconnection.pas#L3740-L3747](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3740-L3747).
  - Two query tabs running against the same connection are not prevented; only the running tab itself is disabled [main.pas#L6754-L6755](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6754-L6755). The second thread would overwrite the lock owner [INFERENCE: race].
- The keep-alive timer skips while the connection is locked. `Ping` also toggles the VCL `TTimer` from the worker thread [INFERENCE: not thread-safe].
- The UI thread calls `Conn.Ping(True)` right before starting the worker, so reconnects normally happen on the UI thread [main.pas#L3349](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3349).

### 10.4 Reconnect, errors, charset, logging
- **Auto-reconnect:** `Ping(True)` → `Active:=False; Active:=True`, which runs the full connect path again. That includes the login prompt, SSH tunnel, charset, time zone, startup script and `OnConnected` [dbconnection.pas#L3598-L3622](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3598-L3622).
  - `SetActive(True)` resets `Database:=''`; `OnConnected` → `ConnectionReady` then re-applies the database of the focused tree node [main.pas#L10233-L10238](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10233-L10238), [main.pas#L10029-L10051](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10029-L10051) [INFERENCE: so the default database is restored from the UI, not from connection state].
  - **Interbase ping never detects failure** (the result of `FFDHandle.Ping` is ignored) [dbconnection.pas#L3709-L3724](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3709-L3724). The SQLite ping is a no-op `sqlite3_exec(nil)`.
- **Errors:** everything goes through `EDbError(Message, ErrorCode, Hint)` [dbstructures.pas#L127-L135](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.pas#L127-L135). The connect dialog shows the hint as footer text [main.pas#L4459](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4459).
- **Charset:**
  - MySQL: `mysql_set_character_set('utf8mb4')`, with fallbacks [dbconnection.pas#L2647-L2666](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2647-L2666).
  - PG: `SET CLIENT_ENCODING TO 'UTF8'` [dbconnection.pas#L4640-L4644](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4640-L4644).
  - SQLite: UTF-8.
  - FireDAC: `CharacterSet=UTF8` [dbconnection.pas#L3184](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3184).
  - MSSQL: native UTF-16, plus `SET TEXTSIZE 2147483647` [dbconnection.pas#L2834](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2834).
- **SQL log:**
  - Categories are `lcInfo, lcSQL, lcUserFiredSQL, lcError, lcDebug, lcScript` [dbconnection.pas#L418](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L418).
  - Every connection calls `OnLog` → `MainForm.LogSQL`. That buffers into `PostponedLogItems`, filters by category preference, truncates long lines to `logsqlwidth`, wraps non-SQL lines in `/* */`, keeps the last `logsqlnum` lines in a SynEdit, and optionally appends to a session log file [main.pas#L5613-L5702](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5613-L5702).
  - A `LogPrefix` such as "Helper connection" tags messages coming from secondary connections [main.pas#L14347](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14347).

### 10.5 Timeouts and cancellation
- Timeouts: see §5. **ADO `ConnectionTimeout` and libpq `connect_timeout` are never set**, so driver defaults apply [INFERENCE: not present in `SetActive`].
- **Cancel** (Esc, `actCancelOperation`) [main.pas#L14330-L14362](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14330-L14362), [DFM] [main.dfm#L3018-L3026](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3018-L3026):
  - Sets `Aborted` on the thread, which stops the batch after the current statement.
  - Opens a helper connection using the **active connection's** parameters (the same object) and runs `qKillQuery` with `ActiveConnection.ThreadId`.
  - Templates:
    - MySQL: `KILL QUERY %d` (≥5.0), `KILL %d` before that, RDS: `CALL mysql.rds_kill_query(%d)` [dbstructures.mysql.pas#L3280-L3288](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L3280-L3288).
    - PG: `SELECT pg_cancel_backend(%d)`.
    - MSSQL: `KILL %d`.
    - SQLite and Interbase: `KILL %d`, which is meaningless there.
  - Thread IDs come from `CONNECTION_ID()`, `@@SPID`, `PQbackendPID`, or the process ID for SQLite [dbconnection.pas#L4543-L4595](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4543-L4595).
- Data-grid loads and metadata calls **cannot be cancelled** [INFERENCE: they run synchronously on the UI thread with no abort path].

---

## 11. Metadata loading and caching

| Object | How | Cache / invalidation |
|---|---|---|
| Database list | MySQL: `SHOW DATABASES` (ProxySQL: column 1), falling back to `information_schema.SCHEMATA`. MSSQL: `sys.databases` / `master..sysdatabases`. PG: `pg_namespace` (Redshift filtered). SQLite: `pragma_database_list`. FB: FireDAC `GetCatalogNames`. Or the user whitelist | `FAllDatabases` until `RefreshAllDatabases` (F5, session node re-init with PURGECACHE) [dbconnection.pas#L4891-L5003](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4891-L5003), [main.pas#L9868-L9879](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9868-L9879) |
| Objects of a DB (tables, views, routines, triggers, events) | MySQL: `SHOW TABLE STATUS FROM db` *or* an `IS.TABLES` query (see "Full table status"), then `SHOW FUNCTION STATUS`, `SHOW PROCEDURE STATUS WHERE LOWER(Db)=…`, `SHOW TRIGGERS FROM`, `IS.EVENTS`. PG: one `pg_class` UNION `pg_proc` query. Also fetches the DB default collation | `FDatabaseCache`: one "all types" list per db plus derived per-type lists. Refreshing **diffs and merges** into existing `TDBObject`s, then **clears all column/key/FK/check caches** and fires `OnObjectnamesChanged` (which updates highlighter names) [dbconnection.pas#L7059-L7357](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7059-L7357) |
| Columns | Generic `information_schema.COLUMNS … ORDER BY ORDINAL_POSITION` for MySQL ≥5 and MSSQL. Provider templates for PG ≥12 (a `pg_attribute` query), SQLite (`pragma_table_xinfo`) and IB. Fallback `SHOW FULL COLUMNS` for MySQL <5, `pragma_table_info` for ProxySQL. Parses type, length, charset, collation, generation expression, virtuality, invisible, nullability, SRID, default kind, ON UPDATE, comment | `FColumnCache[QuotedDbAndTableName]`; a copy is returned each time [dbconnection.pas#L5824-L6034](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L5824-L6034), [dbconnection.pas#L10637-L10648](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10637-L10648) |
| Keys | MySQL `SHOW KEYS FROM t FROM db` (+ `qIndexSize` sizes, Visible/Ignored, functional parts). Generic IS `KEY_COLUMN_USAGE`×`TABLE_CONSTRAINTS`. PG and SQLite have their own | `FKeyCache` [dbconnection.pas#L6140-L6298](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L6140-L6298) |
| Foreign keys | Generic: two separate IS queries (`REFERENTIAL_CONSTRAINTS`, `KEY_COLUMN_USAGE`) joined client-side, "not by JOIN, as this is too slow" (#852). **After one failure, FK queries are disabled for the rest of the connection** (`FForeignKeyQueriesFailed`). PG/MSSQL/SQLite/IB have their own | `FForeignKeyCache` [dbconnection.pas#L6428-L6491](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L6428-L6491) |
| Row count | `qGetRowCountApprox` (MySQL `SHOW TABLE STATUS LIKE`, PG `reltuples`), or `SELECT COUNT(*)` when exact | on the `TDBObject` [dbconnection.pas#L6817-L6834](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L6817-L6834) |

**What is lazy** [CODE] [main.pas#L9860-L9905](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9860-L9905):
- Database names are read when the session node is expanded.
- Objects are read when a DB node is expanded (or when the DB is selected).
- Columns appear as tree children only when `TreeShowColumns` is on.
- Columns, keys and FKs are also fetched when a table is focused, in `DBtreeFocusChanged` [main.pas#L10053-L10070](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10053-L10070).
- **F5** (`actRefresh`, ShortCut 116 [DFM] [main.dfm#L2532-L2538](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2532-L2538)) re-reads the whole session subtree, keeping expanded nodes and the focused object [main.pas#L10460-L10520](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10460-L10520).
- Object editors refresh only the affected DB node.
- Disconnecting clears the session caches; destroying the connection also clears the DB object caches [dbconnection.pas#L6975-L6993](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L6975-L6993).

---

## 12. Capability and dialect system

The design has three layers.

1. **`TFeatureOrRequirement` flags + `Has()`** [CODE] [dbconnection.pas#L429-L442](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L429-L442), [dbconnection.pas#L6780-L6804](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L6780-L6804). They are only meaningful for ngMySQL; every other engine returns False.
   - `frSrid`: MySQL ≥8.0
   - `frTemporalTypesFraction`: MariaDB ≥5.3 / MySQL ≥5.6.4
   - `frIntegerDisplayWidth`: not MySQL ≥8.0.17
   - `frColumnDefaultParentheses`: MySQL ≥8.0.13
   - `frEditVariables`: ≥4.0.3
   - `frCreateView`: ≥5.0.1
   - `frCreateProcedure` / `frCreateFunction`: ≥5.0.3
   - `frCreateTrigger`: ≥5.0.2
   - `frCreateEvent`: ≥5.1
   - `frInvisibleColumns`: MariaDB ≥10.3.3 / MySQL ≥8.0.23
   - `frCompressedColumns`: MariaDB ≥10.3.1

   Where they are used: table editor column visibility [table_editor.pas#L1511-L1515](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L1511-L1515), column SQL generation [dbconnection.pas#L10906-L10947](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10906-L10947), and "Create X" menu gating, which main.pas does only `if IsAnyMySQL` [main.pas#L8129-L8135](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8129-L8135).
2. **`TSqlProvider` query templates** keyed by `TQueryId`, 66 ids [dbstructures.pas#L40-L55](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.pas#L40-L55).
   - `Has(id)` means "the template is not empty". Empty templates are how a feature is disabled (e.g. `qLockedTables`, `qShowEvents` on old servers, `qUSEQuery` for SQLite/IB).
   - Base defaults are in [dbstructures.pas#L209-L221](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.pas#L209-L221).
   - Substitution is either `Format` (`%s`/`%d`) or named `:Placeholders` taken from `TDBObject.AsStringMap` (e.g. `:QuotedDbAndTableName`, `:EscapedDatabase`, `:EscapedName`) [dbstructures.pas#L231-L241](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.pas#L231-L241), [dbconnection.pas#L10689](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10689).
   - Templates branch on `FServerVersion` and `FNetType`. The MySQL provider even guesses **MariaDB as `ServerVersion >= 100000`** [dbstructures.mysql.pas#L3251-L3252](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L3251-L3252).
3. **Imperative special cases** everywhere: `case NetTypeGroup of`, `IsAnyPostgreSQL`, `IsMariaDB`, `ServerVersionInt >= …`. About 255 of these, 139 in dbconnection.pas [CODE: grep]. Examples:
   - `ApplyLimitClause` [dbconnection.pas#L8002-L8056](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L8002-L8056):
     - MySQL: `LIMIT off, n`
     - PG: `LIMIT n OFFSET off`
     - MSSQL ≥2012: `ORDER BY 1 OFFSET … FETCH NEXT …`; older: `TOP n`; `TOP(n)` for UPDATE/DELETE
     - IB: none
   - `LikeClauseTail` (MSSQL `ESCAPE '\'`).
   - Per-engine `FetchDbObjects` / `GetTableKeys` / `GetTableForeignKeys` overrides.
   - Per-family SQL function lists loaded as `mariadb10.6,mariadb10,mariadb,mysql` [dbconnection.pas#L3474-L3493](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3474-L3493).

**Template examples across dialects** [CODE] [dbstructures.mysql.pas#L3247-L3430](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L3247-L3430), [dbstructures.postgresql.pas#L596](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.postgresql.pas#L596), [dbstructures.mssql.pas#L418](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mssql.pas#L418), [dbstructures.sqlite.pas#L398](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.sqlite.pas#L398), [dbstructures.interbase.pas#L183](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.interbase.pas#L183)

| TQueryId | MySQL | PostgreSQL | MSSQL | SQLite |
|---|---|---|---|---|
| qUSEQuery | `USE %s` | `SET search_path TO %s` | `USE %s` | '' (unsupported) |
| qEmptyTable | `TRUNCATE %s` | base `DELETE FROM %s` | base | base |
| qRenameTable | `RENAME TABLE %s TO %s` | `ALTER TABLE %s RENAME TO %s` | `EXEC sp_rename %s, %s` | `ALTER TABLE … RENAME TO` |
| qChangeColumn | `CHANGE COLUMN %s %s` | `ALTER COLUMN %s %s` | `ALTER COLUMN %s %s` | '' (rename only) |
| qLikeCompare | `%s LIKE %s` | `%s ILIKE %s` | `LIKE` | `LIKE` |
| qFuncNow | `NOW()` (ProxySQL `CURRENT_TIMESTAMP`) | `NOW()` | `GETDATE()` | `DATETIME()` |
| qFuncLeft | `LEFT(%s, %d)` | `SUBSTRING(%s, 1, %d)` | `LEFT` | `SUBSTR(%s, 1, %d)` |
| qKillQuery | `KILL QUERY %d` / RDS proc | `SELECT pg_cancel_backend(%d)` | `KILL %d` | `KILL %d` (invalid) |
| qGetRowCountApprox | `SHOW TABLE STATUS FROM :QuotedDatabase LIKE :EscapedName` | `SELECT reltuples::bigint FROM pg_class …` | version-dependent | '' → exact `COUNT(*)` |
| qDisableForeignKeyChecks | `SET @OLD_FOREIGN_KEY_CHECKS=…, FOREIGN_KEY_CHECKS=0` | '' | '' | '' |
| qDatabaseDrop | `DROP DATABASE %s` | `DROP SCHEMA %s` | `DROP DATABASE %s` | `DROP DATABASE %s` |

**For our abstraction** [INFERENCE]: replace these three layers with one declarative capability matrix per (engine, version range, vendor), plus a dialect object with typed methods (quoting, paging, rename, kill, schema-vs-database semantics) and per-engine metadata providers. Keep the TNetType ordinals only for import compatibility.

---

## 13. Multiple simultaneous sessions

- `MainForm.FConnections` holds any number of `TDBConnection`s, each with its own client handle, SSH process, caches, keep-alive timer and SQL provider [CODE] [main.pas#L1261](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1261), [main.pas#L4394-L4474](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4394-L4474).
- Each connection is a root node in the DB tree. **The "active connection" is whichever tree node has focus** (`FActiveDbObj.Connection`) [main.pas#L8562-L8567](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8562-L8567). Query tabs execute against *the active connection at the moment Execute is pressed* [main.pas#L3350-L3351](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3350-L3351); there is no tab-to-session binding [INFERENCE].
- **Blocking:**
  - Connect, SSH wait, metadata, data grid and keep-alive calls all run on the UI thread, so **one session's slow call freezes the entire UI**, including the other sessions [INFERENCE from K2].
  - Query-tab batches in *different* sessions can run concurrently in their own threads [INFERENCE].
- **Startup:**
  - With `AutoReconnect`, every session in `LastSessions` is opened one after another at startup, and `LastActiveSession` gets the focus [main.pas#L2328-L2349](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2328-L2349).
  - Otherwise the Session Manager is shown modally, and Cancel quits the app [main.pas#L2352-L2367](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2352-L2367).
- **Other session-level UI:**
  - Open session paths are saved after every connect (`StoreLastSessions`). A Windows jump list shows sessions used in the last 30 days, ordered by connect count [main.pas#L1759-L1803](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1759-L1803).
  - **Disconnect** removes the connection's tree node. When the last one closes, the Session Manager reopens, and Cancel there exits the app [main.pas#L2651-L2672](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2651-L2672).
  - Picking a session from the quick-session menu just focuses its tree node if it is already connected, instead of connecting twice [main.pas#L4345-L4386](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4345-L4386).
