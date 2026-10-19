using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace RennsportUdpSniffer;

/// <summary>
/// Cuts a small, anonymized test sample out of an "rsudp api" recording for the plugin's built-in test mode:
/// the driving part only (from the first PhysicsUpdate), PhysicsUpdate downsampled to --hz, idle gaps squeezed,
/// numbers rounded to float precision, user/car ids and driver names replaced, gzip-compressed RSUDP001 format.
/// The static messages seen before driving (EntryList, StageInfo) are put at t = 0.
/// </summary>
internal static class SampleMode
{
    private const string AnonId = "00000000-0000-0000-0000-000000000001";
    private static readonly Dictionary<string, string> AnonStrings = new()
    {
        ["userId"] = AnonId, ["carId"] = AnonId, ["firstName"] = "Example", ["lastName"] = "Driver", ["shortName"] = "EXD",
    };
    private static string Precision = "G4"; // significant digits of non-integer numbers
    // times and distances keep their full precision (lap timing, track position)
    private static readonly HashSet<string> FullPrecision = new() { "time", "timestamp", "lapDistance", "totalTime", "timeLeft" };
    private static readonly HashSet<string> SeedTypes = new() { "EntryListUpdate", "GameplayStageInfoUpdate" };

    // PhysicsUpdate fields the plugin reads (MessageParser.ParsePhysics); the rest is dropped to keep the sample small
    private static readonly HashSet<string> PhysicsFields = new()
    {
        "messageType", "time", "version", "data", "pointMetadata", "lF", "rF", "lR", "rR",
        "timestamp", "driverNumber", "speed", "engineRPM", "gearIndex", "throttlePedalPos", "brakePedalPos", "clutchPedalPos",
        "steeringWheelAngle", "steeringShaftTorque", "steeringForceFeedback",
        "latAccel", "lonAccel", "vertAccel", "pitch", "roll", "yaw", "pitchRate", "rollRate", "yawRate",
        "lapNumber", "lapDistance", "lapDistancePct", "bestLapTime", "bLapValid",
        "enginePower", "engineTorque", "engineCoolantTemperature", "engineOilTemperature", "engineOilPressure",
        "turboRPM", "boostPressure", "fuelLevel", "fuelPressure",
        "bABSActive", "aBSLevel", "bTractionControlActive", "tractionControlLevel", "brakeBias",
        "bPitSpeedLimiter", "bHeadlightSwitch", "bIgnition", "drag", "frontDownforce", "rearDownforce", "airTemp",
        "bIsGamePaused", "bIsUserInControl", "updateCounter", "trackLengthMeters",
        "pressure", "coldPressure", "tempSurfaceL", "tempSurfaceM", "tempSurfaceR", "tempSurfaceAvg",
        "tempInnerL", "tempInnerM", "tempInnerR", "tempInnerAvg", "tempCarcass", "wear", "wearL", "wearM", "wearR",
        "brakeTemp", "slipAngle", "slipRatio", "tireLoad", "suspensionForce", "compression", "damperVelocity",
        "rideHeight", "camber", "toe", "rPM",
    };

