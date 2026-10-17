using System.Buffers.Binary;
using System.Numerics;

namespace WheelBridge;

/// <summary>Float fields in wire order. Must match FLOATS in WheelBridgeExporter/Scripts/main.lua.</summary>
public enum F
{
    SpeedKmph, TopSpeedKmph,
    RelVelX, RelVelY, RelVelZ,
    AngVelX, AngVelY, AngVelZ,
    SlipDeg, SteerInput, SteerIntent, Throttle, Brake,
    Rpm, RpmScale,
    OnGroundScale, LevitationScale, TimeInAir,
    Roll, Pitch, SpeedScale,
    QuatX, QuatY, QuatZ, QuatW,
    GroundNormalX, GroundNormalY, GroundNormalZ,
    RamjetHeat, RamjetBypassHeat, BypassOverheatPct, AfterburnerEnergy, BoostTransitionScale,
    CrashTimeRemaining, DraftingScale, AirBrakeScale, PredictedHitTime,
}

/// <summary>Byte (enum) fields in wire order. Must match BYTES in main.lua.</summary>
public enum B
{
    BoostState, RamjetState, AfterburnerState, LandingState,
    PerfectLandingState, InputMode, DraftingState, EnergyBrakeState,
}

[Flags]
public enum FrameFlags : uint
{
    None = 0,
    HasVehicle = 1,
    Drifting = 2,
    UpsideDown = 4,
    BoostToggled = 8,
    CollisionsOk = 16,
    /// <summary>The player has finished the race (or timed out / was eliminated).</summary>
    RaceDone = 32,
    /// <summary>No vehicle: the pawn is a character (Shade walking the hub).</summary>
    OnFoot = 64,
}

/// <summary>
/// The game's four vehicle physics families (FCarConfig, FBikeConfig, FSkimmerConfig, FPlaneConfig):
/// Car = land speeder, Bike = speeder bike, Skimmer = skim speeder, Pod = podracer (the game calls it "Plane").
/// </summary>
public enum VehicleClass : byte { Unknown, Car, Bike, Skimmer, Pod }

/// <summary>Enum names from the CXXHeaderDump (Griffin_enums.hpp).</summary>
public static class GameEnums
{
    public static readonly string[] BoostState = ["None", "ToAfterburner", "Afterburner", "ToRamjet", "Ramjet"];
    public static readonly string[] RamjetState = ["NotReady", "Ready", "Engaging", "Active", "CriticalWarning", "GraceTime", "Overheated", "Exploded"];
    public static readonly string[] AfterburnerState = ["Active", "Cooldown", "Recovering"];
    public static readonly string[] LandingState = ["None", "Detecting", "Complete", "Skipped", "Crashing"];
    public static readonly string[] PerfectLandingState = ["None", "Preparing", "Bad", "Good", "Perfect"];
    public static readonly string[] InputMode = ["Idle", "StartGrid", "PerfectStart", "Racing", "PlayFromHere"];
    public static readonly string[] DraftingState = ["Inactive", "Gaining", "Maintaining", "Decaying"];
    public static readonly string[] EnergyBrakeState = ["Off", "Charging", "Pending", "Active"];

    public static string Name(string[] names, byte v) => v < names.Length ? names[v] : $"?{v}";
}

public readonly record struct Collision(
    Vector3 WorldDirection, Vector3 LocalLocation, float PointVelocityDelta, int Id, bool OtherIsVehicle);

/// <summary>One decoded telemetry frame. Units: cm, cm/s, rad/s, degrees, km/h.</summary>
public sealed class TelemetryFrame
{
    public const ushort Version = 1;
    public const int HeaderSize = 4 + 2 + 2 + 4 + 8 + 4 + 4 + 1 + 1;
    public const int FloatCount = (int)F.PredictedHitTime + 1;
    public const int ByteCount = (int)B.EnergyBrakeState + 1;
    public const int MaxCollisions = 4;
    public const int CollisionSize = 7 * 4 + 4 + 1;
    public const int Size = HeaderSize + FloatCount * 4 + ByteCount + MaxCollisions * CollisionSize;

    public static ReadOnlySpan<byte> Magic => "SWGR"u8;

    public uint Seq { get; private init; }
    public double SimTime { get; private init; }
    public float Dt { get; private init; }
    public FrameFlags Flags { get; private init; }
    public VehicleClass VehicleClass { get; private init; }
    /// <summary>Total contacts in the game's CollisionList this frame (may exceed Collisions.Length).</summary>
    public int CollisionCount { get; private init; }
    public float[] Floats { get; } = new float[FloatCount];
    public byte[] Bytes { get; } = new byte[ByteCount];
    public Collision[] Collisions { get; private init; } = [];

    public float this[F f] => Floats[(int)f];
    public byte this[B b] => Bytes[(int)b];
    public bool Has(FrameFlags f) => (Flags & f) == f;

    public Quaternion Rotation => new(this[F.QuatX], this[F.QuatY], this[F.QuatZ], this[F.QuatW]);

    /// <summary>Rotates a world-space direction into the vehicle frame (X forward, Y right, Z up).</summary>
    public Vector3 ToLocal(Vector3 world) => Vector3.Transform(world, Quaternion.Inverse(Rotation));

    /// <summary>Parses one frame. Returns null with a reason if the frame is malformed or from another version.</summary>
    public static TelemetryFrame? Parse(ReadOnlySpan<byte> s, out string? error)
    {
        error = null;
        if (s.Length < Size || !s[..4].SequenceEqual(Magic)) { error = "bad magic"; return null; }
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]);
        ushort size = BinaryPrimitives.ReadUInt16LittleEndian(s[6..]);
        if (version != Version || size != Size)
        {
            error = $"frame v{version}/{size}B but bridge expects v{Version}/{Size}B. Update the exporter or the bridge";
            return null;
        }

        int nColl = s[29];
        int collStart = HeaderSize + FloatCount * 4 + ByteCount;
        var colls = new Collision[Math.Min(nColl, MaxCollisions)];
        Span<float> v = stackalloc float[7];
        for (int i = 0; i < colls.Length; i++)
        {
            var c = s.Slice(collStart + i * CollisionSize, CollisionSize);
            for (int k = 0; k < 7; k++) v[k] = Finite(BinaryPrimitives.ReadSingleLittleEndian(c[(k * 4)..]));
            colls[i] = new Collision(
                new Vector3(v[0], v[1], v[2]),
                new Vector3(v[3], v[4], v[5]),
                v[6],
                BinaryPrimitives.ReadInt32LittleEndian(c[28..]),
                c[32] != 0);
        }

        var frame = new TelemetryFrame
        {
            Seq = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
            SimTime = BinaryPrimitives.ReadDoubleLittleEndian(s[12..]),
            Dt = BinaryPrimitives.ReadSingleLittleEndian(s[20..]),
            Flags = (FrameFlags)BinaryPrimitives.ReadUInt32LittleEndian(s[24..]),
            VehicleClass = (VehicleClass)s[28],
            CollisionCount = nColl,
            Collisions = colls,
        };
        int o = HeaderSize;
        // A NaN/∞ from the game would stick in the FFB low-pass filters for the rest of the session; read it as 0.
        for (int i = 0; i < FloatCount; i++, o += 4)
            frame.Floats[i] = Finite(BinaryPrimitives.ReadSingleLittleEndian(s[o..]));
        for (int i = 0; i < ByteCount; i++, o++)
            frame.Bytes[i] = s[o];
        return frame;
    }

    private static float Finite(float v) => float.IsFinite(v) ? v : 0;
}
