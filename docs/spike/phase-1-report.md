# Phase 1 spike report (ADR-02: UI framework)

Date: 1 Oct 2026. Scope: part A (Avalonia 12.1) of the spike plan. Part B (WinForms) was not done: see "Decision on part B".

This report separates three kinds of statement:

- **Measured**: a number produced by a script in this repository, with the command to repeat it.
- **Observed**: seen in a screenshot or a test, without a number.
- **Inferred**: my own conclusion from the data, which needs confirmation.

## 1. Summary

| 20.2 criterion | Target | Measured value | Result |
| --- | --- | --- | --- |
| Scroll (PR-04) | Frame p95 ≤ 16.7 ms with 1M × 60, at 100% and 150% | Avalonia's default: failed on my runs (pacing p95 59–73 ms, ~33 fps) and passed on the author's run (p95 17.2–17.4 ms, ~59.5 fps). With WGL or Software + a 1 ms timer: p95 16.8–17.0 ms, 61–62 fps, 0–1 dropped frames in 10 s. Grid cost p95 ≤ 2.8 ms in every case | **Passes**; the default is unstable on this machine (section 4.1) |
| Startup (PR-01) | ≤ 1.0 s with NativeAOT | Median 499 ms (2 cores: 512 ms); first run after publishing 3,029 ms | **Passes** (warm); cold still to be checked |
| Memory (PR-16) | ≤ 150 MB working set after 60 s idle | 97 MB (WGL: 125 MB) | **Passes** |
| Text and menus | 3 HeidiSQL users, "the same or better" in 2 of 3, at 100/150/200% | Not measured | **Manual** |
| Accessibility | Narrator and NVDA read cell and header | Not measured; Avalonia 12.1.3 has no UIA Grid/Table patterns | **Manual, with risk** |
| IME | Japanese input in a cell and in the editor without errors | Not measured | **Manual** |
| AvaloniaEdit | Works on Avalonia 12 without a fork | AvaloniaEdit 12.0.0 with Avalonia 12.1.3: rendering, input, undo, highlighting and completion in 5 headless tests | **Passes** (one edge case worked around without a fork) |

Recommendation (section 9): **Avalonia 12**. ADR-02 was **accepted by the author on 1 Oct 2026** without the manual checks (section 11); the checks and repeating the measurements on other machines remain as follow-up.

## 2. Measuring machine

This machine is not the section 10 reference machine. The numbers are indicative.

| Item | Value |
| --- | --- |
| Windows | Windows 10 Pro 22H2, build 19045 |
| CPU | Intel Core i7-6700 3.4 GHz, 4 cores, 8 logical (Hyper-Threading) |
| RAM | 16 GB |
| GPU | Intel HD Graphics 530 **and** NVIDIA GeForce GTX 750 Ti (two active GPUs) |
| Screens | 1920×1080 at 60 Hz (main, on the NVIDIA) and 1680×1050 at 59 Hz (on the Intel) |
| Scale | 100% on both screens |
| .NET | SDK 10.0.401, runtime 10.0.12 |
| Build Tools | VS Build Tools with MSVC 14.51 (NativeAOT works) |

The combination of two GPUs and two screens with different refresh rates matters for PR-04 (section 4.1). It is common on laptops with hybrid graphics.

## 3. What was built

| Project | Contents |
| --- | --- |
| `src/Engine.Results` | A columnar `RowBlock` (typed arrays, NULL and truncation bitmaps, a UTF-8 arena), `RowBlockBuilder`, `IRowSource` with asynchronous range requests, `BlockStore` (LRU with a budget and a pinned range), `SyntheticRowSource` (1,000,000 × 60, deterministic, 9 types, ~8% NULL, TEXT cut at 256), `DisplayFormatter`, `FormattedCellCache`, `IFullValueSource` |
| `src/Engine.Sql` | `TreeFilterExpression` (4.3 + ADR-18), `FilterHistory`, an incremental per-line `MySqlLexer`, `MySqlKeywords`, `CompletionCatalog` |
| `src/UI.Controls.Tree` | A lazy `TreeModel` with the 4.3 rules, `SyntheticTreeProvider` (5 sessions × 20 databases × 100 objects = 10,000), a hand-drawn `DatabaseTreeControl`, `DatabaseTreeView` with both filters and ★ |
| `src/UI.Controls.Grid` | Our own virtualised grid: `GridColumns`, `GridSelection`, `GridEditBuffer`, `GridKeymap`, `GridPalette`, `CellTextCache` + `GlyphRunFactory`, `DataGridControl`, `DataGridAutomationPeer`, `DataGridView` |
| `src/UI.Controls.SqlEditor` | AvaloniaEdit with `MySqlColorizer` (the `Engine.Sql` lexer) and completion with Ctrl+Space or 500 ms after `.` |
| `src/App.Desktop` | A window with HeidiSQL's layout (menus, tree and filters, Data and Query tabs, SQL log, status bar), light and dark themes, NativeAOT, measurement hooks |
| `tests/Architecture` | `Engine.*`/`Drivers.*` without Avalonia, WinForms or WPF (direct or transitive) |
| `tests/Unit/*` | 173 tests (including 49 regex cases from 4.3 and one 4.3 rule per test in the tree) |
| `tests/UI.Headless` | 37 headless tests with Skia: grid keyboard and editing, editor, glyph runs, AvaloniaEdit and 10 snapshots |
| `bench/Scenarios`, `bench/Micro` | Repeatable scripts for PR-01, PR-04 and PR-16; BenchmarkDotNet |

