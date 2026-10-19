param([string]$Path = 'C:\Program Files\SimucubeTuner\data\game_db.json', [int]$Port = 9877)
# Points Simucube Tuner's "rennsport" game entry at its built-in Forza telemetry plugin.
# Re-runnable: replaces the whole entry whether it is stock or already patched.
$ErrorActionPreference = 'Stop'
$log = Join-Path $PSScriptRoot 'patch-gamedb.log'
try {
    $text = [IO.File]::ReadAllText($Path)
    $m = [regex]::Match($text, '"rennsport"\s*:\s*\{')
    if (-not $m.Success) { throw 'rennsport entry not found' }
    # find the matching closing brace of the entry
    $depth = 0; $end = -1
    for ($i = $m.Index + $m.Length - 1; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') { $depth--; if ($depth -eq 0) { $end = $i; break } }
    }
    if ($end -lt 0) { throw 'unbalanced rennsport entry' }
    $old = $text.Substring($m.Index, $end - $m.Index + 1)

    # back up only the stock file (also after a Tuner update brings a fresh one)
    $bak = "$Path.bak-rennsport"
    if ($old -notmatch '"plugin"') { Copy-Item $Path $bak -Force }

    $entry = @"
"rennsport": {
        "name": "Rennsport",
        "plugin": "forza",
        "plugin_options": {
            "address": "127.0.0.1",
            "port": $Port
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
"@ -replace "`r?`n", "`r`n"
    $new = $text.Substring(0, $m.Index) + $entry.TrimEnd() + $text.Substring($end + 1)
    $null = $new | ConvertFrom-Json
    [IO.File]::WriteAllText($Path, $new, (New-Object Text.UTF8Encoding($false)))
    "OK patched $Path (backup $bak)" | Out-File $log
} catch {
    "ERROR $_" | Out-File $log
}
