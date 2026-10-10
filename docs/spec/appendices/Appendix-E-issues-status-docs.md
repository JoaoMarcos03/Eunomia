# 05: HeidiSQL project status, official docs, performance and stability issues, feature demand

Research date: 2026-09-28. Scope: current state of the project, what the official help page documents, performance and stability complaints on GitHub (with maintainer explanations and linked fix commits), the most requested open features, and UX-relevant changes over the last ~2 years. No product code was written.

## 0. Method, sources and limitations

**Tags used:** `[SOURCE: …]` is a page I opened or a repo file or commit I read. `[INFERENCE]` is my own conclusion. `[UNVERIFIED]` is plausible but not checked.

**Local repos (read-only):**
- `~/src/heidisql`: master, HEAD `c123ccdb` (2026-09-22).
- `~/src/heidisql-lazarus`: lazarus branch, HEAD `a834721f` (2026-09-18).
- `~/src/heidisql-history`: full, non-shallow clone with all branches and tags.
- Git log extracts in `research/raw/` (`master-log.txt`, `lazarus-log.txt`, `perf-commits.txt`, plus the new `issue-commits.txt`).

**What worked and what did not:**
- WebSearch returned HTTP 403 ("feature not enabled"), so I could not do any search-engine discovery.
- `curl` to api.github.com was refused by the session's GitHub guard. `curl` to heidisql.com was refused by the proxy (403).
- WebFetch worked on www.heidisql.com, on individual github.com issue pages and on the GitHub search API. After about 12 API calls, the API started returning 403 (rate limiting). github.com issue search pages are blocked by robots.txt.
- **The heidisql.com forum was not searched.** I only mention forum threads that commit messages reference.

GitHub search API queries (fetched via WebFetch on 2026-09-28). Rows in §3 cite these codes:

| Code | Query URL |
|---|---|
| Q-slow | https://api.github.com/search/issues?q=repo:HeidiSQL/HeidiSQL+is:issue+slow+in:title&sort=comments&order=desc&per_page=60 (15 hits) |
| Q-perf | https://api.github.com/search/issues?q=repo:HeidiSQL/HeidiSQL+is:issue+label:performance&sort=comments&order=desc&per_page=100 (12 hits) |
| Q-crash | https://api.github.com/search/issues?q=repo:HeidiSQL/HeidiSQL+is:issue+crash+in:title&sort=comments&order=desc&per_page=35 (158 hits, top 10 read) |
| Q-crash2 | https://api.github.com/search/issues?q=repo:HeidiSQL/HeidiSQL+is:issue+memory+OR+%22access+violation%22+OR+crash+OR+crashes+in:title&sort=comments (198 hits, top 12 read) |
| Q-freeze | https://api.github.com/search/issues?q=repo:HeidiSQL/HeidiSQL+is:issue+freeze+in:title&sort=comments&order=desc&per_page=40 (21 hits, 16 listed) |
| Q-mem | https://api.github.com/search/issues?q=repo:HeidiSQL/HeidiSQL+is:issue+memory+in:title&sort=comments&order=desc&per_page=40 (8 hits) |
| Q-ssh | https://api.github.com/search/issues?q=repo:HeidiSQL/HeidiSQL+is:issue+label:ssh-tunnel&sort=comments&order=desc&per_page=40 (43 hits, top 15 read) |
| Q-open | https://api.github.com/search/issues?q=repo:HeidiSQL/HeidiSQL+is:issue+is:open&sort=reactions-%2B1&order=desc&per_page=40 (327 open) |

Issue pages I opened in full: #1694, #547, #1699, #935, #2024, #1638, #548, #155, #1400, #2344.

---

## 1. Current state of the project

### 1.1 Latest release and the "12.21.0.7344" build number

