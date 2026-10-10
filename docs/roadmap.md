# Roadmap

State on 9 Oct 2026: **1.0.0-beta.1**. The MVP (phases 1–9, 12 and 13) and phase 14 (cross-platform, robustness and beta) are done; the details are in `docs/progress.md`, the guide in `docs/user-guide.md` and the latest code validation in `docs/validation.md`.

Order of priority (the same as the principles in spec section 2): first what protects data and stability, then what separates the beta from 1.0, then the engineering quality that prevents regressions, then features by everyday use, and finally new engines.

**Legend.** Priority: **P0** before 1.0; **P1** in the following 1.x cycle; **P2** when there is time. Effort: **S** hours to 2 days; **M** up to 2 weeks; **L** more. "Needs" lists what depends on the author or on external resources.

## 1. Up to 1.0 (beta → stable)

| # | Item | Why | P | Effort | Needs |
| --- | --- | --- | --- | --- | --- |
| 1.1 | Beta with HeidiSQL users on the three systems (the issue templates already exist) | Automated tests do not replace real use on production databases | P0 | M | Users |
| 1.2 | The checks in `docs/manual-checks.md` (side-by-side comparison with HeidiSQL, golden files, reference machine, NVDA/VoiceOver) | Fidelity and performance measured on the right machine | P0 | M | HeidiSQL installed, reference hardware |
| 1.3 | Code signing: Authenticode on Windows, Apple Developer ID and notarisation | Without it SmartScreen and Gatekeeper warn on every install | P0 | S | Certificates (cost) |
| 1.4 | Installers: MSI or winget (Windows), `.dmg` (macOS), `.deb`/`.rpm` or Flatpak/AppImage (Linux) | Today there are a ZIP, an `.app` in a zip and a tar.gz with `install.sh` | P0 | M | Packaging tools, choice of channels |
| 1.5 | Updates: an opt-in check with the signature validated before installing (spec section 23.2) | Shipping security fixes | P0 | M | Item 1.3 |
| 1.6 | License (ADR-16), `LICENSE` and a `SECURITY.md` with the vulnerability disclosure process | Without a license nobody can legally use it; without a security channel, flaws arrive through public issues | P0 | S | Author's decision |
| 1.7 | Make the repository public | The code is already in a repository with fresh history and only fictitious data | P0 | S | The license decision |
| 1.8 | linux-arm64 with NativeAOT (today self-contained with JIT) | Faster startup on Raspberry Pi and ARM Linux | P1 | S | ARM runner or a cross-compile sysroot in CI |
| 1.9 | Transaction state from the protocol (`SERVER_STATUS_IN_TRANS`) instead of reading the statements | The current tracking does not see a `COMMIT` inside a routine | P1 | M | An API in MySqlConnector (or a contribution to the project) |

## 2. Engineering quality

Done on 9 Oct: `.editorconfig` with style checked in the build, `dotnet format --verify-no-changes` and the vulnerability check in CI, minimal CI permissions, contribution rules (`CONTRIBUTING.md`), PR template and `CHANGELOG.md`.

