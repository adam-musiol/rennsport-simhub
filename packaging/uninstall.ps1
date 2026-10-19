<#
.SYNOPSIS
  Removes the Rennsport telemetry plugin from SimHub.

.DESCRIPTION
  Deletes RennsportPlugin.dll from the SimHub folder, the plugin settings and the
  "Rennsport (API)" game definition, and restores Simucube Tuner's stock Rennsport entry if the
  installer changed it. Rennsport's own config is left as is
  (a backup made by the installer is at %LOCALAPPDATA%\Rennsport\Saved\Config\BroadcastingSocket.json.bak).

.PARAMETER SimHubPath    SimHub install folder. Detected automatically when omitted.
.PARAMETER KeepSettings  Keep the plugin settings file (PluginsData\Common\RennsportPlugin.GeneralSettings.json).
.PARAMETER TunerPath     Simucube Tuner install folder. Detected automatically when omitted.
#>
param(
    [string]$SimHubPath,
    [switch]$KeepSettings,
    [string]$TunerPath,
    [switch]$Elevated   # internal: set when the script re-launched itself as Administrator
)

$ErrorActionPreference = 'Stop'

function Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Ok($text)   { Write-Host "    OK  $text" -ForegroundColor Green }
function Fail($text) {
    Write-Host "    ERROR  $text" -ForegroundColor Red
    if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
    exit 1
}
function Ask($question) {
    $a = Read-Host "    $question [Y/n]"
    return ($a -eq '' -or $a -match '^[yY]')
}

function Find-SimHub([string]$hint) {
    $candidates = @()
    if ($hint) { $candidates += $hint }
    if ($env:SIMHUB_INSTALL_PATH) { $candidates += $env:SIMHUB_INSTALL_PATH }
    $keys = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    foreach ($k in $keys) {
        Get-ItemProperty $k -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'SimHub*' -and $_.InstallLocation } |
            ForEach-Object { $candidates += $_.InstallLocation }
    }
    $candidates += "${env:ProgramFiles(x86)}\SimHub", "$env:ProgramFiles\SimHub"
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c 'SimHubWPF.exe'))) { return (Resolve-Path $c).Path }
    }
    return $null
}

function Test-Writable([string]$folder) {
    $probe = Join-Path $folder ('.rennsport_write_test_' + [guid]::NewGuid().ToString('N'))
    try { [IO.File]::WriteAllText($probe, 'x'); Remove-Item $probe -Force; return $true } catch { return $false }
}

function Find-Tuner([string]$hint) {
    # an explicit path is the only candidate
    if ($hint) { if (Test-Path (Join-Path $hint 'data\game_db.json')) { return (Resolve-Path $hint).Path } else { return $null } }
    $candidates = @()
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    foreach ($k in $keys) {
        Get-ItemProperty $k -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'Simucube Tuner*' -and $_.InstallLocation } |
            ForEach-Object { $candidates += $_.InstallLocation }
    }
    $candidates += "$env:ProgramFiles\SimucubeTuner"
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c 'data\game_db.json'))) { return (Resolve-Path $c).Path }
    }
    return $null
}

# Finds the "rennsport": {...} object in the text of Tuner's game_db.json -> @{Start; Length} or $null
function Find-RennsportEntry([string]$text) {
    $m = [regex]::Match($text, '"rennsport"\s*:\s*\{')
    if (-not $m.Success) { return $null }
    $depth = 0
    for ($i = $m.Index + $m.Length - 1; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') { $depth--; if ($depth -eq 0) { return @{ Start = $m.Index; Length = $i - $m.Index + 1 } } }
    }
    return $null
}

# Tuner's stock entry, formatted like the shipped file
$StockTunerEntry = "`"rennsport`": {`r`n        `"name`": `"Rennsport`"`r`n    }"

Write-Host "Rennsport telemetry for SimHub - uninstaller" -ForegroundColor White

