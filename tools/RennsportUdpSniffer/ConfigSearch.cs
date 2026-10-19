namespace RennsportUdpSniffer;

/// <summary>
/// Looks for Rennsport config files and prints lines that mention UDP / telemetry / port / ip.
/// Helps to find which port and IP the game sends to.
/// </summary>
internal static class ConfigSearch
{
    private static readonly string[] Keywords = { "udp", "telemetry", "telemetria", "port", "ipaddress", "ip=", "address", "mmf", "dbox", "motec" };

    public static int Run(Options opts)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"),
        }.Where(Directory.Exists).Distinct().ToList();

        var gameDirs = new List<string>();
        foreach (var root in roots)
        {
            foreach (var dir in SafeDirs(root))
            {
                if (Path.GetFileName(dir).Contains("rennsport", StringComparison.OrdinalIgnoreCase)) gameDirs.Add(dir);
                foreach (var sub in SafeDirs(dir))
                    if (Path.GetFileName(sub).Contains("rennsport", StringComparison.OrdinalIgnoreCase)) gameDirs.Add(sub);
            }
        }

        if (gameDirs.Count == 0)
        {
            Console.WriteLine("No 'Rennsport' folder found in AppData / Documents / Saved Games.");
            return 0;
        }

        foreach (var gd in gameDirs.Distinct())
        {
            Console.WriteLine($"== {gd}");
            foreach (var file in SafeFiles(gd))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".ini" or ".json" or ".cfg" or ".txt" or ".xml" or ".config" or ".yaml" or ".yml" or ".sav" or "")) continue;
                long len;
                try { len = new FileInfo(file).Length; } catch { continue; }
                if (len > 2_000_000) continue;

                string[] lines;
                try { lines = File.ReadAllLines(file); } catch { continue; }
                bool header = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    var l = lines[i];
                    if (!Keywords.Any(k => l.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                    if (l.Any(c => c < 9)) continue; // binary
                    if (!header) { Console.WriteLine($"  {file}"); header = true; }
                    Console.WriteLine($"     {i + 1,5}: {(l.Length > 160 ? l[..160] + "..." : l)}");
                }
            }
        }
        Console.WriteLine();
        Console.WriteLine("Look for something like UdpPort / IpAddress / TelemetryPort above.");
        return 0;
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.GetDirectories(path); } catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string path)
    {
        try { return Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
        catch { return Array.Empty<string>(); }
    }
}
