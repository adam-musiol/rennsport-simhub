using System.Text;

namespace RennsportUdpSniffer;

/// <summary>
/// Offline analysis of a recording. For every packet size it tries to find
/// where the "real" fields are (float32 / float64 / int32 / int16 / uint8)
/// and gives hints like "RPM?", "gear?", "0..1 pedal?".
/// </summary>
internal static class AnalyzeMode
{
    internal sealed class Field
    {
        public int Offset;
        public string Type = "";
        public int Width;
        public double Min, Max, Last, Mean;
        public int Distinct; // capped at 257
        public double Smooth; // mean |delta| / range (0 = very smooth, 1 = noise)
        public bool Monotonic;
        public double Score;
        public string Hint = "";
        public List<double> SampleValues = new();
    }

    public static int Run(Options opts)
    {
        if (opts.Positional.Count == 0)
        {
            Console.WriteLine("Usage: rsudp analyze session.bin [--size N] [--csv out.csv] [--top 80]");
            return 1;
        }
        string path = opts.Positional[0];
        if (Directory.Exists(path))
        {
            // capture folder from 'rsudp record' -> analyze every .bin, biggest first
            var files = new DirectoryInfo(path).GetFiles("*.bin").OrderByDescending(f => f.Length).ToList();
            if (files.Count == 0) { Console.WriteLine("No .bin files in folder."); return 1; }
            var summary = Path.Combine(path, "summary.txt");
            if (File.Exists(summary)) Console.WriteLine(File.ReadAllText(summary));
            foreach (var f in files)
            {
                Console.WriteLine();
                Console.WriteLine(new string('#', 90));
                AnalyzeFile(f.FullName, opts);
            }
            return 0;
        }
        return AnalyzeFile(path, opts);
    }

    private static int AnalyzeFile(string path, Options opts)
    {
        var (packets, markers) = RecordingReader.Read(path);
        if (packets.Count == 0) { Console.WriteLine("No packets in recording."); return 1; }

        double dur = (packets[^1].TimeUs - packets[0].TimeUs) / 1e6;
        Console.WriteLine($"File: {path}");
        Console.WriteLine($"Packets: {packets.Count}, duration {dur:F1}s, markers: {markers.Count}");
        foreach (var m in markers)
            Console.WriteLine($"   marker #{m.Id} at {(m.TimeUs - packets[0].TimeUs) / 1e6:F2}s");
        Console.WriteLine();

        var groups = packets.GroupBy(p => p.Data.Length).OrderByDescending(g => g.Count()).ToList();
        Console.WriteLine("Packet sizes:");
        foreach (var g in groups)
        {
            var ts = g.Select(p => p.TimeUs).ToList();
            double gd = (ts[^1] - ts[0]) / 1e6;
            double rate = gd > 0 ? (g.Count() - 1) / gd : 0;
            Console.WriteLine($"   {g.Key,5} bytes  x{g.Count(),7}  ~{rate,6:F1} Hz");
        }
        Console.WriteLine();

        int onlySize = opts.GetInt("size", -1);
        int top = opts.GetInt("top", 80);
        // --csv           -> <recording>.csv next to the recording
        // --csv out.csv   -> that file
        string? csv = opts.Has("csv") ? (opts.Get("csv") ?? Path.ChangeExtension(path, ".csv")) : null;

        foreach (var g in groups)
        {
            if (onlySize > 0 && g.Key != onlySize) continue;
            if (onlySize <= 0 && g.Count() < 10) continue;
            var data = g.Select(p => p.Data).ToList();
            // keep analysis fast on long recordings: use at most ~40k evenly spaced packets
            int stride = Math.Max(1, data.Count / 40000);
            if (stride > 1) data = data.Where((_, i) => i % stride == 0).ToList();
            AnalyzeSize(g.Key, data, top);

            if (csv != null && (onlySize > 0 ? g.Key == onlySize : g == groups[0]))
                WriteCsv(csv, g.Key, g.ToList(), markers, packets[0].TimeUs);
        }
        return 0;
    }

    private static List<Field> AnalyzeSize(int size, List<byte[]> pkts, int top)
    {
        Console.WriteLine(new string('=', 90));
        Console.WriteLine($"SIZE {size} bytes  ({pkts.Count} packets)");
        Console.WriteLine(new string('=', 90));

        // constant bytes
        var changing = new bool[size];
        for (int i = 1; i < pkts.Count; i++)
            for (int b = 0; b < size; b++)
                if (pkts[i][b] != pkts[0][b]) changing[b] = true;
        int constCount = changing.Count(c => !c);
        Console.WriteLine($"Constant bytes: {constCount}/{size}. First 16 bytes of first packet: {HexBytes(pkts[0], 0, 16)}");

        var known = KnownFormats.Check(size, pkts);
        Console.WriteLine();
        Console.WriteLine("Known format check:");
        if (known.Count == 0) Console.WriteLine("   no match with F1 / Forza / Codemasters / AC / PC2-AMS2 / OutGauge / OutSim / GT7");
        foreach (var k in known)
        {
            if (k.StartsWith("STRONG")) Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"   * {k}");
            Console.ResetColor();
        }

