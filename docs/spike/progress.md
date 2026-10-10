# Phase 1 spike progress

## Current step

**Phase 1 closed on 5 Oct 2026** (spike, ADR-02 accepted, 20 ADRs, `Drivers.Abstractions` contracts, command registry, CI). The last item of the definition of done ("CI green with one test of each kind") can only be confirmed after the first push: the author pushes and CI runs on GitHub. Next: phase 2 (Application Shell), once the author gives the go-ahead.

## Plan

| # | Step | State |
| --- | --- | --- |
| 1 | `progress.md`, `.gitignore`, `global.json`, `Directory.*.props`, solution, empty `Engine.*` projects, architecture test | Done |
| 2 | Confirm Avalonia 12.1.x and a compatible AvaloniaEdit without a fork; create empty UI projects | Done |
| 3 | `Engine.Results`: synthetic 1M × 60 `IRowSource`, deterministic, asynchronous ranges, tests | Done |
| 4 | `Engine.Sql` (MySQL lexer, completion list) and `UI.Controls.Tree` (the 4.3 model and filters, tests per rule) | Done |
| 5 | `UI.Controls.Grid`: selection, inline editing, the 4.4/FAM-G05/G06 keymap, `#` column, colours per type, `(NULL)`, Ctrl+C | Done |
| 6 | `UI.Controls.SqlEditor` and `App.Desktop` (HeidiSQL layout, light and dark themes) | Done |
| 7 | `tests/UI.Headless`: snapshots of the grid, tree and editor | Done |
| 8 | `bench/`: PR-04 scroll (100% and 150%), PR-01 NativeAOT startup, PR-16 memory | Done |
| 9 | `docs/spike/phase-1-report.md` and `docs/adr/ADR-02-ui-framework.md` | Done |
| 10 | Requests from the author (1 Oct 2026): double click in the tree inserts the name into the query; free scrolling with the middle button in the grid | Done |
| 11 | ADR-02 accepted by the author, without the manual checks | Done |
| 12 | Second measurement run (by the author): PR-04 with the default passes; report and ADR updated | Done |
| 13 | Plan for closing phase 1 | Done |
| 14 | ADR-01 and ADR-03 to ADR-20 in `docs/adr/` | Done |
| 15 | `src/Drivers.Abstractions` (14.1–14.4) | Done |
| 16 | `src/App`: command registry (21.2) | Done |
| 17 | CI in `.github/workflows` | Done |
| 18 | Closing phase 1 | Done |

## Done

- Step 1: the `Eunomia.slnx` solution with `src/Engine.Results`, `src/Engine.Sql` and `tests/Architecture`. The architecture test checks, for each `Engine.*`/`Drivers.*` project, the csproj (no `UseWindowsForms`/`UseWPF`) and `project.assets.json` (no Avalonia/UI/App packages or projects, direct or transitive, and no `Microsoft.WindowsDesktop.App`). Confirmed that it fails when `UseWindowsForms` is put in an `Engine.*` project.

