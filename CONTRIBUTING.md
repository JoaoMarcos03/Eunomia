# Contributing to Eunomia

Rules, coding standards and process for anyone who contributes. If a rule conflicts with the specification, the specification wins and the rule gets fixed.

## 1. Documents

| Document | Purpose |
| --- | --- |
| `docs/progress.md` | Current state, provisional decisions, what is left |
| `docs/roadmap.md` | What comes next, by priority |
| `docs/spec/specification.md` | Source of truth (~2,000 lines), with appendices A–G in `docs/spec/appendices/` |
| `docs/validation.md` | Latest code validation (metrics, findings, test status) |
| `docs/mvp-criteria.md`, `docs/phases/` | MVP criteria; the plan of each phase |
| `docs/adr/` | Architecture decisions. A new architecture decision is a new ADR |
| `docs/manual-checks.md` | What cannot be checked automatically |
| `docs/user-guide.md` | User guide |
| `docs/performance.md`, `docs/security-audit.md`, `docs/threat-model.md` | Performance on remote servers; security audit; threat model (review it when touching the areas in its section 6) |

## 2. Setting up

1. Install the .NET SDK pinned in `global.json` (10.0.4xx). To publish with NativeAOT on Windows, also install the C++ Build Tools.
2. Clone the repository, build and run the tests:

   ```bash
   dotnet build Eunomia.slnx
   dotnet test Eunomia.slnx
   ```

