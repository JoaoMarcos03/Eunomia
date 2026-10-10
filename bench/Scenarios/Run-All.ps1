<#
.SYNOPSIS
  Corre todas as medições do spike (PR-01, PR-04, PR-16) e grava os resultados em bench\results\<data>-<máquina>\.

.DESCRIPTION
  1. Publica o executável NativeAOT (e o JIT self-contained, só como indicação para o PR-01).
  2. PR-01: 20 arranques AOT com todos os núcleos e 20 com 2 núcleos; 10 arranques AOT com WGL; 10 JIT.
  3. PR-04: scroll de 10 s com a configuração por omissão do Avalonia e com as variantes
     (WGL ou Software, timer de 1 ms), a 100% e com escala 1,5, com todos os núcleos e com 2.
  4. PR-16: working set após 60 s em repouso, 3 corridas.
  Escreve machine.json, um JSON por medição e summary.md.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File bench\Scenarios\Run-All.ps1
#>
param([switch]$Quick)
. "$PSScriptRoot\Common.ps1"

$dir = New-ResultDir
Write-Host "Resultados em $dir"
Get-MachineInfo | ConvertTo-Json | Out-File -Encoding utf8 (Join-Path $dir 'machine.json')

Write-Host 'A publicar (NativeAOT e JIT)...'
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

# Resumo em Markdown.
$m = Get-Content (Join-Path $dir 'machine.json') -Raw | ConvertFrom-Json
$lines = @()
$lines += "# Resultados $(Split-Path $dir -Leaf)"
$lines += ''
$lines += "Máquina: $($m.windows); $($m.cpu) ($($m.cores) núcleos, $($m.logical) lógicos); $($m.ramGB) GB; GPU $($m.gpu); $($m.resolution); escala $($m.scalePercent)%; .NET SDK $($m.dotnet)."
$lines += ''
$lines += '## PR-01 arranque (ms)'
$lines += ''
$lines += '| Variante | 1.ª corrida | Mediana | p95 | Mín. | Máx. | Passa (mediana <= 1000) |'
$lines += '| --- | --- | --- | --- | --- | --- | --- |'
foreach ($k in $startup.Keys) { $s = $startup[$k]; $lines += "| $k | $($s.firstMs) | $($s.medianMs) | $($s.p95Ms) | $($s.minMs) | $($s.maxMs) | $($s.pass) |" }
$lines += ''
$lines += '## PR-04 scroll (ms)'
$lines += ''
$lines += '| Variante | Pacing p50 | Pacing p95 | UI p95 | Render thread p95 | Frame p95 | Perdidos | fps | Passa |'
$lines += '| --- | --- | --- | --- | --- | --- | --- | --- | --- |'
foreach ($k in $scroll.Keys) { $s = $scroll[$k]; $lines += "| $k | $($s.framePacingMs.p50) | $($s.framePacingMs.p95) | $($s.uiThreadMs.p95) | $($s.renderThreadMs.p95) | $($s.frameP95Ms) | $($s.droppedFrames) | $($s.appFps) | $($s.pass) |" }
$lines += ''
$lines += '## PR-16 memória em repouso'
$lines += ''
$lines += "| Variante | Working set mediana (MB) | Máx. (MB) | Passa (<= 150 MB) |"
$lines += '| --- | --- | --- | --- |'
$lines += "| omissão | $($memory.workingSetMB.median) | $($memory.workingSetMB.max) | $($memory.pass) |"
$lines += "| wgl | $($memoryWgl.workingSetMB.median) | $($memoryWgl.workingSetMB.max) | $($memoryWgl.pass) |"
$lines | Out-File -Encoding utf8 (Join-Path $dir 'summary.md')
Get-Content (Join-Path $dir 'summary.md')
