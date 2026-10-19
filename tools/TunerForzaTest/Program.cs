// Test tool for the Simucube Tuner feed, no game or SimHub needed.
// The exe is named Rennsport.exe, so a Tuner with the patched game_db.json detects it as the running game.
//
//   Rennsport --in api.bin [--port 9877] [--loop]   stream an "rsudp api" recording as Forza packets
//                                                   (the plugin's own ForzaBridge, ~60 Hz)
//   Rennsport --idle                                only exist as a process; use with SimHub + "rsudp replay"
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using RennsportPlugin;

string input = null;
int port = ForzaBridge.DefaultPort;
bool loop = false, idle = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--in": input = args[++i]; break;
        case "--port": port = int.Parse(args[++i]); break;
        case "--loop": loop = true; break;
        case "--idle": idle = true; break;
    }
}
Console.WriteLine($"Fake Rennsport.exe pid={Environment.ProcessId}");
if (idle || input == null) { Console.WriteLine("idle, Ctrl+C to quit"); Thread.Sleep(Timeout.Infinite); return; }

using var bridge = new ForzaBridge(port);
CarLiveInfo car = null;
long physicsCount = 0;

do
{
    using var f = File.OpenRead(input);
    var magic = new byte[8];
    f.ReadExactly(magic);
    if (Encoding.ASCII.GetString(magic) != "RSUDP001") throw new InvalidDataException("not an RSUDP001 recording");

    var hdr = new byte[12];
    var buf = new byte[64 * 1024];
    var clock = Stopwatch.StartNew();
    double playT = 0, prevRecT = -1, lastReport = 0;
    while (f.Read(hdr, 0, 12) == 12)
    {
        long tUs = BinaryPrimitives.ReadInt64LittleEndian(hdr);
        int len = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(8));
        if (len < 0) { f.Seek(4, SeekOrigin.Current); continue; }   // marker record: int32 id, no data
        if (buf.Length < len) buf = new byte[len];
        f.ReadExactly(buf, 0, len);

        // pace by the recording's timestamps, squeezing long gaps (menus) to 1 s
        double recT = tUs / 1e6;
        if (prevRecT >= 0) playT += Math.Min(recT - prevRecT, 1.0);
        prevRecT = recT;

        var type = MessageParser.PeekMessageType(buf, len);
        if (type == "CarLiveUpdate")
        {
            car = MessageParser.ParseCarLive((Newtonsoft.Json.Linq.JObject)MessageParser.ParseObject(buf, len)["data"]);
            continue;
        }
        if (type != "PhysicsUpdate") continue;

        var wait = playT - clock.Elapsed.TotalSeconds;
        if (wait > 0.001) Thread.Sleep(TimeSpan.FromSeconds(wait));

        // the recording has 400 Hz physics; parse every 4th like the plugin (~100 Hz), ForzaBridge sends ~60 Hz
        if (++physicsCount % 4 != 0) continue;
        var p = MessageParser.ParsePhysics(buf, len);
        bridge.Send(p, car);

        if (playT - lastReport > 5)
        {
            lastReport = playT;
            Console.WriteLine($"t={playT,7:F1}s  rpm={p.EngineRpm,5:F0}  speed={p.Speed * 3.6,4:F0} km/h  gear={p.GearIndex}");
        }
    }
} while (loop);
