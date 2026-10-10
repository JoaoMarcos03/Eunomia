<#
.SYNOPSIS
  PR-04: scroll programático de 10 s no grid com 1M linhas x 60 colunas residentes.

.DESCRIPTION
  Corre o executável com --bench-scroll numa janela de 1280x800 DIPs e grava os tempos de frame:
  intervalo entre frames (pacing), custo na UI thread e custo de desenho no render thread.
  -Scale 1.5 aproxima 150% sem mudar a escala do Windows: o conteúdo é desenhado com escala 1,5
  (mesmos pixels por célula que a 150%). A medição a 150% real é uma verificação manual.
  Aprovação (PR-04): frame p95 <= 16,7 ms, com frame = max(UI thread, render thread), e pacing p95
  <= 18,3 ms (16,7 ms + 10% de jitter do vsync).

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
    target          = 'frame p95 <= 16.7 ms (max UI/render thread) e pacing p95 <= 18.3 ms'
    scale           = $Scale
    cores           = $(if ($Cores -gt 0) { "$Cores (afinidade 0x{0:X})" -f (Get-AffinityMask $Cores) } else { 'todos' })
    render          = $Render
    timerResolution = $(if ($TimerResolution -gt 0) { "$TimerResolution ms" } else { 'omissao do Windows' })
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
    gc              = "gen0 $($r.gc0), gen1 $($r.gc1), gen2 $($r.gc2), $($r.allocatedMB) MB alocados"
    viewportRows    = $r.viewportRows
    phases          = $r.phases
    frameP95Ms      = [math]::Round($frameP95, 2)
    pass            = (($frameP95 -le 16.7) -and ([double]$r.intervalMs.p95 -le 18.3))
}
$json = $result | ConvertTo-Json -Depth 6
if ($OutFile) { $json | Out-File -Encoding utf8 $OutFile }
$json
