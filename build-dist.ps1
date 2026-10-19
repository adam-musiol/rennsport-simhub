# Builds the shareable package: dist\RennsportSimHub-<version>\ + dist\RennsportSimHub-<version>.zip
# <version> = <Version> in RennsportPlugin.csproj. Upload the zip as a GitHub release asset.
# Usage: pwsh -File build-dist.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'RennsportPlugin\RennsportPlugin.csproj'

dotnet build $proj -c Release -nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "RennsportSimHub-$version"
$out = Join-Path $root "dist\$name"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

Copy-Item (Join-Path $root 'RennsportPlugin\bin\Release\net48\RennsportPlugin.dll') $out
# README and LICENSE come from the repository root, the scripts from packaging\
$files = @{
    'README.md'     = Join-Path $root 'README.md'
    'LICENSE.txt'   = Join-Path $root 'LICENSE'
    'install.ps1'   = Join-Path $root 'packaging\install.ps1'
    'install.cmd'   = Join-Path $root 'packaging\install.cmd'
    'uninstall.ps1' = Join-Path $root 'packaging\uninstall.ps1'
    'uninstall.cmd' = Join-Path $root 'packaging\uninstall.cmd'
}
foreach ($f in $files.Keys) {
    $text = [IO.File]::ReadAllText($files[$f])
    # Windows line endings for scripts; .ps1 must stay ASCII for Windows PowerShell 5.1
    $text = $text -replace "`r?`n", "`r`n"
    if ($f -like '*.ps1' -and $text -match '[^\x00-\x7F]') { throw "$f contains non-ASCII characters" }
    [IO.File]::WriteAllText((Join-Path $out $f), $text, (New-Object Text.UTF8Encoding($false)))
}

$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Package: $zip"
Get-ChildItem $out | Format-Table Name, Length -AutoSize | Out-Host
