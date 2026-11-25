# Changelog

Visible changes to Eunomia, in the [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format; versions follow [SemVer](https://semver.org/). The details of each phase are in `docs/progress.md`.

## [Unreleased]

### Added

- Update check: at startup, at most once a day and a few seconds after the window opens, one anonymous request to the project's GitHub releases; a bar under the toolbar says when a newer version is out, with **Open release page**, **Skip this version** and **Turn off update checks**. Pre-releases are offered only to pre-release builds. Help › Check for updates asks at once; Preferences › General turns the startup check off. Nothing is downloaded or installed (user guide, "Update check"; threat model).
- Run SQL file: a target dialog before the run, with the server's databases (searchable). For files that select their own database with `USE`, **As in the file** or **Everything in the database above** (the file's `USE` and `CREATE`/`DROP`/`ALTER DATABASE` are skipped). The progress window shows the server and the database, and follows the file's `USE`.
- Export database as SQL: rows of the **Server: <session>** outputs in the session colour, as in the tree; the target database box is searchable ("Type to search"; the arrow lists them all); an export with errors ends with a message listing the objects that failed.
- Preferences › Grid formatting › "Lower the memory limits to fit this computer" (on by default): below 8 GB of memory, the limits for results in memory follow the computer (all results: an eighth of the memory, at least 256 MB; one result: a quarter of that, at least 64 MB); values set lower are kept. The page shows the computer's processors and memory.
- Data tab: while a table loads, the label shows the time taken and "(Esc to cancel)"; after cancelling, it says how many rows were loaded.
- Export database as SQL: with the tree's **Group objects by type** on, the objects show in the same groups (Tables, Views, Procedures, Functions, Triggers, Events); checking a group checks its visible objects (#5).

### Changed

- Data tab pages, and the full load used by Export grid rows and Copy as, no longer stop at the 30 s query timeout; the timeout of other queries counts from when the statement starts, not from when it was queued behind another one.
- Data tab: tables with large rows (BLOBs) are read in smaller pages (at least 10 rows), so the first rows of a very large table show without the server reading gigabytes first; the SQL log says so once per table.
- Export to a file or ZIP: writing (and compressing) runs on its own thread, in parallel with reading the rows; the status shows the SQL size and, for a ZIP, the compressed size.
- Database dumps lift the session's statement time limit (`MAX_EXECUTION_TIME`/`MAX_STATEMENT_TIME`) and set `NET_WRITE_TIMEOUT` to an hour while they read, restoring both at the end.
- GitHub Actions only builds the packages and publishes the release when a pull request is merged into `main`; the build, tests, coverage, formatting check and benchmarks run locally (the `quality`, `test`, `integration` and `coverage` jobs, the executable smoke test and `bench.yml` were removed).
- `tests/Integration/Start-TestServer.ps1` also starts the MySQL 8.0/8.4 of the Windows ZIP archives (`-Mysqld`, `-Name`, `-Port`; `--initialize-insecure` with `root@127.0.0.1`, `--no-defaults`, no X Protocol, no service), and `-Stop` also stops MySQL's server process, which runs as a child of a monitor process.
- Run SQL file: on MySQL 8.4 and later, the import connection turns off `restrict_fk_on_non_standard_key`, so dumps from MySQL 8.0 with foreign keys to non-unique keys load.
- Run SQL file: the tree, the table structure and the Data tab of other databases no longer wait while an import runs.
- Bottom filter panel (Filter:): plain text by default, with `;` as "or" and `\;` for a literal `;`; the **.\*** button switches to regular expressions. In the Data tab it searches the whole table on the server (with the WHERE filter), so it finds rows beyond the first page, and it stays on F5, sorting and WHERE changes. It applies half a second after typing or at once with Enter; closing the panel clears every filter.

### Fixed

- Export database as SQL: a BLOB larger than half of the target's `max_allowed_packet` made a statement the server refused, and the import stopped there, leaving every later table, view and routine out. Binary values of 1 MiB or more are now written as `FROM_BASE64('…')` (4/3 of the value instead of twice its size in hex).
- Read-only sessions: `INTO OUTFILE`/`DUMPFILE` and `READ WRITE` split by line breaks, `SET STATEMENT … FOR`, `SET transaction_read_only` and MariaDB `/*M! … */` comments got through.
- The SQL log and query history masked fewer passwords than they should (`SOURCE_PASSWORD=`, `PASSWORD='x'` without spaces, `REPLACE '<old>'`, and error lines).
- Grid: BIT(64) with every bit set failed to load; partial zero dates (`2024-00-00`) showed as `0000-00-00`; a SET cell left empty wrote `''` over NULL; stepping a negative TIME failed; new rows lost their edits when more rows loaded; edits made during a save were lost.
- CSV import: a quoted empty one-column record was skipped; TRUNCATE ran before a server import that `local_infile=OFF` refuses.
- Run SQL file: an error in a multi-statement packet pointed at its first statement and resume ran the earlier ones again; a file that is not valid UTF-8 now fails with a clear message instead of drifting.
- Sessions: a wrong prompted password stayed cached; the SSH agent fallback missed agent errors; `--ssh-password` was ignored and a `-d` clone with overrides lost its saved secrets.
- Late results after switching session, database or object no longer overwrite the current view, and Kill/Drop/Empty/Save act on the object that was shown when you asked.
- Copy all lines missed rows not loaded yet; per-object export files with names that differ only in case overwrote each other; a local sort or filter on an edited result tab now asks first.
- Auto upper case no longer upper-cases table and column names (`from event`, `t.status`); the SQL log colours each line on its own.
- Data tab: a large table (11 GB) stopped after 30 s with nothing shown.
- Drop from the Database tab list's context menu dropped the database focused in the tree instead of the selected objects; with nothing selected in the list, Drop, Empty and Clear filter now do nothing.
- Copy, Copy as and Edit › Copy on query results acted on the wrong grid, or did nothing, when a menu had taken the focus; keyboard shortcuts no longer run while a menu is open.
- Cancelling a query or a Data tab load that waits behind another one cancels it at once.
- Export: a full disk or a lost network share is reported as a file error (not a lost connection); an incomplete file (failed close, damaged ZIP, wrong size) fails the export, and the `.partial` file is deleted on error or cancel (before, a failed ZIP write kept it open).
- Run SQL file: resuming an interrupted import ran in the wrong database and without the dump's `SET`s (it now replays the file's `USE` and `SET` statements first) and counted the statement that stopped it twice; a file that can no longer be read fails with a file error.

