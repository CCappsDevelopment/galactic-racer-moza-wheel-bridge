namespace WheelBridge;

/// <summary>FFB settings (the "Ffb" section of wheelbridge.json). Torque values are fractions of full motor force.</summary>
public sealed class FfbConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Overall strength, 0..1. 1 = the feel the profiles were tuned for on a 3.9 Nm R3. The bridge also scales by the
    /// connected base (see <see cref="BaseScale"/>), so 1 on an R21 is not 21 Nm.
    /// </summary>
    public float MasterGain { get; set; } = 1.0f;
    /// <summary>
    /// Output scale for the wheelbase: 0 = automatic from the detected model (<see cref="Wheelbase.AutoScale"/>,
    /// conservative for untested bases), otherwise a fixed fraction of the base's full torque chosen by the user.
    /// </summary>
    public float BaseScale { get; set; } = 0;
    /// <summary>+1 or -1 so that positive torque turns the wheel toward positive angle (right). Set by the [T] motor test.</summary>
    public int ForceSign { get; set; } = 1;
    /// <summary>
    /// The motor test has measured ForceSign on this wheelbase. Until then no force is sent: with the wrong sign every
    /// spring pushes away from center (force center slammed the wheel from stop to stop on a fresh install).
    /// </summary>
    public bool ForceSignTested { get; set; }
    /// <summary>Used only if no MOZA base (USB vendor 346E) is found: part of the DirectInput product name to drive.</summary>
    public string Device { get; set; } = "MOZA";
    public int UpdateHz { get; set; } = 500;
    /// <summary>Telemetry older than this is treated as lost and forces fade out.</summary>
    public float StaleMs { get; set; } = 120;
    /// <summary>Fade all forces out while the game is paused (telemetry frozen).</summary>
    public bool MuteWhenPaused { get; set; } = true;
    /// <summary>Fade all forces out while the vehicle is not under player control (menus, results) or after the finish line.</summary>
    public bool MuteWhenNotRacing { get; set; } = true;

    /// <summary>Per-layer gain and on/off, in display order. Number keys 1–9/0 toggle them on the dashboard.</summary>
    public Dictionary<string, FfbLayer> Layers { get; set; } = new()
    {
        [FfbLayers.Centering] = new(0.35f, "speed-scaled centering spring"),
        [FfbLayers.Aligning] = new(0.45f, "self-aligning torque from slip angle (lightens past the peak)"),
        [FfbLayers.Load] = new(0.30f, "cornering load from lateral acceleration"),
        [FfbLayers.Damping] = new(0.25f, "speed-scaled damping on wheel velocity"),
        [FfbLayers.Impacts] = new(0.80f, "directional kicks on new contacts"),
        [FfbLayers.Scrape] = new(0.20f, "vibration while a contact persists"),
        [FfbLayers.Road] = new(0.05f, "speed-scaled road texture"),
        [FfbLayers.Engine] = new(0.03f, "engine hum from RPM"),
        [FfbLayers.Boost] = new(0.12f, "afterburner / Ramjet buzz and overheat warning"),
        [FfbLayers.Landing] = new(0.60f, "landing thump by grade"),
        [FfbLayers.Rumble] = new(0.25f, "the game's own controller rumble"),
        // Off unless a profile turns it up: only the speeder bike has been tuned with it.
        [FfbLayers.Lean] = new(0f, "light weight from the bike's lean angle"),
    };

    // Force center (the GUI's pin-the-wheel toggle).
    public float CenterLockForce { get; set; } = 1.0f;       // torque limit while pinned (1 = full R3 torque; scaled by BaseScale)
    // The hold runs on the PC, so every correction reaches the motor a few ms late. A stiff spring (the first
    // version reached full force at 4°) overshoots and buzzes. A soft spring with light damping stays stable in a
    // simulation with 6–20 ms delay and 0.015–0.08 kg·m² wheel inertia (2026-10-14).
    public float CenterLockFullAtDeg { get; set; } = 20f;    // spring reaches ~76% force this far off center (tanh), ~96% at twice it
    public float CenterLockDampingDps { get; set; } = 400f;  // filtered wheel speed (deg/s) that gives full damping force
    public float CenterLockDeadbandDeg { get; set; } = 0.2f; // no spring this close to center, so sensor jitter can't buzz the motor
    public float CenterLockSweepDps { get; set; } = 180f;    // how fast the wheel is carried to center when the pin engages

    // Shape parameters (from the 2026-10-13 tuning recording).
    public float AligningPeakSlipDeg { get; set; } = 10f;
    public float LoadReference { get; set; } = 12f;        // lateral "g" (v·yaw/981) that gives full load; p90 was 11.5
    public float ImpactReference { get; set; } = 3000f;    // PointVelocityDelta for a full-strength kick; p90 2191, p99 5055
    public float ImpactDecayMs { get; set; } = 70f;
    public float AirCentering { get; set; } = 0.15f;       // fraction of centering kept while airborne
    public float DriftLightening { get; set; } = 0.5f;     // how much centering/load drop while drifting
    public float WreckCentering { get; set; } = 0.2f;      // gentle centering while wrecked
    // Lean (2026-10-13 bike lap): Roll reaches ±1.5 at full lean, p95 |Roll| 1.42; lean follows steering by ~0.2 s.
    public float LeanReference { get; set; } = 1.0f;       // |Roll| that gives ~76% lean weight (tanh)

    /// <summary>
    /// The per-vehicle feel a profile carries (plus every layer's gain and on/off). Everything else belongs to the
    /// wheelbase or the user: above all MasterGain, so a profile tuned on a 3.9 Nm R3 can never switch a 21 Nm base
    /// to full torque; also ForceSign, Device, the update rate, mute choices and the force-center hold.
    /// </summary>
    private static readonly string[] FeelProps =
    [
        nameof(AligningPeakSlipDeg), nameof(LoadReference), nameof(ImpactReference), nameof(ImpactDecayMs),
        nameof(AirCentering), nameof(DriftLightening), nameof(WreckCentering),
        nameof(LeanReference),
    ];

    /// <summary>
    /// Copies the feel settings of another config into this one in place (the FFB thread holds references to
    /// this object and its layers, so they are updated rather than replaced).
    /// </summary>
    public void CopyFeelFrom(FfbConfig other)
    {
        foreach (var name in FeelProps)
        {
            var p = typeof(FfbConfig).GetProperty(name)!;
            p.SetValue(this, p.GetValue(other));
        }
        foreach (var (name, src) in other.Layers)
            if (Layers.TryGetValue(name, out var dst)) { dst.Gain = src.Gain; dst.Enabled = src.Enabled; }
    }

    /// <summary>Restores every setting <see cref="CopyFeelFrom"/> does not cover to its default (the GUI's full reset).</summary>
    public void ResetNonFeel()
    {
        var d = new FfbConfig();
        CenterLockForce = d.CenterLockForce; CenterLockFullAtDeg = d.CenterLockFullAtDeg;
        CenterLockDampingDps = d.CenterLockDampingDps; CenterLockDeadbandDeg = d.CenterLockDeadbandDeg;
        CenterLockSweepDps = d.CenterLockSweepDps; StaleMs = d.StaleMs;
        MuteWhenPaused = d.MuteWhenPaused; MuteWhenNotRacing = d.MuteWhenNotRacing;
    }

    /// <summary>Fixes nulls and out-of-range values from a hand-edited file (see <see cref="BridgeConfig.Sanitize"/>).</summary>
    public void Sanitize()
    {
        static float C(float v, float min, float max, float fallback) => BridgeConfig.Clamp(v, min, max, fallback);
        var d = new FfbConfig();
        Device ??= d.Device;
        Layers ??= [];
        foreach (var key in Layers.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) Layers.Remove(key);
        // Layers added in newer builds get their defaults; existing tuning is kept.
        foreach (var (name, layer) in d.Layers) Layers.TryAdd(name, layer);
        foreach (var layer in Layers.Values) { layer.Gain = C(layer.Gain, 0, 2, 0); layer.Description ??= ""; }
        MasterGain = C(MasterGain, 0, 1, d.MasterGain);
        ForceSign = ForceSign < 0 ? -1 : 1;
        UpdateHz = Math.Clamp(UpdateHz, 50, 1000);
        StaleMs = C(StaleMs, 30, 2000, d.StaleMs);
        CenterLockForce = C(CenterLockForce, 0, 1, d.CenterLockForce);
        CenterLockFullAtDeg = C(CenterLockFullAtDeg, 1, 180, d.CenterLockFullAtDeg);
        CenterLockDampingDps = C(CenterLockDampingDps, 10, 5000, d.CenterLockDampingDps);
        CenterLockDeadbandDeg = C(CenterLockDeadbandDeg, 0, 10, d.CenterLockDeadbandDeg);
        CenterLockSweepDps = C(CenterLockSweepDps, 1, 2000, d.CenterLockSweepDps);
        AligningPeakSlipDeg = C(AligningPeakSlipDeg, 0.5f, 90, d.AligningPeakSlipDeg);
        LoadReference = C(LoadReference, 0.5f, 100, d.LoadReference);
        ImpactReference = C(ImpactReference, 50, 50000, d.ImpactReference);
        ImpactDecayMs = C(ImpactDecayMs, 5, 2000, d.ImpactDecayMs);
        AirCentering = C(AirCentering, 0, 1, d.AirCentering);
        DriftLightening = C(DriftLightening, 0, 1, d.DriftLightening);
        WreckCentering = C(WreckCentering, 0, 1, d.WreckCentering);
        LeanReference = C(LeanReference, 0.05f, 5, d.LeanReference);
        BaseScale = C(BaseScale, 0, 1, 0);
    }
}

public sealed class FfbLayer(float gain, string description)
{
    public float Gain { get; set; } = gain;
    public bool Enabled { get; set; } = true;
    public string Description { get; set; } = description;
    public FfbLayer() : this(0, "") { }
}

public static class FfbLayers
{
    public const string Centering = "Centering", Aligning = "Aligning", Load = "Load", Damping = "Damping",
        Impacts = "Impacts", Scrape = "Scrape", Road = "Road", Engine = "Engine", Boost = "Boost",
        Landing = "Landing", Rumble = "Rumble", Lean = "Lean";
}
