# Changelog

Visible changes to Eunomia, in the [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format; versions follow [SemVer](https://semver.org/). The details of each phase are in `docs/progress.md`.

## [Unreleased]

### Added

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

- Names inserted in the editor, keywords, functions and highlighting go through the language layer (`ISqlLanguage`, `Engine.Sql`); an architecture test keeps `MySql*` types and MySqlConnector out of everything but the driver (groundwork for PostgreSQL, no visible change).
- CI with minimal token permissions and cancellation of older runs on the same PR.
- `docs/roadmap.md` extended with engineering quality, security, architecture and product items.

### Security

- Escaping in the HTML, XML, PHP, LaTeX and Markdown exports; query history without statements that hold passwords; warning for an SSH key of a different type; the connection says whether it is encrypted (a warning in the SQL log for remote servers without TLS or SSH). Report in `docs/security-audit.md`.

### Performance

- Remote servers: fewer round trips per action (table definition in one round trip, collations and engines once per session, `SHOW TRIGGERS`), tree statistics on a job connection. Opening a database: 1.1–1.7 s → 0.26 s with 50 ms of latency. Details in `docs/performance.md`.

### Fixed

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