Total: 215 tests, all passing; `dotnet build` without warnings (`TreatWarningsAsErrors`, AOT and trimming analyzers on).

## 4. Results per criterion

Full results: `bench/results/20261001-0204-DESKTOP-CQRRFMG/` (`summary.md` and one JSON per measurement).

### 4.1 Scroll (PR-04)

**How it is measured.** `Eunomia.exe --bench-scroll` (the NativeAOT executable) in a 1280×800 window with the grid in the Data tab. Before measuring, it loads every row the scroll will go through (resident data). There are 10 s in three phases: wheel (3 rows/frame, 40%), page (25 rows/frame, all new, 30%) and horizontal (40 px/frame, 30%). Per frame it measures:

- **pacing**: the interval between consecutive animation callbacks, that is, what the user sees;
- **UI thread**: from the callback to the end of the grid's `Render`;
- **render thread**: the time between two marker composition visuals, one below and one above the content.

Frame = max(UI, render thread). To pass, the frame p95 must be ≤ 16.7 ms and the pacing p95 ≤ 18.3 ms (16.7 ms plus 10% vsync jitter).

**Command.** `powershell -ExecutionPolicy Bypass -File bench\Scenarios\Measure-Scroll.ps1 [-Scale 1.5] [-Cores 2] [-Render wgl|software] [-TimerResolution 1]`

**Measured** (ms; "2c" = 2 physical cores through affinity; scale 1.5 = an approximation of 150%):

| Variant | Pacing p50 | Pacing p95 | UI p95 | Render thread p95 | Dropped | fps | Passes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Default, 100% | 19.2 | 66.2 | 1.6 | 2.2 | 120 | 33.0 | No |
| Default, 1.5 | 19.0 | 59.2 | 1.0 | 1.1 | 117 | 34.3 | No |
| Default, 100%, 2c | 19.0 | 71.3 | 1.6 | 2.1 | 123 | 33.8 | No |
| Default, 1.5, 2c | 18.9 | 60.8 | 0.9 | 1.1 | 117 | 34.3 | No |
| Default + 1 ms timer | 19.1 | 72.8 | 1.8 | 2.2 | 117 | 32.8 | No |
| WGL + 1 ms timer, 100% | 16.1 | 16.8 | 1.5 | 1.9 | 0 | 61.8 | Yes |
| WGL + 1 ms timer, 1.5 | 16.4 | 16.9 | 0.9 | 1.0 | 1 | 61.2 | Yes |
| WGL + 1 ms timer, 100%, 2c | 16.1 | 16.9 | 1.2 | 1.6 | 1 | 61.7 | Yes |
| WGL + 1 ms timer, 1.5, 2c | 16.4 | 16.9 | 0.8 | 1.1 | 0 | 61.4 | Yes |
| Software + 1 ms timer, 100% | 16.1 | 16.8 | 1.6 | 2.8 | 0 | 61.8 | Yes |
| Software + 1 ms timer, 1.5 | 16.2 | 16.9 | 0.9 | 1.7 | 0 | 61.6 | Yes |
| Software + 1 ms timer, 100%, 2c | 16.1 | 16.9 | 1.5 | 2.7 | 1 | 61.7 | Yes |
| Software + 1 ms timer, 1.5, 2c | 16.2 | 17.0 | 0.9 | 1.7 | 1 | 61.6 | Yes |

