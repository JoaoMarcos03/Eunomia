<#
.SYNOPSIS
  Eunomia portable package (phase 12; spec section 22: build/ with packaging, signing and installer).
.DESCRIPTION
  Publishes the win-x64 NativeAOT executable (the same function as the benchmarks and CI), adds the user guide and
  makes a ZIP in artifacts\Eunomia-<version>-win-x64.zip. With -Portable, the ZIP carries a portable_settings.json
  with {}, so the data stays next to the executable. It does not sign (the certificates are not in the repo) or
  create an installer: see docs/manual-checks.md (D1 and D2).

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

# Only the executable and the native libraries (no .pdb).
Get-ChildItem $aot -File | Where-Object { $_.Extension -in '.exe', '.dll' } | Copy-Item -Destination $stage
Copy-Item (Join-Path $RepoRoot 'docs\user-guide.md') (Join-Path $stage 'README.md')
if ($Portable) {
    Set-Content -Path (Join-Path $stage 'portable_settings.json') -Value '{}' -Encoding UTF8
}

$zip = Join-Path $RepoRoot "artifacts\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Get-Item $zip | Select-Object Name, Length
