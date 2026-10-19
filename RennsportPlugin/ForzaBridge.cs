using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace RennsportPlugin
{
    /// <summary>
    /// Feeds Simucube Tuner's Telemetry FFB. Tuner has no Rennsport telemetry plugin, so its game_db.json entry
    /// for Rennsport is patched to use Tuner's built-in Forza plugin (127.0.0.1:9877), and this class sends
    /// Forza Motorsport 8 "Dash" packets (331 B, little-endian) at ~60 Hz like the real game does.
    /// Forza local space: X right, Y up, Z forward. Rennsport axis signs are NOT verified.
    /// </summary>
    public sealed class ForzaBridge : IDisposable
    {
        public const int DefaultPort = 9877;
        public const int PacketLength = 331;
        private const double SendInterval = 1.0 / 60;
        private const float RpmToRadPerSec = (float)(Math.PI / 30);

        // suspension compression range seen in the Monza recording (0.015..0.083 m)
        private const float CompressionMin = 0.01f, CompressionRange = 0.08f;
        private const float SteeringHalfLockDeg = 450;   // 900° wheel, matches the Tuner profile
        private const float IdleRpm = 900;              // not in the API

        private readonly UdpClient _udp = new UdpClient();
        private readonly IPEndPoint _target;
        private readonly byte[] _buffer = new byte[PacketLength];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _nextSendAt;

        public ForzaBridge(int port)
        {
            _target = new IPEndPoint(IPAddress.Loopback, port);
        }

        /// <summary>Called for every parsed PhysicsUpdate (~100 Hz); sends at ~60 Hz on average.</summary>
        public void Send(PhysicsSnapshot p, CarLiveInfo car)
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (now < _nextSendAt) return;
            // keep the 60 Hz cadence, but don't burst after a pause
            _nextSendAt = Math.Max(_nextSendAt + SendInterval, now);

            Build(p, car);
            try { _udp.Send(_buffer, _buffer.Length, _target); } catch { }
        }

        private void Build(PhysicsSnapshot p, CarLiveInfo car)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            var wheels = new[] { p.FL, p.FR, p.RL, p.RR };

            // ---- sled ----
            I32(0, p.IsGamePaused ? 0 : 1);                         // IsRaceOn
            I32(4, (int)(p.Timestamp * 1000));                      // TimestampMS
            F32(8, car?.MaxEngineRpm ?? 0);
            F32(12, IdleRpm);
            F32(16, p.EngineRpm);
            F32(20, p.LatAccel);                                    // AccelerationX
            F32(24, p.VertAccel);                                   // AccelerationY
            F32(28, p.LonAccel);                                    // AccelerationZ
            F32(40, p.Speed);                                       // VelocityZ (no velocity vector in the API)
            F32(44, p.PitchRate);                                   // AngularVelocityX
            F32(48, p.YawRate);                                     // AngularVelocityY
            F32(52, p.RollRate);                                    // AngularVelocityZ
            F32(56, p.Yaw);
            F32(60, p.Pitch);
            F32(64, p.Roll);
            for (int i = 0; i < 4; i++)
            {
                var w = wheels[i];
                int o = i * 4;
                F32(68 + o, Clamp01(((float)w.Compression - CompressionMin) / CompressionRange)); // NormalizedSuspensionTravel
                F32(84 + o, w.SlipRatio);
                F32(100 + o, (float)w.Rpm * RpmToRadPerSec);        // WheelRotationSpeed rad/s
                // 116 WheelOnRumbleStrip, 132 WheelInPuddleDepth, 148 SurfaceRumble: unknown in the API -> 0
                F32(164 + o, w.SlipAngle);
                F32(180 + o, Math.Sqrt(w.SlipRatio * w.SlipRatio + w.SlipAngle * w.SlipAngle)); // TireCombinedSlip
                F32(196 + o, w.Compression);                        // SuspensionTravelMeters
            }
            // 212 CarOrdinal, 216 CarClass, 220 CarPerformanceIndex = 0
            I32(224, 1);                                            // DrivetrainType: RWD
            I32(228, 6);                                            // NumCylinders

            // ---- dash ----
            F32(244, p.Speed);
            F32(248, p.EnginePower * 1000);                         // W
            F32(252, p.EngineTorque);
            for (int i = 0; i < 4; i++) F32(256 + i * 4, wheels[i].TempSurfaceAvg * 9 / 5 + 32); // TireTemp °F
            F32(272, (p.BoostPressure - 101.325) * 0.1450377);      // kPa abs -> psi gauge
            F32(276, p.FuelLevel / 100);
            F32(280, p.LapDistance);
            F32(284, Math.Max(0, car?.BestLapTime ?? p.BestLapTime));
            F32(288, Math.Max(0, car?.LastLapTime ?? 0));
            F32(292, car?.CurrentLapTime ?? 0);
            U16(300, Math.Max(0, p.LapNumber));
            _buffer[302] = (byte)Math.Max(0, Math.Min(255, car?.Position ?? 1));
            _buffer[303] = Byte01(p.Throttle);
            _buffer[304] = Byte01(p.Brake);
            _buffer[305] = Byte01(p.Clutch);
            _buffer[307] = ForzaGear(p.GearIndex);
            _buffer[308] = (byte)(sbyte)Math.Max(-127, Math.Min(127, p.SteeringWheelAngle / SteeringHalfLockDeg * 127));
            for (int i = 0; i < 4; i++) F32(311 + i * 4, 1 - wheels[i].Wear);  // TireWear (game: 1 = new)
            // 327 TrackOrdinal = 0
        }

        // Forza: 0 = R, 11 = N (assumed); Rennsport: 0 = N, negative = R (assumed, see ExternalSimBridge.GearText)
        private static byte ForzaGear(int gearIndex) => (byte)(gearIndex < 0 ? 0 : gearIndex == 0 ? 11 : Math.Min(gearIndex, 10));

        private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
        private static byte Byte01(double v) => (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);

        private void F32(int o, double v) => I32(o, BitConverter.ToInt32(BitConverter.GetBytes(double.IsNaN(v) || double.IsInfinity(v) ? 0f : (float)v), 0));
        private void I32(int o, int v) { for (int i = 0; i < 4; i++) _buffer[o + i] = (byte)(v >> (8 * i)); }
        private void U16(int o, int v) { _buffer[o] = (byte)v; _buffer[o + 1] = (byte)(v >> 8); }

        public void Dispose() => _udp.Dispose();
    }
}
