# ADR-02: UI framework

- **Status:** Accepted (1 Oct 2026)
- **Date:** proposed and accepted on 1 Oct 2026
- **Decided by:** the user (product owner). Accepted with the spike data, without doing the manual checks of text and menus, accessibility and IME (see "Accepted risks")
- **Affected requirements:** PR-01, PR-02, PR-04, PR-05, PR-16; FAM-G01 to FAM-G16, FAM-T01 to FAM-T09, FAM-Q01 to FAM-Q11; sections 15, 16, 19, 20.2 and 25 of the spec
- **Related:** ADR-01 (C#/.NET 10), ADR-03 (our own grid), ADR-12 (AvaloniaEdit), ADR-20 (Windows 10 22H2 and 11)

## Context

Section 19 recommended Avalonia 12 with our own grid and tree, and WinForms as plan B, subject to the phase 1 spike (section 20.2). The spike implemented part A: a lazy tree with 10,000 nodes and the 4.3 filters, our own grid with 1M × 60, the AvaloniaEdit editor and the window with HeidiSQL's layout. It measured the automatic criteria with repeatable scripts. The details are in `docs/spike/phase-1-report.md` and the data in `bench/results/20261001-0204-DESKTOP-CQRRFMG/`.

The measuring machine is not the reference one: Windows 10 22H2, an i7-6700 with 4 cores and 8 logical processors, 16 GB, two GPUs (Intel HD 530 and GTX 750 Ti) and two screens (59 Hz and 60 Hz). The numbers are indicative.

## Options

1. **Avalonia 12.1** with our own grid, tree and editor on top of AvaloniaEdit.
2. **WinForms (.NET 10)** with our own grid on top of Win32 controls (plan B, Windows only, no official NativeAOT).
3. The other options in section 19 (Qt, WPF, WinUI, web) are outside this spike.

## Spike data (part A)

| 20.2 criterion | Result |
| --- | --- |
| Scroll (PR-04) | On a second run (by the user), the default **passed** (pacing p95 17.2–17.4 ms, ~59.5 fps). On the first, with Avalonia's default configuration, it **failed**: pacing p95 59–73 ms, ~33 fps, also in a control phase without scrolling. With `Win32RenderingMode.Wgl` or `Software` + `timeBeginPeriod(1)` it **passes** at 100%, at 1.5 (an approximation of 150%) and with 2 cores: pacing p95 16.8–17.0 ms, 0–1 dropped frames in 10 s. Our grid's cost stays at ≤ 2.8 ms p95 in every case |
| Startup (PR-01, NativeAOT) | **Passes**: median 499 ms (2 cores: 512 ms). The first run after publishing took 3.0 s; cold start was not measured |
| Memory (PR-16) | **Passes**: 97 MB working set after 60 s (with WGL: 125 MB) |
| AvaloniaEdit without a fork | **Passes**: AvaloniaEdit 12.0.0 with Avalonia 12.1.3, with one edge case worked around (`SelectItem` before the template) |
| Text and menus | To be assessed (manual) |
| Accessibility | To be assessed (manual). Risk: Avalonia 12.1.3 has no UIA Grid/Table patterns |
| IME | To be assessed (manual) |

Part B (WinForms) was not done. Under the 20.2 rule, failures in the grid or in scrolling first require reviewing our grid, and that was done: with the `GlyphRun` path, the UI thread went from 12.5 to ~1.6 ms p95. The remaining cause lies in the platform's render loop and is solved by configuration. Only a failure in text, menus or AvaloniaEdit would lead to part B.

## Recommendation

**Avalonia 12**, without a fork, with WinForms as plan B. Reasons:

- Our own grid on Avalonia has a ~6× margin on the frame budget. This confirms the design in section 15.
- NativeAOT works with the AOT and trimming analyzers enabled and gives ~0.5 s startup. WinForms has no official NativeAOT (section 19).
- Memory stays well within PR-16.
- Headless snapshots with Skia work (section 25.1); with WinForms there would only be E2E tests.
- The pacing failure has a solution inside Avalonia through public options (`Win32PlatformOptions.RenderingMode`) and `timeBeginPeriod`.

Provisional rendering configuration proposal, to be confirmed in item 2 of "Follow-up": `RenderingMode = [Wgl, Software]` and `timeBeginPeriod(1)` while there is animation or scrolling. If on the reference machine and on a hybrid-graphics laptop the default (ANGLE) meets PR-04, the default stays.

## Consequences

- **Positive:** a single UI code base, with a path to Linux and macOS (section 21.8); themes through tokens; headless visual tests in CI; NativeAOT.
- **Negative and risks:**
  - pacing depends on the hardware and on the rendering configuration on Windows 10;
  - WGL depends on the quality of the OpenGL drivers;
  - Software rendering costs CPU at 4K or at 200% (not measured);
  - grid accessibility without UIA Grid/Table patterns: it may need our own UIA provider through Win32 interop (the `Infra.Shell.Windows` layer) or an upstream contribution;
  - Skia text in greyscale, against HeidiSQL's ClearType, still to be assessed.
- **If text and menus fail:** WinForms now and Avalonia once there is cross-platform work (20.2). Do part B with the same measurements. The `Engine.*` projects serve both, as the architecture test guarantees.

## Decision

**Avalonia 12.1, without a fork, with WinForms as plan B.** Avalonia's default rendering configuration stays: it passed on the second run, and the first run's failure did not reproduce. The rendering configuration on Windows (keep the ANGLE default or move to `[Wgl, Software]` + `timeBeginPeriod(1)`) is not part of this decision. It is decided once there are measurements on the reference machine and on a hybrid-graphics laptop (item 2 below), without reopening the framework choice.

## Accepted risks

The manual checks in 20.2 were not done. The user accepted the decision without them, so they remain as risks with follow-up, not as a condition:

- Skia text and menus (greyscale) against HeidiSQL's ClearType: no blind comparison.
- Narrator and NVDA on the grid, without the UIA Grid/Table patterns in Avalonia 12.1.3: not checked.
- Japanese IME in the grid's in-place editor and in AvaloniaEdit: not checked.

If any of these fails later and cannot be solved inside Avalonia (our own UIA provider, text adjustments), 20.2 still applies: WinForms with the same `Engine.*`.

## Follow-up (does not block the decision)

1. The report's manual checks (section 8): blind comparison of text and menus with 3 HeidiSQL users at 100/150/200%; Narrator and NVDA reading a cell and a header; Japanese IME in a cell and in the editor.
2. Run `bench\Scenarios\Run-All.ps1` on the reference machine (Windows 11, 8 cores, one GPU) and on a laptop with hybrid graphics, with Windows 10 and 11, plus PR-04 at a real 150%. Decide the rendering configuration. Confirm the pacing with PresentMon.
3. PR-01 cold (after a restart or clearing the standby list).
4. If accessibility fails: estimate the cost of our own UIA provider for the grid and the tree before accepting.
5. Answer questions 2 and 3 of section 20.3 (team experience; budget for code signing), which also weigh on this decision.
