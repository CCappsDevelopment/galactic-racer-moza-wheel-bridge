using System.Text.Json;
using System.Text.Json.Serialization;

namespace WheelBridge;

/// <summary>User settings, persisted as wheelbridge.json next to the project. Edit the file while the bridge is closed.</summary>
public sealed class BridgeConfig
{
    /// <summary>Total wheel rotation (lock to lock, degrees) that maps to full stick travel. Arcade handling likes 270–540.</summary>
    public float SteeringLockDegrees { get; set; } = 360;
    /// <summary>Response exponent: 1 = linear, &gt;1 = softer around center.</summary>
    public float SteeringLinearity { get; set; } = 1.0f;
    /// <summary>Stick output added just outside center to cancel the game's own deadzone (0–0.3). Tune from the dashboard.</summary>
    public float SteeringAntiDeadzone { get; set; } = 0.0f;
    public bool InvertSteering { get; set; }

    /// <summary>Raw pedal travel (from calibration). Null until calibrated with [C].</summary>
    public PedalRange? Throttle { get; set; }
    public PedalRange? Brake { get; set; }
    public PedalRange? Clutch { get; set; }
    /// <summary>Fraction of pedal travel ignored at the top and bottom.</summary>
    public float PedalDeadzone { get; set; } = 0.03f;

    /// <summary>Which pedal drives which trigger/button. Values: Throttle, Brake, Clutch, None.</summary>
    public string RightTriggerPedal { get; set; } = "Throttle";
    public string LeftTriggerPedal { get; set; } = "Brake";

    /// <summary>
    /// Xbox control → wheel button numbers (0-based, as shown on the dashboard).
    /// Xbox names: A, B, X, Y, LB, RB, Back, Start, LS, RS, Up, Down, Left, Right, Guide.
    /// Game defaults: A boost, RS ramjet mode, X drift/maneuver, B malfunction recovery, LB ability,
    /// RB camera, D-pad ability select, Start pause.
    /// </summary>
    public Dictionary<string, int[]> Buttons { get; set; } = new()
    {
        ["A"] = [], ["B"] = [], ["X"] = [], ["Y"] = [],
        ["LB"] = [], ["RB"] = [], ["RS"] = [], ["Start"] = [], ["Back"] = [],
        ["Up"] = [], ["Down"] = [], ["Left"] = [], ["Right"] = [],
    };

    /// <summary>
    /// Pedals that act as Xbox buttons once pressed past Threshold (0..1 of calibrated travel).
    /// e.g. { "Button": "X", "Pedal": "Clutch", "Threshold": 0.5 }: 0.5 is the raw axis crossing zero on a full-range pedal.
    /// </summary>
    public List<PedalButton> PedalButtons { get; set; } = [];

    /// <summary>Wheel buttons that press RT / LT fully (e.g. paddles for a wheel without pedals). Combined with the trigger's pedal.</summary>
    public Dictionary<string, int[]> TriggerButtons { get; set; } = new() { ["RT"] = [], ["LT"] = [] };

    public FfbConfig Ffb { get; set; } = new();

    /// <summary>Send nothing to the virtual pad (keep it connected but neutral), handy while setting up.</summary>
    public bool PadOutputEnabled { get; set; } = true;

    /// <summary>Hold the virtual pad neutral while Shade is on foot in the hub, so another controller/keyboard drives.</summary>
    public bool PausePadOnFoot { get; set; } = true;

    /// <summary>The first-run setup (motor test, pedal calibration, controls) was finished or skipped in the GUI.</summary>
    public bool SetupDone { get; set; }

    /// <summary>Name of the profile last loaded or saved from the GUI (informational).</summary>
    public string? ActiveProfile { get; set; }

    /// <summary>Load the vehicle's profile from <see cref="VehicleProfiles"/> whenever the player gets into another vehicle type.</summary>
    public bool AutoSwitchProfiles { get; set; } = true;
    /// <summary>Vehicle class (Car, Bike, Skimmer, Pod) → profile name. A missing or empty entry leaves the settings as they are.</summary>
    public Dictionary<string, string> VehicleProfiles { get; set; } = DefaultVehicleProfiles();

    public static Dictionary<string, string> DefaultVehicleProfiles() => new()
    {
        [nameof(VehicleClass.Car)] = "Land Speeder",
        [nameof(VehicleClass.Bike)] = "Speeder Bike",
        [nameof(VehicleClass.Skimmer)] = "Skim Speeder",
        [nameof(VehicleClass.Pod)] = "Podracer",
    };

