using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using SimHub.Plugins.ExternalSims;
using SimHub.Plugins.ExternalSims.Fields;
using SimHub.Plugins.ExternalSims.Fields.StandardFields;

namespace RennsportPlugin
{
    /// <summary>
    /// Feeds SimHub's normalized GameData (stock dashboards, LEDs, ShakeIt) through SimHub's own
    /// "External Sims" mechanism (SimHub 9.13+, namespace SimHub.Plugins.ExternalSims):
    ///  1. a manifest (.simdef) in %LOCALAPPDATA%\SimHub\ExternalSims\Definitions makes SimHub list
    ///     "Rennsport (API)" as a game; SimHub polls that folder every second,
    ///  2. every telemetry frame is sent as a binary UDP packet to SimHub's reader for that game:
    ///     TelemetryHeader (55 B, Pack=1) + the standard fields in manifest order, little-endian.
    /// The manifest is built with SimHub's own classes, so its signatures/packet length always match the reader.
    /// </summary>
    public sealed class ExternalSimBridge : IDisposable
    {
        public static readonly Guid SimId = new Guid("6f1c2a9e-3b7d-4e55-9a41-2f0d8c7b5e13");
        public const string SimName = "Rennsport (API)";
        public const int DefaultPort = 30559;

        // Order = wire order. Appending is fine, anything else changes the telemetry signature.
        private static readonly StandardFieldType[] Fields =
        {
            StandardFieldType.SpeedKmh,
            StandardFieldType.EngineRpm,
            StandardFieldType.EngineMaxRpm,
            StandardFieldType.EngineShiftRpm,
            StandardFieldType.Gear,
            StandardFieldType.Throttle,
            StandardFieldType.Brake,
            StandardFieldType.Clutch,
            StandardFieldType.BrakeBias,
            StandardFieldType.Fuel,
            StandardFieldType.WaterTemperature,
            StandardFieldType.OilTemperature,
            StandardFieldType.TurboPressureBar,
            StandardFieldType.AbsActive,
            StandardFieldType.AbsSettingLevel,
            StandardFieldType.TcActive,
            StandardFieldType.TcSettingLevel,
            StandardFieldType.PitLimiterOn,
            StandardFieldType.TrackLength,
            StandardFieldType.TrackProgression,
            StandardFieldType.CompletedLaps,
            StandardFieldType.RacePosition,
            StandardFieldType.CurrentLapTime,
            StandardFieldType.LastLapTimeSeconds,
            StandardFieldType.BestLapTimeSeconds,
            StandardFieldType.SessionTimeLeft,
            StandardFieldType.SessionType,
            StandardFieldType.EnvironmentName,
            StandardFieldType.TyrePressureFrontLeft,
            StandardFieldType.TyrePressureFrontRight,
            StandardFieldType.TyrePressureRearLeft,
            StandardFieldType.TyrePressureRearRight,
            StandardFieldType.TyreTemperatureFrontLeft,
            StandardFieldType.TyreTemperatureFrontRight,
            StandardFieldType.TyreTemperatureRearLeft,
            StandardFieldType.TyreTemperatureRearRight,
            StandardFieldType.TyreWearFrontLeft,
            StandardFieldType.TyreWearFrontRight,
            StandardFieldType.TyreWearRearLeft,
            StandardFieldType.TyreWearRearRight,
            StandardFieldType.BrakeTemperatureFrontLeftDegreesC,
            StandardFieldType.BrakeTemperatureFrontRightDegreesC,
            StandardFieldType.BrakeTemperatureRearLeftDegreesC,
            StandardFieldType.BrakeTemperatureRearRightDegreesC,
            StandardFieldType.WheelSlipFrontLeft,
            StandardFieldType.WheelSlipFrontRight,
            StandardFieldType.WheelSlipRearLeft,
            StandardFieldType.WheelSlipRearRight,
            // motion / ShakeIt - axis signs not verified against the game yet
            StandardFieldType.LocalSurgeMs2,
            StandardFieldType.LocalSwayMs2,
            StandardFieldType.LocalHeaveMs2,
            StandardFieldType.PitchDegrees,
            StandardFieldType.RollDegrees,
            StandardFieldType.YawDegrees,
            StandardFieldType.PitchRateDegreesPerSecond,
            StandardFieldType.RollRateDegreesPerSecond,
            StandardFieldType.YawRateDegreesPerSecond,
            StandardFieldType.SuspensionVelocityFrontLeftMps,
            StandardFieldType.SuspensionVelocityFrontRightMps,
            StandardFieldType.SuspensionVelocityRearLeftMps,
            StandardFieldType.SuspensionVelocityRearRightMps,
        };