**Second run (by the author, same machine, 1 Oct 2026 19:12, `Run-All.ps1 -Quick`, 4 s per variant; `bench/results/20261001-1912-DESKTOP-CQRRFMG/`):** the default configuration **passed** in every variant. Pacing p95 of 17.2–17.4 ms, 59.4–59.7 fps and 1–3 dropped frames; UI p95 ≤ 1.6 ms and render thread p95 ≤ 1.9 ms. WGL and Software with a 1 ms timer also passed (p95 16.8–17.7 ms).

**Inferred.** The default's failure is not systematic on this machine. It showed up on some runs and not on others. The cause is not identified. Unverified hypotheses: another application animating or using the GPU at the same time, and the GPU's power state. In any case, the grid never went above ~3 ms per frame.

**Diagnosis (measured, in exploratory runs with the Release build):**

- With the default, the 58–73 ms drops also show up in a control phase without scrolling, in which the grid only redraws with cached text. The base between drops is ~15.5 ms (the Windows timer tick), not vsync's 16.7 ms.
- Software and WGL without `timeBeginPeriod(1)` stay fixed at ~31 ms (2 × 15.6 ms). With 1 ms they stay at 16.1 ms.
- With ANGLE/D3D11, the 1 ms timer does not help: on the NVIDIA there are 58 ms drops, on the Intel (`--adapter Intel`) 31 ms ones. The WinUI and DirectComposition composition modes behave the same; RedirectionSurface stays fixed at 46 ms; LowLatencyDxgiSwapChain has no vsync limit (~400 fps).
- Grid review, as 20.2 requires for scroll failures: the first version created a `TextLayout` (HarfBuzz shaping) per new cell, with UI p95 of 12.5 ms and 19 ms in the page phase. It now has a path with a `GlyphRun` built from the font's cmap and advances, with a fallback to `TextLayout` for complex text. It ended up with UI p95 of ~1.6 ms and 0 shapings on the dataset.

**Inferred.** Our grid has a ~6× margin on the frame budget, even with 2 cores. What fails is Avalonia's render loop with ANGLE and composition on this machine (Windows 10, two GPUs, two screens). It is not known whether it happens with a single GPU or on Windows 11. That is the first thing to check on the reference machine.

**Limitations.**

- Pacing is measured on Avalonia's side (animation callbacks), not by an independent tool. PresentMon was left to the manual checks.
- Scale 1.5 is an approximation: a `LayoutTransformControl` in a window with the same pixels. A real 150% is left to the manual checks. The headless snapshots at 150% (`SetRenderScaling(1.5)`) confirm that drawing is correct at that scale.
- PR-05 (non-resident data) is outside the spike's scope. The placeholder exists (an empty cell until the block arrives), but it was not measured.

### 4.2 Startup (PR-01)

**How it is measured.** `Eunomia.exe --startup-probe f --exit`. The time goes from the process creation time given by the kernel (`Process.StartTime`) to the interactive moment: the first frame with `CompositionBatch.Rendered` and then a job with `Input` priority run on the UI thread. There are 20 runs; the median uses runs 2 to 20 and the first is reported separately. There is no HeidiSQL baseline (phase 0 was not done), so only the absolute 1.0 s limit applies.

**Command.** `powershell -ExecutionPolicy Bypass -File bench\Scenarios\Measure-Startup.ps1 -Runs 20 [-Cores 2] [-Jit] [-AppArgs "--render wgl"]`

**Measured** (ms):

| Variant | First run | Median | p95 | Min | Max | Passes |
| --- | --- | --- | --- | --- | --- | --- |
| NativeAOT | 3,029 | 499 | 843 | 476 | 847 | Yes |
| NativeAOT, 2 cores | 480 | 512 | 821 | 465 | 892 | Yes |
| NativeAOT, WGL | 824 | 483 | 686 | 472 | 790 | Yes |
| Self-contained JIT (for reference) | 5,863 | 2,042 | 2,063 | 2,018 | 2,067 | No |

Breakdown of a typical AOT run (ms since process creation): `Main` 16, Avalonia initialisation (Win32 platform, Skia, ANGLE) up to `App.Initialize` 282, window created 316, window opened 367, interactive 479.

**Observed and inferred.**

- The first run after publishing (3.0 s, almost all of it before `Main`) is Windows reading and scanning a new 22 MB exe. Antivirus and the file cache are my inference; which of the two was not measured.
- The 26.1 "cold start" (after clearing the standby list) was not measured.
- What is measured is the spike's main window, heavier than the product's Session Manager.

