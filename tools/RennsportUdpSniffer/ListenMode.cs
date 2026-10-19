using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RennsportUdpSniffer;

/// <summary>
/// Listens on a UDP port, shows the latest packet live (hex / float / int views,
/// changed bytes highlighted) and optionally records everything to a .bin file.
/// </summary>
internal static class ListenMode
{
    private sealed class SizeState
    {
        public int Size;
        public byte[] Last = Array.Empty<byte>();
        public byte[]? Shown;
        public bool[] EverChanged = Array.Empty<bool>();
        public long Count;
        public long WindowCount;
        public double Rate;
        public string Remote = "";
    }

    private enum View { Hex, Float32, Int32, Int16 }

    public static int Run(Options opts)
    {
        int port = opts.GetInt("port", -1);
        if (port <= 0)
        {
            Console.WriteLine("Missing --port. Example: rsudp listen --port 20777");
            return 1;
        }
        var bind = IPAddress.Parse(opts.Get("bind") ?? "0.0.0.0");
        string? recordPath = opts.Get("record");
        bool live = !opts.Has("no-live") && !Console.IsOutputRedirected && !Console.IsInputRedirected;
        int seconds = opts.GetInt("seconds", 0);

        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.ReceiveBufferSize = 8 * 1024 * 1024;
        try
        {
            client.Client.Bind(new IPEndPoint(bind, port));
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"Cannot bind {bind}:{port}: {ex.Message}");
            Console.WriteLine("Is another app (SimHub, motion software...) already using this port? Close it and try again.");
            return 1;
        }

        using var rec = recordPath != null ? new RecordingWriter(recordPath) : null;
        var states = new Dictionary<int, SizeState>();
        var sw = Stopwatch.StartNew();
        long total = 0;
        var cts = new CancellationTokenSource();

