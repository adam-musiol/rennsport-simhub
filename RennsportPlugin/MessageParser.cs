using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RennsportPlugin
{
    /// <summary>
    /// Parsing of Rennsport API messages. PhysicsUpdate (~8 KB) is read with a streaming JsonTextReader
    /// straight into a snapshot; the low-rate messages are small, so they go through JObject.
    /// </summary>
    public static class MessageParser
    {
        private static readonly byte[] MessageTypeKey = Encoding.ASCII.GetBytes("\"messageType\"");

        /// <summary>Reads the messageType value without parsing the whole datagram (it is the first property).</summary>
        public static string PeekMessageType(byte[] d, int length)
        {
            int limit = Math.Min(length, 128);
            int i = IndexOf(d, limit, MessageTypeKey);
            if (i < 0) return null;
            i += MessageTypeKey.Length;
            while (i < limit && (d[i] == ' ' || d[i] == ':' || d[i] == '\t' || d[i] == '\r' || d[i] == '\n')) i++;
            if (i >= limit || d[i] != '"') return null;
            int start = ++i;
            while (i < limit && d[i] != '"') i++;
            return i < limit ? Encoding.ASCII.GetString(d, start, i - start) : null;
        }

        private static int IndexOf(byte[] d, int limit, byte[] key)
        {
            for (int i = 0; i + key.Length <= limit; i++)
            {
                int k = 0;
                while (k < key.Length && d[i + k] == key[k]) k++;
                if (k == key.Length) return i;
            }
            return -1;
        }

        /// <summary>Parses a whole (small) message into a JObject.</summary>
        public static JObject ParseObject(byte[] d, int length) => JObject.Parse(Encoding.UTF8.GetString(d, 0, length));

        // ---------------- PhysicsUpdate ----------------

        /// <summary>Parses a PhysicsUpdate datagram into a new snapshot; unknown fields are skipped.</summary>
        public static PhysicsSnapshot ParsePhysics(byte[] d, int length)
        {
            var s = new PhysicsSnapshot();
            using (var reader = new JsonTextReader(new StreamReader(new MemoryStream(d, 0, length, false), Encoding.UTF8)))
            {
                reader.FloatParseHandling = FloatParseHandling.Double;
                if (!reader.Read() || reader.TokenType != JsonToken.StartObject) throw new FormatException("PhysicsUpdate is not a JSON object");
                while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                {
                    var name = (string)reader.Value;
                    reader.Read();
                    if (name == "data" && reader.TokenType == JsonToken.StartObject) ReadPhysicsData(reader, s);
                    else if (name == "time") s.Time = D(reader);
                    else reader.Skip();
                }
            }
            return s;
        }

        private static void ReadPhysicsData(JsonTextReader r, PhysicsSnapshot s)
        {
            while (r.Read() && r.TokenType == JsonToken.PropertyName)
            {
                var name = (string)r.Value;
                r.Read();
                switch (name)
                {
                    case "pointMetadata": ReadMetadata(r, s); break;
                    case "lF": ReadWheel(r, s.FL); break;
                    case "rF": ReadWheel(r, s.FR); break;
                    case "lR": ReadWheel(r, s.RL); break;
                    case "rR": ReadWheel(r, s.RR); break;

                    case "timestamp": s.Timestamp = D(r); break;
                    case "driverNumber": s.DriverNumber = I(r); break;
                    case "speed": s.Speed = D(r); break;
                    case "engineRPM": s.EngineRpm = D(r); break;
                    case "gearIndex": s.GearIndex = I(r); break;
                    case "throttlePedalPos": s.Throttle = D(r); break;
                    case "brakePedalPos": s.Brake = D(r); break;
                    case "clutchPedalPos": s.Clutch = D(r); break;
                    case "steeringWheelAngle": s.SteeringWheelAngle = D(r); break;
                    case "steeringShaftTorque": s.SteeringShaftTorque = D(r); break;
                    case "steeringForceFeedback": s.SteeringForceFeedback = D(r); break;

                    case "latAccel": s.LatAccel = D(r); break;
                    case "lonAccel": s.LonAccel = D(r); break;
                    case "vertAccel": s.VertAccel = D(r); break;
                    case "pitch": s.Pitch = D(r); break;
                    case "roll": s.Roll = D(r); break;
                    case "yaw": s.Yaw = D(r); break;
                    case "pitchRate": s.PitchRate = D(r); break;
                    case "rollRate": s.RollRate = D(r); break;
                    case "yawRate": s.YawRate = D(r); break;

                    case "lapNumber": s.LapNumber = I(r); break;
                    case "lapDistance": s.LapDistance = D(r); break;
                    case "lapDistancePct": s.LapDistancePct = D(r); break;
                    case "bestLapTime": s.BestLapTime = D(r); break;
                    case "bLapValid": s.LapValid = B(r); break;

                    case "enginePower": s.EnginePower = D(r); break;
                    case "engineTorque": s.EngineTorque = D(r); break;
                    case "engineCoolantTemperature": s.CoolantTemp = D(r); break;
                    case "engineOilTemperature": s.OilTemp = D(r); break;
                    case "engineOilPressure": s.OilPressure = D(r); break;
                    case "turboRPM": s.TurboRpm = D(r); break;
                    case "boostPressure": s.BoostPressure = D(r); break;
                    case "fuelLevel": s.FuelLevel = D(r); break;
                    case "fuelPressure": s.FuelPressure = D(r); break;

                    case "bABSActive": s.AbsActive = B(r); break;
                    case "aBSLevel": s.AbsLevel = I(r); break;
                    case "bTractionControlActive": s.TcActive = B(r); break;
                    case "tractionControlLevel": s.TcLevel = I(r); break;
                    case "brakeBias": s.BrakeBias = D(r); break;
                    case "bPitSpeedLimiter": s.PitLimiter = B(r); break;
                    case "bHeadlightSwitch": s.Headlights = B(r); break;
                    case "bIgnition": s.Ignition = B(r); break;

                    case "drag": s.Drag = D(r); break;
                    case "frontDownforce": s.FrontDownforce = D(r); break;
                    case "rearDownforce": s.RearDownforce = D(r); break;
                    case "airTemp": s.AirTemp = D(r); break;

                    default: r.Skip(); break;
                }
            }
        }

        private static void ReadMetadata(JsonTextReader r, PhysicsSnapshot s)
        {
            if (r.TokenType != JsonToken.StartObject) { r.Skip(); return; }
            while (r.Read() && r.TokenType == JsonToken.PropertyName)
            {
                var name = (string)r.Value;
                r.Read();
                switch (name)
                {
                    case "bIsGamePaused": s.IsGamePaused = B(r); break;
                    case "bIsUserInControl": s.IsUserInControl = B(r); break;
                    case "updateCounter": s.UpdateCounter = (long)D(r); break;
                    case "trackLengthMeters": s.TrackLengthMeters = D(r); break;
                    default: r.Skip(); break;
                }
            }
        }

        private static void ReadWheel(JsonTextReader r, WheelSnapshot w)
        {
            if (r.TokenType != JsonToken.StartObject) { r.Skip(); return; }
            while (r.Read() && r.TokenType == JsonToken.PropertyName)
            {
                var name = (string)r.Value;
                r.Read();
                switch (name)
                {
                    case "pressure": w.Pressure = D(r); break;
                    case "coldPressure": w.ColdPressure = D(r); break;
                    case "tempSurfaceL": w.TempSurfaceL = D(r); break;
                    case "tempSurfaceM": w.TempSurfaceM = D(r); break;
                    case "tempSurfaceR": w.TempSurfaceR = D(r); break;
                    case "tempSurfaceAvg": w.TempSurfaceAvg = D(r); break;
                    case "tempInnerL": w.TempInnerL = D(r); break;
                    case "tempInnerM": w.TempInnerM = D(r); break;
                    case "tempInnerR": w.TempInnerR = D(r); break;
                    case "tempInnerAvg": w.TempInnerAvg = D(r); break;
                    case "tempCarcass": w.TempCarcass = D(r); break;
                    case "wear": w.Wear = D(r); break;
                    case "wearL": w.WearL = D(r); break;
                    case "wearM": w.WearM = D(r); break;
                    case "wearR": w.WearR = D(r); break;
                    case "brakeTemp": w.BrakeTemp = D(r); break;
                    case "slipAngle": w.SlipAngle = D(r); break;
                    case "slipRatio": w.SlipRatio = D(r); break;
                    case "tireLoad": w.TireLoad = D(r); break;
                    case "suspensionForce": w.SuspensionForce = D(r); break;
                    case "compression": w.Compression = D(r); break;
                    case "damperVelocity": w.DamperVelocity = D(r); break;
                    case "rideHeight": w.RideHeight = D(r); break;
                    case "camber": w.Camber = D(r); break;
                    case "toe": w.Toe = D(r); break;
                    case "rPM": w.Rpm = D(r); break;
                    default: r.Skip(); break;
                }
            }
        }

        // number (or bool as 0/1) at the current token; anything else is skipped and read as 0
        private static double D(JsonReader r)
        {
            switch (r.TokenType)
            {
                case JsonToken.Float:
                case JsonToken.Integer: return Convert.ToDouble(r.Value, CultureInfo.InvariantCulture);
                case JsonToken.Boolean: return (bool)r.Value ? 1 : 0;
                default: r.Skip(); return 0;
            }
        }

        private static int I(JsonReader r) => (int)Math.Round(D(r));

        private static bool B(JsonReader r) => r.TokenType == JsonToken.Boolean ? (bool)r.Value : D(r) != 0;

        // ---------------- low-rate messages ----------------

        /// <summary>Maps CarLiveUpdate.data (one car) to <see cref="CarLiveInfo"/>.</summary>
        public static CarLiveInfo ParseCarLive(JObject data)
        {
            return new CarLiveInfo
            {
                CarNumber = (int?)data["carNumber"] ?? 0,
                Position = (int?)data["position"] ?? 0,
                CompletedLaps = (int?)data["completedLaps"] ?? 0,
                CurrentLapTime = (double?)data["currentLap"]?["time"] ?? 0,
                LastLapTime = (double?)data["lastLap"]?["time"] ?? 0,
                BestLapTime = (double?)data["bestLap"]?["time"] ?? 0,
                LapProgress = (double?)data["lapProgress"] ?? 0,
                Gear = (int?)data["gear"] ?? 0,
                MaxEngineRpm = (double?)data["maxEngineRPM"] ?? 0,
                OptimalShiftRpm = (double?)data["optimalShiftRPM"] ?? 0,
                CurrentFlag = (string)data["currentFlag"],
                InPitStatus = (string)data["inPitStatus"],
                PitLimiter = (bool?)data["pitLimiter"] ?? false,
                CarLeft = (int?)data["carLeft"] ?? -1,
                CarRight = (int?)data["carRight"] ?? -1,
            };
        }
    }
}
