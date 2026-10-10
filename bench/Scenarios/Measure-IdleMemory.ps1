<#
.SYNOPSIS
  PR-16: idle memory (working set after 60 s idle) with a session open.

.DESCRIPTION
  Starts the NativeAOT executable with --ready-file, waits for the window to be interactive ("localhost"
  session expanded, Data tab with the 1M x 60 grid at the top), leaves it idle and reads the working set
  and the private bytes from outside. The spike does not connect to a server: the "connected session" is
  the equivalent UI state with synthetic data.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File bench\Scenarios\Measure-IdleMemory.ps1
  powershell -ExecutionPolicy Bypass -File bench\Scenarios\Measure-IdleMemory.ps1 -Seconds 60 -Runs 3
#>
param(
    [int]$Seconds = 60,
    [int]$Runs = 3,
    [string]$AppArgs = '',
    [switch]$NoPublish,
    [string]$OutFile
)
. "$PSScriptRoot\Common.ps1"

$exe = Get-ExePath
if (-not $NoPublish -or -not (Test-Path $exe)) { Publish-Eunomia }

$samples = @()
for ($i = 1; $i -le $Runs; $i++) {
    $ready = Join-Path $env:TEMP "eunomia-ready.txt"
    Remove-Item $ready -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $exe -ArgumentList "--ready-file `"$ready`" $AppArgs" -PassThru
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $ready)) {
        if ((Get-Date) -gt $deadline) { $p | Stop-Process -Force; throw 'The window was not ready within 60 s' }
        Start-Sleep -Milliseconds 100
    }
    Start-Sleep -Seconds $Seconds
    $p.Refresh()
    $sample = [ordered]@{
        workingSetMB   = [math]::Round($p.WorkingSet64 / 1MB, 1)
        privateMB      = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        peakWorkingSetMB = [math]::Round($p.PeakWorkingSet64 / 1MB, 1)
        threads        = $p.Threads.Count
        cpuSeconds     = [math]::Round($p.TotalProcessorTime.TotalSeconds, 2)
    }
    $samples += [pscustomobject]$sample
    $p.CloseMainWindow() | Out-Null
    if (-not $p.WaitForExit(10000)) { $p | Stop-Process -Force }
    Write-Host ("  run {0}: working set {1} MB, private {2} MB" -f $i, $sample.workingSetMB, $sample.privateMB)
}

$ws = $samples | ForEach-Object { $_.workingSetMB }
$result = [ordered]@{
    metric        = 'PR-16'
    target        = 'working set <= 150 MB after 60 s idle'
    appArgs       = $AppArgs
    idleSeconds   = $Seconds
    runs          = $Runs
    workingSetMB  = [ordered]@{ median = (Get-Percentile $ws 0.5); max = ($ws | Measure-Object -Maximum).Maximum }
    samples       = $samples
    pass          = ((Get-Percentile $ws 0.5) -le 150)
}
$json = $result | ConvertTo-Json -Depth 4
if ($OutFile) { $json | Out-File -Encoding utf8 $OutFile }
$json