| # | Item | Why | P | Effort | Needs |
| --- | --- | --- | --- | --- | --- |
| 2.1 | Protect `main` on GitHub: required PRs, required `test`, `publish`, `integration` and `quality` jobs, no force pushes | Today nothing stops a direct push that skips CI | P0 | S | Repo owner (a GitHub setting) |
| 2.2 | Code coverage in CI (`dotnet test --collect "Code Coverage"`, already part of the Test SDK) with a per-project report and a threshold that only goes up; starting with the core (`Engine.*`, `Drivers.*`, `App`) | Knowing what the 1,377 tests do not exercise, above all on the write paths | Done (9 Oct): `coverage` job, `build/coverage.py` | S | — |
| 2.3 | Microsoft analyzers in `AnalysisMode=Recommended`, project by project (core first), with justified exceptions in `.editorconfig` | Today only the minimal set runs; see the count per rule in `docs/validation.md` | P1 | M | — |
| 2.4 | Headless tests with a per-test timeout and an unexpected modal dialog failing the test (instead of waiting) | An error dialog waiting on the shared UI thread hung the whole run (fixed for the stopped-server case; the general safeguard was missing) | Done (9 Oct): `UnexpectedDialogGuard`, `UiTimeouts` | S | — |
| 2.5 | Automated release per tag: deterministic build (`ContinuousIntegrationBuild`), SHA-256, SBOM (CycloneDX or SPDX, spec section 23.2), provenance attestation and notes from `CHANGELOG.md` | A verifiable supply chain; today releases are made by hand | P1 | M | Approval of the new actions/tools |
| 2.6 | Assisted dependency updates (Dependabot or Renovate) for NuGet and GitHub Actions, grouped and respecting the pinned limits (Avalonia 12.1.x, xUnit 3.x) | Dependency security fixes without manual work | P1 | S | Author's decision (it opens PRs) |
| 2.7 | Actions pinned by SHA and versioned service images (today `linuxserver/openssh-server:latest`) | Reproducibility and protection against moved tags | P1 | S | Item 2.6 to keep them up to date |
| 2.8 | Static security analysis (CodeQL for C#) and OpenSSF Scorecard | Finding unsafe patterns the tests do not look for | P1 | S | — |
| 2.9 | Mutation testing (Stryker.NET) on the write paths: `RowWriter`, the DML and DDL composers, dialect literals | Makes sure the data safety tests fail when the code is wrong | P1 | M | Approval of the tool |
| 2.10 | NuGet lock files (`RestorePackagesWithLockFile`, restore in `--locked-mode` in CI) | Reproducible restore; mind the NativeAOT packages per RID | P2 | S | — |
| 2.11 | Continuous fuzzing (SharpFuzz/libFuzzer) of the lexer, splitter and CSV reader, on top of the current deterministic fuzzing | Real pathological inputs (customer scripts, broken CSV files) | P2 | M | Approval of the tool |
| 2.12 | Architecture test: no project outside `Drivers.MySql` and `Engine.Sql` uses `MySqlIdentifier` or `MySql*` types (after item 3.1) | Keeps the core from depending on MySQL again | Done (9 Oct): `DialectBoundaryTests` | S | — |
| 2.13 | Snapshots per system (reviewed Linux and macOS baselines) | Today only Windows compares pixels; on the other systems the PNGs stay as a CI artifact | P1 | M | — |
| 2.14 | End-to-end UI tests against real servers on Linux and macOS in CI too | Today the headless tests with a server run locally on Windows | P1 | M | — |
| 2.15 | Measure PR-01 to PR-21 on the three systems (bench in CI per system) with a regression threshold | The current numbers are Windows' and do not block regressions | P2 | M | — |

## 3. Architecture and technical debt

| # | Item | Why | P | Effort |
| --- | --- | --- | --- | --- |
| 3.1 | Take `MySqlIdentifier` out of the code outside the driver and go through `IDialect`: `App/Query/QueryCompletion.cs:154` (backticks built by hand), `App/Query/QueryHelpers.cs:100-114`, `UI.Shell/MainWindow.Objects.cs:258-268`, `UI.Shell/MainWindow.Query.cs:313` | It breaks "the core does not know MySQL" and "SQL is never assembled by hand"; a prerequisite for PostgreSQL | Done (9 Oct): through `Engine.Sql`'s `ISqlLanguage` | S |
| 3.2 | Split `MainWindow` (30 partial files, 7,700 lines; `UI.Shell` is 22% of the code) into controllers per area (data, queries, objects, sessions) with tests without UI | The shell concentrates too much state; changes to different flows touch the same class | P1 | L |
| 3.3 | Split `DataGridControl` (1,671 lines) and `TreeListControl` (1,102) into input, layout, rendering and selection | Files too large to review safely; the grid is the hottest path | P1 | M |
| 3.4 | Move value validation and raw text (`FormatRaw`) into a per-column value type shared by the grid, export and DML | Today the text ↔ SQL rules live in three consistent but separate places | P1 | M |
| 3.5 | Lexer and splitter per dialect in `Engine.Sql` (today only MySQL) behind an interface | A prerequisite for PostgreSQL (`$$`, `E''`, quoted identifiers) | P1 | M |
| 3.6 | Watchdog process (spec section 21.6): detects a crash or hang of the main process, offers to restart and restore the tabs | Today there are crash reports and the journal, but a hang means killing the application | P2 | M |

## 4. Observability, security and accessibility

| # | Item | Why | P | Effort |
| --- | --- | --- | --- | --- |
| 4.1 | Local tracing with an `ActivitySource` per operation (connection, query, fetch, job), including MySqlConnector's signals, and a "Diagnostics" panel with the timeline (spec section 21.5) | Explaining "why is it slow" (network, server, client or UI) without guessing | P1 | M |
| 4.2 | Secret vault in a file encrypted with a passphrase (portable mode and Linux without a keyring; Argon2id or PBKDF2 + AES-GCM, spec section 23.2) and settings export with secrets | Today portable mode and Linux without a keyring do not keep passwords | P1 | M |
| 4.3 | Documented threat model (SSH, TLS, `LOAD DATA LOCAL`, imported files, crash reports) | Explicit security decisions, reviewed on every change to those areas | Done (9 Oct): `docs/threat-model.md` | S |
| 4.4 | A test that walks every window and requires `AutomationProperties.Name` on the interactive controls (today there are 14 explicit names in 4 files plus the grid's peer) | Screen readers; the manual check with NVDA/VoiceOver is pending | Done (9 Oct): `AccessibilityTests`, `AccessibleNames` | S |
| 4.5 | Theme contrast checked from the tokens (WCAG 2.1 AA) and snapshots at 150% and 200% scale | Readability in every theme and screen. Done (9 Oct): `ThemeContrastTests` (compliant pairs cannot drop; the others cannot get worse). Missing: snapshots at 150% and 200% | P2 | S |
| 4.7 | Decide on the colours below AA (list in `ThemeContrastTests`): white text on the accent (focused grid cell, 4.28:1), secondary text on dark panels (4.26:1), HeidiSQL highlighting in the light theme (comments 3.95:1, identifiers 3.14:1, variables 4.20:1), editor and SQL log line numbers (2.4–3.5:1), the favourites star and the key icon in light (1.7 and 2.3:1); and the field borders (1.3–1.6:1; 1.4.11 asks for 3:1) | Fidelity to HeidiSQL (spec section 5.6) against readability; changing the accent touches every snapshot baseline | P2 | S |
| 4.6 | Crash reports with opt-in sending and a preview (spec section 21.5) | Today they stay on the user's machine | P2 | M |

## 5. 1.x features (in order of use)

1. Structured editors for views, routines, triggers and events (today they are edited, safely, through their `CREATE`).
2. User manager (accounts, privileges), refused by read-only sessions.
3. Table tools: Bulk table editor, Generate data (Maintenance and Copy table done on 9 Oct).
4. Export to another database or server; Insert files (Save BLOB and Image preview done on 9 Oct).
5. Query profile and bind parameters in the editor; function signatures in the autocomplete (code folding done on 9 Oct).
6. Partitions in the table editor; dragging columns to indexes.
7. A native global menu on macOS generated from the command registry (today the menu is in the window).
8. New rows in the grid after the focused row (as in HeidiSQL), instead of at the end.
9. Safer production sessions by default: a persistent warning strip on sessions marked with the production colour and a read-only suggestion for those sessions.
10. Query history searchable by session and database, with re-running and "open in a tab".
11. Command palette (Ctrl+Shift+P) on top of the command registry: finds any command by name without changing HeidiSQL's UX. Done on 9 Oct.
12. Import of DBeaver and MySQL Workbench sessions; SSH hosts from `~/.ssh/config`.
13. Schema comparison between two databases or servers, with the ALTER script generated by the composers (reviewed and run by the user).
14. Autocomplete B (grammar) and diagnostics in the editor (spec phase 11).

## 6. 2.0: other engines

1. PostgreSQL (spec phase 13): Npgsql provider, dialect, DDL composer, types (arrays, enums, intervals), COPY, schemas. The architecture already separates driver, dialect and composers; before that, items 3.1, 3.5 and 2.12.
2. SQLite and SQL Server after PostgreSQL.

## Out of scope (spec, section 2)

Database modelling (ERD forward engineering), UX redesign by taste, and arbitrary scripts or extensions. An idea like these only comes in with an explicit decision by the author and an ADR.
