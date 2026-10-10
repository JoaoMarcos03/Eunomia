# Code validation

Validation of 9 Oct 2026 on `main` at `9ed5669` (1.0.0-beta.1), on the author's machine (Windows 10 22H2, i7-6700, 16 GB). The open items moved to `docs/roadmap.md` (reference in brackets).

## Summary

| Check | Result |
| --- | --- |
| Build (Debug, `TreatWarningsAsErrors`, AOT and trimming analyzers) | 0 warnings, 0 errors |
| Tests (`dotnet test Eunomia.slnx`, local MariaDB 10.4 server) | 1,377 tests: 1,364 pass, 13 skipped, 0 fail |
| Skipped tests | 9 integration tests (measurements, soak and SSH, which need their own variables), 2 for other systems' vaults (Keychain, Secret Service), 2 headless measurements (`EUNOMIA_PERF`) |
| Packages with known vulnerabilities (`dotnet list package --vulnerable --include-transitive`) | None |
| Formatting and style (`dotnet format --verify-no-changes` with the new `.editorconfig`) | Passes, after fixing 1 file with different indentation, 22 files with `using` directives out of order and 4 fields that should be `readonly` |
| Microsoft analyzers in `AnalysisMode=Recommended` (not enabled) | 137 warnings in 14 rules (table below) |

The code is in good shape: a very consistent style (LF, UTF-8 without BOM, no trailing spaces, file-scoped namespaces, braces always, `sealed` by default), no `TODO`/`FIXME`, no empty `catch` blocks, 3 warning suppressions, all justified, `ConfigureAwait(false)` in the libraries and tested dependency rules between layers.

## Metrics

| Measure | Value |
| --- | --- |
| Production code (`src/`) | 239 C# files, ~51,600 lines, 30 projects |
| Tests (`tests/`) | 141 C# files, ~17,100 lines, 19 projects (16 unit, integration, headless, architecture) |
| Largest project | `UI.Shell`: 11,234 lines (22% of the code); `MainWindow` has 30 partial files (7,675 lines) |
| Largest files | `DataGridControl.cs` 1,671, `TreeListControl.cs` 1,102, `SqlExportJob.cs` 835, `DatabaseTreeControl.cs` 801, `SqlExportDialog.cs` 799 |
| `async void` | 6, all UI event handlers or benchmark hooks |
| Synchronous waits | 2 `Wait()` calls in the tab journal, only when the window closes (acceptable: the journal is not lost) |
| `catch (Exception …)` | 113, at boundaries (commands, jobs, clean-up) |

## Strengths

- Data safety with tests against real servers (MySQL 8.0/8.4, MariaDB 10.4/10.6/11.4): `DataSafetyTests`, round trip per type, lossless ALTER, conflict detection, read-only sessions.
- CI on Windows, Linux and macOS, NativeAOT publish of the 5 targets with a smoke test of the executable.
- Deterministic fuzzing of the lexer, splitter and CSV; headless snapshots of every screen; layer architecture tests.
- Secrets kept out of logs and errors (`SecretMasking`, a dedicated test), system vaults on the three systems.

## Findings

