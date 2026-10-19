using System.Globalization;

namespace RennsportUdpSniffer;

/// <summary>
/// rsudp: the console tool used to reverse-engineer Rennsport's telemetry (raw UDP capture and analysis)
/// and to develop the plugin without the game (record the API with "api", play it back with "replay").
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        var opts = Options.Parse(args.Skip(1).ToArray());
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "scan" => ScanMode.Run(opts, record: false),
                "record" => ScanMode.Run(opts, record: true),
                "api" => ApiMode.Run(opts),
                "replay" => ReplayMode.Run(opts),
                "listen" => ListenMode.Run(opts),
                "analyze" => AnalyzeMode.Run(opts),
                "config" => ConfigSearch.Run(opts),
                _ => Fail($"Unknown command '{args[0]}'. Run 'rsudp help'."),
            };
        }
        catch (Exception ex)
        {
            Console.ResetColor();
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static int Fail(string msg)
    {
        Console.Error.WriteLine(msg);
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        rsudp - Rennsport UDP telemetry sniffer

        RENNSPORT API - register with the game's UDP API socket and record what it sends:
          rsudp api [--out folder] [--host 127.0.0.1] [--port 559] [--password x] [--seconds N]
            Port/password are read from %LOCALAPPDATA%\Rennsport\Saved\Config\BroadcastingSocket.json.
            No Administrator needed. Stops by itself when you close the game.

        FAKE GAME - play an api.bin recording back as the game's API server (test the plugin without Rennsport):
          rsudp replay --in api1/api.bin [--port 559] [--host 127.0.0.1] [--speed 1.0] [--start sec] [--max-gap 1] [--loop] [--no-wait]
            Answers RegisterClient / RequestUpdate like the game. Waits for the first client unless --no-wait.
            Silent stretches (menu) longer than --max-gap seconds are shortened; --start skips into the recording.
            Port 559 is the game's port - close Rennsport first.

        record everything, hands-free (needs Administrator, Windows only):
          rsudp record [--out folder]
            Captures ALL UDP flows (any port) to separate .bin files.
            Stops by itself when you close Rennsport (or Ctrl+C / Q).

        find where the game sends data (needs Administrator, Windows only):
          rsudp scan [--seconds 30] [--min-rate 10]

        watch / record packets on a known port:
          rsudp listen --port 20777 [--bind 0.0.0.0] [--record session.bin] [--no-live]
            keys:  V = view (hex / float32 / int32 / int16)   A = alignment shift 0..3
                   Tab = next packet size   Up/Down/PgUp/PgDn = scroll
                   M = add marker (in recording)   P = pause   Q = quit

        analyze a recording (known-format check + field guessing):
          rsudp analyze session.bin [--size 324] [--csv fields.csv] [--top 80]

        EXTRA - search game config files for UDP/telemetry settings:
          rsudp config
        """);
    }
}

/// <summary>Minimal "--key value" / "--flag" / positional argument parser.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positional { get; } = new();

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                var key = a[2..];
                string? val = null;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    val = args[++i];
                o._named[key] = val;
            }
            else o.Positional.Add(a);
        }
        return o;
    }

    public bool Has(string key) => _named.ContainsKey(key);
    public string? Get(string key) => _named.TryGetValue(key, out var v) ? v : null;
    public int GetInt(string key, int def) => int.TryParse(Get(key), out var v) ? v : def;
}
