# Shared functions of the benchmark scenarios (spec section 26). Windows PowerShell 5.1.
# Usage: . "$PSScriptRoot\Common.ps1"

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Get-ExePath([switch]$Jit) {
    if ($Jit) { return Join-Path $RepoRoot 'artifacts\jit\Eunomia.exe' }
    return Join-Path $RepoRoot 'artifacts\aot\Eunomia.exe'
}

# Publishes the executable. AOT: NativeAOT win-x64 (needs the C++ Build Tools).
# The ILCompiler's findvcvarsall.bat calls vswhere.exe without a path; the Visual Studio installer folder is added
# to this process's PATH (nothing changes outside the repo).
function Publish-Eunomia([switch]$Jit) {
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if ((Test-Path $installer) -and ($env:PATH -notlike "*$installer*")) { $env:PATH = "$installer;$env:PATH" }
    $project = Join-Path $RepoRoot 'src\App.Desktop\App.Desktop.csproj'
    if ($Jit) {
        $out = Join-Path $RepoRoot 'artifacts\jit'
        & dotnet publish $project -c Release -r win-x64 --self-contained -p:PublishAot=false -o $out | Out-Host
    } else {
        $out = Join-Path $RepoRoot 'artifacts\aot'
        & dotnet publish $project -c Release -r win-x64 -o $out | Out-Host
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}

# Affinity mask for N physical cores: with Hyper-Threading it uses one logical processor per physical core
# (0, 2, 4...), so the two "cores" are not on the same physical core.
function Get-AffinityMask([int]$Cores) {
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $step = 1
    if ($cpu.NumberOfLogicalProcessors -gt $cpu.NumberOfCores) { $step = 2 }
    $mask = 0
    for ($i = 0; $i -lt $Cores; $i++) { $mask = $mask -bor (1 -shl ($i * $step)) }
    return $mask
}

# Runs the exe and waits for it to finish. With -Cores > 0, the process starts with the affinity already set (start /affinity).
function Invoke-Eunomia([string]$Exe, [string]$Arguments, [int]$Cores = 0, [int]$TimeoutSeconds = 120) {
    if ($Cores -gt 0) {
        $mask = '{0:X}' -f (Get-AffinityMask $Cores)
        $p = Start-Process -FilePath 'cmd.exe' -ArgumentList "/c start `"`" /affinity $mask /wait `"$Exe`" $Arguments" -PassThru -WindowStyle Hidden
    } else {
        $p = Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru
    }
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        Get-Process -Name 'Eunomia' -ErrorAction SilentlyContinue | Stop-Process -Force
        throw "Eunomia did not finish within $TimeoutSeconds s"
    }
}

function Get-Percentile([double[]]$Values, [double]$P) {
    $sorted = $Values | Sort-Object
    if ($sorted.Count -eq 0) { return $null }
    $rank = $P * ($sorted.Count - 1)
    $lo = [math]::Floor($rank); $hi = [math]::Ceiling($rank)
    return [math]::Round($sorted[$lo] + ($sorted[$hi] - $sorted[$lo]) * ($rank - $lo), 1)
}

# Windows, CPU, RAM, GPU and display scale of this machine (for the report).
function Get-MachineInfo {
    $os = Get-CimInstance Win32_OperatingSystem
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $gpus = @(Get-CimInstance Win32_VideoController | Where-Object { $_.CurrentHorizontalResolution })
    $dpi = (Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name AppliedDPI -ErrorAction SilentlyContinue).AppliedDPI
    if (-not $dpi) { $dpi = 96 }
    [ordered]@{
        windows      = "$($os.Caption) $($os.Version) (build $($os.BuildNumber))"
        cpu          = $cpu.Name.Trim()
        cores        = $cpu.NumberOfCores
        logical      = $cpu.NumberOfLogicalProcessors
        ramGB        = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
        gpu          = ($gpus | ForEach-Object { $_.Name }) -join ' + '
        resolution   = ($gpus | ForEach-Object { "$($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution) @ $($_.CurrentRefreshRate) Hz" }) -join ' + '
        scalePercent = [math]::Round($dpi / 96 * 100)
        dotnet       = (& dotnet --version)
    }
}

function New-ResultDir {
    $dir = Join-Path $RepoRoot ("bench\results\" + (Get-Date -Format 'yyyyMMdd-HHmm') + "-$env:COMPUTERNAME")
    New-Item -ItemType Directory -Force $dir | Out-Null
    return $dir
}
