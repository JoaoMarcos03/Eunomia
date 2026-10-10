<#
.SYNOPSIS
  PR-04: 10 s of programmatic scrolling in the grid with 1M rows x 60 resident columns.

.DESCRIPTION
  Runs the executable with --bench-scroll in a 1280x800 DIP window and records the frame times:
  interval between frames (pacing), cost on the UI thread and drawing cost on the render thread.
  -Scale 1.5 approximates 150% without changing the Windows scale: the content is drawn at scale 1.5
  (the same pixels per cell as at 150%). The measurement at a real 150% is a manual check.
  Pass (PR-04): frame p95 <= 16.7 ms, with frame = max(UI thread, render thread), and pacing p95
  <= 18.3 ms (16.7 ms + 10% vsync jitter).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File bench\Scenarios\Measure-Scroll.ps1
  powershell -ExecutionPolicy Bypass -File bench\Scenarios\Measure-Scroll.ps1 -Scale 1.5
  powershell -ExecutionPolicy Bypass -File bench\Scenarios\Measure-Scroll.ps1 -Cores 2
  powershell -ExecutionPolicy Bypass -File bench\Scenarios\Measure-Scroll.ps1 -Render wgl -TimerResolution 1
#>
param(
    [double]$Scale = 1.0,
    [int]$Cores = 0,
    [double]$Seconds = 10,
    [ValidateSet('default', 'angle', 'wgl', 'vulkan', 'software')]
    [string]$Render = 'default',
    [switch]$NoMarkers,
    [int]$TimerResolution = 0,
    [string]$AppArgs = '',
    [string]$Exe,
    [switch]$NoPublish,
    [string]$OutFile
)
. "$PSScriptRoot\Common.ps1"

if ($Exe) { $exe = $Exe } else {
    $exe = Get-ExePath
    if (-not $NoPublish -or -not (Test-Path $exe)) { Publish-Eunomia }
}

$out = Join-Path $env:TEMP "eunomia-scroll.json"
Remove-Item $out -ErrorAction SilentlyContinue
$inv = [Globalization.CultureInfo]::InvariantCulture
$scaleText = $Scale.ToString($inv)
$secondsText = $Seconds.ToString($inv)
$extra = ''
if ($Render -ne 'default') { $extra += " --render $Render" }
if ($NoMarkers) { $extra += ' --no-markers' }
if ($TimerResolution -gt 0) { $extra += " --timer-resolution $TimerResolution" }
if ($AppArgs) { $extra += " $AppArgs" }
Invoke-Eunomia -Exe $exe -Arguments "--bench-scroll `"$out`" --seconds $secondsText --scale $scaleText$extra" -Cores $Cores -TimeoutSeconds ([int]$Seconds + 90)
$r = Get-Content $out -Raw | ConvertFrom-Json

$frameP95 = [math]::Max([double]$r.uiMs.p95, [double]$r.renderThreadMs.p95)
$result = [ordered]@{
    metric          = 'PR-04'
    target          = 'frame p95 <= 16.7 ms (max UI/render thread) and pacing p95 <= 18.3 ms'
    scale           = $Scale
    cores           = $(if ($Cores -gt 0) { "$Cores (affinity 0x{0:X})" -f (Get-AffinityMask $Cores) } else { 'all' })
    render          = $Render
    timerResolution = $(if ($TimerResolution -gt 0) { "$TimerResolution ms" } else { 'Windows default' })
    refreshHz       = $r.refreshHz
    appFps          = $r.appFramesPerSecond
    adapter         = $r.adapter
    nativeAot       = $r.nativeAot
    frames          = $r.frames
    idlePacingMs    = $r.idleIntervalMs
    framePacingMs   = $r.intervalMs
    uiThreadMs      = $r.uiMs
    gridRenderMs    = $r.gridRenderMs
    renderThreadMs  = $r.renderThreadMs
    droppedFrames   = $r.droppedFrames
    textLayouts     = $r.textLayoutsCreated
    gc              = "gen0 $($r.gc0), gen1 $($r.gc1), gen2 $($r.gc2), $($r.allocatedMB) MB allocated"
    viewportRows    = $r.viewportRows
    phases          = $r.phases
    frameP95Ms      = [math]::Round($frameP95, 2)
    pass            = (($frameP95 -le 16.7) -and ([double]$r.intervalMs.p95 -le 18.3))
}
$json = $result | ConvertTo-Json -Depth 6
if ($OutFile) { $json | Out-File -Encoding utf8 $OutFile }
$json
