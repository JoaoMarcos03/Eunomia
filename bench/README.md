# Spike benchmarks (phase 1)

Repeatable measurements of the section 20.2 criteria that can be measured automatically. They run on Windows,
in PowerShell 5.1 or 7, from the repository root. The results go into `bench/results/` (only the BenchmarkDotNet
artifacts are kept out of git; each run's JSON files and `summary.md` are versioned).

## Prerequisites

- .NET 10 SDK (`global.json`).
- NativeAOT: Visual Studio Build Tools with "Desktop development with C++". The scripts add
  `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` to the PATH of their own process, because the
  ILCompiler's `findvcvarsall.bat` calls `vswhere.exe` without a path.
- Do not touch the mouse or put another window on top during the scroll: the benchmark window has to be visible.

## Everything at once

```powershell
powershell -ExecutionPolicy Bypass -File bench\Scenarios\Run-All.ps1
```

Publishes the NativeAOT executable (`artifacts\aot\Eunomia.exe`) and the self-contained JIT one (`artifacts\jit`),
runs PR-01, PR-04 and PR-16 with their variants and writes `bench\results\<date>-<machine>\summary.md`.
It takes ~10 minutes. `-Quick` runs a short version to check that everything works.

## One criterion at a time

| Criterion | Command | What it measures |
| --- | --- | --- |
| PR-01 startup | `bench\Scenarios\Measure-Startup.ps1 -Runs 20` | From process creation (kernel time) to the first frame on screen with the UI thread free for input. Median of runs 2..N; the first is reported separately |
| PR-01 with 2 cores | `bench\Scenarios\Measure-Startup.ps1 -Runs 20 -Cores 2` | The same, with the process limited to 2 physical cores from the start (`start /affinity`) |
| PR-04 scroll | `bench\Scenarios\Measure-Scroll.ps1` | 10 s of programmatic scrolling (wheel, page, horizontal) over 1M × 60 resident rows. Pacing (interval between frames), cost on the UI thread and on the render thread |
| PR-04 at 150% (approx.) | `bench\Scenarios\Measure-Scroll.ps1 -Scale 1.5` | Content drawn at a 1.5 scale (the same pixels per cell as 150%), without changing the Windows scale |
| PR-04 variants | `... -Render wgl -TimerResolution 1`, `... -Render software -TimerResolution 1`, `... -Cores 2` | Win32 rendering mode and the Windows timer resolution |
| PR-16 memory | `bench\Scenarios\Measure-IdleMemory.ps1 -Seconds 60` | Working set read from outside the process after 60 s idle with a session open and the 1M × 60 grid |
| Micro (PR-11, lexer, blocks) | `dotnet run -c Release --project bench/Micro -- --filter *` | BenchmarkDotNet: Table filter over 10,000 nodes, the lexer per line, building and formatting blocks |

The scripts accept `-NoPublish` to reuse the already published executable and `-OutFile` to save the JSON.

## Executable options used by the scripts

`Eunomia.exe` accepts these options (for measuring only; the default configuration is Avalonia's):

| Option | Effect |
| --- | --- |
| `--startup-probe <file> --exit` | Saves the startup times and the per-phase marks as JSON and exits |
| `--ready-file <file>` | Writes the file when the window becomes interactive |
| `--bench-scroll <file> [--seconds N] [--scale F]` | Runs the scroll and saves the per-frame statistics |
| `--render angle\|wgl\|vulkan\|software` | `Win32PlatformOptions.RenderingMode` |
| `--composition winui\|dcomp\|dxgi\|redirection` | `Win32PlatformOptions.CompositionMode` |
| `--adapter <text>` | The GPU used by ANGLE/D3D11 (by its description) |
| `--timer-resolution <ms>` | `timeBeginPeriod` for this process |
| `--theme light\|dark` | Theme |
| `--no-markers` | Turns off the composition markers that measure the render thread |

## Limitations

- This machine is not the reference machine from section 10: the values are indicative.
- Pacing is measured through Avalonia's animation callbacks (one per UI frame). Independent confirmation
  with PresentMon is left to the manual checks.
- A true "cold start" (after a restart or clearing the standby list) is manual.
