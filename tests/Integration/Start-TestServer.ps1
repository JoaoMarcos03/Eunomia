<#
.SYNOPSIS
  Starts a local MariaDB/MySQL, in an instance of its own, for the integration tests (tests/Integration).

.DESCRIPTION
  Uses an mysqld.exe that is already installed (EUNOMIA_MYSQLD variable, or XAMPP in E:\xampp or C:\xampp, or the
  PATH). Creates a new datadir in %TEMP%\eunomia-it\<name> (it never touches the installed server's datadir),
  starts on port 33106 on 127.0.0.1 only, with root without a password, and writes the connection string for the
  tests to %TEMP%\eunomia-it\connection.txt. The tests read EUNOMIA_IT_MYSQL or that file; with neither, they are
  reported as skipped.

.EXAMPLE
  powershell -File tests\Integration\Start-TestServer.ps1
  dotnet test tests\Integration
  powershell -File tests\Integration\Start-TestServer.ps1 -Stop
#>
param(
    [int]$Port = 33106,
    [string]$Name = 'mariadb',
    [switch]$Stop,
    [switch]$Reset
)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP 'eunomia-it'
$datadir = Join-Path $root $Name
$pidFile = Join-Path $root "$Name.pid"
$connFile = Join-Path $root 'connection.txt'

function Find-Mysqld {
    if ($env:EUNOMIA_MYSQLD -and (Test-Path $env:EUNOMIA_MYSQLD)) { return $env:EUNOMIA_MYSQLD }
    foreach ($candidate in @('E:\xampp\mysql\bin\mysqld.exe', 'C:\xampp\mysql\bin\mysqld.exe')) {
        if (Test-Path $candidate) { return $candidate }
    }
    $cmd = Get-Command mysqld.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'mysqld.exe not found: set EUNOMIA_MYSQLD.'
}

if ($Stop -or $Reset) {
    if (Test-Path $pidFile) {
        $id = [int](Get-Content $pidFile)
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($p) { $p.Kill(); $p.WaitForExit(10000) | Out-Null }
        Remove-Item $pidFile -Force
    }
    if ($Stop) { Write-Host 'Test server stopped.'; return }
}

$mysqld = Find-Mysqld
$bin = Split-Path $mysqld
New-Item -ItemType Directory -Force -Path $root | Out-Null
if ($Reset -and (Test-Path $datadir)) { Remove-Item -Recurse -Force $datadir }

if (Test-Path $pidFile) {
    $p = Get-Process -Id ([int](Get-Content $pidFile)) -ErrorAction SilentlyContinue
    if ($p) { Write-Host "Test server already running (pid $($p.Id), port $Port)."; return }
}

if (-not (Test-Path (Join-Path $datadir 'mysql'))) {
    $install = Join-Path $bin 'mysql_install_db.exe'
    & $install "--datadir=$datadir" "--port=$Port" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "mysql_install_db failed ($LASTEXITCODE)" }
}

$args = @("--datadir=$datadir", "--port=$Port", '--bind-address=127.0.0.1', '--skip-name-resolve',
          '--character-set-server=utf8mb4', '--collation-server=utf8mb4_general_ci', '--max-connections=200',
          "--log-error=$root\$Name.err")
$process = Start-Process -FilePath $mysqld -ArgumentList $args -WindowStyle Hidden -PassThru
Set-Content -Path $pidFile -Value $process.Id

# Waits for the port to accept connections.
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline) {
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $client.Connect('127.0.0.1', $Port)
        $client.Close()
        break
    } catch { Start-Sleep -Milliseconds 300 }
}
$conn = "Server=127.0.0.1;Port=$Port;User ID=root;Password=;"
Set-Content -Path $connFile -Value $conn
Write-Host "Test server on 127.0.0.1:$Port (pid $($process.Id)). Connection string in $connFile"
