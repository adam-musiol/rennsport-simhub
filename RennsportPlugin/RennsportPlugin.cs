using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        public bool BlockBuiltInGame = true; // stop SimHub's own "Rennsport" game from taking over on auto-detect
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
    public class RennsportPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        private RennsportSettings _settings;
        private RennsportApiClient _client;
        private ExternalSimBridge _bridge;
        private string _bridgeError;
        private ForzaBridge _forza;
        private TestServer _test;
        private int _bridgeErrors, _forzaErrors;

        public PluginManager PluginManager { get; set; }

        // ---------------- status page (StatusControl) ----------------

        public string LeftMenuTitle => "Rennsport";
        public ImageSource PictureIcon => MenuIcon.Value;
        public Control GetWPFSettingsControl(PluginManager pluginManager) => new StatusControl(this);

        internal RennsportSettings Settings => _settings;
        internal RennsportApiClient Client => _client;
        internal bool GameDataFeedActive => _bridge != null;
        internal string GameDataFeedError => _bridgeError;
        internal string CurrentGameName => PluginManager?.GameName;
        internal bool SimHubGameRunning => PluginManager?.LastData?.GameRunning ?? false;
        internal bool TestRunning => _test != null;

        // Material Design "speedometer" icon, white on transparent 24x24
        private static readonly Lazy<ImageSource> MenuIcon = new Lazy<ImageSource>(() =>
        {
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
            drawing.Children.Add(new GeometryDrawing(Brushes.White, null, Geometry.Parse(
                "M12,16A3,3 0 0,1 9,13C9,11.88 9.61,10.9 10.5,10.39L20.21,4.77L14.68,14.35C14.18,15.33 13.17,16 12,16" +
                "M12,3C13.81,3 15.5,3.5 16.97,4.32L14.87,5.53C14,5.19 13,5 12,5A8,8 0 0,0 4,13C4,15.21 4.89,17.21 6.34,18.65" +
                "C6.73,19.04 6.73,19.67 6.34,20.06C5.95,20.45 5.32,20.45 4.93,20.07C3.12,18.26 2,15.76 2,13A10,10 0 0,1 12,3" +
                "M22,13C22,15.76 20.88,18.26 19.07,20.07C18.68,20.45 18.05,20.45 17.66,20.06C17.27,19.67 17.27,19.04 17.66,18.65" +
                "C19.11,17.2 20,15.21 20,13C20,12 19.81,11 19.46,10.1L20.67,8C21.5,9.5 22,11.18 22,13Z")));
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        });

        public void Init(PluginManager pluginManager)
        {
            _settings = this.ReadCommonSettings("GeneralSettings", () => new RennsportSettings());
            StartComponents();
            RegisterProperties();
        }

        /// <summary>Saves new settings (from the status page) and restarts the client and both bridges with them.</summary>
        internal void ApplySettings(RennsportSettings settings)
        {
            Log("Settings changed, restarting");
            StopComponents();
            _settings = settings;
            this.SaveCommonSettings("GeneralSettings", _settings);
            StartComponents();
        }

        /// <summary>Test mode: the client reads the built-in Monza sample from a local fake game API instead of the game.</summary>
        internal void StartTest()
        {
            if (_test != null) return;
            var test = new TestServer(Log);   // loads the sample first, so a failure leaves the plugin untouched
            StopComponents();
            _test = test;
            _test.Start();
            StartComponents();
        }

        internal void StopTest()
        {
            if (_test == null) return;
            StopComponents();
            _test.Dispose();
            _test = null;
            Log("Test mode stopped");
            StartComponents();
        }

        private void StartComponents()
        {
            RennsportApiClient client;
            if (_test != null)
                client = new RennsportApiClient("127.0.0.1", _test.Port, "", Log);
            else
            {
                bool hasCfg = RennsportApiClient.TryReadGameConfig(out int cfgPort, out string cfgPassword);
                int port = _settings.Port > 0 ? _settings.Port : cfgPort;
                string password = _settings.Password ?? cfgPassword;
                Log(hasCfg ? $"Game config: {RennsportApiClient.GameConfigPath}" : "Game config not found, using defaults");
                client = new RennsportApiClient(_settings.Host, port, password, Log);
            }

            Log(BuiltInGameBlocker.Apply(_settings.BlockBuiltInGame));

            _bridgeError = null;
            _bridgeErrors = _forzaErrors = 0;
            if (_settings.FeedGameData)
            {
                try
                {
                    _bridge = new ExternalSimBridge(_settings.ExternalSimPort, Log);
                }
                catch (Exception ex) { _bridgeError = ex.Message; Log("External sim bridge disabled: " + ex.Message); }
            }
            if (_settings.FeedSimucube)
            {
                _forza = new ForzaBridge(_settings.SimucubePort);
                Log($"Simucube Tuner feed (Forza format) -> 127.0.0.1:{_settings.SimucubePort}");
            }
            client.PhysicsParsed = p => OnPhysics(client, p);
            _client = client;
            client.Start();
        }

        private void StopComponents()
        {
            _client?.Dispose();   // joins the receive thread, so OnPhysics no longer runs
            _bridge?.Dispose();
            _forza?.Dispose();
            _bridge = null;
            _forza = null;
        }

        // receive thread, ~100 Hz; each bridge throttles itself
        private void OnPhysics(RennsportApiClient client, PhysicsSnapshot p)
        {
            var car = client.GetCar(p.DriverNumber);
            var bridge = _bridge;
            if (bridge != null)
            {
                try { bridge.Send(p, car, client.Stage); }
                catch (Exception ex) { if (++_bridgeErrors <= 3) Log("External sim send failed: " + ex.Message); }
            }
            var forza = _forza;
            if (forza != null)
            {
                try { forza.Send(p, car); }
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
            StopComponents();
            _test?.Dispose();
            _test = null;
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