        private const double KpaToPsi = 0.1450377;
        private const double RadToDeg = 180.0 / Math.PI;

        private readonly (StandardFieldType type, BinaryDataType wire, int strLen)[] _layout;
        private readonly uint _gameSignature, _telemetrySignature;
        private readonly ushort _layoutMajor, _layoutMinor;
        private readonly int _packetLength;
        private readonly UdpClient _udp = new UdpClient();
        private readonly IPEndPoint _target;
        private readonly ulong _emitterId = RandomUlong();
        private readonly Action<string> _log;
        private readonly byte[] _buffer;
        private ulong _counter, _sessionId = RandomUlong();
        private string _sessionKey;

        public ExternalSimBridge(int port, Action<string> log)
        {
            _log = log ?? (_ => { });
            _target = new IPEndPoint(IPAddress.Loopback, port);

            var manifest = BuildManifest(port);
            _gameSignature = manifest.GameSignature;
            _telemetrySignature = manifest.TelemetrySignature;
            _layoutMajor = manifest.TelemetryDefinition.LayoutMajorVersion;
            _layoutMinor = manifest.TelemetryDefinition.LayoutMinorVersion;
            _packetLength = manifest.PacketLength;

            _layout = new (StandardFieldType, BinaryDataType, int)[Fields.Length];
            for (int i = 0; i < Fields.Length; i++)
            {
                var attr = typeof(StandardFieldType).GetField(Fields[i].ToString()).GetCustomAttribute<FieldFormatAttribute>(false)
                           ?? throw new InvalidOperationException("No wire format for " + Fields[i]);
                _layout[i] = (Fields[i], attr.DataType, (attr as StringFieldFormatAttribute)?.Length ?? 0);
            }
            _buffer = new byte[_packetLength];

            WriteManifest(manifest);
        }

        public static string DefinitionPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimHub", "ExternalSims", "Definitions", "Rennsport", "Rennsport.simdef");

        public static ExternalSimManifest BuildManifest(int port)
        {
            var m = new ExternalSimManifest
            {
                UniqueId = SimId,
                Name = SimName,
                ManagerVersion = ExternalSimManagerVersion.V3,
                DefaultUDPPort = port,
            };
            m.TelemetryBehaviour.DefaultMaxSpeed = 300;
            // lets SimHub switch to this game automatically (matched like Process.GetProcessesByName: no ".exe")
            m.DetectionProcesses.Add(new ExternalSimProcessDetection { ProcessName = "Rennsport" });
            foreach (var f in Fields) m.TelemetryDefinition.Fields.Add(new StandardField { FieldType = f });
            return m;
        }

        // only rewrite when something changed: every write makes SimHub reload the game definition
        private void WriteManifest(ExternalSimManifest manifest)
        {
            var path = DefinitionPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            manifest.SaveToFile(tmp);
            var content = File.ReadAllText(tmp);
            if (File.Exists(path) && File.ReadAllText(path) == content) { File.Delete(tmp); return; }
            File.Copy(tmp, path, true);
            File.Delete(tmp);
            _log($"External sim definition written: {path} (select game '{SimName}' in SimHub)");
        }

        /// <summary>Builds and sends one telemetry packet from the latest snapshot.</summary>
        public void Send(PhysicsSnapshot p, CarLiveInfo car, StageInfo stage)
        {
            var key = (stage?.Track ?? "") + "|" + (stage?.StageName ?? "");
            if (key != _sessionKey) { _sessionKey = key; _sessionId = RandomUlong(); }

            int o = 0;
            // ---- TelemetryHeader ----
            W32(ref o, _gameSignature);
            W32(ref o, _telemetrySignature);
            W16(ref o, _layoutMajor);
            W16(ref o, _layoutMinor);
            W64(ref o, _emitterId);
            _buffer[o++] = 0;                                   // PacketId
            W64(ref o, ++_counter);                             // PacketsCounter
            _buffer[o++] = 1;                                   // IsSessionRunning
            _buffer[o++] = (byte)(p.IsGamePaused ? 1 : 0);      // IsSessionPaused
            W64(ref o, _sessionId);
            _buffer[o++] = 0;                                   // IsReplay
            _buffer[o++] = (byte)(p.IsUserInControl ? 1 : 0);   // IsUserInControl
            _buffer[o++] = 0;                                   // IsAIInControl
            _buffer[o++] = 0;                                   // IsSpectator
            WF64(ref o, p.Timestamp);                           // SessionTimeSeconds (PhysicsUpdate "time" is always 0)
            W32(ref o, 0);                                      // PhysicsDiscontinuityCounter

            // ---- fields ----
            foreach (var (type, wire, strLen) in _layout)
            {
                if (wire == BinaryDataType.UTF8String) WStr(ref o, StringValue(type, p, stage), strLen);
                else WNum(ref o, wire, Value(type, p, car, stage));
            }
            if (o != _packetLength) throw new InvalidOperationException($"Packet length {o} != expected {_packetLength}");

            try { _udp.Send(_buffer, _buffer.Length, _target); } catch { }
        }

