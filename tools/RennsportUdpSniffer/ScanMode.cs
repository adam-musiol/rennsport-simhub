using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;

namespace RennsportUdpSniffer;

/// <summary>
/// Finds UDP streams using raw sockets + SIO_RCVALL (like RawCap).
/// Works on loopback (127.0.0.1) and LAN interfaces. Windows + Administrator only.
///
///   scan   - only shows the table of UDP flows
///   record - same, but also saves every flow to its own .bin file and stops
///            by itself when the Rennsport process exits (hands-free).
/// </summary>
internal static class ScanMode
{
    private static readonly HashSet<int> IgnoredPorts = new() { 53, 67, 68, 123, 137, 138, 1900, 3702, 5353, 5355 };

    private sealed class Flow
    {
        public string Src = "", Dst = "";
        public int SrcPort, DstPort;
        public readonly ConcurrentDictionary<int, long> Sizes = new();
        public long Count;
        public long WindowCount;
        public double RateHz, PeakHz;
        public RecordingWriter? Writer;
        public string? FilePath;
        public readonly object Lock = new();
        public int LastSocket = -1;
        public long LastTimeUs;
        public int LastHash;
    }

    public static int Run(Options opts, bool record)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("scan/record work only on Windows. Use 'listen --port N' if you know the port.");
            return 1;
        }
        if (!IsAdmin())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("This needs Administrator rights (raw sockets).");
            Console.WriteLine("Open 'Terminal (Admin)' / 'cmd as Administrator' and run it again.");
            Console.ResetColor();
            return 1;
        }

        int seconds = opts.GetInt("seconds", 0);
        int minRate = opts.GetInt("min-rate", 0);
        string? outDir = null;
        if (record)
        {
            outDir = opts.Get("out") ?? $"capture_{DateTime.Now:yyyyMMdd_HHmmss}";
            Directory.CreateDirectory(outDir);
        }

        var addresses = new List<IPAddress> { IPAddress.Loopback };
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    addresses.Add(ua.Address);
        }

        var flows = new ConcurrentDictionary<string, Flow>();
        var sockets = new List<Socket>();
        var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        for (int i = 0; i < addresses.Count; i++)
        {
            var ip = addresses[i];
            int sockIndex = i;
            try
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
                s.Bind(new IPEndPoint(ip, 0));
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);
                s.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(1), new byte[4]);
                s.ReceiveBufferSize = 16 * 1024 * 1024;
                sockets.Add(s);
                var t = new Thread(() => CaptureLoop(s, sockIndex, flows, sw, outDir, cts.Token)) { IsBackground = true };
                t.Start();
                Console.WriteLine($"Capturing on {ip}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Skip {ip}: {ex.Message}");
            }
        }
        if (sockets.Count == 0) return 1;

        Console.WriteLine();
        if (record)
        {
            Console.WriteLine($"Recording ALL UDP flows to folder: {Path.GetFullPath(outDir!)}");
            Console.WriteLine("Start the game and drive. Recording stops by itself when you CLOSE the game (or Ctrl+C / Q).");
        }
        else Console.WriteLine("Now drive in Rennsport (on track, not in menu). Press Q to stop.");
        Thread.Sleep(2000);

        double lastTick = 0;
        bool interactive = !Console.IsOutputRedirected && !Console.IsInputRedirected;
        if (interactive) { Console.Clear(); Console.CursorVisible = false; }

        string gameName = "";
        bool gameSeen = false;
        double gameGoneAt = -1;
        string stopReason = "stopped by user";

        try
        {
            while (!cts.IsCancellationRequested)
            {
                Thread.Sleep(1000);
                double now = sw.Elapsed.TotalSeconds;
                double dt = now - lastTick;
                lastTick = now;
                foreach (var f in flows.Values)
                {
                    long c = Interlocked.Exchange(ref f.WindowCount, 0);
                    f.RateHz = c / dt;
                    if (f.RateHz > f.PeakHz) f.PeakHz = f.RateHz;
                }

                // ---- game process watch ----
                var game = FindGame();
                if (game != null) { gameSeen = true; gameName = game; gameGoneAt = -1; }
                else if (gameSeen && gameGoneAt < 0) gameGoneAt = now;
                string gameStatus = game != null ? $"Game: RUNNING ({game})"
                                  : gameSeen ? "Game: CLOSED - finishing..."
                                  : "Game: not detected yet (start Rennsport)";

                var rows = flows.Values
                    .Where(f => f.PeakHz >= minRate)
                    .OrderByDescending(f => f.RateHz).ThenByDescending(f => f.Count)
                    .Take(20)
                    .ToList();

                if (interactive) Console.SetCursorPosition(0, 0);
                WriteLine($"{(record ? "RECORDING" : "SCAN")}  {TimeSpan.FromSeconds(now):mm\\:ss}   {gameStatus}", ConsoleColor.Cyan);
                WriteLine(record ? $"Folder: {Path.GetFullPath(outDir!)}" : "Green rows (>= 20 Hz) = telemetry candidates");
                WriteLine("");
                WriteLine($"{"Source",-22} {"Destination",-22} {"Sizes",-16} {"Hz",7} {"Peak",6} {"Packets",9}");
                WriteLine(new string('-', 88));
                foreach (var f in rows)
                {
                    var sizes = string.Join(",", f.Sizes.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key));
                    WriteLine($"{f.Src + ":" + f.SrcPort,-22} {f.Dst + ":" + f.DstPort,-22} {sizes,-16} {f.RateHz,7:F1} {f.PeakHz,6:F0} {f.Count,9}",
                        f.PeakHz >= 20 ? ConsoleColor.Green : ConsoleColor.Gray);
                }
                for (int i = rows.Count; i < 20; i++) WriteLine("");
                WriteLine("");
                WriteLine(record ? "Q / Ctrl+C = stop now. Closing the game also stops recording." : "Q = quit", ConsoleColor.DarkGray);

                if (seconds > 0 && now >= seconds) { stopReason = "time limit"; break; }
                if (gameGoneAt > 0 && now - gameGoneAt >= 3) { stopReason = $"game closed ({gameName})"; break; }
                if (interactive && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q) break;
            }
        }
        finally
        {
            cts.Cancel();
            foreach (var s in sockets) s.Dispose();
            Thread.Sleep(200);
            if (interactive) { Console.CursorVisible = true; Console.Clear(); }
        }

        Console.WriteLine($"Stopped: {stopReason}");
        if (record) Finish(flows, outDir!, sw.Elapsed.TotalSeconds);
        return 0;
    }

    private static void Finish(ConcurrentDictionary<string, Flow> flows, string outDir, double totalSec)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"rsudp record summary  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  duration {totalSec:F0}s");
        sb.AppendLine();
        sb.AppendLine($"{"Source",-22} {"Destination",-22} {"Packets",9} {"Peak Hz",8}  Sizes (bytes x count)          File");

        foreach (var f in flows.Values.OrderByDescending(f => f.Count))
        {
            lock (f.Lock) { f.Writer?.Dispose(); f.Writer = null; }
            bool keep = f.Count >= 200 && f.PeakHz >= 10;
            string file = "";
            if (f.FilePath != null)
            {
                if (keep) file = Path.GetFileName(f.FilePath);
                else { try { File.Delete(f.FilePath); } catch { } }
            }
            var sizes = string.Join(" ", f.Sizes.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key}x{kv.Value}"));
            sb.AppendLine($"{f.Src + ":" + f.SrcPort,-22} {f.Dst + ":" + f.DstPort,-22} {f.Count,9} {f.PeakHz,8:F0}  {sizes,-30} {file}");
        }
        var summaryPath = Path.Combine(outDir, "summary.txt");
        File.WriteAllText(summaryPath, sb.ToString());

        Console.WriteLine();
        Console.WriteLine(sb.ToString());
        Console.WriteLine($"Saved in: {Path.GetFullPath(outDir)}");
        Console.WriteLine("Small / slow flows were deleted. Next step:  rsudp analyze <file.bin>");
    }

    internal static string? FindGame()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                    if (p.ProcessName.Contains("rennsport", StringComparison.OrdinalIgnoreCase))
                        return p.ProcessName;
            }
        }
        catch { }
        return null;
    }

    private static void WriteLine(string s, ConsoleColor color = ConsoleColor.Gray)
    {
        int w = Console.IsOutputRedirected ? 0 : Math.Max(Console.WindowWidth - 1, 20);
        Console.ForegroundColor = color;
        Console.WriteLine(w > 0 ? (s.Length > w ? s[..w] : s.PadRight(w)) : s);
        Console.ResetColor();
    }

    private static void CaptureLoop(Socket s, int sockIndex, ConcurrentDictionary<string, Flow> flows, Stopwatch sw, string? outDir, CancellationToken ct)
    {
        var buf = new byte[65535];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = s.Receive(buf); }
            catch { return; }
            if (ct.IsCancellationRequested) return;
            if (n < 28) continue;
            if ((buf[0] >> 4) != 4) continue;           // IPv4 only
            int ihl = (buf[0] & 0x0F) * 4;
            if (buf[9] != 17 || n < ihl + 8) continue;  // 17 = UDP

            int sp = (buf[ihl] << 8) | buf[ihl + 1];
            int dp = (buf[ihl + 2] << 8) | buf[ihl + 3];
            if (IgnoredPorts.Contains(sp) || IgnoredPorts.Contains(dp)) continue;
            int udpLen = (buf[ihl + 4] << 8) | buf[ihl + 5];
            int payloadLen = Math.Min(udpLen - 8, n - ihl - 8);
            if (payloadLen < 0) continue;
            long tUs = sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

            var src = $"{buf[12]}.{buf[13]}.{buf[14]}.{buf[15]}";
            var dst = $"{buf[16]}.{buf[17]}.{buf[18]}.{buf[19]}";
            var key = $"{src}:{sp}>{dst}:{dp}";
            var f = flows.GetOrAdd(key, _ => new Flow { Src = src, Dst = dst, SrcPort = sp, DstPort = dp });
            var payload = new ReadOnlySpan<byte>(buf, ihl + 8, payloadLen);

            lock (f.Lock)
            {
                // the same packet can show up on two interfaces -> drop the duplicate
                int hash = Hash(payload);
                if (f.LastSocket != -1 && f.LastSocket != sockIndex && hash == f.LastHash && tUs - f.LastTimeUs < 3000)
                    continue;
                f.LastSocket = sockIndex; f.LastHash = hash; f.LastTimeUs = tUs;

                f.Count++;
                Interlocked.Increment(ref f.WindowCount);
                f.Sizes.AddOrUpdate(payloadLen, 1, (_, v) => v + 1);

                if (outDir != null)
                {
                    if (f.Writer == null && f.FilePath == null)
                    {
                        f.FilePath = Path.Combine(outDir, $"udp_{src}_{sp}_to_{dst}_{dp}.bin");
                        f.Writer = new RecordingWriter(f.FilePath);
                    }
                    f.Writer?.WritePacket(tUs, payload);
                }
            }
        }
    }

    private static int Hash(ReadOnlySpan<byte> d)
    {
        var h = new HashCode();
        h.AddBytes(d);
        return h.ToHashCode();
    }

    private static bool IsAdmin()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
