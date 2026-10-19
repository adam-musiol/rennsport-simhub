<#
.SYNOPSIS
  Installs the Rennsport telemetry plugin for SimHub.

.DESCRIPTION
  1. Enables Rennsport's UDP API in the game's BroadcastingSocket.json (a backup is kept).
  2. Copies RennsportPlugin.dll into the SimHub folder (asks for Administrator rights only if needed).
  3. Optional: if Simucube Tuner is installed, points its Rennsport game entry (data\game_db.json) at
     Tuner's built-in Forza telemetry reader, which the plugin feeds - this enables Telemetry FFB.
  4. Starts SimHub.
  Compatible with Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER SimHubPath      SimHub install folder. Detected automatically when omitted.
.PARAMETER SkipGameConfig  Do not touch Rennsport's BroadcastingSocket.json.
.PARAMETER TunerPath       Simucube Tuner install folder. Detected automatically when omitted (no question asked when given).
.PARAMETER SkipSimucube    Do not touch Simucube Tuner.
.PARAMETER NoStart         Do not start SimHub at the end.
#>
param(
    [string]$SimHubPath,
    [switch]$SkipGameConfig,
    [string]$TunerPath,
    [switch]$SkipSimucube,
    [switch]$NoStart,
    [switch]$Elevated   # internal: set when the script re-launched itself as Administrator
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$pluginDll = Join-Path $here 'RennsportPlugin.dll'

function Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Ok($text)   { Write-Host "    OK  $text" -ForegroundColor Green }
function Warn($text) { Write-Host "    !!  $text" -ForegroundColor Yellow }
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

# Rennsport entry for Tuner: its Forza reader on the port the plugin sends to (ForzaBridge.DefaultPort)
$TunerEntry = @'
"rennsport": {
        "name": "Rennsport",
        "plugin": "forza",
        "plugin_options": {
            "address": "127.0.0.1",
            "port": 9877
        },
        "detect": [
            {
                "executable_name": "Rennsport.exe"
            },
            {
                "executable_name": "Rennsport-Win64-Shipping.exe"
            }
        ],
        "properties": {
            "max_rpm_available": true,
            "vehicle_detection_support": false
        }
    }
'@.Trim() -replace "`r?`n", "`r`n"

function Set-JsonProperty($obj, [string]$name, $value) {
    if ($obj.PSObject.Properties[$name]) { $obj.$name = $value }
    else { $obj | Add-Member -NotePropertyName $name -NotePropertyValue $value }
}

Write-Host "Rennsport telemetry for SimHub - installer" -ForegroundColor White

if (-not (Test-Path $pluginDll)) { Fail "RennsportPlugin.dll not found next to this script ($here). Extract the whole zip first." }

# ---------------------------------------------------------------- SimHub
Step "Looking for SimHub"
$SimHubPath = Find-SimHub $SimHubPath
if (-not $SimHubPath) { Fail "SimHub not found. Install SimHub first or pass -SimHubPath 'C:\path\to\SimHub'." }
Ok "SimHub folder: $SimHubPath"

# Feeding stock dashboards/LEDs uses SimHub's "External Sims" feature (SimHub 9.13+)
$pluginsDll = Join-Path $SimHubPath 'SimHub.Plugins.dll'
$hasExternalSims = $false
try { $hasExternalSims = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($pluginsDll)).Contains('ExternalSimManifest') } catch { }
if ($hasExternalSims) { Ok "SimHub supports External Sims" }
else {
    Warn "This SimHub version does not support External Sims (added around SimHub 9.13)."
    Warn "Only the plugin's own RennsportPlugin.* properties would work; stock dashboards/LEDs would stay dark. Update SimHub first."
    if (-not (Ask "Continue anyway?")) { exit 1 }
}

