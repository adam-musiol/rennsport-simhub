using System;
using GameReaderCommon;
using SimHub.Plugins;

namespace RennsportPlugin
{
    /// <summary>Settings saved by SimHub (JSON.net). Empty values = take them from the game's BroadcastingSocket.json.</summary>
    public class RennsportSettings
    {
        public string Host = "127.0.0.1";
        public int Port = 0;            // 0 = from game config (default 559)
        public string Password = null;  // null = from game config
        public bool FeedGameData = true; // send telemetry to SimHub's "Rennsport (API)" external sim (stock dashes / LEDs)
        public int ExternalSimPort = ExternalSimBridge.DefaultPort;
        public bool FeedSimucube = true; // Forza-format packets for Simucube Tuner's Telemetry FFB (needs patched game_db.json)
        public int SimucubePort = ForzaBridge.DefaultPort;
    }

    /// <summary>
    /// SimHub entry point. Wires the API client to three outputs:
    ///  - RennsportPlugin.* properties (delegates over the latest snapshot),
    ///  - ExternalSimBridge: SimHub's "Rennsport (API)" game, i.e. normalized GameData for stock dashboards / LEDs / ShakeIt,
    ///  - ForzaBridge: Forza-format packets for Simucube Tuner's Telemetry FFB.
    /// </summary>
    [PluginDescription("Live telemetry from Rennsport via its UDP API socket")]
    [PluginAuthor("Adam Musiol")]
    [PluginName("Rennsport telemetry")]
    public class RennsportPlugin : IPlugin, IDataPlugin
    {
        private RennsportSettings _settings;
        private RennsportApiClient _client;
        private ExternalSimBridge _bridge;
        private ForzaBridge _forza;
        private int _bridgeErrors, _forzaErrors;

        public PluginManager PluginManager { get; set; }

        public void Init(PluginManager pluginManager)
        {
            _settings = this.ReadCommonSettings("GeneralSettings", () => new RennsportSettings());

            bool hasCfg = RennsportApiClient.TryReadGameConfig(out int cfgPort, out string cfgPassword);
            int port = _settings.Port > 0 ? _settings.Port : cfgPort;
            string password = _settings.Password ?? cfgPassword;
            Log(hasCfg ? $"Game config: {RennsportApiClient.GameConfigPath}" : "Game config not found, using defaults");

            _client = new RennsportApiClient(_settings.Host, port, password, Log);
            if (_settings.FeedGameData)
            {
                try
                {
                    _bridge = new ExternalSimBridge(_settings.ExternalSimPort, Log);
                }
                catch (Exception ex) { Log("External sim bridge disabled: " + ex.Message); }
            }
            if (_settings.FeedSimucube)
            {
                _forza = new ForzaBridge(_settings.SimucubePort);
                Log($"Simucube Tuner feed (Forza format) -> 127.0.0.1:{_settings.SimucubePort}");
            }
            _client.PhysicsParsed = OnPhysics;
            RegisterProperties();
            _client.Start();
        }

        // receive thread, ~100 Hz; each bridge throttles itself
        private void OnPhysics(PhysicsSnapshot p)
        {
            var car = _client.GetCar(p.DriverNumber);
            if (_bridge != null)
            {
                try { _bridge.Send(p, car, _client.Stage); }
                catch (Exception ex) { if (++_bridgeErrors <= 3) Log("External sim send failed: " + ex.Message); }
            }
            if (_forza != null)
            {
                try { _forza.Send(p, car); }
                catch (Exception ex) { if (++_forzaErrors <= 3) Log("Simucube feed send failed: " + ex.Message); }
            }
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // Custom properties are delegates over the client's latest snapshot; normalized GameData
            // comes from SimHub's "Rennsport (API)" external sim fed by ExternalSimBridge.
        }

        public void End(PluginManager pluginManager)
        {
            _client?.Dispose();
            _bridge?.Dispose();
            _forza?.Dispose();
            this.SaveCommonSettings("GeneralSettings", _settings);
        }

        private static void Log(string message) => SimHub.Logging.Current.Info("[Rennsport] " + message);

        // ---------------- properties ----------------

