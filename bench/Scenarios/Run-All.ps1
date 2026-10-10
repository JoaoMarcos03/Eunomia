<#
.SYNOPSIS
  Runs every spike measurement (PR-01, PR-04, PR-16) and writes the results to bench\results\<date>-<machine>\.

.DESCRIPTION
  1. Publishes the NativeAOT executable (and the self-contained JIT one, only as a reference for PR-01).
  2. PR-01: 20 AOT starts with every core and 20 with 2 cores; 10 AOT starts with WGL; 10 JIT.
  3. PR-04: 10 s of scrolling with Avalonia's default configuration and with the variants
     (WGL or Software, 1 ms timer), at 100% and at scale 1.5, with every core and with 2.
  4. PR-16: working set after 60 s idle, 3 runs.
  Writes machine.json, one JSON per measurement and summary.md.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File bench\Scenarios\Run-All.ps1
#>
param([switch]$Quick)
. "$PSScriptRoot\Common.ps1"

$dir = New-ResultDir
Write-Host "Results in $dir"
Get-MachineInfo | ConvertTo-Json | Out-File -Encoding utf8 (Join-Path $dir 'machine.json')

Write-Host 'Publishing (NativeAOT and JIT)...'
Publish-Eunomia
Publish-Eunomia -Jit

$runs = 20; $seconds = 10; $idle = 60
if ($Quick) { $runs = 5; $seconds = 4; $idle = 15 }

function Run([string]$name, [string]$script, [hashtable]$params) {
    Write-Host "== $name"
    $file = Join-Path $dir "$name.json"
    & (Join-Path $PSScriptRoot $script) @params -NoPublish -OutFile $file | Out-Null
    return (Get-Content $file -Raw | ConvertFrom-Json)
}

$startup = [ordered]@{}
$startup['aot'] = Run 'startup-aot' 'Measure-Startup.ps1' @{ Runs = $runs }
$startup['aot-2cores'] = Run 'startup-aot-2cores' 'Measure-Startup.ps1' @{ Runs = $runs; Cores = 2 }
$startup['aot-wgl'] = Run 'startup-aot-wgl' 'Measure-Startup.ps1' @{ Runs = [math]::Max(5, $runs / 2); AppArgs = '--render wgl' }
$startup['jit'] = Run 'startup-jit' 'Measure-Startup.ps1' @{ Runs = [math]::Max(5, $runs / 2); Jit = $true }

$scroll = [ordered]@{}
foreach ($cores in 0, 2) {
    foreach ($scale in 1.0, 1.5) {
        $suffix = "s$($scale.ToString([Globalization.CultureInfo]::InvariantCulture))" + $(if ($cores) { "-${cores}cores" } else { '' })
        $scroll["default-$suffix"] = Run "scroll-default-$suffix" 'Measure-Scroll.ps1' @{ Scale = $scale; Cores = $cores; Seconds = $seconds }
        $scroll["wgl-timer1-$suffix"] = Run "scroll-wgl-timer1-$suffix" 'Measure-Scroll.ps1' @{ Scale = $scale; Cores = $cores; Seconds = $seconds; Render = 'wgl'; TimerResolution = 1 }
        $scroll["software-timer1-$suffix"] = Run "scroll-software-timer1-$suffix" 'Measure-Scroll.ps1' @{ Scale = $scale; Cores = $cores; Seconds = $seconds; Render = 'software'; TimerResolution = 1 }
    }
}
$scroll['default-timer1-s1'] = Run 'scroll-default-timer1-s1' 'Measure-Scroll.ps1' @{ Seconds = $seconds; TimerResolution = 1 }

$memory = Run 'memory-idle' 'Measure-IdleMemory.ps1' @{ Seconds = $idle; Runs = 3 }
$memoryWgl = Run 'memory-idle-wgl' 'Measure-IdleMemory.ps1' @{ Seconds = $idle; Runs = 1; AppArgs = '--render wgl' }

# Summary in Markdown.
$m = Get-Content (Join-Path $dir 'machine.json') -Raw | ConvertFrom-Json
$lines = @()
$lines += "# Results $(Split-Path $dir -Leaf)"
$lines += ''
$lines += "Machine: $($m.windows); $($m.cpu) ($($m.cores) cores, $($m.logical) logical); $($m.ramGB) GB; GPU $($m.gpu); $($m.resolution); scale $($m.scalePercent)%; .NET SDK $($m.dotnet)."
$lines += ''
$lines += '## PR-01 startup (ms)'
$lines += ''
$lines += '| Variant | First run | Median | p95 | Min | Max | Pass (median <= 1000) |'
$lines += '| --- | --- | --- | --- | --- | --- | --- |'
foreach ($k in $startup.Keys) { $s = $startup[$k]; $lines += "| $k | $($s.firstMs) | $($s.medianMs) | $($s.p95Ms) | $($s.minMs) | $($s.maxMs) | $($s.pass) |" }
$lines += ''
$lines += '## PR-04 scroll (ms)'
$lines += ''
$lines += '| Variant | Pacing p50 | Pacing p95 | UI p95 | Render thread p95 | Frame p95 | Dropped | fps | Pass |'
$lines += '| --- | --- | --- | --- | --- | --- | --- | --- | --- |'
foreach ($k in $scroll.Keys) { $s = $scroll[$k]; $lines += "| $k | $($s.framePacingMs.p50) | $($s.framePacingMs.p95) | $($s.uiThreadMs.p95) | $($s.renderThreadMs.p95) | $($s.frameP95Ms) | $($s.droppedFrames) | $($s.appFps) | $($s.pass) |" }
$lines += ''
$lines += '## PR-16 idle memory'
$lines += ''
$lines += "| Variant | Median working set (MB) | Max (MB) | Pass (<= 150 MB) |"
$lines += '| --- | --- | --- | --- |'
$lines += "| default | $($memory.workingSetMB.median) | $($memory.workingSetMB.max) | $($memory.pass) |"
$lines += "| wgl | $($memoryWgl.workingSetMB.median) | $($memoryWgl.workingSetMB.max) | $($memoryWgl.pass) |"
$lines | Out-File -Encoding utf8 (Join-Path $dir 'summary.md')
Get-Content (Join-Path $dir 'summary.md')
