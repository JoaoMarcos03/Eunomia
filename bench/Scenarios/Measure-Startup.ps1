<#
.SYNOPSIS
  PR-01: arranque até a janela estar interativa (primeiro frame no ecrã e UI thread livre para input).

.DESCRIPTION
  Corre o executável N vezes com --startup-probe e calcula mediana, p95, mínimo e máximo.
  O tempo é medido de dentro do processo: hora de criação do processo (kernel) até ao momento interativo.
  A primeira corrida é reportada à parte ("first"), porque depois de publicar o Windows lê e analisa o exe
  novo; o arranque a frio verdadeiro (depois de reiniciar o Windows) é uma verificação manual.

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
    Write-Host ("  corrida {0,2}: {1,7:N1} ms" -f $i, $r.startupMs)
}

$rest = $times | Select-Object -Skip 1
$result = [ordered]@{
    metric     = 'PR-01'
    target     = 'interativo <= 1000 ms'
    mode       = $(if ($Jit) { 'JIT (self-contained)' } else { 'NativeAOT' })
    cores      = $(if ($Cores -gt 0) { "$Cores (afinidade 0x{0:X})" -f (Get-AffinityMask $Cores) } else { 'todos' })
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
