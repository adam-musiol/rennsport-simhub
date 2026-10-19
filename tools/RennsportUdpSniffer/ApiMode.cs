using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RennsportUdpSniffer;

/// <summary>
/// Rennsport does not push telemetry by itself. It runs a UDP "API socket" server
/// (see %LOCALAPPDATA%\Rennsport\Saved\Config\BroadcastingSocket.json: ip/port/accessPassword/uDPDataFormat).
/// A client must send a JSON "RegisterClient" message first, then the game sends
/// StartLightUpdate / EntryListUpdate / CarLiveUpdate / GameplayStage*Update / PhysicsUpdate.
///
/// The exact JSON shape is not documented, so we try a few variants until the game answers.
/// </summary>
internal static class ApiMode
{
    private sealed class TypeStats
    {
        public long Count, Window;
        public double Rate;
        public int LastSize;
        public string? LastText;
        public bool FirstSaved;
    }

    public static int Run(Options opts)
    {
        var host = opts.Get("host") ?? "127.0.0.1";
        int port = opts.GetInt("port", -1);
        string password = opts.Get("password") ?? "";
        int seconds = opts.GetInt("seconds", 0);

        // read port/password from the game's config if not given
        var cfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                   "Rennsport", "Saved", "Config", "BroadcastingSocket.json");
        if (File.Exists(cfgPath))
        {
            try
            {
                var cfg = JsonNode.Parse(File.ReadAllText(cfgPath));
                if (port <= 0 && cfg?["port"] != null) port = (int)cfg["port"]!;
                if (!opts.Has("password") && cfg?["accessPassword"] != null) password = (string)cfg["accessPassword"]!;
                Console.WriteLine($"Game config: {cfgPath}");
                Console.WriteLine($"   {cfg?.ToJsonString()}");
            }
            catch (Exception ex) { Console.WriteLine($"Cannot read {cfgPath}: {ex.Message}"); }
        }
        if (port <= 0) port = 559;

        string outDir = opts.Get("out") ?? $"api_{DateTime.Now:yyyyMMdd_HHmmss}";
        Directory.CreateDirectory(Path.Combine(outDir, "samples"));
        using var rec = new RecordingWriter(Path.Combine(outDir, "api.bin"));
        var log = new StreamWriter(Path.Combine(outDir, "api_log.txt"), false, new UTF8Encoding(false)) { AutoFlush = true };
        void Log(string s) { var line = $"[{DateTime.Now:HH:mm:ss.fff}] {s}"; log.WriteLine(line); }

        var server = new IPEndPoint(IPAddress.Parse(host), port);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        udp.Client.ReceiveBufferSize = 16 * 1024 * 1024;
        // ignore ICMP "port unreachable" resets (game not started yet) - Windows only
        if (OperatingSystem.IsWindows())
        {
            try { udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { }
        }
        var localPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        Console.WriteLine($"Client socket: 0.0.0.0:{localPort}  ->  game API: {server}");
        Log($"client port {localPort}, server {server}, password '{password}'");

        var sw = Stopwatch.StartNew();
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var stats = new ConcurrentDictionary<string, TypeStats>();
        long total = 0;
        double lastPacketAt = -1;
        string lastResponse = "";
        int workingVariant = -1, lastSentVariant = -1;
        var variants = BuildRegisterVariants(password);

        // ---- receive thread ----
        var rx = new Thread(() =>
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (!cts.IsCancellationRequested)
            {
                byte[] data;
                try { data = udp.Receive(ref ep); }
                catch { if (cts.IsCancellationRequested) return; Thread.Sleep(50); continue; }
                if (cts.IsCancellationRequested) return;
                long tUs = sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
                rec.WritePacket(tUs, data);
                Interlocked.Increment(ref total);
                lastPacketAt = sw.Elapsed.TotalSeconds;

                string type = $"binary-{data.Length}B";
                string? text = null;
                if (data.Length > 0 && (data[0] == (byte)'{' || data[0] == (byte)'['))
                {
                    text = Encoding.UTF8.GetString(data);
                    type = GetMessageType(text) ?? "json(no messageType)";
                }
                var st = stats.GetOrAdd(type, _ => new TypeStats());
                lock (st)
                {
                    st.Count++; st.Window++; st.LastSize = data.Length;
                    st.LastText = text ?? Hex(data, 64);
                    if (!st.FirstSaved)
                    {
                        st.FirstSaved = true;
                        SaveSample(outDir, type, st.LastText, "first");
                        Log($"first '{type}' from {ep}: {Trunc(st.LastText, 2000)}");
                    }
                }
                if (type.Contains("Response", StringComparison.OrdinalIgnoreCase) || type.StartsWith("json("))
                {
                    lastResponse = Trunc(text ?? "", 300);
                    Log($"response after variant {lastSentVariant}: {text}");
                    bool ok = text != null && (text.Contains("Success") || text.Contains("AlreadyRegistered"));
                    if (ok && workingVariant < 0) workingVariant = lastSentVariant;
                }
                else if (type.EndsWith("Update") && workingVariant < 0 && lastSentVariant >= 0)
                    workingVariant = lastSentVariant;
            }
        }) { IsBackground = true };
        rx.Start();

