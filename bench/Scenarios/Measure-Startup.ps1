<#
.SYNOPSIS
  PR-01: startup until the window is interactive (first frame on screen and the UI thread free for input).

.DESCRIPTION
  Runs the executable N times with --startup-probe and computes the median, p95, minimum and maximum.
  The time is measured from inside the process: from the process creation time (kernel) to the interactive moment.
  The first run is reported separately ("first"), because after publishing Windows reads and scans the new
  exe; a true cold start (after restarting Windows) is a manual check.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File bench\Scenarios\Measure-Startup.ps1 -Runs 20
  powershell -ExecutionPolicy Bypass -File bench\Scenarios\Measure-Startup.ps1 -Runs 20 -Cores 2
  powershell -ExecutionPolicy Bypass -File bench\Scenarios\Measure-Startup.ps1 -Runs 20 -Jit
#>
param(
    [int]$Runs = 20,
    [int]$Cores = 0,
    [switch]$Jit,
    [string]$AppArgs = '',
    [switch]$NoPublish,
    [string]$OutFile
)
. "$PSScriptRoot\Common.ps1"

$exe = Get-ExePath -Jit:$Jit
if (-not $NoPublish -or -not (Test-Path $exe)) { Publish-Eunomia -Jit:$Jit }

$probe = Join-Path $env:TEMP "eunomia-startup-probe.json"
$times = @()
$marks = $null
for ($i = 1; $i -le $Runs; $i++) {
    Remove-Item $probe -ErrorAction SilentlyContinue
    Invoke-Eunomia -Exe $exe -Arguments "--startup-probe `"$probe`" --exit $AppArgs" -Cores $Cores
    $r = Get-Content $probe -Raw | ConvertFrom-Json
    $times += [double]$r.startupMs
    $marks = $r.marksMs
    Write-Host ("  run {0,2}: {1,7:N1} ms" -f $i, $r.startupMs)
}

$rest = $times | Select-Object -Skip 1
$result = [ordered]@{
    metric     = 'PR-01'
    target     = 'interactive <= 1000 ms'
    mode       = $(if ($Jit) { 'JIT (self-contained)' } else { 'NativeAOT' })
    cores      = $(if ($Cores -gt 0) { "$Cores (affinity 0x{0:X})" -f (Get-AffinityMask $Cores) } else { 'all' })
    appArgs    = $AppArgs
    runs       = $Runs
    firstMs    = [math]::Round($times[0], 1)
    medianMs   = Get-Percentile $rest 0.5
    p95Ms      = Get-Percentile $rest 0.95
    minMs      = [math]::Round(($rest | Measure-Object -Minimum).Minimum, 1)
    maxMs      = [math]::Round(($rest | Measure-Object -Maximum).Maximum, 1)
    lastMarksMs = $marks
    pass       = ((Get-Percentile $rest 0.5) -le 1000)
}
$json = $result | ConvertTo-Json -Depth 4
if ($OutFile) { $json | Out-File -Encoding utf8 $OutFile }
$json
