# Rennsport UDP API – what we know

Rennsport does not document its telemetry interface. Everything below comes from recordings of the real game (Monza, October 2026), made with [`rsudp api`](../tools/RennsportUdpSniffer/README.md). The [samples](samples/) folder has one full message of each type, with the user and car IDs zeroed out.

## Enabling the API

Rennsport does **not** push UDP by itself. It runs a UDP **API server** that sends telemetry only to clients that register with it.

Config file: `%LOCALAPPDATA%\Rennsport\Saved\Config\BroadcastingSocket.json`

```json
{ "ip": "0.0.0.0", "port": 559, "liveUpdateSendFrequency": 4, "accessPassword": "",
  "bAutomaticTelemetry": true, "bSendOverUDP": true, "uDPDataFormat": "json" }
```

In the game, the setting is under *Gameplay → Telemetry*: "Send physics telemetry data over UDP" (in the Polish UI: *Rozgrywka → Telemetria → Wysyłaj dane telemetryczne fizyki przez UDP*).

The game also offers a shared memory option (`RennsportAPISharedMemory`). It has not been explored.

## Protocol

UDP, JSON, UTF-8, one message per datagram.

1. The client sends this to `127.0.0.1:559`:
   ```json
   {"messageType":"RegisterClient","data":{"accessPassword":""}}
   ```
2. The game answers to the client's source port:
   ```json
   {"messageType":"RegisterClientResponse","responseCode":0,"responseCodeString":"Success","time":0,"version":1,"data":{}}
   ```
   Response codes: `0` Success, `1` AlreadyRegistered, then NotRegistered, InvalidPassword, MalformedMessage.
3. Re-send `RegisterClient` every ~5 s. This works as a keep-alive and survives game restarts. `AlreadyRegistered` is fine.
4. Optional: ask the game to resend a message type now:
   ```json
   {"messageType":"RequestUpdate","data":{"requestedMessage":"EntryListUpdate"}}
   ```
   This also works with `GameplayStageInfoUpdate` and `StartLightUpdate`.

Other input messages appear in the executable but were not tried: `EnableAutoDirector`, `ToggleAutoDirector`, `ChangeFollowedPlayer(carNumber)`, `ChangeCamera(cameraType, cameraIndex)` and `ToggleUIVisibility`.

Every output message has this shape: `{"messageType":"...","time":<float>,"version":1,"data":{...}}`.

| messageType | Rate | Content |
|---|---|---|
| `PhysicsUpdate` | **400 Hz**, ~8.3 KB each | Full physics of the player car. Sent only while driving, not in menus. |
| `CarLiveUpdate` | 4 Hz, one message per car | Position, laps, sector times, `maxEngineRPM`, `optimalShiftRPM`, flags, `carLeft` / `carRight` |
| `GameplayStageLiveUpdate` | 4 Hz | `timeLeft`, `lapsLeft`, `currentlyObservedCar`, `bestSectors` |
| `GameplayStageInfoUpdate` | on change / request | `contestName`, `gameplayStageName` (localized, e.g. "Trening"), `track` |
| `EntryListUpdate` | on change / request | `cars[]`: `carNumber`, `userId`, `firstName`, `lastName`, `shortName` |
| `StartLightUpdate` | on change | `startLightCountdown` |

Note that 400 Hz × 8 KB of JSON is about 3.3 MB/s. The plugin parses at most one `PhysicsUpdate` every 10 ms and drops the rest.

On Windows, disable `SIO_UDP_CONNRESET` on the client socket. Otherwise the ICMP "port unreachable" reply you get while the game is not running makes `Receive` throw.

## PhysicsUpdate fields

Units are as observed in the recording. See [samples/PhysicsUpdate.json](samples/PhysicsUpdate.json) for the full message.

- **Car:**
  - `speed` in m/s (max 69.4 at Monza, about 250 km/h), `engineRPM`.
  - `gearIndex`: 0 while standing at idle, 1 after pulling away, so 0 is probably neutral. Reverse is not verified.
  - `throttlePedalPos`, `brakePedalPos`, `clutchPedalPos` from 0 to 1.
  - `steeringWheelAngle` in degrees, `steeringShaftTorque`, `steeringForceFeedback` from -1 to 1.