        var fields = FindFields(size, pkts, changing);

        // byte map
        var map = new char[size];
        for (int i = 0; i < size; i++) map[i] = changing[i] ? '?' : '.';
        foreach (var f in fields)
        {
            char c = f.Type switch { "f32" => 'F', "f64" => 'D', "i32" => 'I', "i16" => 'S', "u8" => 'b', _ => '?' };
            for (int i = f.Offset; i < f.Offset + f.Width; i++) map[i] = i == f.Offset ? c : char.ToLowerInvariant(c);
        }
        Console.WriteLine();
        Console.WriteLine("Byte map ('.' const, F/f float32, D/d double, I/i int32, S/s int16, b byte, ? changing but unknown):");
        for (int off = 0; off < size; off += 32)
            Console.WriteLine($"   {off,5}: {new string(map, off, Math.Min(32, size - off))}");

        Console.WriteLine();
        Console.WriteLine($"{"Offset",-12} {"Type",-4} {"Min",12} {"Max",12} {"Last",12} {"Distinct",8} {"Smooth",7}  Hint");
        Console.WriteLine(new string('-', 90));
        foreach (var f in fields.OrderBy(f => f.Offset).Take(top))
        {
            string distinct = f.Distinct > 256 ? ">256" : f.Distinct.ToString();
            Console.WriteLine($"{f.Offset,5} 0x{f.Offset:X4} {f.Type,-4} {Fmt(f.Min),12} {Fmt(f.Max),12} {Fmt(f.Last),12} {distinct,8} {f.Smooth,7:F3}  {f.Hint}");
            if (f.Distinct <= 12)
                Console.WriteLine($"{"",18}values: {string.Join(", ", f.SampleValues.Select(Fmt))}");
        }
        if (fields.Count > top) Console.WriteLine($"... {fields.Count - top} more (use --top)");
        Console.WriteLine();
        return fields;
    }

    internal static List<Field> FindFields(int size, List<byte[]> pkts, bool[] changing)
    {
        var candidates = new List<Field>();
        // try each type at every offset that touches a changing byte
        foreach (var (type, width) in new[] { ("f64", 8), ("f32", 4), ("i32", 4), ("i16", 2), ("u8", 1) })
        {
            for (int off = 0; off + width <= size; off++)
            {
                bool touches = false;
                for (int i = off; i < off + width; i++) if (changing[i]) { touches = true; break; }
                if (!touches) continue;
                var f = Evaluate(type, width, off, pkts);
                if (f != null) candidates.Add(f);
            }
        }

        // greedy: best score first, no overlapping bytes
        var used = new bool[size];
        var result = new List<Field>();
        foreach (var f in candidates.OrderByDescending(f => f.Score))
        {
            bool overlap = false;
            for (int i = f.Offset; i < f.Offset + f.Width; i++) if (used[i]) { overlap = true; break; }
            if (overlap) continue;
            for (int i = f.Offset; i < f.Offset + f.Width; i++) used[i] = true;
            result.Add(f);
        }
        return result;
    }

    private static double Read(string type, byte[] d, int off) => type switch
    {
        "f64" => BitConverter.ToDouble(d, off),
        "f32" => BitConverter.ToSingle(d, off),
        "i32" => BitConverter.ToInt32(d, off),
        "i16" => BitConverter.ToInt16(d, off),
        _ => d[off],
    };

    private static Field? Evaluate(string type, int width, int off, List<byte[]> pkts)
    {
        int n = pkts.Count;
        double min = double.MaxValue, max = double.MinValue, sum = 0, sumDelta = 0, prev = 0;
        int incr = 0, decr = 0, weird = 0;
        var distinct = new HashSet<double>();
        bool isFloat = type is "f32" or "f64";

        for (int i = 0; i < n; i++)
        {
            double v = Read(type, pkts[i], off);
            if (isFloat)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) return null;
                double a = Math.Abs(v);
                if (a > 1e7) return null;
                if (a != 0 && a < 1e-6) weird++;
            }
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
            if (distinct.Count <= 256) distinct.Add(v);
            if (i > 0)
            {
                sumDelta += Math.Abs(v - prev);
                if (v > prev) incr++; else if (v < prev) decr++;
            }
            prev = v;
        }
        double range = max - min;
        if (range <= 0) return null;
        if (isFloat && weird > n / 100) return null;

        double smooth = n > 1 ? (sumDelta / (n - 1)) / range : 1;
        bool monotonic = incr > 0 && decr <= n / 200;
        int dc = distinct.Count;

        // --- scoring ---
        double score;
        if (isFloat)
        {
            // smooth floats score high; noisy ones (g-force, suspension) still allowed, lower score.
            // Garbage (int bytes read as float) is already filtered by the NaN / magnitude / denormal checks above.
            score = smooth <= 0.25 ? 3.0 - smooth * 4 : 2.0 - smooth;
            if (type == "f64") score += 0.6; // doubles are rare, but when they pass they are strong
            if (off % width == 0) score += 0.3;
            // floats that only take integer values with huge magnitude are suspicious
            if (dc <= 2 && Math.Abs(max) > 1e4) score -= 2;
        }
        else
        {
            bool enumLike = dc <= 32;
            if (!enumLike && !monotonic && smooth > 0.15) return null;
            score = type switch { "i32" => 1.6, "i16" => 1.3, _ => 1.0 };
            if (monotonic) score += 1.0;
            if (enumLike) score += 0.4;
            score -= smooth;
            if (off % width == 0) score += 0.2;
        }

        var f = new Field
        {
            Offset = off, Type = type, Width = width, Min = min, Max = max,
            Last = Read(type, pkts[^1], off), Mean = sum / n, Distinct = dc,
            Smooth = smooth, Monotonic = monotonic, Score = score,
        };
        if (dc <= 12) f.SampleValues = distinct.OrderBy(x => x).ToList();
        f.Hint = Guess(f, isFloat);
        return f;
    }

    private static string Guess(Field f, bool isFloat)
    {
        var h = new List<string>();
        bool integerValued = f.SampleValues.Count > 0 && f.SampleValues.All(v => Math.Abs(v - Math.Round(v)) < 1e-6);
        if (f.Monotonic) h.Add("increasing -> time / lap distance / packet counter?");
        if (f.Distinct <= 12 && integerValued && f.Min >= -1 && f.Max <= 10 && f.Max >= 2) h.Add("gear?");
        if (isFloat && f.Min >= 0 && f.Max <= 1.0001 && f.Max > 0.05) h.Add("0..1 -> throttle/brake/clutch/fuel%?");
        else if (isFloat && f.Min >= -1.0001 && f.Max <= 1.0001) h.Add("-1..1 -> steering / direction vector / quaternion?");
        if (f.Min >= 0 && f.Max is > 800 and < 25000 && f.Smooth < 0.1) h.Add("RPM?");
        if (isFloat && f.Min >= -1 && f.Max is > 5 and < 120 && f.Smooth < 0.05) h.Add("speed m/s?");
        if (isFloat && f.Min >= -1 && f.Max is > 20 and < 400 && f.Smooth < 0.05) h.Add("speed km/h?");
        if (isFloat && f.Min > 10 && f.Max < 150 && f.Max - f.Min < 60 && f.Smooth < 0.02) h.Add("temperature?");
        if (isFloat && Math.Abs(f.Min) > 50 && Math.Abs(f.Max) > 50 && f.Smooth < 0.02 && !h.Contains("RPM?")) h.Add("world position?");
        if (isFloat && f.Smooth > 0.1) h.Add("noisy -> accel/g-force/suspension velocity?");
        return string.Join("  ", h);
    }

    private static void WriteCsv(string path, int size, List<RecPacket> pkts, List<RecMarker> markers, long t0)
    {
        var data = pkts.Select(p => p.Data).ToList();
        var changing = new bool[size];
        for (int i = 1; i < data.Count; i++)
            for (int b = 0; b < size; b++)
                if (data[i][b] != data[0][b]) changing[b] = true;
        var fields = FindFields(size, data, changing).OrderBy(f => f.Offset).ToList();

        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.WriteLine("time_s;marker;" + string.Join(";", fields.Select(f => $"{f.Type}@{f.Offset}")));
        int mi = 0;
        var ms = markers.OrderBy(m => m.TimeUs).ToList();
        foreach (var p in pkts)
        {
            string marker = "";
            while (mi < ms.Count && ms[mi].TimeUs <= p.TimeUs) marker = ms[mi++].Id.ToString();
            var sb = new StringBuilder();
            sb.Append(((p.TimeUs - t0) / 1e6).ToString("F4")).Append(';').Append(marker);
            foreach (var f in fields) sb.Append(';').Append(Read(f.Type, p.Data, f.Offset).ToString("G7"));
            w.WriteLine(sb.ToString());
        }
        Console.WriteLine($"CSV written: {Path.GetFullPath(path)}  ({fields.Count} columns, ';' separated - opens fine in Excel PL)");
    }

    private static string Fmt(double v) => Math.Abs(v) >= 1e6 || (v != 0 && Math.Abs(v) < 1e-3) ? v.ToString("G4") : v.ToString("0.###");

    private static string HexBytes(byte[] d, int off, int len)
    {
        var sb = new StringBuilder();
        for (int i = off; i < Math.Min(d.Length, off + len); i++) sb.Append(d[i].ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
