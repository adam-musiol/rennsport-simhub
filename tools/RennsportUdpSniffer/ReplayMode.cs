using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RennsportUdpSniffer;

/// <summary>
/// Fake Rennsport API server: plays an "rsudp api" recording (api.bin) back to registered clients,
/// so the SimHub plugin can be tested without the game. Speaks the same protocol as the game:
/// RegisterClient -> RegisterClientResponse (0 Success first time, 1 AlreadyRegistered after),
/// RequestUpdate -> last recorded message of the requested type.
/// </summary>
internal static class ReplayMode
{
    private static readonly HashSet<string> SeedTypes = new() { "EntryListUpdate", "GameplayStageInfoUpdate", "StartLightUpdate" };
    private const double ClientTimeoutSeconds = 15;

    public static int Run(Options opts)
    {
        string? path = opts.Get("in") ?? opts.Positional.FirstOrDefault();
        if (path == null || !File.Exists(path))
        {
            Console.Error.WriteLine("Usage: rsudp replay --in api1/api.bin [--port 559] [--host 127.0.0.1] [--speed 1.0] [--start sec] [--max-gap 1] [--loop] [--no-wait]");
            return 1;
        }
        string host = opts.Get("host") ?? "127.0.0.1";
        int port = opts.GetInt("port", 559);
        double speed = GetDouble(opts, "speed", 1.0);
        if (speed <= 0) speed = 1.0;
        double startSec = GetDouble(opts, "start", 0);
        // recordings start with a long idle stretch in the menu (only keep-alive answers); squeeze such gaps
        double maxGap = GetDouble(opts, "max-gap", 1.0);
        bool loop = opts.Has("loop");
        bool waitForClient = !opts.Has("no-wait");

        using var udp = new UdpClient();
        try { udp.Client.Bind(new IPEndPoint(IPAddress.Parse(host), port)); }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"Cannot bind {host}:{port} ({ex.SocketErrorCode}). Is Rennsport running? Close it or use --port.");
            return 1;
        }
        udp.Client.SendBufferSize = 4 * 1024 * 1024;
        // a client that went away must not kill Receive with ICMP "port unreachable"
        if (OperatingSystem.IsWindows())
        {
            try { udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { }
        }

        // last message of each low-rate type, so RequestUpdate can be answered before playback reaches it
        var lastByType = new ConcurrentDictionary<string, byte[]>();
        foreach (var (type, data) in SeedMessages(path)) lastByType[type] = data;
        Console.WriteLine($"Replaying {Path.GetFullPath(path)} at {speed:0.##}x, from {startSec:0.#}s, gaps > {maxGap:0.##}s squeezed{(loop ? ", looping" : "")}");
        Console.WriteLine($"Fake Rennsport API on {host}:{port}, seeded: {string.Join(", ", lastByType.Keys)}");

        var clients = new ConcurrentDictionary<IPEndPoint, double>(); // endpoint -> last RegisterClient time
        var sw = Stopwatch.StartNew();
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        void Send(byte[] data, IPEndPoint ep)
        {
            try { udp.Send(data, data.Length, ep); } catch { }
        }

        // ---- receive thread: protocol ----
        var rx = new Thread(() =>
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (!cts.IsCancellationRequested)
            {
                byte[] data;
                try { data = udp.Receive(ref ep); }
                catch { if (cts.IsCancellationRequested) return; Thread.Sleep(50); continue; }

                var (type, requested) = ParseRequest(data);
                var from = new IPEndPoint(ep.Address, ep.Port);
                switch (type)
                {
                    case "RegisterClient":
                        bool known = clients.ContainsKey(from);
                        clients[from] = sw.Elapsed.TotalSeconds;
                        Send(Response(known ? 1 : 0, known ? "AlreadyRegistered" : "Success"), from);
                        if (!known) Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] client registered: {from}");
                        break;
                    case "RequestUpdate":
                        if (!clients.ContainsKey(from)) { Send(Response(2, "NotRegistered"), from); break; }
                        if (requested != null && lastByType.TryGetValue(requested, out var msg)) Send(msg, from);
                        Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] {from} RequestUpdate {requested}{(requested != null && lastByType.ContainsKey(requested) ? "" : " (nothing recorded)")}");
                        break;
                    default:
                        Send(Response(4, "MalformedMessage"), from);
                        break;
                }
            }
        }) { IsBackground = true };
        rx.Start();

        // ---- playback ----
        long sent = 0, windowSent = 0;
        double lastStatus = 0, recPos = 0;
        IPEndPoint[] targets = Array.Empty<IPEndPoint>();
        try
        {
            if (waitForClient)
            {
                Console.WriteLine("Waiting for a client to send RegisterClient...");
                while (clients.IsEmpty && !cts.IsCancellationRequested) Thread.Sleep(50);
            }

            do
            {
                long t0 = -1, prevUs = -1;
                double startAt = sw.Elapsed.TotalSeconds, playPos = 0;
                foreach (var (timeUs, data) in ReadPackets(path))
                {
                    if (cts.IsCancellationRequested) break;
                    var type = PeekType(data);
                    if (type == "RegisterClientResponse") continue; // that was the game answering the recorder
                    if (type != null && type != "PhysicsUpdate") lastByType[type] = data;

                    if (t0 < 0) t0 = timeUs;
                    recPos = (timeUs - t0) / 1e6;
                    if (recPos < startSec) continue;
                    if (prevUs >= 0) playPos += Math.Min((timeUs - prevUs) / 1e6, maxGap);
                    prevUs = timeUs;
                    double due = startAt + playPos / speed;
                    double now;
                    while ((now = sw.Elapsed.TotalSeconds) < due)
                    {
                        if (due - now > 0.002) Thread.Sleep(1);
                        else Thread.SpinWait(50);
                    }

                    if (now - lastStatus >= 1)
                    {
                        // drop clients that stopped sending keep-alives (the game does the same)
                        foreach (var (ep, last) in clients)
                            if (now - last > ClientTimeoutSeconds && clients.TryRemove(ep, out _))
                                Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] client timed out: {ep}");
                        targets = clients.Keys.ToArray();

                        double dt = lastStatus > 0 ? now - lastStatus : 1;
                        Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] rec {TimeSpan.FromSeconds(recPos):mm\\:ss}  sent {sent}  {windowSent / dt:F0} pkt/s  clients: {(targets.Length == 0 ? "none" : string.Join(", ", targets.Select(c => c.ToString())))}");
                        lastStatus = now;
                        windowSent = 0;
                    }
                    if (targets.Length == 0) targets = clients.Keys.ToArray();

                    foreach (var ep in targets) Send(data, ep);
                    sent++;
                    windowSent++;
                }
            } while (loop && !cts.IsCancellationRequested);
        }
        finally
        {
            cts.Cancel();
            udp.Close();
        }

        Console.WriteLine($"Done: sent {sent} packets, recording position {TimeSpan.FromSeconds(recPos):mm\\:ss}.");
        return 0;
    }

    // ---------------- recording ----------------

    /// <summary>Streams packets (markers skipped) so a 500 MB recording is not loaded into memory.</summary>
    private static IEnumerable<(long timeUs, byte[] data)> ReadPackets(string path)
    {
        using var r = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20));
        if (Encoding.ASCII.GetString(r.ReadBytes(8)) != RecordingWriter.Magic)
            throw new InvalidDataException("Not an rsudp recording (bad header).");
        while (true)
        {
            long t;
            int len;
            try { t = r.ReadInt64(); len = r.ReadInt32(); } catch (EndOfStreamException) { yield break; }
            if (len == -1) { try { r.ReadInt32(); } catch (EndOfStreamException) { yield break; } continue; }
            if (len < 0 || len > 65535) throw new InvalidDataException($"Corrupt record length {len}.");
            var data = r.ReadBytes(len);
            if (data.Length < len) yield break;
            yield return (t, data);
        }
    }

    /// <summary>
    /// Last EntryList / StageInfo / StartLight message before the first PhysicsUpdate
    /// (the very first ones are sent in the menu and are empty).
    /// </summary>
    private static Dictionary<string, byte[]> SeedMessages(string path)
    {
        var seed = new Dictionary<string, byte[]>();
        foreach (var (_, data) in ReadPackets(path))
        {
            var type = PeekType(data);
            if (type == "PhysicsUpdate") break;
            if (type != null && SeedTypes.Contains(type)) seed[type] = data;
        }
        return seed;
    }

    private static double GetDouble(Options opts, string key, double def) =>
        double.TryParse(opts.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : def;

    // ---------------- messages ----------------

    /// <summary>messageType is the first property of every message, so only the first tokens are read.</summary>
    private static string? PeekType(byte[] data)
    {
        if (data.Length == 0 || data[0] != (byte)'{') return null;
        try
        {
            var r = new Utf8JsonReader(data);
            r.Read(); // StartObject
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                bool isType = r.ValueTextEquals("messageType");
                r.Read();
                if (isType) return r.TokenType == JsonTokenType.String ? r.GetString() : null;
                r.Skip();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static (string? type, string? requested) ParseRequest(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            string? type = root.TryGetProperty("messageType", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            string? requested = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                && d.TryGetProperty("requestedMessage", out var rm) && rm.ValueKind == JsonValueKind.String ? rm.GetString() : null;
            return (type, requested);
        }
        catch (JsonException) { return (null, null); }
    }

    private static byte[] Response(int code, string codeString) => Encoding.UTF8.GetBytes(
        $"{{\"messageType\":\"RegisterClientResponse\",\"responseCode\":{code},\"responseCodeString\":\"{codeString}\",\"time\":0,\"version\":1,\"data\":{{}}}}");
}