### 4.3 Idle memory (PR-16)

**How it is measured.** The AOT executable runs with `--ready-file`: the "localhost" session is expanded and the Data tab shows the 1M × 60 grid. After 60 s idle, the script reads `WorkingSet64` and `PrivateMemorySize64` from outside. There are 3 runs. There is no connection to a server: the "connected session" is the same UI state with synthetic data.

**Command.** `powershell -ExecutionPolicy Bypass -File bench\Scenarios\Measure-IdleMemory.ps1 -Seconds 60 -Runs 3`

**Measured:** working set 97.4 / 97.1 / 98.0 MB (median 97.4 MB); private bytes ~118 MB. With WGL: 124.7 MB of working set and 170.8 MB of private bytes. Total process CPU, startup included, ~0.65 s in 60 s. **Passes** (≤ 150 MB), with ~50 MB to spare with the default and ~25 MB with WGL.

### 4.4 AvaloniaEdit without a fork

**Measured (tests):** `AvaloniaEditCompatibilityTests` and `SqlEditorBehaviorTests`, with 5 headless tests over the published `Avalonia.AvaloniaEdit` 12.0.0 package (which declares `Avalonia >= 12.0.0`) and Avalonia 12.1.3. They cover the template, rendering, input, undo, the colorizer across several lines (an open block comment) and completion with Ctrl+Space and with `.` + 500 ms.

**Edge case found:** `CompletionList.SelectItem` throws a `NullReferenceException` if it is called right after `CompletionWindow.Show()`, because the template is not applied yet. It was worked around without a fork by calling it with `DispatcherPriority.Loaded`. Worth reporting upstream.

**AOT note:** AvaloniaEdit's theme has to be included in compiled XAML (`SqlEditorStyles.axaml`). `new StyleInclude(...)` in code is `RequiresUnreferencedCode`.

### 4.5 Other measurements (not 20.2 criteria)

Command: `dotnet run -c Release --project bench/Micro -- --filter *`. Results in `bench/results/micro/results/`.

| Measurement | Value | Reference |
| --- | --- | --- |
| Table filter `b2b;prd_products\|ecm_*product` over 10,000 nodes (rebuilds the visible ones) | 3.37 ms | PR-11: ≤ 50 ms |
| Table filter `^(?!sys\|tmp)` | 1.35 ms | PR-11 |
| Database + Table filter | 0.61 ms | PR-11 |
| MySQL lexer, 1,000 lines | 367 µs (0.37 µs/line, 0 allocations) | 16.1: "microseconds per line" |
| Building a 1,024 × 60 block | 9.2 ms, 9.1 MB allocated | — |
| Formatting a block column (INT / DECIMAL / TEXT) | 0.10 / 0.16 / 0.41 ms | 15.2 |

## 5. Headless snapshots

There are 10 snapshots in `tests/UI.Headless/Snapshots/`, generated with real Skia (`UseHeadlessDrawing = false`):

- the grid in light and dark at 100% and light at 150%;
- the tree in light, dark, filtered (Database filter `^(?!sys|tmp)` and the Table filter from screenshot 1) and light at 150%;
- the editor in light and dark at 100% and light at 150%.

The comparison tolerates ±24 per channel and up to 0.5% of different pixels. Confirmed that it fails when the baseline is wrong (93.87% different). To rewrite them: `EUNOMIA_UPDATE_SNAPSHOTS=1`, and review the PNGs before committing.

Still to do from 25.1: odiff, Git LFS, fonts packaged with the tests and the 200% matrix. The fonts come from Windows (Tahoma, Segoe UI, Consolas), so the baselines depend on the system.

## 6. What failed or was not measured

1. **PR-04 with Avalonia's default configuration** fails on this machine (section 4.1). It passes only with WGL or Software + `timeBeginPeriod(1)`. The spike application keeps the default, because tuning for this machine would go against the instruction; the choice is left to the ADR.
2. **PR-04 at a real 150%** (Windows scale) was not measured: only the approximation.
3. **Cold PR-01** (after a restart or clearing the standby list) was not measured.
4. **Text and menus, accessibility, IME**: manual (section 8).
5. **Fidelity to HeidiSQL** (25.2) and validating the tree's filter and keys in HeidiSQL: not done, because there is no phase 0. Keys T1–T4 and several visual values are provisional (`docs/spike/progress.md`, "Provisional decisions").
6. **Outside the spike's scope:** Ctrl+V/Ctrl+X, header sorting, row Insert/Delete, editors per type, Session Manager and real drivers.