| Fact | Value | Source |
|---|---|---|
| Latest release | **v12.21**, released **03 Aug 2026**, marked "Latest" on GitHub | [SOURCE: https://www.heidisql.com/download.php] [SOURCE: https://github.com/HeidiSQL/HeidiSQL/releases] [SOURCE: tag `v12.21` / `v12.21-Windows`, 2026-08-03, heidisql-history] |
| 12.21 headline features | "table editor shows size of indexes in separate column"; "show columns as children of tables, in database tree" | [SOURCE: https://www.heidisql.com/] |
| Version bump commit | `effb8549` 2026-08-03 "Bump version for v12.21 release" (master) | [SOURCE: raw/master-log.txt] |
| How the 4th number is built | `build.php`: `$lastCommitRevision = count($gitCommits) + 671; // The number of earlier Subversion commits which I could not migrate to Git` | [SOURCE: ~/src/heidisql/build.php:159-161] |
| Revision at `effb8549` | `git rev-list --count effb8549` = 6673, and 6673 + 671 = **7344** | [SOURCE: heidisql-history] |
| Latest nightly (classic Windows) | revision **7348**, 22 Sep 2026 | [SOURCE: https://www.heidisql.com/download.php] |

**Conclusion: "HeidiSQL 12.21.0.7344" is the official v12.21 Windows (Delphi) release build, not a nightly.** Its revision matches the release-bump commit exactly. A nightly built after 3 Aug would show 7345 or higher (the 22 Sep nightly is 7348). [INFERENCE from the build.php formula and the commit count]

The third number shows which code base produced a build. Windows/Delphi builds use `.0.<rev>`, for example tags `v12.13.0.7147` and `v12.15.0.7171`. Lazarus builds use `.1.1`, for example `v12.13.1.1`, `v12.15.1.1`, and issue #2570 "Latest Build 12.21.1.1 Crashes" (macOS). [SOURCE: tags in heidisql-history; Q-crash] [INFERENCE on the convention]

### 1.2 Release cadence

Tag dates [SOURCE: `git log --tags --simplify-by-decoration`, heidisql-history]:

| Period | Releases | Cadence |
|---|---|---|
| 2022 | 12.0 (04-12), 12.1 (08-13), 12.2 (11-15), 12.3 (12-05) | ~4 per year |
| 2023 | 12.4 (02-26), 12.5 (05-08), 12.6 (11-05) | ~3 per year |
| 2024 | 12.7 (05-05), 12.8 (07-11) | 2 per year |
| 2025 | 12.9 (01-18), 12.10 (01-21), 12.11 (06-09), 12.12 (10-06), 12.13 (11-03), 12.14 (12-11) | accelerating once the Lazarus port started |
| 2026 | 12.15 (01-30), 12.16 (03-10), 12.17 (04-12), 12.18 (06-09), 12.19 (06-20), 12.20 (06-22), 12.21 (08-03) | ~every 4–8 weeks, with hotfix bursts |

Since late 2025, each release has two tags: a Windows tag (`vX-Windows`, or `vX.0.rev` earlier) and a Lazarus tag (`vX`, or `vX.1.1`). Nightly builds are published continuously on two tracks: "Classic Windows version" and "v13 Preview with current Linux and macOS versions". [SOURCE: download.php; tags]

### 1.3 Platforms, bitness and supported Windows versions

**64-bit only.**
- 2025-01-12: `37a188c2` "Issue #2071: drop 32bit mode from installer".
- 2025-09-29: `84d35785` "Remove 32 bit support for releases and nightly builds".
- 2025-10-06: `385d83a7` removed the 32-bit MySQL/MariaDB plugins.
- The installer has `ArchitecturesAllowed=x64compatible`.
- The download page offers only "Windows x64 Installer / Portable".

[SOURCE: heidisql-history commits; ~/src/heidisql/out/heidisql.iss:79; download.php]

**Windows versions.** The help page says two slightly different things:
- "HeidiSQL runs fine on Windows 11 (and on Windows 7 + 8 + 10 with some minor issues)."
- In the Compatibility section: "Runs fine on Windows 11 x64, and on Windows 10 x64 with minor issues. On Windows-on-Arm devices (WoA), HeidiSQL runs through emulation."

It also documents a startup failure when Windows Exploit Protection options are on ("Force randomization for images (Mandatory ASLR)", "Randomize memory allocations (Bottom-up ASLR)", "Validate heap integrity"). [SOURCE: https://www.heidisql.com/help.php, "Requirements" and "Compatibility"] Related: commit `3e797e28` (2026-05-15) "create CLI app for adding PE security flags to heidisql.exe". [SOURCE: raw/perf-commits.txt]

**Other runtime notes:**
- "Microsoft Visual C++ Redistributable libraries are required for some drivers." [SOURCE: download.php]
- **Wine is deprecated.** The help page says: "Running HeidiSQL on Wine is currently quite unstable. The native Linux version may be an alternative…" [SOURCE: help.php]
- The update checker shows: "Wine support is deprecated. Future versions will not work reliably. Use the native Linux or macOS releases instead." [SOURCE: ~/src/heidisql/source/updatecheck.pas:168-169]

**Linux and macOS.** Linux ships as x64 .deb and .rpm (Qt6), Flatpak, x64 GTK2/Qt5/Qt6 builds, an arm64 Qt6 build (Raspberry Pi OS), and Arch Linux and FreeBSD packages. macOS ships as an arm64 app bundle. [SOURCE: download.php] macOS "currently supports only Apple silicon (ARM) Macs" [SOURCE: help.php]. The v12.21 release notes add "Stick to macOS 15, because the FPC package is only officially qualified up to macOS 11". [SOURCE: GitHub releases page]

### 1.4 The Lazarus/FreePascal port (future v13)

**Purpose.** The lazarus branch README says:

> "This is the code base for compiling HeidiSQL on Linux and macOS. From v13 onwards, the Windows version will be compiled from here. Since February 2025 I am migrating the sources from the master branch, using Lazarus and FreePascal. I left away some Windows-only stuff which won't ever work on other platforms, such as some Windows message handlings, and ADO driver usage. Therefore, support for MS SQL is being redeveloped via FreeTDS (formerly ADO), but is not yet fully mature."

[SOURCE: ~/src/heidisql-lazarus/README.md] The master README points non-Windows builds to the `lazarus` branch and requires Delphi 12.1 for Windows. [SOURCE: ~/src/heidisql/readme.md]

**Timeline** [SOURCE: heidisql-history, lazarus-log.txt]:

| Date | Milestone |
|---|---|
| 2025-02-24 | First commit `f9cd3e94`: "Issue #1482: first attempt of a code conversion to Lazarus/FPC, with a small bunch of 5 units (out of 50), and most code commented out" |
| 2025-05-06 | First Linux-style tags: `12.10.1.133` (2025-05-06), `12.11.1.167` (2025-06-20), `12.12-Linux` (2025-10-08) [INFERENCE: these are the first Linux releases] |
| 2025-12-10 | macOS app bundle script `af241084` |
| 2025-12-11 | GitHub release titled "HeidiSQL Linux v12.14.1.1", notes "Dirty and basic support for MS SQL" [SOURCE: releases page] |
| 2026-01-07 / 01-13 | Windows installer script from Lazarus `317edcd5`; CI Windows build `10776d69` |
| 2026-01-25 | `d313a596` AppColorSchemes synced with dark mode "(still only on Windows)" |
| 2026-01-28 | `93fdf770` "mention … plans for v13" in readme; `93bfda0a` newer FreeTDS for Windows |
| 2026-03-09/10 | Qt6 build shipped in .deb, libqt6pas dependency |
| 2026-08-22/23 | External contributor PJarczak: Lazarus 4.8 Linux builds, Qt theme selection, Qt VirtualTreeView rendering fixes, "avoid cross-thread keep-alive timer access", Qt6 HiDPI backport |
| Now | "Windows x64 v13 Preview" is downloadable; no v13 release yet [SOURCE: download.php; https://www.heidisql.com/] |

**Status.**
- The branch has 753 commits since 2025-02: Ansgar Becker 703, Gustavo Carreno 25 (build/CI/CHANGELOG), PJarczak 12, others ≤4. [SOURCE: `git log origin/lazarus --format=%an`]
- The branch has a generated `CHANGELOG.md`, but it only covers 12.13.1.1 (2025-11-03). [SOURCE: ~/src/heidisql-lazarus/CHANGELOG.md]
- Lazarus fixes are backported to master, for example `c123ccdb` "(backport of c976d17e…)". [SOURCE: master-log.txt]

**What differs from the Delphi build:**

| Area | Lazarus build | Source |
|---|---|---|
| MS SQL driver | FreeTDS instead of ADO/OLE DB; "not yet fully mature". Master also now warns that named pipe etc. are unsupported and advises TCP/IP (`b20f1214`, #2149) | README; commit |
| SSH passwords | Via `sshpass` (`f2b2dc37`, 2025-11-12, #2302). `sshpass.exe` v1.0.6 is bundled for Windows (`d79d462d`) | commits |
| Widgetsets | GTK2 / Qt5 / Qt6. Qt-specific crashes: #2344, 52 comments (DesignTimePPI 120 vs 96, see §3) | download.php; issue page |
| Dark mode colour sync | Windows only | `d313a596` |
| Command line | v13 parsing is stricter: "Short parameters must not use an equal sign", "Long parameters must use an equal sign" | help.php "Command line switches" |

### 1.5 Maintainer, governance and statements on the future

- **Essentially one maintainer.** On master since 2024-09-28, Ansgar Becker has 399 commits and all other authors combined have 9. [SOURCE: `git log master --since=2024-09-28`]
- **Contribution policy:** "Pull requests will only be accepted for bugfixes. No new features please." [SOURCE: ~/src/heidisql/readme.md]
- **Future direction:**
  - The Windows build moves to the Lazarus code base from v13. [SOURCE: lazarus README]
  - Wine is deprecated in favour of native builds. [SOURCE: updatecheck.pas]
  - Crash reports are uploaded and mined for fixes, for example `7bed7354` "fix: some crashes found in uploaded crash reports". [SOURCE: perf-commits.txt]
- **Build and security:** a `SECURITY.md` was added (`52648cee`, 2026-02-09). There are CVE-driven library updates (`9917a4cd` libssl/libcrypto; `eb81654b` SQLite CVE-2025-70873). [SOURCE: master-log.txt]
- I found **no explicit statement** planning async queries, multiple connections per session, or a UI rewrite. [UNVERIFIED that none exists: the forum was not searched]

### 1.6 Issue tracker numbers

- **327 open issues** on 2026-09-28. [SOURCE: Q-open, total_count]
- 158 issues have "crash" in the title. [SOURCE: Q-crash]
- 21 have "freeze" in the title. [SOURCE: Q-freeze]
- 43 carry the `ssh-tunnel` label. [SOURCE: Q-ssh]
- Only 12 carry the `performance` label. [SOURCE: Q-perf]
- The tracker uses these labels: `bug`, `feature`, `enhancement`, `confirmed`, `needs-info`, `worksforme`, `wontfix`, `upstream`, `nettype-*`, `linux`, `macos`, `wine`, `themes`, `keyboard`, `setup`, `ssh-tunnel`, `performance`. [SOURCE: the Q-* results]
- Reaction counts are low (the top open issue has 13 👍). The tracker is a weak signal for demand, and the forum is probably where most user voice goes. [INFERENCE]

### 1.7 License and the Icons8 restriction

- **GPL-2.0**: `LICENSE` is "GNU GENERAL PUBLIC LICENSE Version 2, June 1991". [SOURCE: ~/src/heidisql/LICENSE] The lazarus branch also contains `LICENSE-openssl`. [SOURCE: heidisql-lazarus listing]
- **Icons8** (master readme): "Icons added in January 2019 into a `TImageCollection` component are copyright by Icons8. Used with a special permission from Icons8 given to Ansgar for this project only. Do not copy them for anything else other than building HeidiSQL." The lazarus README has the same text without the `TImageCollection` mention. [SOURCE: both readmes]
- A GPL successor cannot reuse the Icons8 icon set. A clean-room (non-derived) successor must also avoid copying GPL code. [INFERENCE]

---

## 2. Official help page (https://www.heidisql.com/help.php)

**Sections, in order:**
1. Basic help on using HeidiSQL
2. Requirements
3. Connecting to a server
4. Command line switches
5. The database tree
6. Creating a table / view / stored procedure / trigger / scheduled event
7. The Data tab
8. Running SQL queries
9. SQL export
10. Importing files
11. Compatibility
12. HeidiSQL portable
13. License
14. Credits

Known anchors: `#requirements`, `#connecting`, `#commandline`, `#dbtree`. [SOURCE: help.php]

The page is short and user-level. Many of the topics I was asked about are **not documented there at all** (marked "not documented" below). For those, the behaviour lives only in the UI and the source; see reports 01–04.

| Topic | What help.php documents (section) | Notes |
|---|---|---|
| **Tree: Database filter / Table filter** | Not documented. "The database tree" only covers *Tree style options > Group objects by type*, favourites ("mark important items as so called favorites"), and the "Show only favorites" button. | The filter boxes exist in the UI. See commits `f707a934` (filter history pull-down, 2018) and `44ce3527` (new "Display" menu with a tree-filter toggle, 2026-02-22). Syntax and behaviour: report 04 §A15. [SOURCE: help.php; commits] |
| **Session "Databases" field** | "Connecting to a server": "If empty, HeidiSQL shows all databases to which you have access. You can limit the database tree with this setting, by entering only the database names you want to show." | Server-side pre-filter of the tree. [SOURCE: help.php] |
| **Data grid filter** | "The Data tab": the filter panel (*Edit > Filter panel*, **Ctrl+Alt+F**) is described as "a simple client side filter". | Report 04 shows that the Data-tab filter is actually a SQL WHERE clause. The help wording matches the quick-search part of the panel. [INFERENCE] |
| **Quick filter** | "Right click a value in the grid, then click *Quick filter* to get various one-click options to create a WHERE clause on the grid values." *More values* "quickly collects and displays the top 30 items in the focused column." | The size limit was raised in `1e13f130` (2026-03-24). [SOURCE: help.php; commit] |
| **Search/replace** | "Search and replace dialog (Find mode: Ctrl+F, Replace mode: Ctrl+R)". Works in SQL tabs and grids. | [SOURCE: help.php] |
| **Keyboard shortcuts** | Only these are mentioned: Ctrl+T (new query tab), F9 (execute), F2 (edit cell), Ctrl+F8 (SQL reformatter), Ctrl+F, Ctrl+R, Ctrl+Alt+F. | The full inventory of 163 actions is in report 03 §2. [SOURCE: help.php] |
| **Command-line switches** | See the list after this table. | [SOURCE: help.php; apphelpers.pas:2727-2772] |
| **Portable mode** | "HeidiSQL portable": "If HeidiSQL finds a `portable.lock` file, or a `portable_settings.txt`, or the custom filename as noted above, it starts in portable mode… all settings are restored from that file and when exiting stored again into that file." When upgrading, users must copy `portable_settings.txt` by hand: "Not doing so will show you an empty session manager." | Source also has a read-only fallback: "Switching to read-only mode. Settings won't be saved. Use the command line parameter %s…" [SOURCE: help.php; apphelpers.pas:4727] |
| **Settings export/import** | Not documented. | The feature exists (*More > Import settings file*; commit `0aaa1f21`, #2532; auto-conversion of Windows paths to Linux paths `13338c6b`, #2203). See report 01 §1.8. [SOURCE: commits] |
| **SSH tunnel** | "Connecting to a server": "If your MariaDB/MySQL/PostgreSQL server is located on a remote machine which is only accessible via SSH… The HeidiSQL installer places plink.exe in the right folder… In more recent versions, you can use 'ssh.exe' as an alternative." Example: Hostname 127.0.0.1 : 3306, SSH host : 22, local port 3307. Common error: "ensure you're using '127.0.0.1' in *Settings > Hostname/IP*, and the remote IP of your server in *SSH tunnel > Host*." | Not documented: sshpass, keep-alive, the MS SQL tunnel (added 2023, `9c5da0f6`, #921). [SOURCE: help.php; commit] |
| **SQL export** | "SQL export": comparable to mysqldump. Output to ".sql file", "ZIP compressed .sql file", "clipboard", "another database on the same server", or "a new or existing database on a server on which you have a configured HeidiSQL session". Options: Database(s) and Table(s) drop/create ("This is meant for the target server, not the source server!"), Data (No data / INSERT …), and **Max INSERT size**, which "Defines the maximum size of the extended INSERT statements…"; users must consider `max_allowed_packet`. | Not documented: the BEGIN/COMMIT wrapping option (`c843b196`, 2026). [SOURCE: help.php] |
| **CSV import** | "Importing files > .csv": "Tools > 'Import CSV file'"; user sets "line terminator, enclosing character etc." | The help does not describe the server-side LOAD DATA vs client-side parse choice. See report 02 §3. [SOURCE: help.php] |
| **SQL file import** | "Importing files > .sql": "activate the database… File > 'Run SQL file'". Encoding defaults to "Auto detect", but users should "definitely select the right one, e.g. 'UTF-8'". Progress is displayed. | [SOURCE: help.php] |
| **BLOB import** | "Tools > 'Insert files into TEXT/BLOB fields'", with `'%filecontent%'` and `'%filename%'` placeholders. | [SOURCE: help.php] |
| **Grid export formats** | Not documented on help.php. The homepage lists "CSV, HTML, XML, SQL, LaTeX, JSON, etc." | Source enum `TGridExportFormat`: Excel, CSV, HTML, XML, SQL INSERT / INSERT IGNORE / REPLACE / DELETE+INSERT / UPDATE, LaTeX, Textile, Jira Textile, PHP array, Markdown, JSON, JSON Lines. [SOURCE: https://www.heidisql.com/; ~/src/heidisql/source/exportgrid.pas:11-27] |
| **Query editor** | "Running SQL queries": Ctrl+T for more tabs, F9 to execute. Custom delimiter or `DELIMITER !!`. Query helpers panel ("Columns in <selected-table>" with "Generate..." items, reserved words, functions). "Query profile" (like SHOW PROFILE). **Bind parameters**: "Activate it per click on the checkbox 'Bind parameter'… e.g. `select ':p'`". **Batch**: "Send batch in one go" is "dramatically faster, especially when having tons of mini queries". | **Snippets, autocomplete and query history are not documented.** Snippets exist (`out/Snippets`; rename via right-click `699fde0c`), and so do autocomplete (issues #10, #276) and history (delete single entry `60d4537d`). [SOURCE: help.php; repo; commits] |
| **Session colours** | Not documented. | Per-session colour exists; see reports 01 §1.7 and 04 §A6. |
| **Connection options** | "Prompt for credentials", "Use Windows authentication" (MySQL/MariaDB/MS SQL only), "Compressed client/server protocol" ("Recommended only on low network bandwidth, or when result sets are large"), SQLite cipher/key/encryption parameters, Comment, folders. | [SOURCE: help.php] |
| **Grid display** | Data-type colours (*Tools > Preferences > Data appearance*), F2 or long-click to edit, *Insert value >* (functions, NULL, GUID), BLOB hex/text toggle ("0x" button), UNIX timestamp display. | Not documented: row limits, "Show all", keep-alive, auto-reconnect, memory. [SOURCE: help.php] |

**Command-line switches**, as listed in help.php "Command line switches" [SOURCE: help.php; apphelpers.pas:2727-2772]:

- **Session:** `-d/--description`, `-n/--nettype` (values 0–17: 0–2 MySQL TCP/pipe/SSH; 3–7 MSSQL protocols; 8–9 PostgreSQL TCP/SSH; 10 SQLite; 11 ProxySQL Admin; 12–13 Interbase; 14–15 Firebird; 16 MySQL on RDS; 17 SQLite encrypted).
- **Server:** `-h/--host`, `-l/--library`, `-u/--user`, `-p/--password`, `-P/--port`, `-S/--socket`, `-db/--databases` (semicolon-separated), `-W/--winauth`, `-cte/--cleartextenabled`.
- **SSL:** `--ssl`, `-sslpk`, `-sslca`, `-sslcert`, `-sslcip`, `-sslvrf` (0–2, default 2).
- **Portable settings file:** `--psettings`.
- **SSH:** `-se/--ssh-executable`, `-sh/--ssh-host`, `-sP/--ssh-port`, `-sLP/--ssh-local-port`, `-su`, `-sp`, `-sk`, `-st/--ssh-timeout`.

Other rules from the page: "Parameter names are case sensitive"; "Parameters containing a dot must be wrapped in double quotes".

The source also parses two things the page does not list: `-rf/--runfrom` and trailing SQL file names (loaded into tabs). The SSH switches are new (`1cd583d7`, 2025-12-03, #2322).

---

## 3. Performance and stability issues

### 3.1 Issue table

**Legend:**
- Dates are the created date. "closed → date" gives the close date.
- "Explanation / fix" contains maintainer quotes only where I opened the issue page. Otherwise it gives the linked commit from the repos.
- All rows come from the Q-* query results in §0 unless the row says otherwise. The last group of rows is known only from commit messages; those titles were not fetched.

| # | Title | URL | Date | State | Category | Symptom | Explanation / fix |
|---|---|---|---|---|---|---|---|
| 1694 | Data tab SELECT query blocks main thread | https://github.com/HeidiSQL/HeidiSQL/issues/1694 | 2022-11-01 | open (feature, performance) | UI freeze; architecture | A slow Data-tab filter query locks the UI and cannot be cancelled ("you either have to kill the process or login to the db itself"). v12.1, MySQL 8. | ansgarbecker: "The issue is still open and accepted, the above is just a recommendation or a workaround". He advises "Clear data tab filter" and turning off filter restoration. [SOURCE: issue page] |
| 547 | SLEEP() freezes app | https://github.com/HeidiSQL/HeidiSQL/issues/547 | 2019-02-22 | closed → 2026-03-16 (wontfix) | UI freeze; single connection | A long query makes the whole app white and unclickable. Users propose one connection per query. | ansgarbecker: "There are reasons for sticking to one connection per session, such as user variables and assigned MariaDB roles." "HeidiSQL is designed to support multiple sessions in one main window." "What do you expect when running a half an hour running query?" [SOURCE: issue page] |
| 10 | Autocomplete freezes GUI | https://github.com/HeidiSQL/HeidiSQL/issues/10 | 2017-11-14 | open (enhancement, confirmed), 6 👍 | UI freeze | Code completion blocks the GUI (metadata fetched synchronously [INFERENCE]). | – |
| 276 | Autocomplete does not cache tables in MySQL/MariaDB | https://github.com/HeidiSQL/HeidiSQL/issues/276 | 2018-05-29 | open (confirmed), 8 👍 | UI freeze / metadata | Repeated metadata queries for completion. | – |
| 1699 | Make window interactive while opening DB with long list of tables | https://github.com/HeidiSQL/HeidiSQL/issues/1699 | 2022-11-09 | closed → 2022-11-30 (performance) | Tree slow with many tables | Opening a DB with about 4,000 tables freezes the window while the tree is filled. | Another user (Bens-ct) suggests disabling "get full table status" (Advanced tab). The reporter: "it works like a charm". No maintainer comment on the page. [SOURCE: issue page] |
| 1638 | Slow open and freezes with 120.000 rows | https://github.com/HeidiSQL/HeidiSQL/issues/1638 | 2022-08-10 | closed → 2022-08-12 | Tree/metadata; opening tables | Minutes-long startup and a freeze on every click after a large table appeared. | ansgarbecker: "That sounds as if that large table is causing the slowness of some internal HeidiSQL queries. For example the list of table is retrieved with a `SHOW TABLE STATUS`, which can be slow when the current database contains very large tables." Fixed by turning off "Get full table status". [SOURCE: issue page] |
| 852 | Painfully slow table navigation after recent update(s) | https://github.com/HeidiSQL/HeidiSQL/issues/852 | 2020-01-14 | closed → 2020-01-17 (sqlite, performance) | Metadata / opening tables | Regression: every table switch is slow. | Fix `22b00d43`: "Separate join in TDBConnection.GetTableForeignKeys into two separate and faster queries. Closes #852". [SOURCE: commit] |
| 742 | Slow start ~ | https://github.com/HeidiSQL/HeidiSQL/issues/742 | 2019-09-02 | closed → 2019-09-05 | Startup | Slow application start (7 comments). | – |
| 1672 | Combining system queries into one batch | https://github.com/HeidiSQL/HeidiSQL/issues/1672 | 2022-09-28 | open (performance) | Metadata round-trips | Request to batch the internal metadata queries. | Related later work: `499ee416` "query size of all indexes in one query" (2026-07). [SOURCE: master-log] |
| 1822 | Add option to disable SELECTing every selected row after RMB | https://github.com/HeidiSQL/HeidiSQL/issues/1822 | 2023-06-19 | open (enhancement, performance) | Grid / large selections | A right-click on a multi-row selection triggers row re-selects or queries. | Related: `1cbdf77f` "hanging in a loop over result nodes by right-click on query result" (#2539, 2026-07). [SOURCE: commits] |
| 548 | v10.1 freezes where v9.5 is super fast (BLOB related) | https://github.com/HeidiSQL/HeidiSQL/issues/548 | 2019-02-25 | closed → 2019-03-06 (performance) | Opening large tables / BLOB | Image BLOBs of 3–40 MB: one CPU core at 100% for 2–3+ minutes, about 1 row per 3–5 s rendered. | ansgarbecker points to the fixed build "10.1.0.5485". The cause is not explained on the page. [SOURCE: issue page] |
| 2020 | HeidiSQL freezes (hangs) completely when displaying large cells | https://github.com/HeidiSQL/HeidiSQL/issues/2020 | 2024-09-11 | closed → 2024-10-31 | Large cells / grid rendering | Complete hang on large cell contents. | Same period: `bc9693c6` (2024-09-17, #2000) "disable multiline feature in grids… Aims to fix laggy performance in grid with large contents"; `2ba8bcab` "loading first part of JSON columns in grid"; earlier `6e9be764` "endlessly received WM_PAINT messages on results with large lines". [SOURCE: perf-commits] [INFERENCE that these relate] |
| 660 | Extremely slow (or even crashing) when viewing a table with spatial data | https://github.com/HeidiSQL/HeidiSQL/issues/660 | 2019-05-29 | open (worksforme), 3 👍 | Opening tables / geometry | – | – |
| 915 | Freezes when working with large data | https://github.com/HeidiSQL/HeidiSQL/issues/915 | 2020-02-26 | open | Large result sets | – | – |
| 1852 | HeidiSQL freezes with slow views | https://github.com/HeidiSQL/HeidiSQL/issues/1852 | 2023-09-19 | open | UI freeze / metadata | Selecting a slow view freezes the UI (view SELECT or metadata runs synchronously [INFERENCE]). | – |
| 1748 | HeidiSQL Freeze when clicking on table during SHOW ENGINES | https://github.com/HeidiSQL/HeidiSQL/issues/1748 | 2023-01-11 | closed → 2023-01-13 | UI freeze / metadata | – | – |
| 2512 | SELECT @@SQL_MODE on old server freezes HeidiSQL | https://github.com/HeidiSQL/HeidiSQL/issues/2512 | 2026-06-10 | closed → 2026-06-12 (upstream, documentation) | UI freeze / startup query | – | – |
| 2607 | macOS: bad filter on Data tab freezes HeidiSQL | https://github.com/HeidiSQL/HeidiSQL/issues/2607 | 2026-09-24 | closed → 2026-09-27 (macos) | UI freeze / filter | Same class as #1694, on the Lazarus build. | Lazarus fix `905eb8f1` (2026-09-17) "stuck UI after firing query with SQL error on MS SQL" is similar [INFERENCE]. |
| 2073 | query result view slows down with sql server database | https://github.com/HeidiSQL/HeidiSQL/issues/2073 | 2025-01-20 | closed same day | Large result sets | – | – |
| 107 | slow string column sorting | https://github.com/HeidiSQL/HeidiSQL/issues/107 | 2018-01-29 | closed same day (needs-info) | Sorting | – | Later `0339a096` (#293) "prevent long sort operation on header click in large query results". [SOURCE: commit] |
| 2024 | Improve performance on export CSV | https://github.com/HeidiSQL/HeidiSQL/issues/2024 | 2024-09-20 | closed → 2024-11-20 (bug, performance), 20 comments | Grid export slow | 235,000 rows: 1–2 s in build 6832, then 23 s in 6836. | ansgarbecker: "reading the earlier used `TGroupBox.ItemIndex` was much faster than the newer `TComboboxEx.ItemIndex`"; fixed with "a simple local variable instead". Fix `53b8ea64` "fix slow grid export". [SOURCE: issue page; commit] |
| 1293 | Exporting to csv or delimited text is super slow | https://github.com/HeidiSQL/HeidiSQL/issues/1293 | 2021-01-26 | closed → 2026-02-06 (mssql, confirmed) | Export slow | – | – |
| 1333 | MSSQL csv file export, too slow | https://github.com/HeidiSQL/HeidiSQL/issues/1333 | 2021-03-12 | closed → 2025-11-14 | Export slow | – | – |
| 804 | Exporting in MSSQL is slow for large data sets | https://github.com/HeidiSQL/HeidiSQL/issues/804 | 2019-11-12 | closed → 2020-01-22 (confirmed, performance) | Export slow | – | Fix `feeb17e7` "use first 1000 rows only for calculating the CSV export size, and interpolate the rest. Closes #804". [SOURCE: commit] |
| 343 | SQL Dump exponentially slows down depending on the number of records in a table | https://github.com/HeidiSQL/HeidiSQL/issues/343 | 2018-09-05 | closed → 2020-11-20 (mssql, confirmed, performance) | SQL export slow | Export time grows super-linearly with row count. | `b9b59764` (2020-11-20, same day as close): "Fix slow data exports of large MS SQL tables, by introducing ORDER BY .. OFFSET .. FETCH clauses… Reduces the chunks to ~10000 rows". [SOURCE: commit] [INFERENCE: linked by date] |
| 1428 | Improve MySQL Backup speed with ORDER BY / LIMIT tweak | https://github.com/HeidiSQL/HeidiSQL/issues/1428 | 2021-08-16 | open (feature, performance) | SQL export slow | Chunked export with LIMIT/OFFSET is slow on big tables. | – |
| 1227 | import a 100MB mysql sql file too slowly | https://github.com/HeidiSQL/HeidiSQL/issues/1227 | 2020-11-19 | closed → 2020-12-17 (needs-info) | Large SQL import | – | – |
| 1262 | set autocommit=0 to fix importing slowly | https://github.com/HeidiSQL/HeidiSQL/issues/1262 | 2020-12-17 | closed → 2026-02-12 (feature) | Large SQL import | – | `c843b196` (2026-02-12, same day as close) "create SQL export option for wrapping DML commands in a BEGIN/COMMIT transaction". [SOURCE: master history] [INFERENCE: linked by date] |
| 1269 | Memory problem with large SQL files | https://github.com/HeidiSQL/HeidiSQL/issues/1269 | 2020-12-28 | closed → 2021-01-07 | Memory / SQL import | – | Report 02 §4 covers the "run file directly" size threshold. |
| 1033 | Hangs & high memory usage for various reasons, including MariaDB server connection dropping | https://github.com/HeidiSQL/HeidiSQL/issues/1033 | 2020-05-14 | open | Memory / reconnect | – | – |
| 200 | runaway CPU utilization & memory | https://github.com/HeidiSQL/HeidiSQL/issues/200 | 2018-03-20 | closed → 2026-04-02 (mssql, worksforme) | Memory / CPU | – | – |
| 1921 | Out of Memory error | https://github.com/HeidiSQL/HeidiSQL/issues/1921 | 2024-03-09 | closed → 2026-02-19 (postgresql) | Out of memory | – | – |
| 2574 | Several objects never freed (memory leaks) in forms, table editor and dbconnection | https://github.com/HeidiSQL/HeidiSQL/issues/2574 | 2026-08-06 | closed → 2026-08-18 (confirmed) | Memory leaks | Found by a contributor with a leak checker. | Fix `04d50c55` "bunch of often occurring memory leaks, found by @ArtemRazin". [SOURCE: lazarus-log] |
| 935 | Frequent HeidiSQL Crashes | https://github.com/HeidiSQL/HeidiSQL/issues/935 | 2020-03-08 | **open**, 26 comments, 3 👍 | Crashes; multiple connections; idle/resume | Crashes during CSV import, on disconnect "with lost connections or multiple DB connections", after idle or wake-up, after table-structure edits; one repro: click table → select grid column → switch to Query tab. | ansgarbecker: "It's unlikely I get the cause of all these crashes fixed, especially as there is no reproduction recipe." "You should probably restart HeidiSQL once after waking up the pc." On multiple instances: "There are issues with restoring tabs…". [SOURCE: issue page] |
| 458 | Crash after query (after laptop resumed from standby mode) | https://github.com/HeidiSQL/HeidiSQL/issues/458 | 2018-12-26 | open, 16 comments | Crash / reconnect after sleep | – | – |
| 2123 | HeidiSQL crashes on reconnect | https://github.com/HeidiSQL/HeidiSQL/issues/2123 | 2025-03-26 | closed → 2025-04-01 (postgresql), 19 comments | Reconnect crash | – | Fix `cfaa6ad7` "fix reconnect mechanism in TPGConnection.Ping()". [SOURCE: commit] |
| 1400 | CPU periodic spikes & hang following network/VPN disconnect | https://github.com/HeidiSQL/HeidiSQL/issues/1400 | 2021-06-30 | open (performance) | Reconnect / keep-alive | After a VPN drop: about 30 s CPU bursts and a non-responsive GUI, repeating. | ansgarbecker: "Please update to the latest build and retry. I fixed a CPU performance issue which I believe was a slightly different case." [SOURCE: issue page] |
| 1306 | HeidiSQL freezes if it cannot connect to database | https://github.com/HeidiSQL/HeidiSQL/issues/1306 | 2021-02-07 | closed → 2021-02-10 (needs-info) | Connect blocks UI | – | Connect is synchronous [INFERENCE]. `a073c3f7` (2026) "support cancelling server login dialog". |
| 654 | Frequent CRASHES running long queries or loading SQL files with no default THEME | https://github.com/HeidiSQL/HeidiSQL/issues/654 | 2019-05-22 | closed → 2020-06-11 (themes) | Crashes (VCL styles) | – | – |
| 2112 | HeidiSQL crashes completely after exec SQL statement | https://github.com/HeidiSQL/HeidiSQL/issues/2112 | 2025-03-11 | closed → 2025-06-09 | Crash | – | – |
| 1863 | Errors & access violation | https://github.com/HeidiSQL/HeidiSQL/issues/1863 | 2023-10-18 | closed → 2023-10-22 (wine), 21 comments | Access violations | – | In the same weeks: `b631bf1a` "Try to fix a crash in TMainForm.DBtreeFocusChanged:90, reported several times per upload, happening directly after connecting to a second session"; `f4adbc7f` "Silence a popular crash in DBtreeFocusChanged…"; `8d4dbfe1` "crash in DBtreeGetText… while connecting through SSH tunnel". [SOURCE: perf-commits] |
| 2344 | Crash on Qt when switching tables while on the "Data" tab | https://github.com/HeidiSQL/HeidiSQL/issues/2344 | 2025-12-22 | closed → 2026-01-05 (linux), 52 comments | Crash (Lazarus/Qt, DPI) | ERangeError in `TBaseVirtualTree.UpdateVerticalRange`, Qt5/Qt6 only (not GTK2). | ansgarbecker: "setting DesignTimePPI from 120 back to its default 96 in the .lfm file heals the crash!"; "Solution for now is to disable setting the VT.DefaultNodeHeight in FixVT, at least for Linux." [SOURCE: issue page] |
| 2570 | Latest Build 12.21.1.1 Crashes | https://github.com/HeidiSQL/HeidiSQL/issues/2570 | 2026-08-04 | closed → 2026-08-06 (macos) | Crash on start (macOS) | – | Fix `6727efc5` "crash on macOS while setting listForeignKeys.Width Refs #2570". [SOURCE: lazarus-log] |
| 463 | Crash in TInplaceEditorLink.Destroy when using grid inplace editor | https://github.com/HeidiSQL/HeidiSQL/issues/463 | 2019-01-03 | open, 15 comments | Grid editing | – | Related fixes: `cf88f553` and `b00a6632` (#2225, 2025) "crash in grid value change… Application.QueueAsyncCall in DoEndEdit". [SOURCE: commits] |
| 384 | Do not save data changes on arrow keys, mouse click | https://github.com/HeidiSQL/HeidiSQL/issues/384 | 2018-11-01 | open, 5 👍 | Grid editing: unintended writes | Edits are posted to the server implicitly when leaving the row. | Related: `ea9c4d03` (#2475, 2026-05) "mouse click in edited row calls save action although focus did not change"; `ca238aa2` (2026-08) rephrased the "grid edit/delete did not affect any row" warning. [SOURCE: commits] |
| 1114 | Slow SSH tunnel does not work with plink > v0.70 | https://github.com/HeidiSQL/HeidiSQL/issues/1114 | 2020-08-06 | closed → 2020-08-09 (ssh-tunnel), 14 comments | SSH / plink | – | Fixes `ad706f55` (send Enter after plink's "Access granted. Press Return…") and `c33624aa` (respect the plink timeout setting). [SOURCE: commits] |
| 711 | MySQL SSH Tunnel Issue: plink v0.71 incompatibility | https://github.com/HeidiSQL/HeidiSQL/issues/711 | 2019-07-11 | closed → 2019-08-16 | SSH / plink | – | – |
| 2118 | SSH-tunnel doesn't work with PuTTY > v0.81 | https://github.com/HeidiSQL/HeidiSQL/issues/2118 | 2025-03-14 | closed → 2025-05-24, 17 comments | SSH / plink | Passphrase prompt missing since plink 0.82. | Fix `57d670a3` "activate legacy-stdio-prompts mode in plink". [SOURCE: commit] |
| 2243 | SSH tunnel using plink v0.82+ ignores user input | https://github.com/HeidiSQL/HeidiSQL/issues/2243 | 2025-08-24 | closed → 2025-08-26 | SSH / plink / ssh.exe | Host-key prompt answers are ignored. | Fixes `95b1b02b`, `5f37814c` (disabling StrictHostKeyChecking): "I have no clue to where ssh.exe sends that message, it's neither in stdOu[t]…". [SOURCE: commits] |
| 2259 | HeidiSQL with plink on Windows 11: connection fails while Windows 7 works | https://github.com/HeidiSQL/HeidiSQL/issues/2259 | 2025-09-24 | closed → 2025-11-14 (needs-info) | SSH | – | – |
| 2302 | SSH tunnel parameters broken for Linux | https://github.com/HeidiSQL/HeidiSQL/issues/2302 | 2025-11-12 | closed same day (linux) | SSH (Lazarus) | – | `f2b2dc37` (same day) "support SSH passwords via sshpass". [SOURCE: commit] |
| 774 | Getting stuck on startup if connecting to last used session fails | https://github.com/HeidiSQL/HeidiSQL/issues/774 | 2019-10-20 | closed → 2019-12-08 (ssh-tunnel) | Startup / SSH | – | Fix `9d6a16ec` "Break Plink wait loop when server has sent an 'Access denied' error. Closes #774". [SOURCE: commit] |
| 2525 | Pulling maximized window to drag causes app freeze | https://github.com/HeidiSQL/HeidiSQL/issues/2525 | 2026-06-17 | closed → 2026-06-26 (themes) | UI freeze (styles/DPI) | – | – |

**Commit-only rows** (issue number from the commit message, title not fetched; URL is `https://github.com/HeidiSQL/HeidiSQL/issues/<n>`):

| # | Date of fix | Category | Commit evidence [SOURCE: repo commits] |
|---|---|---|---|
| 2599 | 2026-09-16 | Crash, MS SQL (Lazarus) | `9a209eb6` "access violation on MS SQL connections, after applying filter on data tab" |
| 2539 | 2026-07-03 | Hang | `1cbdf77f` "hanging in a loop over result nodes by right-click on query result" |
| 2388 | 2026-02-20 | High CPU, UI freeze | `7d207a41` "high CPU load and unresponsiveness through SynEdit highlighter when starting to edit large text in popup editor" |
| 2425 / 2426 | 2026-03 | Thread-safety crashes | `1fc1ca38` "potential crash after user query, due to running Connection.ShowWarnings directly in the TQueryThread"; `ee16571c` "crash after canceling query" |
| 2334 | 2026-05-29 | Reconnect | `2151b401` "automatic reconnect when firing query on a disconnected SQL server connection" |
| 2472 | 2026-06-29 | Crash on disconnected session | `0b0106d4` "crash during refresh on disconnected session" |
| 1907 | 2024-08-17 | SQL export slow | `e8287db8` "missing assignments in TDBObject.Assign, causing a default AvgRowLen of -1 which slows down the SQL export" |
| 1771 | 2023-02/03 | SSH on Wine | `603e162b` and `0fedb314`: SSH process-check intervals, "when WaitForSingleObject does not really wait (on Wine)" |
| 1243 | 2020-12-13 | Reconnect crash | `04a32cb0` "crash in reconnect, due to free'd list of IS objects" |
| 1482 / 1503 / 2534 | 2021–2026 | High DPI | A long series (`d46716ff` "reactivate DPI awareness", 2021-12; `b266d147` etc. 2025-03; `905b1265` "DPI related scale glitches, mainly on completion proposal"); `9eab28ea` (2026-01-22) "upgrade DPI awareness to Per-Monitor v2" |
| 685 | 2019-11-13 | CPU | `7b99bcc8` "Fix fatal performance bug, calling GetSQL on each examined character of a TSQLSentence. See report on https://www.heidisql.com/forum.php?t=34935" |
| 1738 | 2022-12-31 | Threads | `d96daa80` "revert recent VirtualTrees update… Could not get query threads to work in 64 bit mode." |

### 3.2 Architectural limits the evidence points to

1. **Only some work runs off the main thread.**
   - User queries in the Query tab run in a `TQueryThread`, and the tab's close button is blocked while it runs. [SOURCE: `f9af910f`, `1fc1ca38`]
   - The Data-tab SELECT, tree/metadata queries (`SHOW TABLE STATUS`, foreign keys, views), autocomplete lookups and connect/reconnect block the UI. See #1694 (open, "accepted"), #10, #1852, #1748, #1306 and #1638. [SOURCE: issue pages; INFERENCE for the ones I did not open]
   - Thread-safety bugs appear wherever the thread touches UI or connection state (`ShowWarnings` in the thread, #2425; keep-alive timer "cross-thread" fix `ae2b9084`). [SOURCE: commits]
2. **One connection per session, by design.** The maintainer defends this with session state ("user variables and assigned MariaDB roles", #547, closed wontfix). While one query runs, nothing else can use that session. [SOURCE: #547] A successor needs a second "metadata/control" connection with session-state replay, or explicit per-tab connections. [INFERENCE]
3. **Metadata cost grows with schema size.** `SHOW TABLE STATUS` is slow on big tables. The workaround is the per-session "Get full table status" opt-out (#1638, #1699). Batching system queries is still open (#1672). [SOURCE: #1638 quote]
4. **Rendering of large cells is fragile.** BLOB, JSON and multi-line cells caused CPU pegging and endless repaints (#548, #2020, `6e9be764`, `bc9693c6`, `2ba8bcab`). [SOURCE: commits]
5. **Exports are dominated by per-row UI or overhead costs rather than I/O.** Examples: the #2024 regression from reading a combobox index per row, MS SQL paging (#343, #804), and AvgRowLen (#1907). [SOURCE: #2024 quote; commits]
6. **Crash handling is reactive.** madExcept crash uploads are triaged in batches. The maintainer admits that crashes without a repro recipe will mostly stay unfixed (#935). Many crashes cluster around focus changes when a second session is involved, and around reconnect or wake-from-sleep (#458, #2123, #1243, `b631bf1a`). [SOURCE: #935 quotes; commits]
7. **SSH depends on external processes and stdout scraping.** plink, ssh.exe and sshpass are driven by parsing their output. Each PuTTY release broke it (0.71: #711; >0.70: #1114; 0.82: #2118 and #2243), plus Wine timing (#1771). A 2012 commit added "an automatic, non-configurable keep-alive ping, to prevent SSH tunnels from disconnecting" (`2f10329e`). [SOURCE: commits] A successor should embed an SSH library. [INFERENCE]
8. **High DPI is a long-running cost.** It dates from 2018 (#213) and was still being fixed in 2026 (Per-Monitor v2, #2534), and it caused the Qt crash #2344. [SOURCE: commits; #2344]
9. **Grid editing posts implicitly.** Row changes are saved on focus change (arrow keys or a click), which users want to turn off (#384, open). Spurious saves were fixed in #2475. [SOURCE: Q-open; commit]

---

## 4. Most requested open features

Open issues ranked by 👍 [SOURCE: Q-open, 2026-09-28]. Absolute numbers are small.

| # | Title | 👍 / total reactions | Comments | Created | Status notes |
|---|---|---|---|---|---|
| 944 | Create a Flatpak package | 13 / 13 | 11 | 2020-03-18 | Still open, although download.php now lists "Linux Flatpak". [SOURCE: download.php] Probably waiting on a Flathub listing. [UNVERIFIED] |
| 135 | Clickhouse support | 11 / 19 | 26 | 2018-02-13 | Branch `clickhouse-135` exists (last commit 2021-05-13). [SOURCE: heidisql-history branches] |
| 502 | Hierarchically group tables as in phpMyAdmin | 9 / 11 | 13 | 2019-01-25 | – |
| 80 | Make user manager compatible to PostgreSQL | 9 / 9 | 19 | 2018-01-08 | 2026 user-manager work (roles, auth plugins) was for MySQL/MariaDB. [SOURCE: master-log] |
| 276 | Autocomplete does not cache tables in MySQL/MariaDB | 8 / 8 | 0 | 2018-05-29 | Performance-related. |
| 116 | View single record as a form | 7 / 7 | 16 | 2018-02-02 | – |
| 10 | Autocomplete freezes GUI | 6 / 6 | 3 | 2017-11-14 | Performance-related. |
| 943 | Custom Redo keybinding doesn't work | 6 / 6 | 5 | 2020-03-16 | Bug (keyboard). |
| 155 | Show multiple tables at once (extra 'Data' tabs) | 6 / 6 | 22 | 2018-02-23 | Partly addressed. ansgarbecker: "Issue #604 made it into the last v12.11 release": select several tables and get a new query tab ("Select top 1000 rows", `1f9df284`). [SOURCE: issue page; commit] |
| 384 | Do not save data changes on arrow keys, mouse click | 5 / 6 | 5 | 2018-11-01 | Data-safety request. |
| 1091 | Support Snowflake DB | 5 / 5 | 6 | 2020-07-17 | – |
| 1525 | Query files quick access | 5 / 5 | 1 | 2022-02-01 | A "Project Manager" helper tab was added (`0081b255`, 2025-07-29) and then removed (`1bc89b12`, 2025-08-19, "remove project manager feature with all its unrelated changes"). [SOURCE: commits] |
| 1478 | Support integer Unix timestamps in milliseconds | 5 / 5 | 5 | 2021-11-05 | Fractional timestamps were added in `97a683b5` (2025-11-12); milliseconds are not confirmed. [INFERENCE] |
| 324 | Possibility to edit toolbar | 4 / 4 | 0 | 2018-08-04 | – |
| 379 | AUTO_INCREMENT does not work on MSSQL | 4 / 5 | 5 | 2018-10-30 | Bug. |
| 667 | SQL Server stored procedure code is invalid | 4 / 6 | 10 | 2019-06-04 | Bug. |
| 963 | Support H2 database | 4 / 4 | 3 | 2020-03-28 | – |
| 1689 | SQL editor shortcuts configuration does not work | 4 / 4 | 3 | 2022-10-21 | A shortcut filter box was added in `e1db0a6c` (#2482). |
| 1682 | Side-by-side tabs / split view | 4 / 4 | 0 | 2022-10-11 | – |
| 607 | Add option to disable Reload file | 4 / 4 | 5 | 2019-04-11 | – |
| 1242 | Execute query for multiple databases | 3 / 3 | 4 | 2020-12-01 | – |
| 1685 | DuckDB support | 3 / 3 | 10 | 2022-10-14 | – |
| 361 | SOCKS4 proxy connection | – | 6 | 2018-10-05 | From Q-ssh (open). |

**Themes** [INFERENCE]:
- More engines: ClickHouse, Snowflake, H2, DuckDB, plus a PostgreSQL user manager.
- Multi-view work: several Data tabs, split view, per-record form view.
- Non-blocking autocomplete with a metadata cache.
- Tree organisation: hierarchical grouping.
- Safer grid editing.
- Customisation: toolbar, keybindings.
- Packaging: Flatpak.

---

## 5. Notable UX-relevant changes, Sep 2024 – Sep 2026

Commits come from master (heidisql-history `git log master --since=2024-09-01`) and the lazarus log. The mapping to releases is by tag date [INFERENCE].

| Area | Change (commit, date) | ≈ Release |
|---|---|---|
| **Platform** | 32-bit dropped (`37a188c2` 2025-01; `84d35785` 2025-09) | 12.10 → 12.12 |
| Platform | Native Linux (GTK2/Qt5/Qt6, deb/rpm/Flatpak, arm64) and macOS arm64 builds from Lazarus | 2025-05 onward |
| Platform | Wine deprecation note in the update check (`05886c43` 2026-02-22) | 12.16 |
| Platform | Per-Monitor v2 DPI awareness (`9eab28ea` 2026-01-22) | 12.15/12.16 |
| Platform | Dark-mode-synced SQL/grid colour schemes (`d313a596`, Lazarus, Windows only) | v13 preview |
| **Database tree** | New "Display" main menu with toggles for the log panel and tree filters (`44ce3527` 2026-02-22, #1891) | 12.16 |
| Tree | Export includes tables hidden by the table filter (`dcebcc64`, #1983); "Check all" becomes "Change all visible" when filtered (`d54e555f`) | 12.16/12.17 |
| Tree | Table columns as child nodes, opt-in (`fc222fb6`, `bb5e724d` 2026-07); index sizes with colour bars in the table editor (`f1d0eb6a`) | 12.21 |
| Tree | Tree keeps database nodes expanded on refresh, and hides tabs of a dropped object (`c976d17e` 2026-09) | next |
| **Session manager** | Folders sortable to the top, locale-aware sorting (`03a58556`, `422c9ed1`, #1828); port column (`a6fa0556`); ForceUnicode opt-out (`b858cd65`); cancellable login dialog (`a073c3f7`) | 12.13–12.18 |
| **Data grid** | Multi-line cells disabled for performance (`bc9693c6` 2024-09); partial JSON loading (`2ba8bcab`) | 12.9 |
| Grid | "Select top 1000 rows" for several tables (`1f9df284` 2025-05, #604); new tab named after the table (`80938b74`); Shift+click uses the fully qualified name (`aa0f3503`) | 12.11, 12.19, next |
| Grid | Highlight the same text across selected rows (`1db76a51`); fractional UNIX timestamps (`97a683b5`); "source table: xyz" header hint (`ee7e7c29`); row/column index in headers (#2175) | 12.13/12.14 |
| Grid | Generated columns not editable (`fa73114c`); PostgreSQL ENUM drop-downs (`5150a7ff`); bypass FK lookup while editing (`118e9d98`); larger distinct-value quick filter (`1e13f130`); clearer "no row affected" warning (`ca238aa2`) | 12.16–12.21 |
| **Query editor** | Function description hint on hover (`b6702541` 2024-12); UTF-8 BOM (`1bd969f2`); option to skip the reformatter dialog (`bb5d5add`) | 12.9/12.10 |
| Editor | Delete a single history entry (`60d4537d`); rename a snippet (`699fde0c`); "copy formatted text" (`da5e781c`); no duplicate SQL-file load (`9fb90d1f`); no save prompt when tabs auto-restore (`27c38954`); automatic backup of closed query tabs (`d5915e04`, #2236) | 12.11–12.18 |
| **Export** | Grid export "Open file after creation" (`13233a2b`, #912); export focused column only (`09c6afa0`); HTML export dark/light aware (`7ef9f45a`); time fractions kept in CSV (`72a3a898`) | 12.13–12.18 |
| Export | SQL export: BEGIN/COMMIT wrapping (`c843b196`), app name/version injected into long queries (`40167417`), type prefix for one-file-per-object (`aaa9b232`), column names for invisible columns (`55c723ff`) | 12.12–12.17 |
| **SSH** | sshpass passwords (`f2b2dc37`); local-port collision check (`fe55cba0`); SSH command-line switches (`1cd583d7`); `LogLevel=error` (`923296df`); plink 0.82 compatibility (#2118, #2243) | 12.11–12.18 |
| **Admin** | MariaDB/MySQL roles and default roles, auth-plugin selection, user-manager filter box, RELOAD privilege no longer required, KILL QUERY instead of KILL (`c0122a8a`, `419acd7e`, `ec6f7ec6`, `a872ed2a`, `e7ea5aa7`, `1f69da5f`) | 12.13–12.18 |
| **Preferences** | Filter box for shortcuts (`e1db0a6c`, #2482) | 12.18 |
| **Stability** | Memory-leak sweep (#2574), batches of crash-report fixes, CVE library updates, SECURITY.md | 12.16–12.21 |

---

## 6. Takeaways for the successor spec [INFERENCE]

1. **Never block the UI on I/O.** Every server call, including metadata, Data-tab SELECTs, autocomplete, connect and reconnect, must be async and cancellable. This is the most persistent complaint class (#1694, #547, #10, #1852, #1306) and the maintainer has not solved it structurally.
2. **Keep session semantics but add a side channel.** Keep a primary connection per session (the user-variable and role argument from #547 is valid), and add a pooled metadata connection that replays session state.
3. **Make metadata scale.** Load the tree lazily and with virtualisation. Make `SHOW TABLE STATUS`-class queries opt-in or deferred, batch system queries (#1672), cache per session, and invalidate explicitly.
4. **Cap and stream large cells.** BLOB, JSON and TEXT values should load previews only and render off the main thread.
5. **Build export and import as streaming pipelines** with progress and cancel, and include benchmarks in CI so regressions like #2024 are caught.
6. **Use an embedded SSH client** with keep-alive and auto-reconnect, and a documented reconnect policy (sleep/resume, VPN drop).
7. **Make grid edits explicit.** Stage edits with an explicit commit, and offer "save on row leave" only as a preference (#384).
8. **Treat crash reporting as a core feature:** structured logs, repro capture, and multi-session tests.
9. **Documentation gap:** the official help omits filters, snippets, autocomplete, session colours, settings export and grid formats. A successor should ship searchable in-app docs.
10. **Licensing:** do not reuse Icons8 assets. Treat the GPL-2.0 code as reference only unless the successor is also GPL.