3. Tests against a real server (optional): `powershell -File tests\Integration\Start-TestServer.ps1` on Windows with XAMPP (MariaDB on port 33106, data directory in `%TEMP%\eunomia-it`, never XAMPP's own), or the `EUNOMIA_IT_MYSQL` environment variable with a connection string. Without a server, those tests are reported as skipped.

Everyday commands:

```bash
dotnet build Eunomia.slnx                                       # build (warnings fail the build)
dotnet test Eunomia.slnx                                        # all tests
dotnet test tests/Unit/Engine.Sql.Tests --filter "FullyQualifiedName~Splitter"   # a subset
dotnet test Eunomia.slnx --collect "Code Coverage;Format=cobertura" --results-directory TestResults   # with coverage
python build/coverage.py TestResults                            # coverage per project against build/coverage-baseline.json
dotnet format Eunomia.slnx --verify-no-changes                  # formatting and style (CI runs this)
dotnet format Eunomia.slnx                                      # fix formatting and style
dotnet build Eunomia.slnx -c Release                            # performance measurements only in Release
```

The .NET SDK is pinned in `global.json`; package versions in `Directory.Packages.props` (central package management); shared options in `Directory.Build.props`. In Windows PowerShell 5.1, `Set-Content`/`Get-Content` corrupt UTF-8 files without a BOM.

## 3. Architecture (non-negotiable)

- Layers from section 21 of the spec. `src/Engine.*`, `src/Drivers.*`, `src/App`, `src/Infra.*` and `src/Presentation` do not reference Avalonia, WinForms, WPF or `UI.*` projects (checked in `tests/Architecture`).
- Platform-specific code only in `Infra.*.<Platform>` (Windows, MacOS, Linux) or behind `OperatingSystem.Is*()`. Paths with `Path.Combine`, never a literal `\`.
- The core does not know MySQL: it uses `IDialect` (`Drivers.Abstractions`), the editor language (`ISqlLanguage`, `Engine.Sql`), capabilities and the type system. `MySql*` types only in `Drivers.MySql` and in the MySQL language in `Engine.Sql` (checked by `DialectBoundaryTests`).
- **SQL is never assembled by hand.** Identifiers through the dialect's `QuoteIdentifier`/`QualifiedName`; literals through the dialect (`StringLiteral` is the same under any `sql_mode`); cell text through `DisplayFormatter.FormatRaw`; application DDL through `SafeDdl`; row edits through `RowWriter`. No hand-written quotes, backticks or concatenated values.
- Clean room: do not copy HeidiSQL code (GPL-2.0) or use the Icons8 icons. The required behaviour is in the spec.
- Check every API in the package's documentation or source, at the pinned version, before using a method or option.
- New NuGet packages, new versions and third-party software only with the maintainer's agreement.
- Changing the architecture or diverging from the spec requires an ADR. Minor doubts: take the most conservative option and record it under "Provisional decisions" in `docs/progress.md`.

## 4. Data safety (top priority)

Eunomia is used on production databases. Order of precedence of the principles (spec section 2): data safety > stability > non-blocking UI > familiarity with HeidiSQL > large datasets > memory > performance.

- No operation changes data the user did not ask to change. An UPDATE or DELETE without a unique key is never generated silently; grid writes carry `LIMIT 1` and check the affected rows.
- When in doubt, refuse, ask or fail fast; never write a truncated, converted or guessed value.
- Any change to a write path (`RowWriter`, DML/DDL composers, `SafeDdl`, export/import, read-only sessions, transactions) comes with an integration test against a real server (see `DataSafetyTests`).
- Secrets (passwords, keys, connection strings) never in JSON, logs, error messages, crash reports or URLs. They live in the OS vault (`Infra.Secrets.*`). The SQL log masks `IDENTIFIED BY`/`SET PASSWORD` (`SecretMasking`).
- User files: atomic writes, a `.bak` copy, 0600/0700 permissions on Unix.

## 5. C# coding standards

They follow Microsoft's [C# coding conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions) and the [Framework Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/), plus what is below. `.editorconfig` encodes what can be checked; CI runs `dotnet format --verify-no-changes`. New code follows these rules; old code is aligned when it is touched.

### Structure

- File-scoped namespaces; `using` outside the namespace, `System.*` first; 4 spaces; LF; UTF-8 without BOM; lines up to 150 columns.
- One main type per file, named after the file. Small related types (a result record, a dedicated exception) may share the file.
- Classes `sealed` by default; `record`/`readonly record struct` for immutable data; primary constructors when they only store dependencies.
- Minimal visibility: `internal` before `public`; `InternalsVisibleTo` only for test projects.
- Short methods with a single responsibility. Files over ~600 lines, or `partial` classes that keep growing by area (`MainWindow`), are a signal to extract a type.
- No dead, commented-out or "for later" code. No `TODO` without an entry in `docs/roadmap.md`.

### Naming

- `PascalCase`: types, methods, properties, events, constants and `static readonly` fields. `camelCase`: parameters and locals. `_camelCase`: private fields. Interfaces start with `I`. Asynchronous methods end in `Async`.
- Everything in English, with descriptive names (no obscure abbreviations). Test names are PascalCase sentences that describe the behaviour (`DamagedAppDbIsSetAsideAndTheAppStarts`).

### Types, nullability and validation

- `Nullable` on and `TreatWarningsAsErrors`: zero warnings. Avoid the `!` operator; if it is really needed, the reason must be obvious from the context.
- Validate public arguments with `ArgumentNullException.ThrowIfNull`, `ArgumentOutOfRangeException.ThrowIf*`.
- String comparisons always with an explicit `StringComparison` (`Ordinal`/`OrdinalIgnoreCase` for identifiers). Data is formatted and parsed with `CultureInfo.InvariantCulture`; only what the user sees uses the current culture.
- Collections are exposed as `IReadOnlyList<T>`/`IReadOnlyDictionary<TKey, TValue>`.

### Async, concurrency and UI

- No I/O (network, disk, driver) on the UI thread. No `.Result`, `.Wait()` or `GetAwaiter().GetResult()` in new code.
- `async void` only in UI event handlers, with a `try/catch` that routes the error to the dialog or the log.
- `ConfigureAwait(false)` in every library without UI (`Engine.*`, `Drivers.*`, `App`, `Infra.*`); never in UI code that touches controls afterwards.
- `CancellationToken` as the last parameter of public asynchronous methods, always passed on. Cancelling is not an error: `OperationCanceledException` never reaches the error dialog.
- State shared between threads is protected (`lock`, `Channel<T>`, immutable types). No `Thread.Sleep`; `Task.Delay` only with a reason.
- Every buffer is bounded; streaming by default (spec section 11).

### Errors and resources

- Exceptions for exceptional cases, with dedicated types when the caller has to tell them apart (`RowWriteException`, `RowConflictException`).
- `catch (Exception)` only at boundaries (UI command, background job, cleanup), with the error logged and `#pragma warning disable CA1031 // why`. Never an empty `catch`; never swallow a write error.
- Messages the user sees are in English, as in HeidiSQL, with no secrets or data values.
- `IDisposable`/`IAsyncDisposable` always with `using`/`await using`.

### AOT, trimming and performance

- The executable is NativeAOT: no dynamic reflection, `dynamic` or `Activator.CreateInstance` on unknown types. JSON through `JsonSerializerContext` (source generators); regular expressions through `[GeneratedRegex]`.
- The AOT and trimming analyzers are on: an IL2xxx/IL3xxx warning is an error to fix, not to suppress.
- Hot paths (grid, lexer, splitter, export): `Span<T>`, no per-cell allocations, no LINQ in tight loops. A performance claim needs a number measured in Release (spec section 26).

### UI (Avalonia)

- Same UX as HeidiSQL: command placement, shortcuts, English captions. Commands come from the command registry; items without a handler are not shown.
- Colours and sizes from the theme tokens (`assets/themes`), never loose values; light and dark themes.
- Interactive controls have an accessible name (screen readers): the label next to them through `.Named(label)` (`AccessibleNames`), checked in every window by `AccessibilityTests`.
- Top-level windows use `BrandAssets.WindowIcon` (checked by `BrandTests`).

### Comments and documentation

- Comments and XML docs in English. They explain the why and point to the spec (section, requirement, appendix), not what the code already says.
- Public types and members of libraries have a `/// <summary>`.

## 6. Tests

- xUnit v3. Pyramid: unit tests in `tests/Unit/<Project>.Tests`, integration against a real server in `tests/Integration`, headless UI and snapshots in `tests/UI.Headless`, dependency rules in `tests/Architecture`.
- Every bug fix starts with a failing test. Every new feature has tests for the happy path and the edge cases (NULL, empty, Unicode, maximum values, cancellation).
- New or changed screens and dialogs: a light and a dark headless snapshot, reviewed by eye before accepting the baseline (pixels are compared on Windows only).
- Headless tests: asynchronous ones carry `Timeout = UiTimeouts.Default` (checked by `TestHarnessTests`); questions are answered through the `MainWindow` `*Override` hooks. An unexpected modal dialog is closed and fails the test (`UnexpectedDialogGuard`): an expected dialog is checked with `TakeOpened`, one driven by the test is allowed with `Allow`.
- Tests that need a server call `Assert.Skip` without one; never a false green. Tests are deterministic: no dependency on the clock, order or external network; no `Thread.Sleep` to wait for state.
- Never change a test just to make it pass, and never tweak code just to pass a test.

## 7. Git, commits and pull requests

- One branch per topic (`feat/user-manager`, `fix/grid-conflicts`, `docs/…`); work reaches `main` through a pull request with green CI (Windows, Linux, macOS, integration, quality and coverage).
- One commit per coherent step, with a green build and tests. Messages in English, in the [Conventional Commits](https://www.conventionalcommits.org/) format: `type(scope): imperative summary` (≤ 72 characters), with a body explaining the why when it is not obvious. Types: `feat`, `fix`, `perf`, `refactor`, `test`, `docs`, `build`, `ci`, `chore`. Example: `fix(grid): refuse to save truncated values`.
- Before the PR: `dotnet format Eunomia.slnx --verify-no-changes`, with `CHANGELOG.md` and `docs/progress.md` up to date.
- Small, focused PRs with the template filled in (what, why, how it was tested, risks to data).
- Versions follow [SemVer](https://semver.org/); `CHANGELOG.md` follows [Keep a Changelog](https://keepachangelog.com/) and is updated with every visible change.
- Never: `git push --force` on `main`, `--no-verify`, rewriting published history.

## 8. Code review

Reviewers check, in this order:

1. Data: can it corrupt, truncate or change data the user did not ask for? Is there an integration test on the write path?
2. SQL built through the dialect and the composers (nothing by hand); secrets kept out of logs and errors.
3. Architecture: dependencies between layers, platform code in the right place, no MySQL in the core.
4. UI thread without I/O or synchronous waits; cancellation passed on.
5. Tests that cover the behaviour (not just the line), and reviewed snapshots.
6. Standards from section 5, documentation and `CHANGELOG.md` up to date.

## 9. Documentation

- Documentation, code and UI text in English.
- At the end of each step: `docs/progress.md` with the current step, what is done, the decisions taken and what is left. Visible features: `docs/user-guide.md`. New items for later: `docs/roadmap.md`. Anything that needs people, hardware or certificates: `docs/manual-checks.md`.

External contributions require a CLA until the license is chosen (ADR-16).