- Step 2: Avalonia 12.1.3 and AvaloniaEdit 12.0.0 (the latest published; it declares `Avalonia >= 12.0.0`). A headless test, `AvaloniaEditCompatibilityTests`, with real Skia rendering: the `TextEditor` applies its template, draws the visible lines, accepts text and undoes. Projects `UI.Controls.Grid`, `UI.Controls.Tree`, `UI.Controls.SqlEditor`, `App.Desktop` (NativeAOT) and `tests/UI.Headless`. `dotnet publish -r win-x64` with NativeAOT produces `Eunomia.exe` without AOT/trim warnings.
- Step 3: `Engine.Results` with `ResultSchema`, a columnar `RowBlock` (typed arrays, NULL and truncation bitmaps, a UTF-8 arena), `RowBlockBuilder`, `IRowSource` (non-blocking `TryGetBlock`, `GetBlockAsync`, `RequestRangeAsync`, a `BlocksAvailable` event), `BlockStore` (LRU with a budget and a pinned range), `SyntheticRowSource` (1M × 60, 9 types, ~8% NULL, TEXT cut at 256), `DisplayFormatter` and `FormattedCellCache` (text per block and column, in the background). 25 tests in `tests/Unit/Engine.Results.Tests`.
- Step 4: `Engine.Sql` with `TreeFilterExpression` (4.3 + ADR-18), `FilterHistory` (20), an incremental per-line `MySqlLexer`, `MySqlKeywords` (our own lists, from the MySQL documentation) and `CompletionCatalog`. `UI.Controls.Tree` with `TreeModel` (lazy, placeholder, flattened list, target rules per node type, favourites, sizes), `SyntheticTreeProvider` (5 sessions × 20 databases × 100 objects = 10,000), `NaturalComparer`, `SizeFormatter`, `DatabaseTreeControl` (custom drawing) and `DatabaseTreeView` (filters + ★ + scrollbar). Tests: 75 in `Engine.Sql.Tests` (49 regex cases from 4.3), 36 in `UI.Controls.Tree.Tests` (one rule per test).
- Step 5: `UI.Controls.Grid` with `GridColumns` (prefix sums + binary search), `GridSelection` (ranges + focus + anchor, Ctrl+A/Ctrl+I, status `r:c (n sel)`), `GridEditBuffer` (pending row, saves on row change or Ctrl+Enter, new rows at the end), a declarative `GridKeymap`, `GridPalette` (the 5.6 colours), `CellTextCache` (a TextLayout per cell, LRU per frame), `TextWidthEstimator` (automatic width in the background, max. 300 px), `DataGridControl` (a single visual, row and column virtualisation, a `#` column with the session colour, "no. name" headers with the key, PK in bold, numbers on the right, `(NULL)`, a modified-cell triangle, in-place editing with a `TextBox`, Ctrl+C of the focused cell with the full value, the wheel with Alt/Shift/Ctrl), `DataGridAutomationPeer` and `DataGridView` (scrollbars). `IFullValueSource` in `Engine.Results`. Tests: 35 grid unit tests and 12 headless behaviour tests (keyboard, editing, insertion, Ctrl+C, mouse).
- Step 6: `UI.Controls.SqlEditor` with `MySqlColorizer` (the `Engine.Sql` lexer, per-line state cached, only visible lines) and `SqlEditorControl` (Consolas 9 pt, line numbers, tab 3, completion with Ctrl+Space or 500 ms after `.`, 12 lines × 350 px). `App.Desktop` with `MainWindow` (HeidiSQL's menus, tree + filters, Data and Query tabs, SQL log, status bar, Ctrl+E and Ctrl+D, light/dark themes in the Tools menu or `--theme`), `BenchHooks` (`--startup-probe`, `--ready-file`, `--bench-scroll`, `--scale`) and `StartupTrace`. 4 headless editor tests. First AOT measurement: ~0.5 s warm; 2.7 s on the first run after publishing (1.96 s before `Main`).
- Step 7: 10 headless snapshots with real Skia in `tests/UI.Headless/Snapshots` (grid, tree and editor; light and dark; 100% and 150% with `SetRenderScaling`; the tree with Database and Table filter). Comparison with tolerance (channel ±24, max. 0.5% of pixels); `EUNOMIA_UPDATE_SNAPSHOTS=1` rewrites them. Confirmed that it fails when the baseline is wrong (93.87% of pixels different).
- Step 8: `bench/Scenarios` (`Measure-Startup`, `Measure-Scroll`, `Measure-IdleMemory`, `Run-All`, `Common`), `bench/Micro` (BenchmarkDotNet) and `bench/README.md`. Grid reviewed (the 20.2 rule): a fast path with `GlyphRun` without shaping; the UI thread during scrolling went from p95 12.5 to ~1.6 ms. Results in `bench/results/20261001-0204-DESKTOP-CQRRFMG/summary.md`: PR-01 AOT median 499 ms (2 cores 512 ms; first run after publishing 3,029 ms); PR-04 fails with the default configuration (pacing p95 59–73 ms, ~33 fps) and passes with WGL or Software + a 1 ms timer in every variant (p95 16.8–17.0 ms, grid cost ≤ 2.8 ms); PR-16 97 MB (WGL 125 MB). Micro: Table filter over 10,000 nodes 3.4 ms (PR-11 ≤ 50 ms), lexer 0.37 µs/line, a 1,024 × 60 block in 9.2 ms.
- Step 9: `docs/spike/phase-1-report.md` (each 20.2 criterion with value, target, result and command; risks; 9 manual checks with steps) and `docs/adr/ADR-02-ui-framework.md` (Proposal: Avalonia 12, without a fork; WinForms as plan B). Part B not done: the only measured failure (PR-04 with the default configuration) is in the platform's render loop and is solved by configuration; text and menus are manual.
- Step 10: double clicking an object in the tree with the Query tab active inserts the name at the cursor position (FAM-T07, "Doubleclick inserts node text"; Shift qualifies it with the database; `MySqlIdentifier` quotes with backticks only when needed). Fixed: clicking a table no longer switches to the Data tab when on the Query tab (4.2). Free scrolling with the middle button in the grid (`GridAutoScroll`): speed proportional to the distance from the origin in any direction, a 12 px dead zone, a marker at the origin; press and drag stops on release, press and release stays active until the next click, key or loss of focus. Fixed in `TreeModel`: a second `ExpandAsync` during loading came back with the placeholder. Tests: 12 new unit and 6 new headless tests (241 in total).
- Step 12: the author's run (`bench/results/20261001-1912-DESKTOP-CQRRFMG/`, `-Quick`): PR-01 median 521 ms, PR-04 default p95 17.3 ms (passes), PR-16 97.6 MB. The pacing failure from the first run did not reproduce; cause not identified (hypothesis: another application using the GPU during the earlier runs).
- Step 14: `docs/adr/` with the 20 ADRs (01 and 03–20 transcribed from section 29; spike notes in ADR-12 and ADR-18) and `README.md` with the index.
- Step 15: `src/Drivers.Abstractions` with the 14.1 contracts (`IDbProvider`, `IServerServices`, `IConnectionFactory`, `IDbConnectionHandle`, `IQueryExecutor` with `ResultEvent` events, `IMetadataProvider`, `IDialect`, `IDdlComposer`, `ITypeMapper`, `ICapabilities`, `IErrorClassifier`), the 14.2 object model, the 14.3 capabilities and the 14.4 `LogicalType`. No drivers. 20 tests; the architecture test covers the new project.
- Step 16: `src/App` (assembly `Eunomia.App`) with `KeyChord` (a shortcut without UI types), `CommandDescriptor`, `CommandRegistry` (ordered registry, handlers, `CanExecute`, disabled commands with "Available in version X", redefined shortcuts, resolution by focus context with the context winning over the global one, conflict detection for FAM-K01) and `ICommandBus`. 22 tests; the architecture test now covers `src/App`.
- Step 17: `.github/workflows/ci.yml` (push, PR and manual, `windows-latest`). Job `test`: restore, Release build (warnings break the build), `dotnet test` with TRX; artifacts with the results and, if a snapshot fails, with the actual PNGs. Job `aot`: NativeAOT publish through the scripts' `Publish-Eunomia` function and an artifact with the exe. `.github/workflows/bench.yml` (manual and on Mondays at 03:00 UTC): BenchmarkDotNet; startup and memory with `continue-on-error`. Checked locally: valid YAML; the `test` job's commands pass in Release (287 tests); the AOT step runs with PowerShell 5.1 (there is no PowerShell 7 on this machine; on the runner it runs in `pwsh`). Not run on GitHub: it needs a push.

## Failures and risks found

- Avalonia 12.1.3 has no UIA Grid/Table patterns (`IGridProvider`, `ITableProvider`, `IGridItemProvider`, `ITableItemProvider`): only ExpandCollapse, Invoke, RangeValue, Scroll, Selection, SelectionItem, Toggle and Value providers exist. The grid exposes itself as a `DataGrid` with a `DataItem` child for the focused cell ("header, value") and a polite `LiveSetting`. If Narrator/NVDA do not read cell and header this way, the accessibility criterion fails with Avalonia without extra work (our own UIA through Win32 interop or an upstream contribution).
- AvaloniaEdit 12.0.0: `CompletionList.SelectItem` throws a `NullReferenceException` if it is called right after `CompletionWindow.Show()` (the list template is not applied yet). Worked around without a fork by calling it with `DispatcherPriority.Loaded`. It does not block the "no fork" criterion, but it is an edge case to report upstream.
- `PublishAot=true` sets `RuntimeFeature.IsDynamicCodeSupported = false` in the normal (JIT) build too; to know whether it runs as AOT, use `IsDynamicCodeCompiled`.
- PR-04, first measurements (Release JIT build, 6 s, a 1280×800 window, a 1920×1080 screen at 60 Hz on the GTX 750 Ti; there is a second 1680×1050 screen at 59 Hz on the Intel HD 530): with the default composition (WinUI) the interval between frames has p50 19 ms and p95 58–73 ms **also in the control phase without scrolling**; a base of ~15.5 ms (the Windows timer tick, not vsync) with periodic 58 ms drops. DirectComposition the same; RedirectionSurface fixed at 46 ms; LowLatencyDxgiSwapChain without a vsync limit (~2.4 ms, 400 fps). With ANGLE on the Intel GPU the drops become 30 ms. Software and WGL: a fixed ~31 ms. The grid's cost is smaller: UI thread p50 1.3 ms, p95 11–12 ms (the "page" phase: ~300 new `TextLayout`s per frame); render thread p95 2–4 ms. Provisional conclusion: pacing fails because of Avalonia's composition/timer on this machine, not because of the grid; even so, the 20.2 rule says to review the grid first (next: a fast path with `GlyphRun` without shaping).
- PR-04 with Avalonia's default configuration (ANGLE/D3D11 + WinUI or DirectComposition composition) fails on this machine: periodic 58–73 ms drops even without scrolling. Measured causes: (1) the timer-driven render loop rounds to Windows 10's 15.6 ms tick (Software/WGL without `timeBeginPeriod(1)` stay at 31 ms; with 1 ms they stay at 16.1 ms); (2) the ANGLE + composition path has drops that the 1 ms timer does not solve (a machine with two GPUs and two screens, 59 and 60 Hz). LowLatencyDxgiSwapChain has no vsync limit (400 fps). The application's default configuration was not changed (not tuning for this machine); it is left for the ADR.
- `start /affinity` with 2 cores uses logical processors 0 and 2 (different physical cores with Hyper-Threading).

## Decisions

| Decision | Why |
| --- | --- |
| Phase 0 was not done. PR-01 only uses the absolute limit of 1.0 s; the tree's filter and keys follow the spec, without validation in HeidiSQL (recorded in the report) | The author's instruction (1 Oct 2026) |
| Measurements on this machine: Windows 10 Pro 22H2 (19045), i7-6700 (4C/8T), 16 GB, 1920×1080 and 1680×1050. The report records the Windows version, CPU, RAM and scale | The author's instruction; it is not the section 10 reference machine |
| .NET SDK 10.0.401 pinned in `global.json` (`rollForward: latestFeature`) | Reproducible builds |
| xUnit v3 pinned to 3.2.2 (not 4.x) | `Avalonia.Headless.XUnit` 12.1.3 is compiled against `xunit.v3.extensibility.core` 3.2.2; avoids two major xUnit versions in the solution |
| Tests run with VSTest (`xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk`) | Works with `dotnet test` and with VS Code's Test Explorer without extra configuration |
| `IsAotCompatible` on every project in `src/`; trim, AOT and single-file analyzers on all of them | Section 22 |
| An extra project, `tests/Architecture` (it is in section 22, not in the phase plan) | Checks the "Engine does not reference UI" rule |
| AvaloniaEdit 12.0.0 with Avalonia 12.1.3, without a fork | The 20.2 criterion; the package loads and works (headless test) |
| AvaloniaEdit's theme included in `SqlEditorStyles.axaml` (compiled XAML), not with `new StyleInclude(...)` in code | `StyleInclude` in code has `RequiresUnreferencedCode` and fails with `TreatWarningsAsErrors`; in XAML it is resolved at compile time and is AOT-safe |
| Headless with `UseSkia().UseHarfBuzz()` and `UseHeadlessDrawing = false` | In Avalonia 12, text shaping is the `Avalonia.HarfBuzz` package; snapshots need real pixels |
| NativeAOT: the ILCompiler's `findvcvarsall.bat` fails if `vswhere.exe` is not on the PATH (`vcvarsall.bat` calls it without a path). The `bench/` scripts add `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` to the PATH of their own process | Changes nothing outside the repository |
| `AVALONIA_TELEMETRY_OPTOUT=1` in the scripts | The (transitive) `Avalonia.BuildServices` package sends build statistics; the variable turns it off (confirmed in the assembly's strings) |

## Provisional decisions

Decisions taken without validation (open spec items), one line of justification each:

| Decision | Why |
| --- | --- |
| Blocks of 1,024 rows; source budget 256 MB; formatted text cache 48 MB | The lower limit in 11.1 (smaller blocks = less work per new block during scrolling); 256 MB is the default budget in 11.3; 48 MB is enough for ~15 screens of 20 columns |
| The synthetic source rebuilds evicted blocks from (seed, row, column) | Plays the role of 11.2's keyset refetch without a server; there is no spill in the spike |
| No `IRowView` (filter/sort) | Outside the spike's scope |
| Thousands separator on, with the current culture's separators (screenshot 3 shows `17 174`); dates `yyyy-MM-dd HH:mm:ss` | The same as HeidiSQL shows; tests and snapshots use `InvariantCulture` to be stable |
| DOUBLE with up to 3 decimals (`#,0.###`) | The spec does not fix it; a readable display value |
| Filter history: repeated text moves to the top (exact, case-sensitive comparison) | The spec does not say how duplicates are handled |
| Tree placeholder with the text "Loading…" | Captions in English as in HeidiSQL; the spec only says "loading…" |
| Tree: row background with the session colour on every node of the session; white or black text depending on the luminance; the focused node with the theme's selection colour on top | Screenshot 3 (green session); `selection.tree.onSession` is only observed for green |
| Tree: 16 px indentation, Size column 76 px, simple vector icons of our own per type | Provisional values; there is no icon set yet and Icons8 cannot be used |
| The `+`, `-`, `*` keys always expand/collapse/expand all (they do not go into the incremental search); the search restarts after 1 s without keys | T1–T4 provisional; 1 s is the usual value in Windows trees |
| Grid: Tahoma 8 pt (10.67 px), row height = ceil(size) + 7 = 18 px | `font.grid` and `size.row.grid` from 5.6 (18 px in the screenshots) |
| Grid: unfocused selection = grey #E2E2E2 (light) and #4B5A9A (dark); focused cell #0078D7 (light) and #3D6FD6 (dark) with white text | 5.7 ("grid colour ±29") and the `selection.row` token; #3D6FD6 chosen for contrast |
| Grid: NULL in the dark theme = the category colour mixed 45% with the background (in light, +70 per channel as in the spec) | With −70 the blue NULL was unreadable on the #4B5A62 background; with +70 it looked like normal text |
| Grid: Ctrl+arrows only move the focus; Shift+arrows/PgUp/PgDn/Ctrl+Home/End extend from the anchor; Ctrl+click toggles, Shift+click extends | The normal VirtualTreeView behaviour with multi-selection; the spec only fixes "rows + focused cell" |
| Grid: in the in-place editor, ↑/↓ close the edit and change row; Enter closes; Tab/Shift+Tab move to the next cell and reopen the editor | 4.4 (Tab while editing); ↑/↓ as in VirtualTreeView's editor |
| Grid: incremental search on the focused column, only in resident blocks (up to 8 blocks ahead), restarts after 1 s | The spec only says "incremental search while typing"; walking 1M non-resident rows makes no sense in the spike |
| Grid: no Ctrl+V/Ctrl+X, header sorting, row Insert/Delete or editors per type | Outside the spike's list (only Ctrl+C, selection, inline editing and keymap) |
| Grid: text with more characters than can fit in the column is cut before shaping (width / 2.5 px + 4) | Avoids shaping 256 characters to show 40; the ellipsis still shows |
| Grid: prefetch of 2 screens above and below, redone when the visible area gets within 1 screen of the edge; up to 24 block columns formatted on the spot per frame | 11.3 (2 screens); the per-frame limit avoids long frames when jumping far |
| Editor: our own dark palette (blue #82AAFF, types #E38C8C, functions #9DB5FF, numbers #C792EA, strings #A5DC86, comments #9AA8AF) | 5.6 only says "Dark and Material presets" without values |
| App: FluentTheme with `DensityStyle.Compact`, Segoe UI 12 px in the window, tabs at 12 px | A density close to HeidiSQL's; the default Fluent is much larger |
| App: each tree table opens a synthetic source with seed = FNV-1a of `database.table` | Deterministic between runs (`string.GetHashCode` is not) |
| PR-01 "interactive" = first rendered frame (`CompositionBatch.Rendered`) and then a job with `Input` priority run on the UI thread | No ETW or Session Manager in the spike; it is the moment the UI accepts input |
| `--scale 1.5` = content inside a `LayoutTransformControl` with a 1.5 scale in a window with the same pixels | An approximation of 150% without changing the Windows scale (the same number of pixels per cell and glyphs rasterised at 1.5×); the real 150% measurement is left to the manual checks |
| Snapshots: a tolerance of ±24 per channel and 0.5% of pixels | Skia text can vary between machines and versions of the Windows fonts; real layout or colour differences go well beyond that limit |
| Free scrolling with the middle button: not in the spec, it is the author's request; proposed as a new requirement for the grid (and the other lists that use the same engine, section 15.3) | The author's explicit request on 1 Oct 2026 |
| Free scrolling: 12 px dead zone, 9 px/s per px of distance, max. 6,000 px/s; Windows autoscroll behaviour (a single click stays active, dragging stops on release) | Provisional values; the same behaviour as other Windows applications |
| Double click in the tree: it only inserts with the Query tab active; on another tab it keeps expand/collapse | The spec says it inserts into the query editor; in the spike there is no other text target |
| Identifiers quoted only when needed (reserved words, characters outside `[A-Za-z0-9_$]`, digits only) | The spec says "inserts the quoted name" without saying whether it always quotes; autocomplete "quotes when needed" |
| `Drivers.Abstractions` references `Engine.Results` | 14.1 says the executor emits `RowBlock`, which lives in `Engine.Results`; that project has no dependencies, so there is no cycle and no duplicated type. The section 21 diagram only shows Engine → Drivers.Abstractions; this adds Drivers.Abstractions → Engine.Results (a leaf) |
| `IDbProvider.ForServer(ServerIdentity)` returns the dialect, DDL, types, capabilities and errors | 14.1 says "factory of the rest", and the provider only knows the version after connecting |
| Secrets reach the driver through a `SecretReference` and a resolver function at open time | ADR-10: the driver never keeps the password; the secrets port resolves it |
| `LogicalType.Category`: Boolean → Integer and Json → Text | 14.4 gives two categories for these types; the first one stays until there is a preference |
| Command ids in the `area.action` format (`query.run`); `HeidiSqlAction` only filled in when the spec gives the name (`actExecuteQuery`) | 21.2 gives one example; the remaining HeidiSQL action names are left for phase 2, from the appendix C inventory |
| Shortcut conflict = the same shortcut on commands with a context in common (including both global); context vs global is not a conflict | 21.2: "shortcut conflicts are resolved by focus context" |
| PR-04 (scroll) out of CI | Needs a real screen with vsync; hosted runners do not guarantee it |
| Startup and memory in CI with `continue-on-error` and only in `bench.yml` | The window needs a desktop session on the runner, which is not guaranteed; they serve for trends, not to approve PRs |
| No automatic check of the 5% regression rule (section 10) | Needs a history of results kept between runs; left for when CI is running |
| `FormattedCellCache.TryGet(..., formatIfResident)` formats on the spot if the block is already in memory | Resident data should not show a placeholder (PR-04 is with resident data); the background prefetch is still the normal path |

## Provisional

Values chosen without validation in HeidiSQL (to be reviewed when there is a phase 0):

- Tree keys T1–T4: `+` expands, `-` collapses, `*` expands all, `F2` has no action (the normal behaviour of a Windows tree). Implemented in step 4.
- Themes and fonts (U1–U3): from section 5.6 and the screenshots; where a value is missing, it is recorded here in step 6.

## Left to do

- **Author:** push and look at CI on GitHub (the Actions tab). If it fails, above all in the snapshots (the runner's Windows Server fonts), fix it; the `snapshots-actual` artifact holds the actual PNGs.
- **Non-blocking follow-up** (ADR-02): manual checks of text and menus, Narrator/NVDA and IME; measurements on the reference machine; cold PR-01.
- **Phase 2 (Application Shell, section 28):** to be planned once the author gives the go-ahead.
