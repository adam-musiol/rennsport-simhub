# Rennsport telemetry for SimHub

SimHub has no built-in support for **Rennsport**. This plugin reads Rennsport's live telemetry and feeds it into SimHub, so that:

- **stock dashboards, RPM / shift LEDs and ShakeIt** work, through SimHub's normalized `GameData`,
- about 150 extra `RennsportPlugin.*` properties are available for custom dashboards (tyres, brakes, suspension, aero, aids…), and
- optionally, **Simucube Tuner's Telemetry FFB** works with Rennsport (see [Simucube Tuner](#simucube-tuner-telemetry-ffb)).

Tested with **SimHub 9.13.2**, **Simucube Tuner 3.2.0** and the current Rennsport build (October 2026).

**⬇ Download:** [latest release](https://github.com/adam-musiol/rennsport-simhub/releases/latest) (`RennsportSimHub-<version>.zip`), then follow [Quick install](#quick-install-recommended).

---

## How it works

```
Rennsport ──(UDP API, port 559, JSON)──► RennsportPlugin (inside SimHub)
                                              │  RennsportPlugin.* properties
                                              ├─(UDP, 127.0.0.1:30559)──► SimHub game "Rennsport (API)"
                                              │                           → GameData → dashboards / LEDs / ShakeIt
                                              └─(UDP, 127.0.0.1:9877, Forza format)──► Simucube Tuner → Telemetry FFB
```

1. Rennsport has a built-in **UDP API server**, but it is disabled by default. Once enabled, it sends telemetry to every client that registers with it.
2. The plugin registers with the game (and repeats this every 5 s, so restarting the game is fine), then reads physics (400 Hz) and timing / session data.
3. SimHub plugins cannot write `GameData` directly. Instead, the plugin registers the game **"Rennsport (API)"** through SimHub's built-in *External Sims* feature and sends the telemetry to it. SimHub then treats Rennsport like a natively supported game.
4. For Simucube Tuner, the plugin also sends the physics in the Forza "Dash" format, which Tuner can read. See [Simucube Tuner](#simucube-tuner-telemetry-ffb).

All traffic stays on your PC (`127.0.0.1`). Nothing is sent over the network.

---

## Requirements

- Windows 10/11
- Rennsport, started at least once
- **SimHub 9.13 or newer.** Older versions lack *External Sims*: the plugin's own `RennsportPlugin.*` properties still work there, but stock dashboards and LEDs stay dark.

## Package contents

| File | Purpose |
|---|---|
| `RennsportPlugin.dll` | The SimHub plugin |
| `install.cmd` / `install.ps1` | Automatic installation (steps 1–4 below) |
| `uninstall.cmd` / `uninstall.ps1` | Removes everything the plugin added |
| `README.md` | This file |
| `LICENSE.txt` | MIT license |

---

## Quick install (recommended)

1. Download `RennsportSimHub-<version>.zip` from the [latest release](https://github.com/adam-musiol/rennsport-simhub/releases/latest) and extract the whole zip to any folder (for example `Downloads\RennsportSimHub`).
2. **Close Rennsport.**
3. Double-click **`install.cmd`**. The script will:
   - find your SimHub installation,
   - enable Rennsport's UDP API in the game config, keeping a backup as `BroadcastingSocket.json.bak`,
   - close SimHub if it is running (it asks first),
   - copy `RennsportPlugin.dll` into the SimHub folder (you get a UAC prompt if SimHub is in `Program Files`),
   - if Simucube Tuner is installed, ask whether to enable its Telemetry FFB for Rennsport (see [Simucube Tuner](#simucube-tuner-telemetry-ffb)),
   - start SimHub.
4. Continue with **step 5** of the manual installation (enable the plugin and select the game in SimHub).

Script options (PowerShell): `install.ps1 -SimHubPath "D:\SimHub" -SkipGameConfig -TunerPath "D:\SimucubeTuner" -SkipSimucube -NoStart`.

> If Windows SmartScreen warns about the `.cmd` file, choose *More info → Run anyway*, or follow the manual steps.

---

## Manual installation (step by step)

### 1. Close Rennsport and SimHub

Both must be closed. Rennsport can overwrite its config on exit, and SimHub locks plugin DLLs while it runs.

### 2. Enable Rennsport's UDP API

**Option A – in the game:** open *Settings → Gameplay → Telemetry* and switch on **"Send physics telemetry data over UDP"**. The exact wording may differ slightly between game versions and languages.

**Option B – config file:** open `%LOCALAPPDATA%\Rennsport\Saved\Config\BroadcastingSocket.json` (paste this path into the Explorer address bar) and make sure it looks like this:

```json
{
    "ip": "0.0.0.0",
    "port": 559,
    "liveUpdateSendFrequency": 4,
    "accessPassword": "",
    "bAutomaticTelemetry": true,
    "bSendOverUDP": true,
    "uDPDataFormat": "json"
}
```

What matters: `bSendOverUDP` = `true`, `uDPDataFormat` = `"json"` and `bAutomaticTelemetry` = `true`. You can keep `port` and `accessPassword` as they are, because the plugin reads both from this file.

### 3. Copy the plugin

Copy `RennsportPlugin.dll` into the SimHub folder, next to `SimHubWPF.exe`. The default folder is `C:\Program Files (x86)\SimHub`. Windows asks for administrator permission there.

### 4. Unblock the DLL

Files from a downloaded zip are marked as coming from the internet, and SimHub may refuse to load them. Right-click `RennsportPlugin.dll` in the SimHub folder, choose **Properties**, tick **Unblock** if it is shown, and click **OK**.

### 5. Start SimHub and enable the plugin

- If SimHub shows **"New plugins have been detected!"**, enable **"Rennsport telemetry"** and confirm.
- Within a few seconds a new game called **"Rennsport (API)"** appears in SimHub's game list. The plugin creates it on its first start, in `%LOCALAPPDATA%\SimHub\ExternalSims\Definitions\Rennsport\`.

### 6. Select the game "Rennsport (API)"

Choose **Rennsport (API)** as the current game in SimHub.

- If you created a *custom game* called "Rennsport" in SimHub earlier, **don't use it**. It receives no telemetry.
- Optional: with SimHub's automatic game switching enabled, SimHub switches to "Rennsport (API)" by itself when `Rennsport.exe` starts.

### 7. Drive

Start Rennsport and **go on track**. Physics data is sent only while you are driving, not in menus. SimHub shows the game as running, and dashboards and LEDs come alive.

---

## Checking that it works

In SimHub, open *Available properties* (or use any dashboard) and look at:

| Property | Expected |
|---|---|
| `RennsportPlugin.Connected` | `True` while the game is running (with the UDP API enabled) |
| `RennsportPlugin.Driving` | `True` while you are on track |
| `DataCorePlugin.GameRunning` | `True` while driving, with "Rennsport (API)" selected |
| `DataCorePlugin.GameData.Rpms`, `.Gear`, `.SpeedKmh` | live values |

The plugin writes status lines prefixed with `[Rennsport]` to SimHub's log (`<SimHub folder>\Logs\SimHub.txt`), for example:

```
[Rennsport] UDP client on port 50954 -> game API 127.0.0.1:559
[Rennsport] RegisterClient -> 0 Success
[Rennsport] Receiving PhysicsUpdate
[ExternalSimUDPReader] UDP Reader on port 30559 started receiving valid data
```

---

## What is available

### Normalized SimHub data (`GameData`, used by stock dashboards / LEDs / ShakeIt)

Speed, RPM, max RPM, shift RPM, gear, throttle, brake, clutch, brake bias, fuel, water and oil temperature, turbo pressure, ABS and TC (active and level), pit limiter, track name and length, track position, completed laps, position, current / last / best lap time, session type, session time left, tyre pressure / temperature / wear (per wheel), brake temperatures, wheel slip, accelerations, orientation, suspension velocity.

### Plugin properties (`RennsportPlugin.*`)

| Group | Properties |
|---|---|
| Status | `Connected`, `Driving`, `RegisterResponseCode`, `PacketCount`, `PhysicsPacketCount`, `ParseErrorCount` |
| Car | `SpeedKmh`, `SpeedMs`, `Rpm`, `MaxRpm`, `ShiftRpm`, `GearIndex`, `CarLiveGear`, `Throttle`, `Brake`, `Clutch` (0–100), `SteeringAngle`, `SteeringShaftTorque`, `ForceFeedback` |
| Motion | `LatAccel`, `LonAccel`, `VertAccel` (m/s²), `Pitch`, `Roll`, `Yaw` (rad), `PitchRate`, `RollRate`, `YawRate` |
| Lap | `LapNumber`, `LapDistance`, `LapDistancePct`, `LapValid`, `BestLapTime`, `TrackLength`, `Paused`, `UserInControl` |
| Engine | `WaterTemp`, `OilTemp`, `OilPressure`, `TurboRpm`, `BoostPressure` (kPa abs), `Fuel` (l), `FuelPressure`, `AirTemp` |
| Aids | `AbsActive`, `AbsLevel`, `TcActive`, `TcLevel`, `BrakeBias`, `PitLimiter`, `Headlights` |
| Aero | `Drag`, `FrontDownforce`, `RearDownforce` |
| Tyres (`TyreFL.` / `TyreFR.` / `TyreRL.` / `TyreRR.`) | `Pressure`, `ColdPressure` (kPa), `TempSurface`, `TempSurfaceL/M/R`, `TempInner`, `TempCarcass` (°C), `Wear` (1 = new), `BrakeTemp`, `SlipAngle`, `SlipRatio`, `Load`, `SuspensionForce`, `Compression`, `DamperVelocity`, `RideHeight`, `Rpm` |
| Race | `Position`, `CompletedLaps`, `CurrentLapTime`, `LastLapTime`, `CarBestLapTime`, `Flag`, `InPitStatus`, `CarLeft`, `CarRight` |
| Session | `SessionTimeLeft`, `SessionLapsLeft`, `SessionName`, `ContestName`, `Track` |

---

## Simucube Tuner (Telemetry FFB)

Simucube Tuner lists Rennsport but has no telemetry reader for it, so its *Telemetry Force-feedback* effects (road texture, cornering, ABS, RPM, gear change) stay inactive. The workaround:

1. The installer changes the Rennsport entry in `<Tuner folder>\data\game_db.json` (default `C:\Program Files\SimucubeTuner`) to use Tuner's built-in **Forza** telemetry reader on `127.0.0.1:9877`. It also adds detection of `Rennsport.exe` and `Rennsport-Win64-Shipping.exe`. The stock file is backed up as `game_db.json.bak-rennsport`.
2. While SimHub runs, the plugin sends Rennsport's physics to that port in the Forza "Dash" format, at 60 Hz.

After installing:

- **Restart Simucube Tuner** (tray icon → *Exit*, then start it again). Tuner reads `game_db.json` only at startup.
- **Start SimHub before you drive.** Tuner gets data only through the plugin.
- When Rennsport is running, Tuner shows *Simulator: Rennsport* and loads a profile tagged "Rennsport" if you have one. Enable **Telemetry Force-feedback** on the *Wheelbase* page.

> **Tuner updates undo this change.** After updating Simucube Tuner, run `install.cmd` again.

Notes:

- Tuner shows *Car: Not supported*. This is expected, because the Forza format carries no car identity.
- Rennsport does not report kerbs or surface rumble, and the axis directions are not verified yet. If *Cornering* pulls the wrong way or an effect feels wrong, lower or disable it in Tuner.
- To skip this step, run `install.ps1 -SkipSimucube`, or set `FeedSimucube` to `false` (see Settings). `uninstall.cmd` restores Tuner's stock Rennsport entry.

**Manually:** with Tuner closed, open `data\game_db.json` in the Tuner folder as Administrator. Replace `"rennsport": { "name": "Rennsport" }` with the entry that `install.ps1` writes (search for `$TunerEntry` in the script).

---

## Settings

The settings file is `<SimHub folder>\PluginsData\Common\RennsportPlugin.GeneralSettings.json`. SimHub creates it on first run. Edit it only while SimHub is closed.

```json
{"Host":"127.0.0.1","Port":0,"Password":null,"FeedGameData":true,"ExternalSimPort":30559,"FeedSimucube":true,"SimucubePort":9877}
```

| Setting | Meaning |
|---|---|
| `Host` | Address of the PC running Rennsport (normally `127.0.0.1`) |
| `Port` | Game API port; `0` = read from the game's `BroadcastingSocket.json` (default 559) |
| `Password` | Game API password; `null` = read from the game's `BroadcastingSocket.json` |
| `FeedGameData` | `false` = custom `RennsportPlugin.*` properties only, no "Rennsport (API)" game |
| `ExternalSimPort` | Local port used to pass data to the "Rennsport (API)" game; change it only if 30559 is taken |
| `FeedSimucube` | `false` = don't send the Forza-format feed for Simucube Tuner |
| `SimucubePort` | Port of Tuner's reader; must match `port` in Tuner's `game_db.json` entry (the installer writes 9877) |

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| No "Rennsport (API)" game in SimHub | Check that the plugin is enabled (SimHub *Settings → Plugins*) and the DLL is unblocked (step 4). Update SimHub to 9.13+. Restart SimHub. |
| `RennsportPlugin.Connected` stays `False` | The game's UDP API is off. Repeat step 2 with the game closed, start the game and check the in-game telemetry option. |
| `Connected` is `True` but `Driving` is `False` | You are in a menu. Physics is sent only on track. |
| `Driving` is `True` but dashboards / LEDs are dark | Select **"Rennsport (API)"** as the game (not a custom "Rennsport" game). Check the log for `[ExternalSimUDPReader] … started receiving valid data`. |
| Log shows `RegisterClient -> 3` (InvalidPassword) | Set the same `accessPassword` in the game config and the plugin settings, or leave `Password` as `null`. |
| Log shows `External sim bridge disabled` | Your SimHub version has no External Sims support. Update SimHub. |
| Port 30559 is used by something else | Set `ExternalSimPort` to another free port with SimHub closed, then restart SimHub. |
| Simucube Tuner does not show *Simulator: Rennsport* | Restart Tuner after installing. If Tuner was updated, run `install.cmd` again. |
| Tuner shows Rennsport but Telemetry FFB does nothing | SimHub must be running with the plugin enabled, and you must be on track. Enable *Telemetry Force-feedback* on Tuner's Wheelbase page. |
| Shift lights never light up | Max / shift RPM come from the game's timing data (`CarLiveUpdate`, 4 Hz). They appear about a second after you get on track. |

---

## Uninstall

Double-click **`uninstall.cmd`**. It closes SimHub (it asks first), then removes:

- `RennsportPlugin.dll` from the SimHub folder,
- the plugin settings,
- the "Rennsport (API)" game definition,
- the Simucube Tuner change, if the installer made it (the stock Rennsport entry is restored, and you get a UAC prompt).

To also restore the original game config, replace `BroadcastingSocket.json` with `BroadcastingSocket.json.bak` in `%LOCALAPPDATA%\Rennsport\Saved\Config\`. Leaving the UDP API enabled is harmless.

**Manually:** close SimHub, then delete `RennsportPlugin.dll` from the SimHub folder and the folder `%LOCALAPPDATA%\SimHub\ExternalSims\Definitions\Rennsport`.

---

## Known limitations

- **Motion / ShakeIt axis directions** (surge, sway, heave, pitch, roll, yaw) are passed as the game reports them and have **not been verified**. Check the direction of your motion rig at low intensity first.
- **Reverse gear** is assumed to be a negative gear index (shown as `R`); neutral is shown as `N`.
- Oil pressure is not passed to `GameData` because its unit is unknown. It is available as `RennsportPlugin.OilPressure`.
- Flags (`currentFlag`) are often empty in the game data.
- Opponents / leaderboard are not passed to SimHub yet.

---

## For developers

| Folder | Content |
|---|---|
| `RennsportPlugin/` | The SimHub plugin (C#, .NET Framework 4.8) |
| `packaging/` | Installer and uninstaller scripts shipped in the zip |
| `tools/RennsportUdpSniffer/` | `rsudp`: records the game's API and replays it as a fake game ([README](tools/RennsportUdpSniffer/README.md)) |
| `tools/TunerForzaTest/` | Fake `Rennsport.exe` that feeds Simucube Tuner without the game, plus `patch-gamedb.ps1` |
| `docs/` | [Protocol notes](docs/PROTOCOL.md) and anonymized [sample messages](docs/samples/) |

**Build:** you need the .NET 8+ SDK and SimHub installed. The plugin references SimHub's DLLs from `SIMHUB_INSTALL_PATH`, or from `C:\Program Files (x86)\SimHub\` if that variable is not set.

```
dotnet build RennsportPlugin -c Release                          # build the plugin
dotnet build RennsportPlugin -c Release -p:CopyToSimHub=true     # build and copy the DLL into SimHub (close SimHub first)
pwsh -File build-dist.ps1                                        # dist/RennsportSimHub-<version>.zip
```

**Test without the game:**

1. Record a session once with `rsudp api --out api1`.
2. Run `rsudp replay --in api1/api.bin --port 559 --loop`, with the game closed, and start SimHub.
3. For Simucube Tuner, also run `tools/TunerForzaTest`, either with `--idle` (Tuner detects a running game while the plugin sends data) or with `--in api1/api.bin` (it sends the Forza packets itself).

Plugin log lines start with `[Rennsport]` in `<SimHub>\Logs\SimHub.txt`. Properties can be read without the UI, for example `curl http://localhost:8888/api/getproperty/RennsportPlugin.SpeedKmh`.

---

## License

[MIT](LICENSE) © 2026 Adam Musiol. Not affiliated with Rennsport, SimHub or Simucube.