| # | Finding | Where | Severity | State |
| --- | --- | --- | --- | --- |
| V1 | With a stale `%TEMP%\eunomia-it\connection.txt` (server stopped, for example after a restart), the UI tests that connect opened the error dialog, which waited on the shared UI thread: the run hung for more than 25 minutes | `tests/UI.Headless/RealServer.cs`, `tests/Integration/TestServer.cs` | High (local test runs unusable) | Fixed: the local file only counts if the server answers; with `EUNOMIA_IT_MYSQL` (CI) a server that is down still fails. General safeguard in the roadmap (2.4) |
| V2 | No `.editorconfig` and no formatting or style check | Root, CI | Medium | Fixed: `.editorconfig`, `EnforceCodeStyleInBuild` and `dotnet format --verify-no-changes` in CI |
| V3 | No vulnerability check in CI (section 23.2 asks for one) | `.github/workflows/ci.yml` | Medium | Fixed (the `quality` job). SBOM in the roadmap (2.5) |
| V4 | CI with the token's default permissions and without cancelling old runs | `.github/workflows/ci.yml` | Low | Fixed (`permissions: contents: read`, `concurrency`) |
| V5 | `MySqlIdentifier` used outside the driver, in one case with backticks built by hand | `App/Query/QueryCompletion.cs:154`, `App/Query/QueryHelpers.cs:100-114`, `UI.Shell/MainWindow.Objects.cs:258-268`, `UI.Shell/MainWindow.Query.cs:313` | Medium (blocks PostgreSQL; text inserted in the editor, never run on its own) | Roadmap (3.1, 2.12) |
| V6 | Concentration in the shell and very large controls | `UI.Shell` (30 `MainWindow` partials), `DataGridControl.cs`, `TreeListControl.cs` | Medium (maintenance) | Roadmap (3.2, 3.3) |
| V7 | Types that hold disposable fields without being `IDisposable` (CA1001), to be reviewed one by one | `App.Desktop/App.cs:21`, `App/Transfer/CsvImportJob.cs:70`, `UI.Features.Transfer/CsvImportDialog.cs:29`, `UI.Features.Transfer/SqlExportDialog.cs:29`, `UI.Shell/MainWindow.AutoRefresh.cs:14`, `UI.Shell/Views/SqlFileRunWindow.cs:18` | Low (windows release on closing; to be confirmed) | Roadmap (2.3) |
| V8 | Text comparisons without an ordinal `StringComparison` (CA1309); they may be intentional (sorting for the user) | `App/Sessions/SessionCatalog.cs:325`, `Engine.Results/FilteredRowSource.cs:180`, `UI.Controls.Common/TreeList/TreeListControl.cs:1011` | Low | Roadmap (2.3) |
| V9 | Parts of the spec not yet implemented: tracing with `ActivitySource` and a Diagnostics panel (21.5), watchdog (21.6), opt-in update check and SBOM (23.2), passphrase vault (23.2) | — | — | Roadmap (4.1, 3.6, 1.5, 2.5, 4.2) |
| V10 | Accessibility: 14 explicit automation names in 4 files and the grid's peer; no test requiring it on every control | `src/UI.*` | Medium | Roadmap (4.4) |
| V11 | No `CHANGELOG.md`, `CONTRIBUTING.md` or PR template; `LICENSE` and `SECURITY.md` depend on ADR-16 | Root | Low | Fixed, except the license (roadmap 1.6) |
| V12 | The original repository's history held reference screenshots that cannot be published | Git history | Process | Code published in a new repository with fresh history and without the screenshots |
| V13 | An unversioned service image (`linuxserver/openssh-server:latest`) and actions pinned by tag | `.github/workflows/ci.yml` | Low | Roadmap (2.7) |

## Analyzers in `AnalysisMode=Recommended`

Measured with `dotnet build --no-incremental -p:AnalysisMode=Recommended -p:TreatWarningsAsErrors=false`. Unique warnings per rule:

| Rule | Warnings | What |
| --- | --- | --- |
| CA1826 | 52 | LINQ `First()`/`Last()`/`Count()` on lists instead of the indexer or `Count` |
| CA1859 | 38 | A concrete type instead of an interface on private members (performance) |
| CA1720 | 17 | Identifiers with type names (`Int`, `String`…) |
| CA1822, CA1001 | 6 each | Members that could be `static`; types with disposable fields (V7) |
| CA1861, CA1716 | 4 each | Constant arrays as arguments; names equal to reserved words |
| CA1309 | 3 | Non-ordinal text comparison (V8) |
| CA1711, CA1870, CA1865, CA1834, CA1805, CA1513 | 1–2 each | Name suffixes, `SearchValues`, `char` instead of `string`, redundant initialisations, `ThrowIfDisposed` |

## How to repeat it

```bash
dotnet build Eunomia.slnx
dotnet test Eunomia.slnx
dotnet format Eunomia.slnx --verify-no-changes --severity warn
dotnet list Eunomia.slnx package --vulnerable --include-transitive
dotnet build Eunomia.slnx --no-incremental -p:AnalysisMode=Recommended -p:TreatWarningsAsErrors=false
```

For the tests with a real server: `powershell -File tests\Integration\Start-TestServer.ps1` before `dotnet test`.
