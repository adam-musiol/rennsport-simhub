namespace RennsportPlugin
{
    /// <summary>
    /// Subset of PhysicsUpdate.data (player car, 400 Hz). Units as sent by the game.
    /// A new instance is created per parse and published by reference, so readers never see a half-written packet.
    /// </summary>
    public sealed class PhysicsSnapshot
    {
        public double Time;                 // message "time"
        public double Timestamp;
        public int DriverNumber;

        // pointMetadata
        public bool IsGamePaused;
        public bool IsUserInControl;
        public long UpdateCounter;
        public double TrackLengthMeters;

        public double Speed;                // m/s
        public double EngineRpm;
        public int GearIndex;               // raw, reverse/neutral mapping not verified yet
        public double Throttle, Brake, Clutch;   // 0..1
        public double SteeringWheelAngle;   // degrees
        public double SteeringShaftTorque;
        public double SteeringForceFeedback; // -1..1

        public double LatAccel, LonAccel, VertAccel;     // m/s²
        public double Pitch, Roll, Yaw;                  // rad
        public double PitchRate, RollRate, YawRate;

        public int LapNumber;
        public double LapDistance, LapDistancePct;
        public double BestLapTime = -1;     // -1 = none
        public bool LapValid;

        public double EnginePower, EngineTorque;          // kW (assumed), Nm
        public double CoolantTemp, OilTemp, OilPressure;
        public double TurboRpm, BoostPressure;
        public double FuelLevel, FuelPressure;

        public bool AbsActive; public int AbsLevel;
        public bool TcActive; public int TcLevel;
        public double BrakeBias;
        public bool PitLimiter, Headlights, Ignition;

        public double Drag, FrontDownforce, RearDownforce;
        public double AirTemp;

        public readonly WheelSnapshot FL = new WheelSnapshot(), FR = new WheelSnapshot(),
                                      RL = new WheelSnapshot(), RR = new WheelSnapshot();
    }

    public sealed class WheelSnapshot
    {
        public double Pressure, ColdPressure;            // kPa
        public double TempSurfaceL, TempSurfaceM, TempSurfaceR, TempSurfaceAvg;
        public double TempInnerL, TempInnerM, TempInnerR, TempInnerAvg;
        public double TempCarcass;
        public double Wear, WearL, WearM, WearR;
        public double BrakeTemp;
        public double SlipAngle, SlipRatio;
        public double TireLoad, SuspensionForce, Compression, DamperVelocity;
        public double RideHeight, Camber, Toe;
        public double Rpm;
    }

    /// <summary>CarLiveUpdate.data for one car (4 Hz, one message per car).</summary>
    public sealed class CarLiveInfo
    {
        public int CarNumber;
        public int Position;
        public int CompletedLaps;
        public double CurrentLapTime, LastLapTime, BestLapTime;
        public double LapProgress;
        public int Gear;
        public double MaxEngineRpm, OptimalShiftRpm;
        public string CurrentFlag;
        public string InPitStatus;
        public bool PitLimiter;
        public int CarLeft = -1, CarRight = -1;
    }

    /// <summary>GameplayStageLiveUpdate + GameplayStageInfoUpdate.</summary>
    public sealed class StageInfo
    {
        public double TimeLeft;
        public int LapsLeft = -1;
        public int CurrentlyObservedCar;
        public string ContestName, StageName, Track;
    }
}
