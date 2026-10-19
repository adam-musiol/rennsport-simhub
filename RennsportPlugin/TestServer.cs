using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RennsportPlugin
{
    /// <summary>
    /// Fake Rennsport API for the test mode: plays the built-in Monza sample (TestData/monza.rsudp.gz, made with
    /// "rsudp sample") in a loop on 127.0.0.1:&lt;free port&gt;. Speaks the game's protocol: RegisterClient ->
    /// RegisterClientResponse (0 Success first, then 1 AlreadyRegistered), RequestUpdate -> last message of that type.
    /// </summary>
    public sealed class TestServer : IDisposable
    {
        private const int SIO_UDP_CONNRESET = -1744830452;
        private const string ResourceName = "RennsportPlugin.TestData.monza.rsudp.gz";
        private const string Magic = "RSUDP001";

        private readonly Action<string> _log;
        private readonly List<KeyValuePair<long, byte[]>> _packets;   // time in µs -> message
        private readonly ConcurrentDictionary<string, byte[]> _lastByType = new ConcurrentDictionary<string, byte[]>();
        private readonly ConcurrentDictionary<IPEndPoint, bool> _clients = new ConcurrentDictionary<IPEndPoint, bool>();
        private UdpClient _udp;
        private Thread _rxThread, _playThread;
        private volatile bool _running;

        public TestServer(Action<string> log = null)
        {
            _log = log ?? (_ => { });
            _packets = LoadSample();
            foreach (var p in _packets)
            {
                if (p.Key > 0) break;
                var type = MessageParser.PeekMessageType(p.Value, p.Value.Length);
                if (type != null) _lastByType[type] = p.Value;   // static messages at t = 0 answer RequestUpdate
            }
        }

        public int Port { get; private set; }

        /// <summary>Length of one loop of the sample.</summary>
        public TimeSpan Duration => TimeSpan.FromTicks(_packets.Count == 0 ? 0 : _packets[_packets.Count - 1].Key * 10);

        public void Start()
        {
            if (_running) return;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _udp.Client.SendBufferSize = 1 << 20;
            // a client that went away must not kill Receive with ICMP "port unreachable"
            try { _udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { }
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint).Port;

            _running = true;
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "Rennsport test server receive" };
            _playThread = new Thread(PlayLoop) { IsBackground = true, Name = "Rennsport test server playback" };
            _rxThread.Start();
            _playThread.Start();
            _log($"Test mode: built-in Monza sample ({_packets.Count} messages, {Duration.TotalSeconds:0} s loop) on 127.0.0.1:{Port}");
        }

        public void Dispose()
        {
            if (!_running) return;
            _running = false;
            try { _udp?.Close(); } catch { }
            _rxThread?.Join(1000);
            _playThread?.Join(1000);
            _udp = null;
        }

        private static List<KeyValuePair<long, byte[]>> LoadSample()
        {
            var packets = new List<KeyValuePair<long, byte[]>>();
            using (var res = typeof(TestServer).Assembly.GetManifestResourceStream(ResourceName)
                             ?? throw new InvalidOperationException("Test sample not found in the plugin DLL"))
            using (var gz = new GZipStream(res, CompressionMode.Decompress))
            using (var r = new BinaryReader(gz))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(8)) != Magic) throw new InvalidDataException("Bad test sample header");
                while (true)
                {
                    long t;
                    try { t = r.ReadInt64(); } catch (EndOfStreamException) { break; }
                    int len = r.ReadInt32();
                    packets.Add(new KeyValuePair<long, byte[]>(t, r.ReadBytes(len)));
                }
            }
            return packets;
        }

        private void ReceiveLoop()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data;
                try { data = _udp.Receive(ref ep); }
                catch (ObjectDisposedException) { return; }
                catch (Exception) { if (!_running) return; Thread.Sleep(50); continue; }

                JObject msg;
                try { msg = JObject.Parse(Encoding.UTF8.GetString(data)); }
                catch { continue; }
                var from = new IPEndPoint(ep.Address, ep.Port);
                switch ((string)msg["messageType"])
                {
                    case "RegisterClient":
                        bool known = !_clients.TryAdd(from, true);
                        Send(Response(known ? 1 : 0, known ? "AlreadyRegistered" : "Success"), from);
                        break;
                    case "RequestUpdate":
                        var requested = (string)msg["data"]?["requestedMessage"];
                        if (requested != null && _lastByType.TryGetValue(requested, out var last)) Send(last, from);
                        break;
                }
            }
        }

        private void PlayLoop()
        {
            var clock = Stopwatch.StartNew();
            while (_running)
            {
                double loopStart = clock.Elapsed.TotalSeconds;
                foreach (var p in _packets)
                {
                    double due = loopStart + p.Key / 1e6;
                    double wait;
                    while (_running && (wait = due - clock.Elapsed.TotalSeconds) > 0)
                        Thread.Sleep(Math.Max(1, (int)(wait * 1000) - 1));
                    if (!_running) return;

                    var type = MessageParser.PeekMessageType(p.Value, p.Value.Length);
                    if (type != null && type != "PhysicsUpdate") _lastByType[type] = p.Value;
                    foreach (var client in _clients.Keys) Send(p.Value, client);
                }
            }
        }

        private void Send(byte[] data, IPEndPoint to)
        {
            try { _udp?.Send(data, data.Length, to); } catch { }
        }

        private static byte[] Response(int code, string codeString) => Encoding.UTF8.GetBytes(
            $"{{\"messageType\":\"RegisterClientResponse\",\"responseCode\":{code},\"responseCodeString\":\"{codeString}\",\"time\":0,\"version\":1,\"data\":{{}}}}");
    }
}