## [1.0.0-beta.2] - 2026-11-13

### Added

- Every push to `main` that passes CI publishes a pre-release on GitHub with the Windows, macOS and Linux packages (`release` job in `.github/workflows/ci.yml`, tagged `v<version>-build.<run>`).
- Export database as SQL: the outputs **Database** (another database of the same session) and **Server: <session>** (any other saved session, connected when chosen, into one database or into databases with the same names), as in HeidiSQL. The dump is written to a temporary file and then run on the target, stopping at the first error; a confirmation names the target and every destructive option first, and an exported database can never be the target on the same server.
- Preferences › General › "Open tables and views on their data (Data tab) instead of their structure", on by default.
- `docs/adr/ADR-21-plugin-engine.md`: proposal for an out-of-process plugin engine, starting with connection transports (VPN per connection without connecting the whole machine).
- Clear button (on the right, only while there is text) in the tree's Database filter and Table filter boxes, the bottom filter panel (Filter: regular expression), the helpers filter and the Export database as SQL filters, with the filter icon on the left, as in HeidiSQL.
- Right click on query results: Copy, Copy as (16 formats), Paste, Insert value, Save BLOB to file, Grid view options, Open URL, Post/Cancel, Find and Export grid rows, as in the Data tab. Copy as and Export grid rows use the focused result (with the source table name when there is a single one, otherwise `UnknownTable`).
- Cell editor: syntax choice (Text, JSON, XML, HTML, SQL), detected from the column or the value and saved per column when changed; a Format button that indents JSON, XML or SQL only when asked (Ctrl+Z undoes it); word wrap on by default; line numbers.
- Editable query results open the window editor for long values or values with line breaks (before, only the Data tab did).
- Export database as SQL: the status line shows how many objects are done and, after 5 s, which object the export is waiting for; running objects show "running...".
- Command palette (Ctrl+Shift+P, Tools ▸ Command palette): finds a command by name and runs it.
- Code folding in the SQL editor by `#region`/`#endregion` regions (Query ▸ Code folding), with Insert region start/end marker and Fold selection.
- Create new ▸ Table copy: copies the structure (the chosen columns, indexes and foreign keys) and the data, with a WHERE filter, to a new table; it never writes to a table that already exists.
- Save BLOB to file (cell menu) and Image preview (Grid view options): the full value of the cell to a file, and the image of the focused cell in a panel below the tree.
- Tools ▸ Maintenance (also in the tree menu): Check, Analyze, Checksum, Optimize and Repair with each operation's options, on a jobs connection and with the server's result per table; Optimize and Repair ask for confirmation, showing the session and the server.
- `docs/threat-model.md`: what is protected, from whom, the trust boundaries and the mitigation of each threat, with the test that checks it.
- Theme contrast test (WCAG 2.1 AA) from the tokens; the pairs below AA are recorded for a decision (roadmap 4.7).
- Code coverage in CI: Linux unit tests and MySQL 8.4 integration tests merged per project (`build/coverage.py`), in the summary of the `coverage` job; a core project below its threshold (`build/coverage-baseline.json`) fails it.
- Screen readers (NVDA, Narrator, VoiceOver): every field, list and tab of the windows and dialogs has an accessible name (the label's text), checked by a test.
- `.editorconfig` with the C# conventions; the build fails on deviations from the mandatory rules (`EnforceCodeStyleInBuild`).
- `quality` job in CI: `dotnet format --verify-no-changes` and packages with known vulnerabilities.
- Pull request template; `CONTRIBUTING.md` with the architecture, data safety, C# coding standards, testing and review rules; `docs/validation.md` with the code validation of 9 Oct 2026.
- The README, the documentation, code comments and test names are in English.

### Changed

- Toolbar tooltips of buttons with an arrow list the shortcuts of the other commands in the arrow (Run: Ctrl+F9 runs the selection, Ctrl+Shift+F9 the current query) and say that the arrow has more options.
- Export database as SQL opened from the Database tab list (with the list focused) checks only the objects selected in it, as in HeidiSQL.
- Choosing a table or view in the tree from the Host or Database tab opens the Data tab (HeidiSQL opens the Table tab; the old behaviour is one option away).
- Table structure (Table tab): rows are plain text with the data type in its category colour, PK/UQ/IX marks and a fixed header; the editors appear only in the selected row, and Up/Down move between rows.
- The "Show only favorites" button draws a centred star: an outline when off, filled in the favourites colour on an amber tint when on (it used to be an off-centre text glyph on the accent blue).
- Names inserted in the editor, keywords, functions and highlighting go through the language layer (`ISqlLanguage`, `Engine.Sql`); an architecture test keeps `MySql*` types and MySqlConnector out of everything but the driver (groundwork for PostgreSQL, no visible change).
- CI with minimal token permissions and cancellation of older runs on the same PR.
- `docs/roadmap.md` extended with engineering quality, security, architecture and product items.

### Security

- .NET SDK 10.0.403 (runtime 10.0.14): the NativeAOT compiler of 10.0.12 is affected by CVE-2026-111497 (Brotli decoder out-of-bounds write, GHSA-p3jp-9v83-76w9).
- Escaping in the HTML, XML, PHP, LaTeX and Markdown exports; query history without statements that hold passwords; warning for an SSH key of a different type; the connection says whether it is encrypted (a warning in the SQL log for remote servers without TLS or SSH). Report in `docs/security-audit.md`.

### Performance

- Table structure of wide tables: 300 columns open in about 1 s instead of about 18 s, without freezing the window (measured headless).
- Remote servers: fewer round trips per action (table definition in one round trip, collations and engines once per session, `SHOW TRIGGERS`), tree statistics on a job connection. Opening a database: 1.1–1.7 s → 0.26 s with 50 ms of latency. Details in `docs/performance.md`.

### Fixed

- Toolbar arrows (Session manager, Refresh, Run, Load SQL file) opened an empty menu, so nothing appeared: the items are now built before the menu opens. The Session manager arrow lists the saved sessions again and the Run arrow offers Run, Run Selection and Run current query. The recent files arrow of Export database as SQL had the same problem.
- Export database as SQL opens with the tree's Database filter and Table filter (same syntax, `;` as "or") and exports only what they show: a database or object hidden by the filters is never exported, and checking a database checks only its visible objects, as in HeidiSQL.
- CSV import in server mode: a cancel no longer keeps rows. The `LOAD DATA` runs in a transaction and is rolled back when cancelled, including when the file was cut at the end of a line and the server had accepted the rows sent so far; on MySQL 8 a cancel that arrived as "Row N doesn't contain data for all columns" is now shown as cancelled.
- Import settings file imported nothing from a file written by Export settings file (it only read the HeidiSQL format): sessions, folders, favourites and preferences now move from one Eunomia to another; the export also includes the favourites.
- The bottom filter panel (Filter: regular expression) did nothing in the Table tab: it now filters the table editor columns, and the text stays with the tab. In the Host's Variables, Status and Command-Statistics sub-tabs it filtered the hidden Databases list.
- With the bottom filter active in the Data tab, Quick Filter and Follow foreign key used the values of the wrong row; Next and Show all left the filtered view without the new rows (they now clear the filter, like any reload).
- Large values (JSON from API responses, logs) froze and crashed the application: the grid keeps and draws only the first 1,024 characters of each cell (the whole value is still used for copying, editing and exporting); the cell editor uses a code editor that only draws the visible lines; the memory for large text is no longer reserved three times over.
- Export database as SQL: both workers could end up on the same connection, one behind the other (a sequential export and a Cancel that might never finish); a worker without a connection (for instance "max_user_connections") threw away the finished export at the end and, with no worker at all, the export stayed stuck on "Exporting..." forever; FEDERATED tables without statistics were read from the remote server; an error message left the buttons stuck.
- MySQL 8: on tables with generated columns, expression defaults or CHECK constraints with quoted text, Create new ▸ Table copy failed with a syntax error and the table editor showed those expressions with extra backslashes (MySQL 8's `information_schema` returns them escaped; MySQL bug #100607).
- Text with `_` coming from the databases showed without its first `_` in menus and check boxes: Quick Filter values and columns, column choice in the lists and the Data tab, recent files, table editor indexes, Session Manager databases, sessions, Find text on server check boxes and SET editor values (the saved value was already right).
- Dropping a table or view right after choosing it in the tree (or on a slow server) no longer shows a "Table … doesn't exist" error.
- Cancelling a server-mode CSV import in the middle of `LOAD DATA` sometimes showed up as an error instead of "cancelled".
- Data tab: any other choice in the tree, or a Run on the same session, cancels the load in progress (before, a huge table on a slow server held the session).
- Local filter: switching tab or reloading no longer takes the grid back to the first row.
- Tests with a real server: a stale `connection.txt` (server stopped) no longer hangs the headless test run; the tests are reported as skipped.

## [1.0.0-beta.1] - 2026-10-09

### Added

- macOS and Linux: secrets in the Keychain and the Secret Service, fonts and shortcuts per system (⌘ on macOS), folders per system, application menu; `.app` and `tar.gz` packages built by CI.
- Transactions of the primary connection: state in the status bar, COMMIT/ROLLBACK on closing, lost-transaction warning, atomic Delete rows.
- Read-only sessions enforced by the server and by the application; "Ask before running statements that change data".
- Conflict detection when editing in the grid (the row changed on the server since it was read).
- Per-session "Log queries to file", with passwords masked.

### Changed

- Writes independent of `sql_mode` (literals, `LIMIT 1`, exact comparison without a key, lossless raw text, number validation).
- Table editor: confirmation of data loss, ALTER in strict mode, FKs restored; application DDL with a short `lock_wait_timeout` (`SafeDdl`).
- User files with a `.bak` copy, merging between instances and 0600/0700 permissions on Unix.

### Fixed

- Killing processes no longer kills the session's own connections (metadata and jobs).

## [0.13.1] - 2026-10-09

### Added

- Executable and window icons, logo in the application (About and the Session Manager Start tab), issue templates. First pre-release (portable ZIP for Windows x64).

## [0.13.0] - 2026-10-08

### Added

- UX aligned with HeidiSQL: Host tab with Variables/Status/Command-Statistics, Explain, Auto refresh, Follow FK, Open URL, SQL help (F1), Reformat SQL (Ctrl+F8), Find text on server, editing views, routines, triggers and events through their `CREATE`, import of the sessions of an installed HeidiSQL.
- Consistent light and dark themes, a toolbar with only implemented commands.

## [0.1.0 – 0.12.0] - up to 2026-10-07

- The automatable side of the MVP (phases 1–9 and 12): shell, sessions (TLS, SSH), tree, data grid, SQL editor, editing, import/export, schema visualizer and hardening. Details in `docs/progress.md`.
