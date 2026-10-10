<#
.SYNOPSIS
  Pacote portable do Eunomia (Fase 12; secção 22: build/ com packaging, signing e instalador).
.DESCRIPTION
  Publica o executável NativeAOT win-x64 (mesma função dos benchmarks e do CI), junta o guia do utilizador e faz um
  ZIP em artifacts\Eunomia-<versão>-win-x64.zip. Com -Portable, o ZIP leva um portable_settings.json com {}, para os
  dados ficarem ao lado do executável. Não assina (os certificados não estão no repo) nem cria instalador: ver
  docs/verificacoes-pendentes.md (D1 e D2).

  powershell -File build\Publish-Portable.ps1
  powershell -File build\Publish-Portable.ps1 -Portable
#>
param(
    [switch]$Portable
)

. (Join-Path $PSScriptRoot '..\bench\Scenarios\Common.ps1')

Publish-Eunomia
$aot = Join-Path $RepoRoot 'artifacts\aot'
$props = [xml](Get-Content (Join-Path $RepoRoot 'Directory.Build.props') -Raw)
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$name = "Eunomia-$version-win-x64"
$stage = Join-Path $RepoRoot "artifacts\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

# Só o executável e as bibliotecas nativas (sem .pdb).
Get-ChildItem $aot -File | Where-Object { $_.Extension -in '.exe', '.dll' } | Copy-Item -Destination $stage
Copy-Item (Join-Path $RepoRoot 'docs\utilizador\guia.md') (Join-Path $stage 'LEIA-ME.md')
if ($Portable) {
    Set-Content -Path (Join-Path $stage 'portable_settings.json') -Value '{}' -Encoding UTF8
}

$zip = Join-Path $RepoRoot "artifacts\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Get-Item $zip | Select-Object Name, Length
