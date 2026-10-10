<#
.SYNOPSIS
  Arranca um MariaDB/MySQL local, numa instância própria, para os testes de integração (tests/Integration).

.DESCRIPTION
  Usa um mysqld.exe já instalado (variável EUNOMIA_MYSQLD, ou XAMPP em E:\xampp ou C:\xampp, ou o PATH).
  Cria um datadir novo em %TEMP%\eunomia-it\<nome> (nunca toca no datadir do servidor instalado), arranca na
  porta 33106 só em 127.0.0.1, com root sem password, e escreve a connection string para os testes em
  %TEMP%\eunomia-it\connection.txt. Os testes leem EUNOMIA_IT_MYSQL ou esse ficheiro; sem nenhum, ficam
  marcados como "skipped".

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
    throw 'mysqld.exe não encontrado: definir EUNOMIA_MYSQLD.'
}

if ($Stop -or $Reset) {
    if (Test-Path $pidFile) {
        $id = [int](Get-Content $pidFile)
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($p) { $p.Kill(); $p.WaitForExit(10000) | Out-Null }
        Remove-Item $pidFile -Force
    }
    if ($Stop) { Write-Host 'Servidor de teste parado.'; return }
}

$mysqld = Find-Mysqld
$bin = Split-Path $mysqld
New-Item -ItemType Directory -Force -Path $root | Out-Null
if ($Reset -and (Test-Path $datadir)) { Remove-Item -Recurse -Force $datadir }

if (Test-Path $pidFile) {
    $p = Get-Process -Id ([int](Get-Content $pidFile)) -ErrorAction SilentlyContinue
    if ($p) { Write-Host "Servidor de teste já a correr (pid $($p.Id), porta $Port)."; return }
}

if (-not (Test-Path (Join-Path $datadir 'mysql'))) {
    $install = Join-Path $bin 'mysql_install_db.exe'
    & $install "--datadir=$datadir" "--port=$Port" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "mysql_install_db falhou ($LASTEXITCODE)" }
}

$args = @("--datadir=$datadir", "--port=$Port", '--bind-address=127.0.0.1', '--skip-name-resolve',
          '--character-set-server=utf8mb4', '--collation-server=utf8mb4_general_ci', '--max-connections=200',
          "--log-error=$root\$Name.err")
$process = Start-Process -FilePath $mysqld -ArgumentList $args -WindowStyle Hidden -PassThru
Set-Content -Path $pidFile -Value $process.Id

# Espera que a porta aceite ligações.
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
Write-Host "Servidor de teste em 127.0.0.1:$Port (pid $($process.Id)). Connection string em $connFile"