    public static int Run(Options opts)
    {
        string? input = opts.Get("in");
        string? output = opts.Get("out");
        if (input == null || output == null || !File.Exists(input))
        {
            Console.Error.WriteLine("Usage: rsudp sample --in api1/api.bin --out RennsportPlugin/TestData/monza.rsudp.gz [--hz 30] [--digits 4] [--seconds N]");
            return 1;
        }
        double hz = double.TryParse(opts.Get("hz"), NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h > 0 ? h : 30;
        Precision = "G" + opts.GetInt("digits", 4);
        double maxSeconds =double.TryParse(opts.Get("seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : double.MaxValue;

        var seeds = new Dictionary<string, byte[]>();
        var packets = new List<(long timeUs, byte[] data)>();
        long t0 = -1, prevUs = -1, playUs = 0, lastPhysicsUs = -1_000_000_000;
        long physics = 0, rawBytes = 0;
        foreach (var (timeUs, data) in ReplayMode.ReadPackets(input))
        {
            var type = ReplayMode.PeekType(data);
            if (type == null || type == "RegisterClientResponse") continue;
            if (t0 < 0)
            {
                // before driving: remember the static messages, skip the rest of the menu
                if (type != "PhysicsUpdate") { if (SeedTypes.Contains(type)) seeds[type] = data; continue; }
                t0 = timeUs;
                foreach (var seed in seeds.Values) packets.Add((0, Rewrite(seed)));
            }
            if (prevUs >= 0) playUs += Math.Min(timeUs - prevUs, 1_000_000); // squeeze pauses to 1 s
            prevUs = timeUs;
            if (playUs / 1e6 > maxSeconds) break;

            if (type == "PhysicsUpdate")
            {
                if (playUs - lastPhysicsUs < 1e6 / hz) continue;
                lastPhysicsUs = playUs;
                physics++;
            }
            rawBytes += data.Length;
            packets.Add((playUs, Rewrite(data, type == "PhysicsUpdate" ? PhysicsFields : null)));
        }
        if (t0 < 0) { Console.Error.WriteLine("No PhysicsUpdate in the recording."); return 1; }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using (var file = File.Create(output))
        using (var gz = new GZipStream(file, CompressionLevel.SmallestSize))
        using (var w = new BinaryWriter(gz))
        {
            w.Write(Encoding.ASCII.GetBytes(RecordingWriter.Magic));
            foreach (var (timeUs, data) in packets)
            {
                w.Write(timeUs);
                w.Write(data.Length);
                w.Write(data);
            }
        }
        long jsonBytes = packets.Sum(p => (long)p.data.Length);
        Console.WriteLine($"{packets.Count} messages ({physics} PhysicsUpdate at {hz:0.#} Hz), {playUs / 1e6:0.0} s, " +
                          $"JSON {rawBytes / 1024 / 1024.0:0.0} MB -> {jsonBytes / 1024 / 1024.0:0.0} MB rounded -> {new FileInfo(output).Length / 1024.0:0} KB gzip: {output}");
        return 0;
    }

    /// <summary>Re-writes a message compactly: numbers as float32, anonymized ids / names, only <paramref name="keep"/> fields if given.</summary>
    private static byte[] Rewrite(byte[] data, HashSet<string>? keep = null)
    {
        var reader = new Utf8JsonReader(data);
        using var ms = new MemoryStream(data.Length);
        using (var w = new Utf8JsonWriter(ms))
        {
            string? property = null;
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject: w.WriteStartObject(); break;
                    case JsonTokenType.EndObject: w.WriteEndObject(); break;
                    case JsonTokenType.StartArray: w.WriteStartArray(); break;
                    case JsonTokenType.EndArray: w.WriteEndArray(); break;
                    case JsonTokenType.PropertyName:
                        property = reader.GetString()!;
                        if (keep != null && !keep.Contains(property)) { reader.Read(); reader.Skip(); property = null; continue; }
                        w.WritePropertyName(property);
                        continue;
                    case JsonTokenType.String:
                        w.WriteStringValue(property != null && AnonStrings.TryGetValue(property, out var anon) ? anon : reader.GetString());
                        break;
                    case JsonTokenType.Number:
                        if (reader.TryGetInt64(out long l)) w.WriteNumberValue(l);
                        else w.WriteRawValue(reader.GetDouble().ToString(property != null && FullPrecision.Contains(property) ? "R" : Precision,
                            CultureInfo.InvariantCulture), skipInputValidation: true);
                        break;
                    case JsonTokenType.True: w.WriteBooleanValue(true); break;
                    case JsonTokenType.False: w.WriteBooleanValue(false); break;
                    case JsonTokenType.Null: w.WriteNullValue(); break;
                }
                property = null;
            }
        }
        return ms.ToArray();
    }
}