        // ---- main loop: register, keep alive, draw ----
        bool interactive = !Console.IsOutputRedirected && !Console.IsInputRedirected;
        if (interactive) { Thread.Sleep(1500); Console.Clear(); Console.CursorVisible = false; }
        double lastSend = -10, lastRate = 0, lastGameCheck = -10, gameGoneAt = -1, lastSampleSave = 0;
        int variantIdx = 0;
        bool gameSeen = false, requestedExtra = false;
        string stopReason = "stopped by user";

        try
        {
            while (!cts.IsCancellationRequested)
            {
                Thread.Sleep(250);
                double now = sw.Elapsed.TotalSeconds;
                bool flowing = lastPacketAt > 0 && now - lastPacketAt < 5;
                bool gotDataUpdates = stats.Keys.Any(k => k.EndsWith("Update") && !k.Contains("Response"));

                // register until real data flows; rotate message variants until the game answers
                if (!gotDataUpdates || !flowing)
                {
                    if (now - lastSend >= (workingVariant >= 0 ? 3.0 : 1.5))
                    {
                        lastSend = now;
                        int vi = workingVariant >= 0 ? workingVariant : variantIdx++ % variants.Count;
                        lastSentVariant = vi;
                        Send(udp, server, variants[vi].json);
                        Log($"sent RegisterClient variant {vi} ({variants[vi].name}): {variants[vi].json}");
                    }
                }
                else if (!requestedExtra)
                {
                    requestedExtra = true;
                    var shape = workingVariant >= 0 ? variants[workingVariant].name : variants[0].name;
                    foreach (var what in new[] { "EntryListUpdate", "GameplayStageInfoUpdate", "StartLightUpdate" })
                    {
                        var j = BuildMessage(shape, "RequestUpdate", new JsonObject { ["requestedMessage"] = what });
                        Send(udp, server, j);
                        Log($"sent RequestUpdate: {j}");
                    }
                }

                if (now - lastRate >= 1)
                {
                    double dt = now - lastRate; lastRate = now;
                    foreach (var s in stats.Values) lock (s) { s.Rate = s.Window / dt; s.Window = 0; }
                }
                if (now - lastSampleSave >= 5)
                {
                    lastSampleSave = now;
                    foreach (var (k, s) in stats) { string? t; lock (s) t = s.LastText; if (t != null) SaveSample(outDir, k, t, "last"); }
                }

                if (now - lastGameCheck >= 1)
                {
                    lastGameCheck = now;
                    if (ScanMode.FindGame() != null) { gameSeen = true; gameGoneAt = -1; }
                    else if (gameSeen && gameGoneAt < 0) gameGoneAt = now;
                    if (gameGoneAt > 0 && now - gameGoneAt >= 3) { stopReason = "game closed"; break; }
                }
                if (seconds > 0 && now >= seconds) { stopReason = "time limit"; break; }
                if (interactive && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q) break;

                if (interactive) Draw(stats, now, server, total, gameSeen, gameGoneAt, flowing, gotDataUpdates, lastResponse, outDir);
                else if ((int)(now * 4) % 8 == 0)
                    Console.WriteLine($"[{now,6:F1}s] packets {total}  types: {string.Join(", ", stats.Select(kv => $"{kv.Key}={kv.Value.Count}"))}");
            }
        }
        finally
        {
            cts.Cancel();
            udp.Close();
            if (interactive) { Console.CursorVisible = true; Console.Clear(); }
        }