        private static string StringValue(StandardFieldType t, PhysicsSnapshot p, StageInfo stage)
        {
            switch (t)
            {
                case StandardFieldType.Gear: return GearText(p.GearIndex);
                case StandardFieldType.SessionType: return stage?.StageName;
                case StandardFieldType.EnvironmentName: return stage?.Track;
                default: return null;
            }
        }

        // gearIndex: 0 seen while standing at idle, 1.. while driving -> 0 = N; negative assumed reverse (not verified)
        public static string GearText(int gearIndex) => gearIndex < 0 ? "R" : gearIndex == 0 ? "N" : gearIndex.ToString();

        private static double Value(StandardFieldType t, PhysicsSnapshot p, CarLiveInfo car, StageInfo stage)
        {
            switch (t)
            {
                case StandardFieldType.SpeedKmh: return p.Speed * 3.6;
                case StandardFieldType.EngineRpm: return p.EngineRpm;
                case StandardFieldType.EngineMaxRpm: return car?.MaxEngineRpm ?? 0;
                case StandardFieldType.EngineShiftRpm: return car?.OptimalShiftRpm ?? 0;
                case StandardFieldType.Throttle: return p.Throttle;
                case StandardFieldType.Brake: return p.Brake;
                case StandardFieldType.Clutch: return p.Clutch;
                case StandardFieldType.BrakeBias: return p.BrakeBias;
                case StandardFieldType.Fuel: return p.FuelLevel;
                case StandardFieldType.WaterTemperature: return p.CoolantTemp;
                case StandardFieldType.OilTemperature: return p.OilTemp;
                case StandardFieldType.TurboPressureBar: return Math.Max(0, (p.BoostPressure - 101.325) / 100.0); // kPa abs -> bar gauge
                case StandardFieldType.AbsActive: return p.AbsActive ? 1 : 0;
                case StandardFieldType.AbsSettingLevel: return p.AbsLevel;
                case StandardFieldType.TcActive: return p.TcActive ? 1 : 0;
                case StandardFieldType.TcSettingLevel: return p.TcLevel;
                case StandardFieldType.PitLimiterOn: return p.PitLimiter ? 1 : 0;
                case StandardFieldType.TrackLength: return p.TrackLengthMeters;
                case StandardFieldType.TrackProgression: return p.LapDistancePct;
                case StandardFieldType.CompletedLaps: return car?.CompletedLaps ?? Math.Max(0, p.LapNumber - 1);
                case StandardFieldType.RacePosition: return car?.Position ?? 0;
                case StandardFieldType.CurrentLapTime: return car?.CurrentLapTime ?? 0;
                case StandardFieldType.LastLapTimeSeconds: return Positive(car?.LastLapTime);
                case StandardFieldType.BestLapTimeSeconds: return Positive(car?.BestLapTime ?? p.BestLapTime);
                case StandardFieldType.SessionTimeLeft: return Math.Max(0, stage?.TimeLeft ?? 0);

                case StandardFieldType.TyrePressureFrontLeft: return p.FL.Pressure * KpaToPsi;
                case StandardFieldType.TyrePressureFrontRight: return p.FR.Pressure * KpaToPsi;
                case StandardFieldType.TyrePressureRearLeft: return p.RL.Pressure * KpaToPsi;
                case StandardFieldType.TyrePressureRearRight: return p.RR.Pressure * KpaToPsi;
                case StandardFieldType.TyreTemperatureFrontLeft: return p.FL.TempSurfaceAvg;
                case StandardFieldType.TyreTemperatureFrontRight: return p.FR.TempSurfaceAvg;
                case StandardFieldType.TyreTemperatureRearLeft: return p.RL.TempSurfaceAvg;
                case StandardFieldType.TyreTemperatureRearRight: return p.RR.TempSurfaceAvg;
                case StandardFieldType.TyreWearFrontLeft: return p.FL.Wear * 100; // game: 1 = new
                case StandardFieldType.TyreWearFrontRight: return p.FR.Wear * 100;
                case StandardFieldType.TyreWearRearLeft: return p.RL.Wear * 100;
                case StandardFieldType.TyreWearRearRight: return p.RR.Wear * 100;
                case StandardFieldType.BrakeTemperatureFrontLeftDegreesC: return p.FL.BrakeTemp;
                case StandardFieldType.BrakeTemperatureFrontRightDegreesC: return p.FR.BrakeTemp;
                case StandardFieldType.BrakeTemperatureRearLeftDegreesC: return p.RL.BrakeTemp;
                case StandardFieldType.BrakeTemperatureRearRightDegreesC: return p.RR.BrakeTemp;
                case StandardFieldType.WheelSlipFrontLeft: return Slip(p.FL);
                case StandardFieldType.WheelSlipFrontRight: return Slip(p.FR);
                case StandardFieldType.WheelSlipRearLeft: return Slip(p.RL);
                case StandardFieldType.WheelSlipRearRight: return Slip(p.RR);

                case StandardFieldType.LocalSurgeMs2: return p.LonAccel;
                case StandardFieldType.LocalSwayMs2: return p.LatAccel;
                case StandardFieldType.LocalHeaveMs2: return p.VertAccel;
                case StandardFieldType.PitchDegrees: return p.Pitch * RadToDeg;
                case StandardFieldType.RollDegrees: return p.Roll * RadToDeg;
                case StandardFieldType.YawDegrees: return p.Yaw * RadToDeg;
                case StandardFieldType.PitchRateDegreesPerSecond: return p.PitchRate * RadToDeg;
                case StandardFieldType.RollRateDegreesPerSecond: return p.RollRate * RadToDeg;
                case StandardFieldType.YawRateDegreesPerSecond: return p.YawRate * RadToDeg;
                case StandardFieldType.SuspensionVelocityFrontLeftMps: return p.FL.DamperVelocity;
                case StandardFieldType.SuspensionVelocityFrontRightMps: return p.FR.DamperVelocity;
                case StandardFieldType.SuspensionVelocityRearLeftMps: return p.RL.DamperVelocity;
                case StandardFieldType.SuspensionVelocityRearRightMps: return p.RR.DamperVelocity;
                default: return 0;
            }
        }

