<#
.SYNOPSIS
  Runs the whole test suite locally and checks line coverage against build/coverage-baseline.json.

.DESCRIPTION
  The tests are kept out of GitHub Actions on purpose, so this is the local gate: build with warnings as errors,
  dotnet format check, every test project with the "Code Coverage" collector (cobertura), then build/coverage.py over
  the results. It fails (exit code 1) on a build warning, a format change, a failing test or a project below its
  coverage threshold.

  Start the integration server first (tests\Integration\Start-TestServer.ps1); without it the integration tests are
  reported as skipped and the coverage of the core projects drops below the baseline.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tests\Integration\Start-TestServer.ps1
  powershell -ExecutionPolicy Bypass -File build\Run-Tests.ps1
  powershell -ExecutionPolicy Bypass -File tests\Integration\Start-TestServer.ps1 -Stop
#>
param(
    [switch]$NoCoverage,
    [switch]$UpdateBaseline
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'Eunomia.slnx'
$results = Join-Path ([IO.Path]::GetTempPath()) 'eunomia-coverage'

function Invoke-Step([string]$name, [scriptblock]$step) {
    Write-Host "== $name" -ForegroundColor Cyan
    & $step
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $name" -ForegroundColor Red
        exit 1
    }
}

Invoke-Step 'Build (warnings as errors)' { dotnet build $solution -warnaserror -nologo -v q }
Invoke-Step 'Format check' { dotnet format $solution --verify-no-changes }

if ($NoCoverage) {
    Invoke-Step 'Tests' { dotnet test $solution --no-build }
    exit 0
}

if (Test-Path $results) { Remove-Item -Recurse -Force $results }
Invoke-Step 'Tests with coverage' {
    dotnet test $solution --no-build --collect 'Code Coverage;Format=cobertura' --results-directory $results
}

$coverageArgs = @((Join-Path $root 'build\coverage.py'), $results, '--baseline', (Join-Path $root 'build\coverage-baseline.json'))
if ($UpdateBaseline) { $coverageArgs += '--update' }
Invoke-Step 'Coverage against the baseline' { python @coverageArgs }
