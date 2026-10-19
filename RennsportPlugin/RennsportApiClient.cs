using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RennsportPlugin
{
    /// <summary>
    /// Client of the Rennsport UDP API socket (protocol: docs/PROTOCOL.md).
    /// Registers with the game, re-registers every few seconds (keep-alive, survives game restarts)
    /// and keeps only the latest state. Independent of SimHub, so it can be tested on its own.
    ///
    /// PhysicsUpdate arrives at 400 Hz (~8 KB each); parsing all of them is wasted work, so the
    /// receive thread parses at most one per <see cref="PhysicsParseInterval"/> and drops the rest.
    /// </summary>
    public sealed class RennsportApiClient : IDisposable
    {
        private const int SIO_UDP_CONNRESET = -1744830452;

        private readonly IPEndPoint _server;
        private readonly string _password;
        private readonly Action<string> _log;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ConcurrentDictionary<int, CarLiveInfo> _cars = new ConcurrentDictionary<int, CarLiveInfo>();

        private UdpClient _udp;
        private Thread _rxThread;
        private Timer _keepAlive;
        private volatile bool _running;

        private PhysicsSnapshot _physics;
        private StageInfo _stage = new StageInfo();
        private double _lastPacketAt = -1, _lastPhysicsAt = -1, _lastPhysicsParseAt = -1;
        private long _packets, _physicsPackets, _parseErrors;
        private int _lastResponseCode = -1;
        private bool _loggedFirstPhysics;

        public RennsportApiClient(string host, int port, string password, Action<string> log = null)
        {
            _server = new IPEndPoint(IPAddress.Parse(host), port);
            _password = password ?? "";
            _log = log ?? (_ => { });
        }

        public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan PhysicsParseInterval { get; set; } = TimeSpan.FromMilliseconds(10);

        public IPEndPoint Server => _server;

        /// <summary>Called on the receive thread for every parsed PhysicsUpdate (at most once per PhysicsParseInterval).</summary>
        public Action<PhysicsSnapshot> PhysicsParsed { get; set; }

        /// <summary>Latest parsed PhysicsUpdate, null until the first one arrives.</summary>
        public PhysicsSnapshot Physics => Volatile.Read(ref _physics);
        public StageInfo Stage => Volatile.Read(ref _stage);
        public CarLiveInfo GetCar(int carNumber) => _cars.TryGetValue(carNumber, out var c) ? c : null;

        public long PacketCount => Interlocked.Read(ref _packets);
        public long PhysicsPacketCount => Interlocked.Read(ref _physicsPackets);
        public long ParseErrorCount => Interlocked.Read(ref _parseErrors);
        public int LastResponseCode => Volatile.Read(ref _lastResponseCode);

        /// <summary>Seconds since any message / since the last PhysicsUpdate (infinity if never).</summary>
        public double SecondsSinceLastPacket => Since(Volatile.Read(ref _lastPacketAt));
        public double SecondsSinceLastPhysics => Since(Volatile.Read(ref _lastPhysicsAt));

        /// <summary>The game answers (any message within 3 s).</summary>
        public bool IsConnected => SecondsSinceLastPacket < 3;
        /// <summary>Player is on track: PhysicsUpdate within 0.5 s (game sends it only while driving).</summary>
        public bool IsReceivingPhysics => SecondsSinceLastPhysics < 0.5;

        private double Now => _clock.Elapsed.TotalSeconds;
        private double Since(double t) => t < 0 ? double.PositiveInfinity : Now - t;

        /// <summary>Opens the socket, starts the receive thread and the RegisterClient keep-alive timer.</summary>
        public void Start()
        {
            if (_running) return;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            _udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
            // Without this an ICMP "port unreachable" (game not running) makes Receive throw on Windows
            try { _udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { }

            _running = true;
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "Rennsport UDP receive" };
            _rxThread.Start();
            _keepAlive = new Timer(_ => Register(), null, TimeSpan.Zero, KeepAliveInterval);
            _log($"UDP client on port {((IPEndPoint)_udp.Client.LocalEndPoint).Port} -> game API {_server}");
        }

        /// <summary>Stops the keep-alive and the receive thread. The game drops the client by itself after a timeout.</summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _keepAlive?.Dispose();
            _keepAlive = null;
            try { _udp?.Close(); } catch { }
            _rxThread?.Join(1000);
            _rxThread = null;
            _udp = null;
        }

        public void Dispose() => Stop();

        // ---------------- sending ----------------

        private void Register() =>
            Send(new JObject { ["messageType"] = "RegisterClient", ["data"] = new JObject { ["accessPassword"] = _password } });

        /// <summary>Asks the game to resend a message type now, e.g. "EntryListUpdate" or "GameplayStageInfoUpdate".</summary>
        public void RequestUpdate(string requestedMessage) =>
            Send(new JObject { ["messageType"] = "RequestUpdate", ["data"] = new JObject { ["requestedMessage"] = requestedMessage } });

        private void Send(JObject msg)
        {
            var udp = _udp;
            if (!_running || udp == null) return;
            var bytes = Encoding.UTF8.GetBytes(msg.ToString(Newtonsoft.Json.Formatting.None));
            try { udp.Send(bytes, bytes.Length, _server); } catch { }
        }

        // ---------------- receiving ----------------

        private void ReceiveLoop()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data;
                try { data = _udp.Receive(ref ep); }
                catch (ObjectDisposedException) { return; }
                catch (Exception) { if (!_running) return; Thread.Sleep(50); continue; }

                try { Handle(data); }
                catch (Exception ex)
                {
                    if (Interlocked.Increment(ref _parseErrors) <= 5) _log($"Cannot parse message ({data.Length} B): {ex.Message}");
                }
            }
        }

        // Runs on the receive thread. Only PhysicsUpdate is hot (400 Hz); everything else is a few messages per second.
        private void Handle(byte[] data)
        {
            double now = Now;
            Interlocked.Increment(ref _packets);
            Volatile.Write(ref _lastPacketAt, now);

            var type = MessageParser.PeekMessageType(data, data.Length);
            if (type == "PhysicsUpdate")
            {
                Interlocked.Increment(ref _physicsPackets);
                Volatile.Write(ref _lastPhysicsAt, now);
                if (now - _lastPhysicsParseAt < PhysicsParseInterval.TotalSeconds) return;
                _lastPhysicsParseAt = now;
                var physics = MessageParser.ParsePhysics(data, data.Length);
                Volatile.Write(ref _physics, physics);
                if (!_loggedFirstPhysics) { _loggedFirstPhysics = true; _log("Receiving PhysicsUpdate"); }
                PhysicsParsed?.Invoke(physics);
                return;
            }
            if (type == null) return;

            var msg = MessageParser.ParseObject(data, data.Length);
            var d = msg["data"] as JObject;
            switch (type)
            {
                case "RegisterClientResponse":
                    int code = (int?)msg["responseCode"] ?? -1;
                    int prev = Interlocked.Exchange(ref _lastResponseCode, code);
                    if (code != prev) _log($"RegisterClient -> {code} {(string)msg["responseCodeString"]}");
                    // 0 = Success means a fresh registration (first start or game restarted): ask for the static info
                    if (code == 0)
                    {
                        RequestUpdate("GameplayStageInfoUpdate");
                        RequestUpdate("EntryListUpdate");
                    }
                    break;
                case "CarLiveUpdate":
                    if (d != null) { var car = MessageParser.ParseCarLive(d); _cars[car.CarNumber] = car; }
                    break;
                case "GameplayStageLiveUpdate":
                    if (d != null) UpdateStage(s =>
                    {
                        s.TimeLeft = (double?)d["timeLeft"] ?? 0;
                        s.LapsLeft = (int?)d["lapsLeft"] ?? -1;
                        s.CurrentlyObservedCar = (int?)d["currentlyObservedCar"] ?? 0;
                    });
                    break;
                case "GameplayStageInfoUpdate":
                    if (d != null) UpdateStage(s =>
                    {
                        s.ContestName = (string)d["contestName"];
                        s.StageName = (string)d["gameplayStageName"];
                        s.Track = (string)d["track"];
                    });
                    _cars.Clear(); // new stage, drop cars from the previous one
                    break;
            }
        }

        // copy-on-write so readers on other threads always get a consistent StageInfo
        private void UpdateStage(Action<StageInfo> change)
        {
            var old = Volatile.Read(ref _stage);
            var s = new StageInfo
            {
                TimeLeft = old.TimeLeft, LapsLeft = old.LapsLeft, CurrentlyObservedCar = old.CurrentlyObservedCar,
                ContestName = old.ContestName, StageName = old.StageName, Track = old.Track,
            };
            change(s);
            Volatile.Write(ref _stage, s);
        }

        // ---------------- game config ----------------

        public static string GameConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Rennsport", "Saved", "Config", "BroadcastingSocket.json");

        /// <summary>Reads port and password from the game's BroadcastingSocket.json; returns false if missing/unreadable.</summary>
        public static bool TryReadGameConfig(out int port, out string password)
        {
            port = 559;
            password = "";
            try
            {
                if (!File.Exists(GameConfigPath)) return false;
                var cfg = JObject.Parse(File.ReadAllText(GameConfigPath));
                port = (int?)cfg["port"] ?? 559;
                password = (string)cfg["accessPassword"] ?? "";
                return true;
            }
            catch { return false; }
        }
    }
}