        private static double Positive(double? v) => v.HasValue && v.Value > 0 ? v.Value : 0;
        private static double Slip(WheelSnapshot w) => Math.Min(1, Math.Abs(w.SlipRatio));

        // ---------------- little-endian writers ----------------

        private void W16(ref int o, ushort v) { _buffer[o++] = (byte)v; _buffer[o++] = (byte)(v >> 8); }
        private void W32(ref int o, uint v) { for (int i = 0; i < 4; i++) _buffer[o++] = (byte)(v >> (8 * i)); }
        private void W64(ref int o, ulong v) { for (int i = 0; i < 8; i++) _buffer[o++] = (byte)(v >> (8 * i)); }
        private void WF64(ref int o, double v) => W64(ref o, (ulong)BitConverter.DoubleToInt64Bits(v));
        private void WF32(ref int o, float v) => W32(ref o, BitConverter.ToUInt32(BitConverter.GetBytes(v), 0));

        private void WNum(ref int o, BinaryDataType t, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
            switch (t)
            {
                case BinaryDataType.Int8: _buffer[o++] = (byte)(sbyte)Clamp(v, sbyte.MinValue, sbyte.MaxValue); break;
                case BinaryDataType.Byte: _buffer[o++] = (byte)Clamp(v, 0, byte.MaxValue); break;
                case BinaryDataType.Int16: W16(ref o, (ushort)(short)Clamp(v, short.MinValue, short.MaxValue)); break;
                case BinaryDataType.UInt16: W16(ref o, (ushort)Clamp(v, 0, ushort.MaxValue)); break;
                case BinaryDataType.Int32: W32(ref o, (uint)(int)Clamp(v, int.MinValue, int.MaxValue)); break;
                case BinaryDataType.UInt32: W32(ref o, (uint)Clamp(v, 0, uint.MaxValue)); break;
                case BinaryDataType.UInt64: W64(ref o, (ulong)Math.Max(0, v)); break;
                case BinaryDataType.Float32: WF32(ref o, (float)v); break;
                case BinaryDataType.Double: WF64(ref o, v); break;
                default: throw new NotSupportedException(t.ToString());
            }
        }

        private static double Clamp(double v, double min, double max) => Math.Round(Math.Max(min, Math.Min(max, v)));

        // fixed-size, zero-terminated/padded UTF-8
        private void WStr(ref int o, string s, int len)
        {
            Array.Clear(_buffer, o, len);
            if (!string.IsNullOrEmpty(s))
            {
                var b = Encoding.UTF8.GetBytes(s);
                Buffer.BlockCopy(b, 0, _buffer, o, Math.Min(b.Length, len - 1));
            }
            o += len;
        }

        private static ulong RandomUlong()
        {
            var b = Guid.NewGuid().ToByteArray();
            return BitConverter.ToUInt64(b, 0) | 1;
        }

        public void Dispose() => _udp.Dispose();
    }
}
