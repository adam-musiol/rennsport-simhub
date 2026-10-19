using System.Text;

namespace RennsportUdpSniffer;

/// <summary>
/// Simple binary recording format:
///   header: "RSUDP001" (8 bytes ASCII)
///   records: int64 timeMicroseconds, int32 length, byte[length]
///            length == -1 means MARKER, followed by int32 markerId (no data)
/// Little-endian. Easy to read from Python / C#.
/// </summary>
internal sealed class RecordingWriter : IDisposable
{
    public const string Magic = "RSUDP001";
    private readonly BinaryWriter _w;
    private readonly object _lock = new();
    public long Packets { get; private set; }

    public RecordingWriter(string path)
    {
        _w = new BinaryWriter(new BufferedStream(File.Create(path), 1 << 20));
        _w.Write(Encoding.ASCII.GetBytes(Magic));
    }

    public void WritePacket(long timeUs, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            _w.Write(timeUs);
            _w.Write(data.Length);
            _w.Write(data);
            Packets++;
        }
    }

    public void WriteMarker(long timeUs, int id)
    {
        lock (_lock)
        {
            _w.Write(timeUs);
            _w.Write(-1);
            _w.Write(id);
            _w.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock) { _w.Flush(); _w.Dispose(); }
    }
}

internal readonly record struct RecPacket(long TimeUs, byte[] Data);
internal readonly record struct RecMarker(long TimeUs, int Id);

internal static class RecordingReader
{
    public static (List<RecPacket> packets, List<RecMarker> markers) Read(string path)
    {
        var packets = new List<RecPacket>();
        var markers = new List<RecMarker>();
        using var r = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20));
        var magic = Encoding.ASCII.GetString(r.ReadBytes(8));
        if (magic != RecordingWriter.Magic) throw new InvalidDataException("Not an rsudp recording (bad header).");
        while (true)
        {
            long t;
            try { t = r.ReadInt64(); } catch (EndOfStreamException) { break; }
            int len;
            try { len = r.ReadInt32(); } catch (EndOfStreamException) { break; }
            if (len == -1)
            {
                try { markers.Add(new RecMarker(t, r.ReadInt32())); } catch (EndOfStreamException) { break; }
                continue;
            }
            if (len < 0 || len > 65535) throw new InvalidDataException($"Corrupt record length {len}.");
            var data = r.ReadBytes(len);
            if (data.Length < len) break; // truncated tail (app was killed) - ignore
            packets.Add(new RecPacket(t, data));
        }
        return (packets, markers);
    }
}