## 7. Risks found

| Risk | Evidence | Impact | Proposed mitigation |
| --- | --- | --- | --- |
| Pacing of Avalonia's render loop on Windows 10 with ANGLE and composition (at least with two GPUs) | Section 4.1: p95 59–73 ms without any grid work | Scrolling with visible jumps on part of the target hardware (ADR-20 includes Windows 10) | Measure on the reference machine and on a hybrid laptop. If it repeats, use `RenderingMode = [Wgl, Software]` + `timeBeginPeriod(1)` while there is animation, or contribute upstream. No fork needed |
| WGL depends on the quality of the OpenGL drivers on Windows | Inferred (general knowledge; not tested here on AMD) | Rendering failures with some drivers | Fall back to Software (Avalonia accepts a list of modes); tests on Intel/AMD/NVIDIA |
| Software rendering costs CPU in large windows or at 200% | Render thread p95 2.8 ms at 1280×800 (measured); 4K not measured | May no longer fit in 16.7 ms at 4K | Measure at 4K and 200% before choosing Software as the default |
| No UIA Grid/Table patterns in Avalonia 12.1.3 | Only ExpandCollapse, Invoke, RangeValue, Scroll, Selection, SelectionItem, Toggle and Value providers exist | Screen readers may not announce "row X, column Y" or the header | Manual check with Narrator and NVDA. If it fails: our own UIA provider through Win32 interop in the `Infra.Shell.Windows` layer, or an upstream contribution |
| AvaloniaEdit 12.0.0's `CompletionList.SelectItem` before the template | Section 4.4 | Small; worked around | Report upstream |
| The first run of a new exe takes ~3 s | Section 4.2 | First impression after installing or updating | Code signing with a recognised signature (question 3 of 20.3); check cold |
| Snapshots depend on the Windows fonts | Section 5 | False positives between machines and Windows versions | Package fonts with the tests (25.1) |
| `Avalonia.BuildServices` sends build statistics | Confirmed in the assembly's strings | Privacy and compliance | `AVALONIA_TELEMETRY_OPTOUT=1` in the scripts and in CI |

## 8. Manual checks