        private PhysicsSnapshot P => _client.Physics ?? Empty;
        private static readonly PhysicsSnapshot Empty = new PhysicsSnapshot();

        /// <summary>CarLiveUpdate of the player car (carNumber == PhysicsUpdate.driverNumber).</summary>
        private CarLiveInfo PlayerCar
        {
            get
            {
                var p = _client.Physics;
                return p == null ? null : _client.GetCar(p.DriverNumber);
            }
        }

        private void RegisterProperties()
        {
            // status
            this.AttachDelegate("Connected", () => _client.IsConnected);
            this.AttachDelegate("Driving", () => _client.IsReceivingPhysics);
            this.AttachDelegate("RegisterResponseCode", () => _client.LastResponseCode);
            this.AttachDelegate("PacketCount", () => _client.PacketCount);
            this.AttachDelegate("PhysicsPacketCount", () => _client.PhysicsPacketCount);
            this.AttachDelegate("ParseErrorCount", () => _client.ParseErrorCount);

            // car / driver inputs
            this.AttachDelegate("DriverNumber", () => P.DriverNumber);
            this.AttachDelegate("SpeedKmh", () => P.Speed * 3.6);
            this.AttachDelegate("SpeedMs", () => P.Speed);
            this.AttachDelegate("Rpm", () => P.EngineRpm);
            this.AttachDelegate("MaxRpm", () => PlayerCar?.MaxEngineRpm ?? 0);
            this.AttachDelegate("ShiftRpm", () => PlayerCar?.OptimalShiftRpm ?? 0);
            this.AttachDelegate("GearIndex", () => P.GearIndex);         // raw, mapping not verified
            this.AttachDelegate("CarLiveGear", () => PlayerCar?.Gear ?? 0); // raw, for comparison
            this.AttachDelegate("Throttle", () => P.Throttle * 100);     // 0..100 like SimHub's own
            this.AttachDelegate("Brake", () => P.Brake * 100);
            this.AttachDelegate("Clutch", () => P.Clutch * 100);
            this.AttachDelegate("SteeringAngle", () => P.SteeringWheelAngle);
            this.AttachDelegate("SteeringShaftTorque", () => P.SteeringShaftTorque);
            this.AttachDelegate("ForceFeedback", () => P.SteeringForceFeedback);

            // motion
            this.AttachDelegate("LatAccel", () => P.LatAccel);
            this.AttachDelegate("LonAccel", () => P.LonAccel);
            this.AttachDelegate("VertAccel", () => P.VertAccel);
            this.AttachDelegate("Pitch", () => P.Pitch);
            this.AttachDelegate("Roll", () => P.Roll);
            this.AttachDelegate("Yaw", () => P.Yaw);
            this.AttachDelegate("PitchRate", () => P.PitchRate);
            this.AttachDelegate("RollRate", () => P.RollRate);
            this.AttachDelegate("YawRate", () => P.YawRate);

            // lap
            this.AttachDelegate("LapNumber", () => P.LapNumber);
            this.AttachDelegate("LapDistance", () => P.LapDistance);
            this.AttachDelegate("LapDistancePct", () => P.LapDistancePct);
            this.AttachDelegate("LapValid", () => P.LapValid);
            this.AttachDelegate("BestLapTime", () => P.BestLapTime);
            this.AttachDelegate("TrackLength", () => P.TrackLengthMeters);
            this.AttachDelegate("Paused", () => P.IsGamePaused);
            this.AttachDelegate("UserInControl", () => P.IsUserInControl);

            // engine / fuel
            this.AttachDelegate("WaterTemp", () => P.CoolantTemp);
            this.AttachDelegate("OilTemp", () => P.OilTemp);
            this.AttachDelegate("OilPressure", () => P.OilPressure);
            this.AttachDelegate("TurboRpm", () => P.TurboRpm);
            this.AttachDelegate("BoostPressure", () => P.BoostPressure);
            this.AttachDelegate("Fuel", () => P.FuelLevel);
            this.AttachDelegate("FuelPressure", () => P.FuelPressure);
            this.AttachDelegate("AirTemp", () => P.AirTemp);

            // aids
            this.AttachDelegate("AbsActive", () => P.AbsActive);
            this.AttachDelegate("AbsLevel", () => P.AbsLevel);
            this.AttachDelegate("TcActive", () => P.TcActive);
            this.AttachDelegate("TcLevel", () => P.TcLevel);
            this.AttachDelegate("BrakeBias", () => P.BrakeBias);
            this.AttachDelegate("PitLimiter", () => P.PitLimiter);
            this.AttachDelegate("Headlights", () => P.Headlights);

            // aero
            this.AttachDelegate("Drag", () => P.Drag);
            this.AttachDelegate("FrontDownforce", () => P.FrontDownforce);
            this.AttachDelegate("RearDownforce", () => P.RearDownforce);

            // wheels
            AttachWheel("FL", () => P.FL);
            AttachWheel("FR", () => P.FR);
            AttachWheel("RL", () => P.RL);
            AttachWheel("RR", () => P.RR);

            // race (CarLiveUpdate of the player car)
            this.AttachDelegate("Position", () => PlayerCar?.Position ?? 0);
            this.AttachDelegate("CompletedLaps", () => PlayerCar?.CompletedLaps ?? 0);
            this.AttachDelegate("CurrentLapTime", () => PlayerCar?.CurrentLapTime ?? 0);
            this.AttachDelegate("LastLapTime", () => PlayerCar?.LastLapTime ?? 0);
            this.AttachDelegate("CarBestLapTime", () => PlayerCar?.BestLapTime ?? 0);
            this.AttachDelegate("Flag", () => PlayerCar?.CurrentFlag);
            this.AttachDelegate("InPitStatus", () => PlayerCar?.InPitStatus);
            this.AttachDelegate("CarLeft", () => PlayerCar?.CarLeft ?? -1);
            this.AttachDelegate("CarRight", () => PlayerCar?.CarRight ?? -1);

            // session
            this.AttachDelegate("SessionTimeLeft", () => _client.Stage.TimeLeft);
            this.AttachDelegate("SessionLapsLeft", () => _client.Stage.LapsLeft);
            this.AttachDelegate("SessionName", () => _client.Stage.StageName);
            this.AttachDelegate("ContestName", () => _client.Stage.ContestName);
            this.AttachDelegate("Track", () => _client.Stage.Track);
        }