# ---------------------------------------------------------------- Rennsport config
if (-not $SkipGameConfig) {
    Step "Enabling Rennsport UDP API"
    if (Get-Process -Name 'Rennsport' -ErrorAction SilentlyContinue) {
        Warn "Rennsport is running. It may overwrite its config when it exits."
        if (-not (Ask "Close Rennsport yourself now, then press Y to continue (n = skip this step)")) { $SkipGameConfig = $true }
    }
}
if (-not $SkipGameConfig) {
    $cfgPath = Join-Path $env:LOCALAPPDATA 'Rennsport\Saved\Config\BroadcastingSocket.json'
    if (Test-Path $cfgPath) {
        $raw = [IO.File]::ReadAllText($cfgPath)
        try { $cfg = $raw | ConvertFrom-Json } catch { Fail "Cannot parse $cfgPath : $($_.Exception.Message)" }
        $backup = "$cfgPath.bak"
        if (-not (Test-Path $backup)) { Copy-Item $cfgPath $backup; Ok "Backup: $backup" }
    } else {
        New-Item -ItemType Directory -Force (Split-Path $cfgPath) | Out-Null
        $cfg = New-Object PSObject
        Warn "Config not found, creating it (start the game once if this looks wrong): $cfgPath"
    }
    if (-not $cfg.PSObject.Properties['ip']) { Set-JsonProperty $cfg 'ip' '0.0.0.0' }
    if (-not $cfg.PSObject.Properties['port']) { Set-JsonProperty $cfg 'port' 559 }
    if (-not $cfg.PSObject.Properties['liveUpdateSendFrequency']) { Set-JsonProperty $cfg 'liveUpdateSendFrequency' 4 }
    if (-not $cfg.PSObject.Properties['accessPassword']) { Set-JsonProperty $cfg 'accessPassword' '' }
    Set-JsonProperty $cfg 'bAutomaticTelemetry' $true
    Set-JsonProperty $cfg 'bSendOverUDP' $true
    Set-JsonProperty $cfg 'uDPDataFormat' 'json'
    [IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    Ok "UDP API enabled in $cfgPath (port $($cfg.port), JSON format)"
}

# ---------------------------------------------------------------- Simucube Tuner (optional)
$tunerDb = $null
if (-not $SkipSimucube) {
    $tuner = Find-Tuner $TunerPath
    if (-not $tuner) {
        if ($TunerPath) { Warn "Simucube Tuner not found in $TunerPath - Telemetry FFB step skipped." }
    } elseif ($TunerPath -or (Ask "Simucube Tuner found ($tuner). Enable its Telemetry FFB for Rennsport?")) {
        $TunerPath = $tuner
        $tunerDb = Join-Path $tuner 'data\game_db.json'
    }
}

# ---------------------------------------------------------------- copy plugin
Step "Installing the plugin into SimHub"
# only the SimHub running from the target folder matters (Path is null if it cannot be read -> assume it does)
$simhub = @(Get-Process -Name 'SimHubWPF' -ErrorAction SilentlyContinue |
    Where-Object { -not $_.Path -or $_.Path.StartsWith($SimHubPath, [StringComparison]::OrdinalIgnoreCase) })
if ($simhub.Count -gt 0) {
    if (-not (Ask "SimHub is running and must be closed. Close it now?")) { Fail "Close SimHub and run the installer again." }
    foreach ($p in $simhub) { [void]$p.CloseMainWindow() }
    foreach ($p in $simhub) { if (-not $p.WaitForExit(30000)) { Fail "SimHub did not close. Close it manually and run the installer again." } }
    Ok "SimHub closed"
}

# a DLL extracted from a downloaded zip is marked as "from the internet"; .NET may refuse to load it
Unblock-File -Path $pluginDll -ErrorAction SilentlyContinue

$readOnly = @($SimHubPath)
if ($tunerDb) { $readOnly += (Split-Path $tunerDb) }
$readOnly = @($readOnly | Where-Object { -not (Test-Writable $_) })
if ($readOnly.Count -gt 0) {
    if ($Elevated) { Fail "No write access to $($readOnly -join ', ') even as Administrator." }
    Warn "Administrator rights are needed to write to $($readOnly -join ', ') - a UAC prompt will appear."
    $relaunchArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
                      '-SimHubPath', "`"$SimHubPath`"", '-SkipGameConfig', '-Elevated')
    if ($tunerDb) { $relaunchArgs += '-TunerPath', "`"$TunerPath`"" } else { $relaunchArgs += '-SkipSimucube' }
    if ($NoStart) { $relaunchArgs += '-NoStart' }
    try { $proc = Start-Process powershell.exe -Verb RunAs -ArgumentList $relaunchArgs -Wait -PassThru }
    catch { Fail "Administrator rights were refused. Copy RennsportPlugin.dll to $SimHubPath manually." }
    exit $proc.ExitCode
}

Copy-Item $pluginDll (Join-Path $SimHubPath 'RennsportPlugin.dll') -Force
Unblock-File -Path (Join-Path $SimHubPath 'RennsportPlugin.dll') -ErrorAction SilentlyContinue
Ok "Copied RennsportPlugin.dll to $SimHubPath"

# ---------------------------------------------------------------- patch Simucube Tuner
$tunerPatched = $false
if ($tunerDb) {
    Step "Enabling Simucube Tuner Telemetry FFB for Rennsport"
    $text = [IO.File]::ReadAllText($tunerDb)
    $entry = Find-RennsportEntry $text
    if (-not $entry) { Warn "This Tuner version has no Rennsport entry in game_db.json - skipped." }
    else {
        $old = $text.Substring($entry.Start, $entry.Length)
        if ($old -match '"plugin"' -and $old -notmatch '"plugin"\s*:\s*"forza"') {
            Warn "Simucube Tuner already has its own Rennsport telemetry - left unchanged."
        } else {
            # back up only the stock file (a Tuner update brings a fresh stock file -> refresh the backup)
            if ($old -notmatch '"plugin"') { Copy-Item $tunerDb "$tunerDb.bak-rennsport" -Force; Ok "Backup: $tunerDb.bak-rennsport" }
            $new = $text.Substring(0, $entry.Start) + $TunerEntry + $text.Substring($entry.Start + $entry.Length)
            $valid = $true
            try { $null = $new | ConvertFrom-Json } catch { $valid = $false }
            if (-not $valid) { Warn "Patched game_db.json would not be valid JSON - left unchanged." }
            else {
                [IO.File]::WriteAllText($tunerDb, $new, (New-Object Text.UTF8Encoding($false)))
                $tunerPatched = $true
                Ok "Rennsport now uses Tuner's telemetry reader on 127.0.0.1:9877 ($tunerDb)"
                if (Get-Process -Name 'sc-tuner' -ErrorAction SilentlyContinue) {
                    Warn "Simucube Tuner is running: restart it (tray icon -> Exit, then start it again) to apply the change."
                }
            }
        }
    }
}

# ---------------------------------------------------------------- start SimHub
if (-not $NoStart) {
    Step "Starting SimHub"
    Start-Process (Join-Path $SimHubPath 'SimHubWPF.exe') -WorkingDirectory $SimHubPath
    Ok "SimHub started"
}

Write-Host ""
Write-Host "Done. Next steps in SimHub:" -ForegroundColor White
Write-Host "  1. If SimHub says 'New plugins have been detected', enable 'Rennsport telemetry'."
Write-Host "  2. Select the game 'Rennsport (API)' (NOT any custom 'Rennsport' game you may have created)."
Write-Host "  3. Start Rennsport and drive on track - data flows only while you are on track."
if ($tunerPatched) {
    Write-Host "  4. Simucube Tuner detects Rennsport while SimHub is running; turn on Telemetry Force-feedback on its"
    Write-Host "     Wheelbase page. Tuner updates undo this change - run the installer again after updating Tuner."
}
Write-Host "  See README.md for details and troubleshooting."
if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
exit 0
