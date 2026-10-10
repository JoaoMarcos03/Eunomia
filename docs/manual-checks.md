# Manual checks

Updated on 9 Oct 2026 (phase 14: section G). These checks cannot be done within the project's rules: they need HeidiSQL installed (the golden files and the comparisons), the reference machine (the measurements), a person (manual tests and the beta) or material that is not in the repository (certificates). Nothing here blocks development; they are recorded for when the means exist. The state of each MVP criterion is in `docs/mvp-criteria.md`.

Commands in PowerShell, at the repository root. The tests against a server need the test server:

```powershell
powershell -File tests\Integration\Start-TestServer.ps1
```

## A. Comparisons with HeidiSQL

They need HeidiSQL 12.x installed on the same machine. Once it is installed, comparisons A1–A3 can be automated (generate the files from the command line, or by hand once, keep them in `tests/Golden/` and compare them in the tests).

| # | Check | How | Expected result |
| --- | --- | --- | --- |
| A1 | SQL export identical to HeidiSQL's (FAM-E06; criterion 27.3 #3) | On a database with tables, two views that depend on each other, a trigger, a procedure and data with NULL, binary and text with quotes, run Tools › Export database as SQL in HeidiSQL and in Eunomia with the same options; compare with `fc /n` or a diff | Identical except the version line ("HeidiSQL Version" / "Eunomia Version"). The format was reconstructed from appendix B: blank lines and block comments are the most likely places to differ |
| A2 | The 16 Copy as / Export grid rows formats (FAM-C03; 27.3 #3) | Export the same result (with NULL, dates, numbers, text with quotes and line breaks) in each format in both programs, with the default options | Identical byte for byte, except the spec's D6 differences |
| A3 | Settings import (FAM-S05) | Export the settings of a real HeidiSQL 12.21 with a password containing "ú" and an SSH session; copy them to `tests/Unit/Infra.Settings.Tests/` in place of the sample file and run `dotnet test tests\Unit\Infra.Settings.Tests`. With HeidiSQL installed, open Eunomia without sessions (an empty portable folder) and accept "Import them now?" on the first start | Every session, folder, shortcut and favourite imported, from the file and from the registry; the password with "ú" correct |
| A4 | Client-mode CSV baseline (PR-15) | Import a 1 GB CSV in HeidiSQL with "Client parses file contents" and time it | Serves as the reference for Eunomia's client mode |

## B. Measurements on the reference machine

Run on a quiet machine (no other applications), with the Release/NativeAOT executable (`powershell -File build\Publish-Portable.ps1`). The results go into `docs/mvp-criteria.md`.

| # | Requirement | How |
| --- | --- | --- |
| B1 | PR-01 (startup), PR-04 (scroll), PR-16 (idle memory), including at 150% Windows scaling and cold | `powershell -File bench\Scenarios\Run-All.ps1`; cold: restart the PC before the first run |
| B2 | PR-13, PR-14, PR-15 with 1 GB and 10 GB, 3 runs | `$env:EUNOMIA_PERF=1; $env:EUNOMIA_PERF_ROWS=8000000; dotnet test tests\Integration -c Release --filter "FullyQualifiedName~TransferPerformanceTests"` (raise the rows for 10 GB); needs XAMPP's `mysql.exe` and `mysqldump.exe`, or `EUNOMIA_MYSQL_BIN` |
| B3 | PR-02, PR-03, PR-18, PR-19 with the real UI | Use the application with 10 open sessions, long queries, 2 exports and 1 import; the diagnostic log (`%LOCALAPPDATA%\Eunomia\logs\diagnostic.jsonl`) records each stall > 250 ms; the automatic B14 (`$env:EUNOMIA_PERF=1; dotnet test tests\Integration -c Release --filter "FullyQualifiedName~SoakTests.StressB14"`) only measures the engine |
| B4 | 24 h soak (27.3 #4) | `$env:EUNOMIA_SOAK_MINUTES=1440; dotnet test tests\Integration -c Release --filter "FullyQualifiedName~SoakTests.SoakWithoutMemoryLeaks"`; here it ran for 10 min without leaks |
| B5 | PR-05, PR-08, PR-12 | PR-08 measured 1.6 s with 5,000 tables on MariaDB 10.4 (above the target on that version); repeat on MariaDB 10.6+ and MySQL 8 |

## C. Manual tests

| # | Check | How | Expected result |
| --- | --- | --- | --- |
| C1 | PR-21 (loss after a crash) | Type in a query tab, wait 3 s, type more, kill `Eunomia.exe` in Task Manager and reopen | The text up to about 2 s before the kill comes back |
| C2 | SSH with an agent | Connect over SSH with a key in Pageant and then in the OpenSSH agent | Connects without asking for the passphrase; CI only tests OpenSSH on Linux |
| C3 | Accessibility | Go through the Session Manager, the tree, the Data tab, a query tab, the export and import dialogs and the Schema Visualizer with Narrator and with NVDA, using only the keyboard | Every control reachable by keyboard and with its name read. Avalonia 12.1.3 has no UIA Grid/Table patterns: the grid is not read cell by cell |
| C4 | IME, text and menus (ADR-02) | Type with an IME (Japanese or Chinese) in the editor and the grid; review the menu text at 100% and 150% | Correct composition; crisp text |
| C5 | Real CSV files | Import an Excel CSV with `;` and locale numbers, and an export with `\N`, in both modes | Values identical to the file |
| C6 | Splitter with real scripts | Gather 100+ real scripts (dumps, procedures with DELIMITER) in `tests/Unit/Engine.Sql.Tests/` and compare with the expected split | The same statements that `mysql` runs |
| C7 | Branding on Windows and GitHub | Look at the icon in Explorer, the taskbar, Alt+Tab and the title bar at 100% and 150%; open the README on GitHub with the light and the dark theme; open Issues › New issue | A crisp icon (sizes ≤ 32 px come from the small variant); the lockup text readable in both themes; the Bug and Feedback forms appear and there is no blank issue. Checked automatically only: the exe's Win32 resources, the windows' `WM_GETICON` and a capture of the window at 100% |
| C8 | Data tab on a slow remote server (beta feedback, 9 Oct) | With the new build, the reporter opens a large table on the remote server and, halfway through loading, chooses another table, a table in another database, the database node, and runs a query on the same session; repeat with Show all; walk through small tables with the arrow keys; open the server's largest database and click a table straight away | The window always responds; what was chosen shows in 1 to 2 s (the time of `KILL QUERY` over the network), small tables without delay, and the table in the large database without waiting for the statistics; no errors in the SQL log |

## D. Distribution

| # | What | Missing |
| --- | --- | --- |
| D1 | Installers | Windows: MSIX, WiX or Inno Setup (today a portable ZIP from `build/Publish-Portable.ps1`); macOS: `.dmg`; Linux: `.deb`/`.rpm` or Flatpak/AppImage (today a tar.gz with `install.sh`, from `build/package-unix.sh`) |
| D2 | Code signing | Certificates (not in the repository): Authenticode for `Eunomia.exe`; Apple Developer ID and notarisation for the `.app` (today ad-hoc signing) |
| D3 | Schema Visualizer layout | If our own layout is not good enough on large real schemas, vendor MSAGL (MIT) behind `IGraphLayout` and compare with 300 and 1,000 real tables (section 17.4). Needs the code to be downloaded |

## E. Beta (27.3 #5)

Three daily HeidiSQL users use Eunomia for a week in real work and report the blocked workflows. Their guide is `docs/user-guide.md`. They get a pre-release built by CI, and report through the Bug and Feedback issue templates.

## G. macOS and Linux (phase 14)

CI builds, runs the tests (unit, headless with real rendering, system vaults) and starts the NativeAOT executable on Linux (xvfb) and macOS; the rendered screens are kept as the `snapshots-actual-*` artifact. What needs a person on a real computer:

| # | Check | How | Expected result |
| --- | --- | --- | --- |
| G1 | Installing on macOS | Download `Eunomia-<version>-osx-arm64.zip` (or `osx-x64` on an Intel Mac) from the CI artifact, move it to Applications, open it with right click › Open | It opens; the Dock shows the icon; the application menu says "Eunomia" and "About Eunomia" opens the dialog |
| G2 | Keychain | Save a session with a password, close, reopen and connect; open Keychain Access | Connects without asking for the password; there is an "Eunomia (conn/…)" item |
| G3 | ⌘ shortcuts | ⌘T, ⌘Enter, ⌘C/⌘V in the grid, ⌘-click in the tree and the grid, ⌘Z in the editor | They do what Ctrl does on Windows |
| G4 | Linux GNOME (Wayland) and KDE | `eunomia/install.sh` from the tar.gz; open from the menu; save a session with a password | Icon and shortcut in the menu; the password in GNOME Keyring/KWallet (Seahorse shows "Eunomia (conn/…)") |
| G5 | Linux without a keyring or portal (for example, a minimal WM) | Start; Open file | A "No system keyring" warning in the SQL log; Open shows the box to type the path |
| G6 | HiDPI scaling | macOS Retina; Linux at 150%/200% scaling (GNOME) | Crisp text, nothing cut off in the dialogs |
| G7 | Screen readers | VoiceOver (macOS) and Orca (Linux) on the tree, the grid and the dialogs | The automation names are read |