        private void AttachWheel(string w, Func<WheelSnapshot> wheel)
        {
            this.AttachDelegate($"Tyre{w}.Pressure", () => wheel().Pressure);
            this.AttachDelegate($"Tyre{w}.ColdPressure", () => wheel().ColdPressure);
            this.AttachDelegate($"Tyre{w}.TempSurface", () => wheel().TempSurfaceAvg);
            this.AttachDelegate($"Tyre{w}.TempSurfaceL", () => wheel().TempSurfaceL);
            this.AttachDelegate($"Tyre{w}.TempSurfaceM", () => wheel().TempSurfaceM);
            this.AttachDelegate($"Tyre{w}.TempSurfaceR", () => wheel().TempSurfaceR);
            this.AttachDelegate($"Tyre{w}.TempInner", () => wheel().TempInnerAvg);
            this.AttachDelegate($"Tyre{w}.TempCarcass", () => wheel().TempCarcass);
            this.AttachDelegate($"Tyre{w}.Wear", () => wheel().Wear);
            this.AttachDelegate($"Tyre{w}.BrakeTemp", () => wheel().BrakeTemp);
            this.AttachDelegate($"Tyre{w}.SlipAngle", () => wheel().SlipAngle);
            this.AttachDelegate($"Tyre{w}.SlipRatio", () => wheel().SlipRatio);
            this.AttachDelegate($"Tyre{w}.Load", () => wheel().TireLoad);
            this.AttachDelegate($"Tyre{w}.SuspensionForce", () => wheel().SuspensionForce);
            this.AttachDelegate($"Tyre{w}.Compression", () => wheel().Compression);
            this.AttachDelegate($"Tyre{w}.DamperVelocity", () => wheel().DamperVelocity);
            this.AttachDelegate($"Tyre{w}.RideHeight", () => wheel().RideHeight);
            this.AttachDelegate($"Tyre{w}.Rpm", () => wheel().Rpm);
        }
    }
}
