using System.Text;

namespace RennsportUdpSniffer;

/// <summary>
/// Compares packets of one size against well-known sim racing UDP formats.
/// These are heuristics - a "match" means "worth trying this game profile in SimHub".
/// </summary>
internal static class KnownFormats
{
    public static List<string> Check(int size, List<byte[]> pkts)
    {
        var hits = new List<string>();
        if (pkts.Count == 0) return hits;
        var p0 = pkts[0];

        // ---- text based formats (SimTools / custom motion software often send text) ----
        int printable = p0.Count(b => b is >= 32 and < 127 or 9 or 10 or 13);
        if (size > 0 && printable >= size * 0.9)
        {
            var txt = Encoding.ASCII.GetString(p0, 0, Math.Min(size, 200)).Replace("\r", "\\r").Replace("\n", "\\n");
            hits.Add($"TEXT packet (not binary)! Sample: \"{txt}\"");
            return hits;
        }

        // ---- ASCII magic at start ----
        if (size >= 4 && p0.Take(4).All(b => b is >= 65 and <= 122) && AllSame(pkts, 0, 4))
            hits.Add($"Starts with ASCII magic \"{Encoding.ASCII.GetString(p0, 0, 4)}\" (BeamNG MotionSim uses 'BNG1', others vary)");

        // ---- Codemasters F1 2018+ : uint16 packetFormat = year ----
        if (size >= 24)
        {
            int fmt = BitConverter.ToUInt16(p0, 0);
            if (fmt is >= 2018 and <= 2030 && pkts.All(p => BitConverter.ToUInt16(p, 0) == fmt))
            {
                int idOff = fmt >= 2023 ? 6 : 5;
                var ids = pkts.Select(p => p[idOff]).Distinct().OrderBy(x => x);
                hits.Add($"Codemasters/EA F1 {fmt} format (packetFormat={fmt}, packetIds: {string.Join(",", ids)}) -> try 'F1 {fmt % 100}' in SimHub");
            }
        }

        // ---- Forza "Data Out" ----
        if (size is 232 or 311 or 324 or 331)
        {
            bool raceOnOk = pkts.All(p => BitConverter.ToInt32(p, 0) is 0 or 1);
            var raceOn = pkts.Where(p => BitConverter.ToInt32(p, 0) == 1).ToList();
            bool maxRpmOk = raceOn.Count > 0 && raceOn.All(p => BitConverter.ToSingle(p, 8) is > 1000 and < 25000);
            string game = size switch
            {
                232 => "Forza Motorsport 7 (Sled)",
                311 => "Forza Motorsport 7 (Dash)",
                324 => "Forza Horizon 4/5",
                _ => "Forza Motorsport 2023",
            };
            if (raceOnOk && maxRpmOk) hits.Add($"STRONG: {game} Data Out layout (IsRaceOn @0, EngineMaxRpm @8) -> try '{game}' in SimHub");
            else hits.Add($"Size matches {game}, but fields don't look right (IsRaceOn/EngineMaxRpm check failed)");
        }

        // ---- Codemasters legacy extradata (DiRT Rally, GRID, old F1) ----
        if (size is 264 or 256 or 152 or 64 && IsFloatArray(pkts, size))
        {
            float t0 = BitConverter.ToSingle(pkts[0], 0), t1 = BitConverter.ToSingle(pkts[^1], 0);
            string ex = size switch { 264 => "extradata=3", 256 => "extradata=2", 152 => "extradata=1", _ => "extradata=0" };
            hits.Add($"Looks like Codemasters legacy UDP ({ex}, float[{size / 4}]) - time@0 {t0:F2} -> {t1:F2}. Try 'DiRT Rally 2.0' / 'Codemasters' profile");
        }

        // ---- Assetto Corsa RTCarInfo (328 bytes, int size @4) ----
        if (size == 328 && BitConverter.ToInt32(p0, 4) == 328)
            hits.Add("STRONG: Assetto Corsa RTCarInfo (size field @4 = 328)");

        // ---- Project CARS 2 / AMS2 UDP v2 ----
        if (size >= 12)
        {
            byte type = p0[10];
            var known2 = new Dictionary<int, string> { [559] = "telemetry", [308] = "race", [1136] = "participants", [1063] = "timings", [24] = "game state", [1040] = "time stats", [1452] = "vehicle names" };
            if (known2.TryGetValue(size, out var what) && type <= 8)
                hits.Add($"Size matches Project CARS 2 / AMS2 UDP v2 '{what}' packet (packetType @10 = {type})");
        }
        if (size == 1367) hits.Add("Size matches Project CARS 1 sTelemetryData");

        // ---- LFS / BeamNG OutGauge & OutSim ----
        if (size is 92 or 96 && p0.Skip(4).Take(3).All(b => b is >= 32 and < 127))
            hits.Add($"Looks like OutGauge (car name @4 = \"{Encoding.ASCII.GetString(p0, 4, 4).TrimEnd('\0')}\") -> 'Live for Speed' / BeamNG OutGauge");
        if (size is 64 or 68 && IsFloatArray(pkts, size, start: 4))
            hits.Add("Size/layout like OutSim (time + float[] motion data)");

        // ---- GT7 (Salsa20 encrypted) / encrypted or compressed data ----
        if (LooksRandom(pkts, size))
            hits.Add($"Bytes look RANDOM (encrypted/compressed?){(size is 296 or 316 or 344 ? " - size matches Gran Turismo 7 (Salsa20)" : "")}. Hard to parse without the key.");

        // ---- generic float array ----
        if (hits.Count == 0 && IsFloatArray(pkts, size))
            hits.Add($"Whole packet is a plain float32[{size / 4}] array - easy to parse, typical for motion-platform outputs");

        return hits;
    }

    private static bool AllSame(List<byte[]> pkts, int off, int len) =>
        pkts.All(p => p.AsSpan(off, len).SequenceEqual(pkts[0].AsSpan(off, len)));

    /// every aligned 4-byte slot is a sane float (or zero) in every sampled packet
    private static bool IsFloatArray(List<byte[]> pkts, int size, int start = 0)
    {
        if ((size - start) % 4 != 0) return false;
        int step = Math.Max(1, pkts.Count / 500);
        for (int i = 0; i < pkts.Count; i += step)
            for (int off = start; off + 4 <= size; off += 4)
            {
                float f = BitConverter.ToSingle(pkts[i], off);
                if (float.IsNaN(f) || float.IsInfinity(f)) return false;
                float a = Math.Abs(f);
                if (a != 0 && (a < 1e-12 || a > 1e7)) return false;
            }
        return true;
    }

    /// byte histogram of changing bytes is close to uniform -> encrypted / compressed
    private static bool LooksRandom(List<byte[]> pkts, int size)
    {
        if (size < 32 || pkts.Count < 20) return false;
        var hist = new long[256];
        long total = 0;
        int step = Math.Max(1, pkts.Count / 2000);
        for (int i = 0; i < pkts.Count; i += step)
            foreach (var b in pkts[i]) { hist[b]++; total++; }
        double entropy = 0;
        foreach (var c in hist)
        {
            if (c == 0) continue;
            double p = (double)c / total;
            entropy -= p * Math.Log2(p);
        }
        return entropy > 7.5; // max is 8.0; normal telemetry with zeros/floats is usually < 6.5
    }
}