    [JsonIgnore] public string? LoadedFrom { get; private set; }
    /// <summary>Set when the config file could not be read and defaults were used (the bad file is kept as .bad).</summary>
    [JsonIgnore] public string? LoadWarning { get; private set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly object SaveLock = new();

    public static BridgeConfig Load(string path)
    {
        BridgeConfig cfg = new();
        string? warning = null;
        if (File.Exists(path))
        {
            try { cfg = JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(path), Json) ?? new(); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                // A hand-edit gone wrong must not stop the bridge from starting. Keep the broken file for reference.
                string bad = path + ".bad";
                try { File.Copy(path, bad, overwrite: true); } catch { }
                warning = $"{Path.GetFileName(path)} could not be read ({ex.Message}). Started with defaults; the old file is saved as {Path.GetFileName(bad)}.";
                cfg = new();
            }
        }
        cfg.Sanitize();
        cfg.Save(path);
        cfg.LoadedFrom = path;
        cfg.LoadWarning = warning;
        return cfg;
    }

    /// <summary>Writes to a temporary file first, so a crash or power cut mid-save never leaves a half-written config.</summary>
    public void Save(string path)
    {
        lock (SaveLock)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); return; }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 3)
                {
                    Thread.Sleep(30);   // an antivirus scan or editor briefly holding the file
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still held open (e.g. by an editor that allows writing): write in place instead of failing.
                    File.Copy(tmp, path, overwrite: true);
                    File.Delete(tmp);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Repairs what a hand-edited file can get wrong: missing sections (null), unknown layers' defaults, and values
    /// outside the ranges the model is safe with (a zero divisor or NaN would silently kill all force).
    /// </summary>
    public void Sanitize()
    {
        SteeringLockDegrees = Clamp(SteeringLockDegrees, 90, 2520, 360);
        SteeringLinearity = Clamp(SteeringLinearity, 0.1f, 5, 1);
        SteeringAntiDeadzone = Clamp(SteeringAntiDeadzone, 0, 0.9f, 0);
        PedalDeadzone = Clamp(PedalDeadzone, 0, 0.4f, 0.03f);
        RightTriggerPedal ??= "Throttle";
        LeftTriggerPedal ??= "Brake";
        Buttons ??= [];
        foreach (var key in Buttons.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) Buttons[key] = [];
        TriggerButtons ??= [];
        foreach (var t in PadMapping.Triggers) if (!TriggerButtons.TryGetValue(t, out var v) || v is null) TriggerButtons[t] = [];
        PedalButtons = PedalButtons?.Where(p => p is not null).ToList() ?? [];
        foreach (var pb in PedalButtons) { pb.Button ??= ""; pb.Pedal ??= "Clutch"; pb.Threshold = Clamp(pb.Threshold, 0.01f, 1, 0.5f); }
        VehicleProfiles ??= [];
        foreach (var (k, v) in DefaultVehicleProfiles()) VehicleProfiles.TryAdd(k, v);
        Ffb ??= new();
        Ffb.Sanitize();
    }

    internal static float Clamp(float v, float min, float max, float fallback) => float.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    /// <summary>
    /// Applies a profile's feel settings (steering response + all FFB tuning) in place. Hardware-specific
    /// settings (pedal calibration, button map, ForceSign, FFB device) stay as they are.
    /// </summary>
    public void ApplyProfile(BridgeConfig p)
    {
        SteeringLockDegrees = p.SteeringLockDegrees;
        SteeringLinearity = p.SteeringLinearity;
        SteeringAntiDeadzone = p.SteeringAntiDeadzone;
        InvertSteering = p.InvertSteering;
        PedalDeadzone = p.PedalDeadzone;
        Ffb.CopyFeelFrom(p.Ffb);
    }

    /// <summary>JSON of just the profile (feel) settings; equal fingerprints mean nothing a profile stores differs.</summary>
    public string FeelFingerprint()
    {
        var c = new BridgeConfig();
        c.ApplyProfile(this);
        return JsonSerializer.Serialize(c, Json);
    }

    public static BridgeConfig ReadFile(string path)
    {
        var c = JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(path), Json) ?? new();
        c.Sanitize();
        return c;
    }
}

public sealed class PedalButton
{
    public string Button { get; set; } = "";
    public string Pedal { get; set; } = "Clutch";
    public float Threshold { get; set; } = 0.5f;
}

public sealed class PedalRange
{
    public int Released { get; set; }
    public int Pressed { get; set; }

    /// <summary>Maps a raw reading to 0..1 using the calibrated travel (works whichever direction the axis runs).</summary>
    public float Normalize(int raw, float deadzone)
    {
        float span = Pressed - Released;
        if (Math.Abs(span) < 1) return 0;
        float t = (raw - Released) / span;
        deadzone = Math.Clamp(deadzone, 0, 0.45f);   // 0.5 would divide by zero
        t = (t - deadzone) / (1 - 2 * deadzone);
        return Math.Clamp(t, 0, 1);
    }
}