Step "Looking for SimHub"
$SimHubPath = Find-SimHub $SimHubPath
if (-not $SimHubPath) { Fail "SimHub not found. Pass -SimHubPath 'C:\path\to\SimHub'." }
Ok "SimHub folder: $SimHubPath"

# only the SimHub running from the target folder matters (Path is null if it cannot be read -> assume it does)
$simhub = @(Get-Process -Name 'SimHubWPF' -ErrorAction SilentlyContinue |
    Where-Object { -not $_.Path -or $_.Path.StartsWith($SimHubPath, [StringComparison]::OrdinalIgnoreCase) })
if ($simhub.Count -gt 0) {
    if (-not (Ask "SimHub is running and must be closed. Close it now?")) { Fail "Close SimHub and run the uninstaller again." }
    foreach ($p in $simhub) { [void]$p.CloseMainWindow() }
    foreach ($p in $simhub) { if (-not $p.WaitForExit(30000)) { Fail "SimHub did not close. Close it manually and try again." } }
    Ok "SimHub closed"
}

# Simucube Tuner: only touch game_db.json if it holds the entry written by the installer (Forza reader)
$tunerDb = $null
$tuner = Find-Tuner $TunerPath
if ($tuner) {
    $db = Join-Path $tuner 'data\game_db.json'
    $text = [IO.File]::ReadAllText($db)
    $entry = Find-RennsportEntry $text
    if ($entry -and $text.Substring($entry.Start, $entry.Length) -match '"plugin"\s*:\s*"forza"') { $tunerDb = $db; $TunerPath = $tuner }
}

$readOnly = @($SimHubPath)
if ($tunerDb) { $readOnly += (Split-Path $tunerDb) }
$readOnly = @($readOnly | Where-Object { -not (Test-Writable $_) })
if ($readOnly.Count -gt 0) {
    if ($Elevated) { Fail "No write access to $($readOnly -join ', ') even as Administrator." }
    Write-Host "    Administrator rights are needed to modify $($readOnly -join ', ') - a UAC prompt will appear." -ForegroundColor Yellow
    $relaunchArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
                      '-SimHubPath', "`"$SimHubPath`"", '-Elevated')
    if ($KeepSettings) { $relaunchArgs += '-KeepSettings' }
    if ($tunerDb) { $relaunchArgs += '-TunerPath', "`"$TunerPath`"" }
    try { $proc = Start-Process powershell.exe -Verb RunAs -ArgumentList $relaunchArgs -Wait -PassThru }
    catch { Fail "Administrator rights were refused." }
    exit $proc.ExitCode
}

Step "Removing files"
$targets = @(
    (Join-Path $SimHubPath 'RennsportPlugin.dll'),
    (Join-Path $SimHubPath 'RennsportPlugin.pdb'),
    (Join-Path $env:LOCALAPPDATA 'SimHub\ExternalSims\Definitions\Rennsport')
)
if (-not $KeepSettings) { $targets += (Join-Path $SimHubPath 'PluginsData\Common\RennsportPlugin.GeneralSettings.json') }
foreach ($t in $targets) {
    if (Test-Path $t) { Remove-Item $t -Recurse -Force; Ok "Removed $t" }
}

if ($tunerDb) {
    Step "Restoring Simucube Tuner's Rennsport entry"
    $text = [IO.File]::ReadAllText($tunerDb)
    $entry = Find-RennsportEntry $text
    $new = $text.Substring(0, $entry.Start) + $StockTunerEntry + $text.Substring($entry.Start + $entry.Length)
    [IO.File]::WriteAllText($tunerDb, $new, (New-Object Text.UTF8Encoding($false)))
    Ok "Restored stock Rennsport entry in $tunerDb"
    if (Get-Process -Name 'sc-tuner' -ErrorAction SilentlyContinue) {
        Write-Host "    !!  Simucube Tuner is running: restart it to apply the change." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Done. In SimHub, switch to another game if 'Rennsport (API)' was selected." -ForegroundColor White
if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
exit 0