First run `powershell -ExecutionPolicy Bypass -File bench\Scenarios\Run-All.ps1` on the machine where the tests will be done, to get an up-to-date `artifacts\aot\Eunomia.exe`. To open the application: `artifacts\aot\Eunomia.exe` (Windows' default theme), `artifacts\aot\Eunomia.exe --theme dark`, or the Tools menu.

1. **Text and menus (blind comparison).**
   1. Install HeidiSQL 12.21.
   2. At 100%, 150% and 200% (Settings › System › Display › Scale; sign out if Windows asks), open both applications side by side, maximised on the same screen, with the light theme.
   3. Take captures of the grid, the tree, the editor and the open File and Edit menus. Crop them and label them A and B at random; someone else keeps the key.
   4. Show each pair to 3 HeidiSQL users and ask "which has more readable text and menus: A, B or the same?".
   5. It passes if at least 2 of 3 say "the same or better" for Eunomia, at each scale.
2. **Accessibility, Narrator.**
   1. Open Eunomia and turn on Narrator (Ctrl+Win+Enter).
   2. Click a grid cell and move with the arrow keys.
   3. Confirm that Narrator reads the column name and the value (the spike announces "header, value" and "row N, column M").
   4. Repeat in the tree (node names) and the editor (text).
3. **Accessibility, NVDA.** Install NVDA (nvaccess.org) and repeat item 2. Record what each reader says, word for word.
4. **Japanese IME.**
   1. In Settings › Time & language › Language, add "Japanese" with the Microsoft IME keyboard and switch with Win+Space.
   2. In the grid, F2 on a text cell. Type `nihongo`, Space to convert (日本語) and Enter to confirm. Then Enter to close the edit and ↓ to save.
   3. Confirm that the cell shows 日本語, that the candidate window appears next to the cell and that no character is lost.
   4. Repeat in the Query tab's SQL editor.
5. **PR-04 at a real 150%.** Set the Windows scale to 150% and run `bench\Scenarios\Measure-Scroll.ps1`, `... -Render wgl -TimerResolution 1` and `... -Render software -TimerResolution 1`. Compare with section 4.1.
6. **Independent pacing confirmation.**
   1. Install PresentMon (github.com/GameTechDev/PresentMon).
   2. Run `PresentMon.exe -process_name Eunomia.exe -timed 15 -output_file presentmon.csv` while `Measure-Scroll.ps1` runs.
   3. Compare `MsBetweenPresents` with the report's pacing.
7. **Cold PR-01.**
   1. Restart Windows and wait 2 minutes after logging in.
   2. Run `bench\Scenarios\Measure-Startup.ps1 -Runs 1 -NoPublish`. The value that matters is `firstMs`.
   3. Alternatively, clear the standby list with RAMMap (Sysinternals, "Empty Standby List") before each run.
8. **Reference machine and varied hardware.** Run `Run-All.ps1` on the reference machine (Windows 11, 8 cores, one GPU) and on a laptop with hybrid graphics (Intel + NVIDIA/AMD), with Windows 10 and with Windows 11. The main question is whether the default configuration (ANGLE) meets PR-04 there.
9. **Visual inspection in the real application.** Open the application in both themes and confirm that nothing differs from what the headless snapshots show: type colours, `(NULL)`, the `#` column with the session colour, the header with the key, the modified-cell triangle and the bold path in the tree.

## 9. Recommendation for ADR-02

**Avalonia 12** (without a fork), with WinForms as plan B. The conditional choice in section 29 stands, now with data:

- Our own grid meets the budget with a large margin: UI and render thread ≤ 2.8 ms p95 with 1M × 60, also with 2 cores and at 1.5. The section 15 design (columnar blocks, text prepared per cell, glyph runs) is confirmed.
- NativeAOT works with the analyzers on. Startup is ~0.5 s warm, half the limit.
- Idle memory is ~97 MB, with 50 MB to spare.
- AvaloniaEdit 12.0.0 works without a fork.
- The pacing failure belongs to the rendering platform in this hardware configuration and has a configuration fix (WGL or Software + a 1 ms timer), without a fork. Under the 20.2 rule, it is no reason to switch frameworks: the criterion that would require part B (text, menus or AvaloniaEdit) did not fail on what is measurable. Text and menus are still to be assessed manually.

Before the ADR could be accepted, these were missing (see section 11): (1) manual checks 1 to 4 (text and menus, Narrator/NVDA, IME); (2) repeating PR-04 on the reference machine and on a hybrid laptop to decide the rendering configuration; (3) cold PR-01. If item 1 comes out "worse" for text and menus, or if accessibility cannot be solved, 20.2 requires part B (WinForms) with the same measurements.

### Decision on part B

It was not done. The instruction says to do it only if the failure is in text, menus or AvaloniaEdit. The only measured failure was in scrolling and, once the grid was reviewed, the cause lies in the platform's render loop and is solved by configuration. The text and menu criteria are manual. If they fail, part B becomes mandatory.

## 10. How to run

```powershell
dotnet build Eunomia.slnx
dotnet test Eunomia.slnx
powershell -ExecutionPolicy Bypass -File bench\Scenarios\Run-All.ps1
dotnet run -c Release --project bench/Micro -- --filter *
```

To run the application without AOT: `dotnet run --project src/App.Desktop`. The commands for each measurement are in `bench/README.md`. `dotnet build` restores and compiles the solution; `dotnet test` runs the 215 tests. NativeAOT compiles to machine code with the C++ Build Tools; the scripts take care of that.

## 11. After the report (1 Oct 2026)

- **ADR-02 accepted** by the author without the manual checks. The ones in section 8 no longer block and become follow-up. The accepted risks are in the ADR.
- **The author's requests**, already implemented and tested (step 10 of `docs/spike/progress.md`):
  - double clicking a table in the tree, with the Query tab active, inserts the name into the query at the cursor position (FAM-T07; with Shift, `database.table`). Clicking a table no longer switches to the Data tab when on the Query tab (section 4.2).
  - free scrolling with the middle mouse button in the grid, in any direction, diagonal included. It is not in the spec; proposed as a new requirement of the grid engine (section 15.3).
- **Bug found and fixed:** expanding a tree node while it was still loading returned only the placeholder.
- **Second measurement run**, done by the author: PR-01 AOT median 521 ms; PR-04 with Avalonia's default **passes** (pacing p95 17.3 ms); PR-16 97.6 MB. See section 4.1. The folder `bench/results/20261001-1856-DESKTOP-CQRRFMG/` came from an interrupted run (it only has `machine.json`) and did not go into git.
