# rsudp: Rennsport telemetry sniffer and replay tool

A .NET 8 console tool. It was used to find out what Rennsport sends, and it lets you develop and test the SimHub plugin without the game.

## Build

```
dotnet build -c Release
```

The output is `bin\Release\net8.0\rsudp.exe`. It runs on .NET 8 or newer.

## Record the game's API (`api`)

Rennsport sends nothing by itself. It runs a UDP API server: port 559 by default, configured in `%LOCALAPPDATA%\Rennsport\Saved\Config\BroadcastingSocket.json`. A client must send `RegisterClient` first. See [docs/PROTOCOL.md](../../docs/PROTOCOL.md).

```
rsudp api --out api1
```

No Administrator rights are needed. Start it before or during a session, drive, then close the game. The tool stops by itself. The `api1` folder then contains:

- `api.bin` with every received message,
- `api_log.txt` with what was sent and what the game answered,
- `samples\*.json` with the first and last example of each message type,
- `summary.txt`.

Recordings contain your in-game name and account ID. Remove them before sharing.

## Fake game for plugin development (`replay`)

```
rsudp replay --in api1/api.bin --port 559 --loop
```

This plays the recording back to any registered client, paced by its timestamps. It answers `RegisterClient` and `RequestUpdate` like the game does.

- Menu stretches longer than `--max-gap` seconds (default 1) are shortened.
- `--start <sec>` skips into the recording.
- `--speed` changes the playback speed.
- Port 559 belongs to the game, so close Rennsport first.

## Test sample for the plugin (`sample`)

```
rsudp sample --in api1/api.bin --out RennsportPlugin/TestData/monza.rsudp.gz [--hz 30] [--digits 4] [--seconds N]
```

This cuts the sample that the plugin's **Start test** button plays (it is embedded in the plugin DLL). It keeps only the driving part, puts the entry list and stage info at the start, shortens pauses to 1 s and keeps PhysicsUpdate at `--hz`. PhysicsUpdate keeps only the fields the plugin reads, and numbers are rounded to `--digits` significant digits (times and lap distance stay exact). User and car IDs and driver names are replaced with `Example Driver`. The output is a gzip-compressed recording in the same format as `api.bin`. The defaults give about 1.5 MB for 2.5 minutes.

## Raw UDP capture and analysis

These tools were used before the API was found. They work for any game.

| Command | What it does |
|---|---|
| `rsudp record [--out folder]` | Captures **all** UDP flows on the PC (raw sockets, needs Administrator) into one `.bin` per flow. Stops when Rennsport exits. |
| `rsudp scan` | Shows a live table of UDP flows (Administrator) |
| `rsudp listen --port N [--record x.bin]` | Live view of packets on a known port (hex / float / int). Keys: `V` view, `A` alignment, `Tab` packet size, `P` pause, `M` marker, `Q` quit |
| `rsudp analyze file.bin [--size N] [--csv]` | Checks against known formats (F1, Forza, Codemasters, AC, PC2/AMS2, OutGauge/OutSim…) and guesses fields (`RPM?`, `gear?`, `0..1 pedal?`) |
| `rsudp config` | Searches the game's config files for UDP / telemetry settings |

## `.bin` format

```
"RSUDP001"                                  8 bytes, ASCII
repeated: int64 time_us, int32 length, byte[length]
          length == -1 -> marker, followed by int32 id
```

Little-endian.