        foreach (var (k, s) in stats) if (s.LastText != null) SaveSample(outDir, k, s.LastText, "last");
        var summary = new StringBuilder();
        summary.AppendLine($"rsudp api summary {DateTime.Now:yyyy-MM-dd HH:mm:ss}, stop: {stopReason}, server {server}, packets {total}");
        summary.AppendLine($"working register variant: {(workingVariant >= 0 ? variants[workingVariant].name : "unknown")}");
        foreach (var (k, s) in stats.OrderByDescending(kv => kv.Value.Count))
            summary.AppendLine($"  {k,-30} count {s.Count,8}  last size {s.LastSize,6} B");
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary.ToString());
        Log(summary.ToString());
        log.Dispose();

        Console.WriteLine($"Stopped: {stopReason}");
        Console.WriteLine(summary.ToString());
        Console.WriteLine($"Saved in: {Path.GetFullPath(outDir)}  (api.bin, api_log.txt, summary.txt, samples\\*.json)");
        return 0;
    }

    // ---------------- message building ----------------

    private static List<(string name, string json)> BuildRegisterVariants(string pw)
    {
        var data = new JsonObject { ["accessPassword"] = pw };
        return new List<(string, string)>
        {
            ("data", BuildMessage("data", "RegisterClient", data)),
            ("flat", BuildMessage("flat", "RegisterClient", data)),
            ("messageData", BuildMessage("messageData", "RegisterClient", data)),
            ("Pascal", BuildMessage("Pascal", "RegisterClient", data)),
            ("enumInt", BuildMessage("enumInt", "RegisterClient", data)),
        };
    }

    private static string BuildMessage(string shape, string type, JsonObject data)
    {
        var d = JsonNode.Parse(data.ToJsonString())!.AsObject();
        JsonObject msg;
        switch (shape)
        {
            case "flat":
                msg = new JsonObject { ["messageType"] = type };
                foreach (var (k, v) in d) msg[k] = v?.DeepClone();
                break;
            case "messageData":
                msg = new JsonObject { ["messageType"] = type, ["messageData"] = d };
                break;
            case "Pascal":
                var pd = new JsonObject();
                foreach (var (k, v) in d) pd[char.ToUpperInvariant(k[0]) + k[1..]] = v?.DeepClone();
                msg = new JsonObject { ["MessageType"] = type, ["Data"] = pd };
                break;
            case "enumInt":
                int idx = Array.IndexOf(new[] { "RegisterClient", "RequestUpdate", "EnableAutoDirector", "ToggleAutoDirector", "ChangeFollowedPlayer", "ChangeCamera", "ToggleUIVisibility" }, type);
                msg = new JsonObject { ["messageType"] = idx, ["data"] = d };
                break;
            default:
                msg = new JsonObject { ["messageType"] = type, ["data"] = d };
                break;
        }
        return msg.ToJsonString();
    }

    private static void Send(UdpClient udp, IPEndPoint server, string json)
    {
        var b = Encoding.UTF8.GetBytes(json);
        try { udp.Send(b, b.Length, server); } catch { }
    }

    // ---------------- helpers ----------------

    private static string? GetMessageType(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("messageType", StringComparison.OrdinalIgnoreCase))
                    return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : $"type#{p.Value}";
        }
        catch { return "invalid-json"; }
        return null;
    }

    private static void SaveSample(string outDir, string type, string text, string suffix)
    {
        var safe = string.Concat(type.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        string pretty = text;
        try { pretty = JsonNode.Parse(text)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }); } catch { }
        try { File.WriteAllText(Path.Combine(outDir, "samples", $"{safe}_{suffix}.json"), pretty); } catch { }
    }

    private static void Draw(ConcurrentDictionary<string, TypeStats> stats, double now, IPEndPoint server, long total,
                             bool gameSeen, double gameGoneAt, bool flowing, bool gotData, string lastResponse, string outDir)
    {
        var lines = new List<(string, ConsoleColor)>
        {
            ($"RENNSPORT API  {TimeSpan.FromSeconds(now):mm\\:ss}  server {server}  packets {total}", ConsoleColor.Cyan),
            ($"Game: {(gameGoneAt > 0 ? "CLOSED - finishing" : gameSeen ? "RUNNING" : "not detected (start Rennsport)")}   " +
             $"Status: {(gotData && flowing ? "RECEIVING DATA" : flowing ? "got reply, waiting for data (drive on track)" : "registering... (sending RegisterClient)")}",
             gotData && flowing ? ConsoleColor.Green : ConsoleColor.Yellow),
            ($"Folder: {Path.GetFullPath(outDir)}", ConsoleColor.DarkGray),
            ($"Last response: {lastResponse}", ConsoleColor.DarkGray),
            ("", ConsoleColor.Gray),
            ($"{"messageType",-32} {"Hz",7} {"Count",9} {"Size",7}", ConsoleColor.Gray),
            (new string('-', 60), ConsoleColor.Gray),
        };
        foreach (var (k, s) in stats.OrderBy(kv => kv.Key))
            lines.Add(($"{k,-32} {s.Rate,7:F1} {s.Count,9} {s.LastSize,7}", ConsoleColor.White));

        var preview = stats.TryGetValue("PhysicsUpdate", out var ph) ? ph : stats.TryGetValue("CarLiveUpdate", out var cl) ? cl : null;
        if (preview?.LastText != null)
        {
            lines.Add(("", ConsoleColor.Gray));
            lines.Add(("Preview (last PhysicsUpdate / CarLiveUpdate):", ConsoleColor.Cyan));
            foreach (var l in Wrap(preview.LastText, Math.Max(40, Console.WindowWidth - 2)).Take(Math.Max(3, Console.WindowHeight - lines.Count - 3)))
                lines.Add((l, ConsoleColor.Gray));
        }
        lines.Add(("Q / Ctrl+C = stop. Closing the game also stops.", ConsoleColor.DarkGray));

        int w = Math.Max(20, Console.WindowWidth - 1), h = Math.Max(5, Console.WindowHeight - 1);
        Console.SetCursorPosition(0, 0);
        int row = 0;
        foreach (var (t, c) in lines.Take(h))
        {
            Console.ForegroundColor = c;
            Console.WriteLine(t.Length > w ? t[..w] : t.PadRight(w));
            row++;
        }
        Console.ResetColor();
        for (; row < h; row++) Console.WriteLine(new string(' ', w));
    }

    private static IEnumerable<string> Wrap(string s, int w)
    {
        for (int i = 0; i < s.Length; i += w) yield return s.Substring(i, Math.Min(w, s.Length - i));
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private static string Hex(byte[] d, int max)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(max, d.Length); i++) sb.Append(d[i].ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