        var rx = new Thread(() =>
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (!cts.IsCancellationRequested)
            {
                byte[] data;
                try { data = client.Receive(ref ep); }
                catch { if (cts.IsCancellationRequested) return; continue; }
                if (cts.IsCancellationRequested) return;
                long tUs = sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
                rec?.WritePacket(tUs, data);
                Interlocked.Increment(ref total);

                lock (states)
                {
                    if (!states.TryGetValue(data.Length, out var st))
                    {
                        st = new SizeState { Size = data.Length, EverChanged = new bool[data.Length] };
                        states[data.Length] = st;
                    }
                    else
                    {
                        for (int i = 0; i < data.Length; i++)
                            if (data[i] != st.Last[i]) st.EverChanged[i] = true;
                    }
                    st.Last = data;
                    st.Count++;
                    st.WindowCount++;
                    st.Remote = ep.ToString();
                }
            }
        }) { IsBackground = true, Name = "udp-rx" };
        rx.Start();

        Console.WriteLine($"Listening on udp {bind}:{port}" + (rec != null ? $", recording to {recordPath}" : ""));
        if (!live) Console.WriteLine("(no live view - printing a summary every second)");

        var view = View.Hex;
        int align = 0, scroll = 0, selected = -1, markerId = 0;
        bool paused = false;
        string flash = "";
        double lastRateCalc = 0, lastGameCheck = 0, gameGoneAt = -1;
        bool gameSeen = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var render = new Render();

        if (live) { Console.Clear(); Console.CursorVisible = false; }
        try
        {
            while (!cts.IsCancellationRequested)
            {
                Thread.Sleep(live ? 200 : 1000);
                double now = sw.Elapsed.TotalSeconds;

                // hands-free: stop when the game process exits
                if (now - lastGameCheck >= 1.0)
                {
                    lastGameCheck = now;
                    if (ScanMode.FindGame() != null) { gameSeen = true; gameGoneAt = -1; }
                    else if (gameSeen && gameGoneAt < 0) gameGoneAt = now;
                    if (gameGoneAt > 0 && now - gameGoneAt >= 3) { flash = "game closed"; break; }
                }

                List<SizeState> list;
                lock (states)
                {
                    if (now - lastRateCalc >= 1.0)
                    {
                        double dt = now - lastRateCalc;
                        foreach (var s in states.Values) { s.Rate = s.WindowCount / dt; s.WindowCount = 0; }
                        lastRateCalc = now;
                    }
                    list = states.Values.OrderByDescending(s => s.Count).ToList();
                }

                if (!live)
                {
                    Console.WriteLine($"[{now,6:F1}s] total {Interlocked.Read(ref total)}");
                    foreach (var s in list)
                    {
                        byte[] last; lock (states) last = s.Last;
                        Console.WriteLine($"   size {s.Size,5}  {s.Rate,7:F1} Hz  count {s.Count,8}  from {s.Remote}  first bytes: {Hex(last, 24)}");
                    }
                    if (seconds > 0 && now >= seconds) break;
                    continue;
                }

                // ---- keys ----
                while (Console.KeyAvailable)
                {
                    var k = Console.ReadKey(true).Key;
                    switch (k)
                    {
                        case ConsoleKey.Q: goto done;
                        case ConsoleKey.V: view = (View)(((int)view + 1) % 4); scroll = 0; break;
                        case ConsoleKey.A: align = (align + 1) % 4; break;
                        case ConsoleKey.P: paused = !paused; break;
                        case ConsoleKey.Tab:
                            if (list.Count > 0)
                            {
                                int idx = list.FindIndex(s => s.Size == selected);
                                selected = list[(idx + 1) % list.Count].Size;
                                scroll = 0;
                            }
                            break;
                        case ConsoleKey.UpArrow: scroll = Math.Max(0, scroll - 1); break;
                        case ConsoleKey.DownArrow: scroll++; break;
                        case ConsoleKey.PageUp: scroll = Math.Max(0, scroll - 10); break;
                        case ConsoleKey.PageDown: scroll += 10; break;
                        case ConsoleKey.M:
                            markerId++;
                            rec?.WriteMarker(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency, markerId);
                            flash = rec != null ? $"Marker #{markerId} saved at {now:F1}s" : "Markers work only with --record";
                            break;
                    }
                }
                if (seconds > 0 && now >= seconds) break;

                // ---- draw ----
                if (selected == -1 && list.Count > 0) selected = list[0].Size;
                var cur = list.FirstOrDefault(s => s.Size == selected);

                render.Begin();
                render.Line($"udp {bind}:{port} | packets {Interlocked.Read(ref total)} | {now:F0}s" +
                            (rec != null ? $" | REC {recordPath} ({rec.Packets} pkts, {markerId} markers)" : "") +
                            (paused ? " | PAUSED" : ""), ConsoleColor.Cyan);

                render.Text("Sizes: ");
                foreach (var s in list.Take(8))
                    render.Text($" {s.Size}B {s.Rate:F0}Hz ", s.Size == selected ? ConsoleColor.Black : ConsoleColor.Gray,
                                s.Size == selected ? ConsoleColor.Gray : null);
                render.EndLine();
                render.Line($"View: {view}  align +{align}  from {cur?.Remote ?? "-"}   {flash}", ConsoleColor.DarkCyan);
                render.Line("gray = never changed, white = changes, yellow = changed right now", ConsoleColor.DarkGray);

                int bodyHeight = Math.Max(5, Console.WindowHeight - 7);
                if (cur == null)
                {
                    render.Line("");
                    render.Line("Waiting for packets... (drive on track, check the port, check UDP is ON in game)", ConsoleColor.Yellow);
                }
                else
                {
                    byte[] data; bool[] ever; byte[]? shown;
                    lock (states) { data = cur.Last; ever = (bool[])cur.EverChanged.Clone(); shown = cur.Shown; }
                    if (paused && shown != null) data = shown;
                    var lines = BuildBody(view, align, data, shown, ever);
                    scroll = Math.Min(scroll, Math.Max(0, lines.Count - bodyHeight));
                    foreach (var l in lines.Skip(scroll).Take(bodyHeight)) render.AddSegments(l);
                    if (!paused) lock (states) cur.Shown = data;
                }
                render.Line("V view | A align | Tab size | arrows/PgUp/PgDn scroll | M marker | P pause | Q quit", ConsoleColor.DarkGray);
                render.Flush();
            }
        done:;
        }
        finally
        {
            cts.Cancel();
            client.Close();
            if (live) { Console.ResetColor(); Console.Clear(); Console.CursorVisible = true; }
        }

        Console.WriteLine($"Done. Packets: {Interlocked.Read(ref total)}");
        if (rec != null) Console.WriteLine($"Recording saved: {Path.GetFullPath(recordPath!)}  ->  run: rsudp analyze \"{recordPath}\"");
        return 0;
    }

    // ---------- body builders ----------

    private static ConsoleColor ColorFor(int start, int len, byte[] data, byte[]? shown, bool[] ever)
    {
        bool now = false, any = false;
        for (int i = start; i < start + len && i < data.Length; i++)
        {
            if (ever[i]) any = true;
            if (shown != null && shown.Length == data.Length && shown[i] != data[i]) now = true;
        }
        return now ? ConsoleColor.Yellow : any ? ConsoleColor.White : ConsoleColor.DarkGray;
    }

    private static List<List<(string, ConsoleColor)>> BuildBody(View view, int align, byte[] d, byte[]? shown, bool[] ever)
    {
        var lines = new List<List<(string, ConsoleColor)>>();
        if (view == View.Hex)
        {
            for (int off = 0; off < d.Length; off += 16)
            {
                var l = new List<(string, ConsoleColor)> { ($"{off,5} 0x{off:X4} | ", ConsoleColor.DarkCyan) };
                for (int i = off; i < off + 16; i++)
                    l.Add(i < d.Length ? ($"{d[i]:X2} ", ColorFor(i, 1, d, shown, ever)) : ("   ", ConsoleColor.Gray));
                var ascii = new StringBuilder(" |");
                for (int i = off; i < Math.Min(off + 16, d.Length); i++) ascii.Append(d[i] is >= 32 and < 127 ? (char)d[i] : '.');
                l.Add((ascii.ToString(), ConsoleColor.DarkGray));
                lines.Add(l);
            }
            return lines;
        }

        int width = view == View.Int16 ? 2 : 4;
        int perLine = view == View.Int16 ? 8 : 4;
        int start = align;
        var cur = new List<(string, ConsoleColor)>();
        int n = 0;
        for (int off = start; off + width <= d.Length; off += width)
        {
            if (n % perLine == 0)
            {
                if (cur.Count > 0) lines.Add(cur);
                cur = new List<(string, ConsoleColor)>();
            }
            string val = view switch
            {
                View.Float32 => FormatFloat(BitConverter.ToSingle(d, off)),
                View.Int32 => BitConverter.ToInt32(d, off).ToString(),
                _ => BitConverter.ToInt16(d, off).ToString(),
            };
            cur.Add(($"{off,5}:", ConsoleColor.DarkCyan));
            cur.Add((val.PadLeft(view == View.Int16 ? 7 : 14) + "  ", ColorFor(off, width, d, shown, ever)));
            n++;
        }
        if (cur.Count > 0) lines.Add(cur);
        return lines;
    }

    private static string FormatFloat(float f)
    {
        if (float.IsNaN(f)) return "NaN";
        if (float.IsInfinity(f)) return "Inf";
        float a = Math.Abs(f);
        if (a != 0 && (a < 1e-4 || a > 1e7)) return "~garbage";
        return f.ToString("0.#####");
    }

    private static string Hex(byte[] d, int max)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(max, d.Length); i++) sb.Append(d[i].ToString("X2")).Append(' ');
        if (d.Length > max) sb.Append("...");
        return sb.ToString();
    }

    // ---------- flicker-free console renderer ----------

    private sealed class Render
    {
        private readonly List<List<(string text, ConsoleColor fg, ConsoleColor? bg)>> _lines = new();
        private List<(string, ConsoleColor, ConsoleColor?)> _cur = new();
        private int _prevLineCount;

        public void Begin() { _lines.Clear(); _cur = new(); }
        public void Text(string t, ConsoleColor c = ConsoleColor.Gray, ConsoleColor? bg = null) => _cur.Add((t, c, bg));
        public void EndLine() { _lines.Add(_cur); _cur = new(); }
        public void Line(string t, ConsoleColor c = ConsoleColor.Gray) { Text(t, c); EndLine(); }
        public void AddSegments(List<(string, ConsoleColor)> segs)
        {
            foreach (var (t, c) in segs) Text(t, c);
            EndLine();
        }

        public void Flush()
        {
            int w = Math.Max(20, Console.WindowWidth - 1);
            int h = Math.Max(5, Console.WindowHeight - 1);
            Console.SetCursorPosition(0, 0);
            int row = 0;
            foreach (var line in _lines)
            {
                if (row >= h) break;
                int used = 0;
                foreach (var (text, fg, bg) in line)
                {
                    if (used >= w) break;
                    var t = text.Length + used > w ? text[..(w - used)] : text;
                    Console.ForegroundColor = fg;
                    if (bg.HasValue) Console.BackgroundColor = bg.Value; else Console.ResetColor();
                    Console.ForegroundColor = fg;
                    Console.Write(t);
                    used += t.Length;
                }
                Console.ResetColor();
                Console.Write(new string(' ', Math.Max(0, w - used)));
                Console.WriteLine();
                row++;
            }
            for (int i = row; i < Math.Min(_prevLineCount, h); i++) Console.WriteLine(new string(' ', w));
            _prevLineCount = row;
        }
    }
}