- **Motion:**
  - `latAccel`, `lonAccel`, `vertAccel` in m/s², with spikes on kerbs and crashes.
  - `pitch`, `roll`, `yaw` in rad, plus `pitchRate`, `rollRate`, `yawRate`.
  - Axis signs are not verified. `latAccel` correlates positively with `yawRate × speed` and with `steeringWheelAngle`.
- **Lap:** `lapNumber`, `lapDistance`, `lapDistancePct`, `bestLapTime` (-1 = none), `bLapValid`, `timestamp`. The top-level `time` is always 0, so use `data.timestamp`.
- **Engine:** `enginePower`, `engineTorque`, `engineCoolantTemperature`, `engineOilTemperature`, `engineOilPressure` (unit unknown), `turboRPM`, `boostPressure` (kPa, absolute), `fuelLevel` (litres), `fuelPressure`.
- **Aids:** `bABSActive`, `aBSLevel`, `bTractionControlActive`, `tractionControlLevel`, `brakeBias`, `bPitSpeedLimiter`, `bHeadlightSwitch`.
- **`pointMetadata`:** `bIsGamePaused`, `bIsUserInControl`, `updateCounter`, `trackLengthMeters`.
- **Wheels `lF`, `rF`, `lR`, `rR`:**
  - Tyres: `pressure` and `coldPressure` (kPa), `tempSurfaceL/M/R/Avg`, `tempInner*`, `tempCarcass` (°C).
  - Wear: `wear` from 1 (new) down, plus `wearL/M/R`.
  - Brakes and slip: `brakeTemp`, `slipAngle`, `slipRatio`, `tireLoad`.
  - Suspension: `suspensionForce`, `compression` (m, 0.015 to 0.083 seen), `damperVelocity`, `rideHeight`, `camber`, `toe`.
  - Wheel speed: `rPM` in rev/min, wheel radius about 0.33 m.
  - Surface: `contactSurface` is mostly 1; the values 0, 2, 3 and 4 also appear, meaning unknown.
- **World:** `altitude`, `latitude`, `longitude`; aero `drag`, `frontDownforce`, `rearDownforce`. `extra1..3` are random integers; ignore them.

Max RPM and shift RPM are **not** in `PhysicsUpdate`. Take `maxEngineRPM` and `optimalShiftRPM` from the `CarLiveUpdate` whose `carNumber` equals `PhysicsUpdate.driverNumber`.

## SimHub integration

SimHub plugins cannot write normalized `GameData`. SimHub 9.13+ has a built-in *External Sims* mechanism (`SimHub.Plugins.ExternalSims`), which works in two parts:

- **Manifest:** the plugin writes a manifest to `%LOCALAPPDATA%\SimHub\ExternalSims\Definitions\Rennsport\Rennsport.simdef`, built with SimHub's own `ExternalSimManifest`. SimHub polls that folder every second.
- **Packets:** the plugin sends binary packets to `127.0.0.1:30559` at about 100 Hz. Each packet is a 55-byte `TelemetryHeader` followed by the standard fields in manifest order, little-endian.

The field order in `ExternalSimBridge.Fields` is the wire order. Only append to it; any other change alters the telemetry signature. SimHub then lists the game "Rennsport (API)".

## Simucube Tuner integration

Simucube Tuner 3.x (a native Qt app) reads its game list from `<Tuner>\data\game_db.json`. The stock Rennsport entry is just `{"name": "Rennsport"}`: Tuner has no telemetry reader for the game and doesn't detect it.

The installer replaces that entry so that it uses Tuner's built-in **Forza** reader on a dedicated port. It also adds detection of both game processes:

```json
"rennsport": {
    "name": "Rennsport",
    "plugin": "forza",
    "plugin_options": { "address": "127.0.0.1", "port": 9877 },
    "detect": [ { "executable_name": "Rennsport.exe" }, { "executable_name": "Rennsport-Win64-Shipping.exe" } ],
    "properties": { "max_rpm_available": true, "vehicle_detection_support": false }
}
```

`ForzaBridge` then sends Forza Motorsport 8 "Dash" packets (331 bytes, little-endian) to that port at 60 Hz. Real Forza titles keep port 9876, so they are not affected. Tuner reads `game_db.json` only at startup, and Tuner updates overwrite it.

The mapping is not verified for:

- **Axis signs** (Cornering).
- **ABS:** the Forza format has no ABS flag.
- **Rumble and surface fields:** sent as 0.
- **Gears:** Forza gear numbering (0 = R, 11 = N) is assumed.
