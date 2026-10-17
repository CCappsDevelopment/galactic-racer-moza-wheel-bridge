using System.Numerics;

namespace WheelBridge;

/// <summary>
/// Deterministic FFB synthesis. Given the same telemetry frames, wheel motion and tick times it produces the same torque.
/// Sign convention: positive torque turns the wheel toward positive angle (right); positive slip/yaw/local Y = right.
/// Call <see cref="OnFrame"/> for every telemetry frame and <see cref="Step"/> at the FFB rate.
/// </summary>
public sealed class FfbModel(FfbConfig cfg)
{
    public static readonly string[] LayerOrder =
    [
        FfbLayers.Centering, FfbLayers.Aligning, FfbLayers.Load, FfbLayers.Damping, FfbLayers.Impacts,
        FfbLayers.Scrape, FfbLayers.Road, FfbLayers.Engine, FfbLayers.Boost, FfbLayers.Landing, FfbLayers.Rumble,
        FfbLayers.Lean,
    ];

    private sealed record Impact(double Start, float Amplitude);

    private TelemetryFrame? _frame;
    private double _frameTime = double.NegativeInfinity;   // engine clock (s) when _frame arrived
    private readonly Dictionary<int, (int frames, float pvd, float side)> _contacts = [];
    private readonly HashSet<int> _seen = [];
    private readonly List<Impact> _impacts = [];
    private double _landingStart = double.NegativeInfinity;
    private float _landingAmp;
    private byte _lastLandingGrade;

    // Low-passed signals (the telemetry arrives at 60–80 Hz; the FFB runs at 500 Hz).
    private float _speed, _slip, _latG, _drift, _ground, _rpm, _wheelVel, _lean;
    private float _leanRaw;   // from the latest frame: lean (+ = leaning right)
    private double _distance;   // metres travelled while on the ground; phases the road texture
    private float _liveness;    // 0..1 fade for stale/missing/paused telemetry
    private (float, float, float, float, float, float, float, float) _lastSig;
    private int _frozenFrames;

    /// <summary>Per-layer torque from the last Step, in LayerOrder.</summary>
    public float[] Contributions { get; } = new float[LayerOrder.Length];
    public bool Wrecked { get; private set; }
    public bool Airborne { get; private set; }
    public float Liveness => _liveness;
    /// <summary>The game is paused: frames keep arriving but the vehicle state is bit-for-bit frozen while moving.</summary>
    public bool Paused { get; private set; }
    /// <summary>The vehicle is not under player control (InputMode Idle: menus, results, attract) or the player has finished.</summary>
    public bool NotRacing { get; private set; }
    /// <summary>The player has crossed the finish line (or timed out / was eliminated); the car may still be rolling.</summary>
    public bool RaceDone { get; private set; }
    /// <summary>Live telemetry from a vehicle the player is racing (grid onward, not paused, not finished).</summary>
    public bool Racing { get; private set; }
    /// <summary>Why forces are (not) playing, for display.</summary>
    public string State { get; private set; } = "no vehicle";

    public void OnFrame(TelemetryFrame f, double now)
    {
        if (!f.Has(FrameFlags.HasVehicle))
        {
            _frame = null;
            Paused = NotRacing = RaceDone = Wrecked = Airborne = false;
            _contacts.Clear();
            _impacts.Clear();
            return;
        }

        // Pause detection. While the pause menu is open the exporter still sends frames, but velocity and
        // orientation repeat exactly (the 2026-10-13 recording opens with 1.6 s frozen at 249 km/h). A moving
        // vehicle never repeats its quaternion bit for bit, so 3 identical frames at speed means paused.
        var sig = (f[F.RelVelX], f[F.RelVelY], f[F.RelVelZ], f[F.AngVelZ], f[F.QuatX], f[F.QuatY], f[F.QuatZ], f[F.QuatW]);
        _frozenFrames = sig.Equals(_lastSig) ? _frozenFrames + 1 : 0;
        _lastSig = sig;
        Paused = _frozenFrames >= 3 && f[F.SpeedKmph] > 1;
        RaceDone = f.Has(FrameFlags.RaceDone);
        NotRacing = f[B.InputMode] == 0 || RaceDone;

        // Contacts: a new ID gives a kick; an ID that persists becomes a scrape.
        var seen = _seen;
        seen.Clear();
        foreach (var c in f.Collisions)
        {
            seen.Add(c.Id);
            // Side of the hit, -1 (left) .. +1 (right), scaled smoothly over ±40 cm. A hard ±30 cm cutoff dropped
            // 16 of 38 contacts on the narrow speeder bike (incl. a full-strength hit from another racer).
            float side = Math.Clamp(c.LocalLocation.Y / 40f, -1, 1);
            if (Math.Abs(side) < 0.15f) side = 0;   // dead-center (head-on / underside): no steering direction
            if (_contacts.TryGetValue(c.Id, out var known))
            {
                _contacts[c.Id] = (known.frames + 1, Math.Max(known.pvd, c.PointVelocityDelta), side);
                continue;
            }
            _contacts[c.Id] = (1, c.PointVelocityDelta, side);
            float strength = MathF.Pow(Math.Clamp(c.PointVelocityDelta / cfg.ImpactReference, 0, 1.5f), 0.7f);
            // Front hits steer harder than rear ones. The side force acts behind the steering axis (trail),
            // so the wheel snaps toward the side that was hit.
            float frontWeight = 0.5f + 0.5f * Math.Clamp(c.LocalLocation.X / 250f, 0, 1);
            if (side != 0 && strength > 0.02f) _impacts.Add(new Impact(now, side * strength * frontWeight));
        }
        // Expire old kicks here too: Step only does it while forces play, so a muted run-out full of contacts grew the list.
        float tau = cfg.ImpactDecayMs / 1000f;
        _impacts.RemoveAll(i => now - i.Start > tau * 6);
        foreach (var id in _contacts.Keys)
            if (!seen.Contains(id)) _contacts.Remove(id);   // removing during enumeration is allowed for Dictionary since .NET Core 3

        // Landing thump when the game grades a landing.
        byte grade = f[B.PerfectLandingState];
        if (grade != _lastLandingGrade && grade is 2 or 3 or 4)
        {
            _landingStart = now;
            _landingAmp = grade switch { 2 => 1.0f, 3 => 0.55f, _ => 0.35f };
        }
        _lastLandingGrade = grade;

        // Lean: the game's Roll is negative when leaning right (it follows right steer by ~0.2 s), so flip it.
        _leanRaw = -f[F.Roll];

        _frame = f;
        _frameTime = now;
    }

    /// <summary>One FFB tick. Returns total torque (-1..1 before master gain).</summary>
    public float Step(double now, float dt, float wheelAngleDeg, float wheelVelDps, float lockDeg,
                      (float large, float small, double age) rumble)
    {
        Array.Clear(Contributions);
        var f = _frame;
        bool fresh = f is not null && (now - _frameTime) * 1000 < cfg.StaleMs;
        bool muted = (cfg.MuteWhenPaused && Paused) || (cfg.MuteWhenNotRacing && NotRacing);
        State = f is null ? "no vehicle" : !fresh ? "telemetry lost" : Paused ? (muted ? "paused (muted)" : "paused")
              : NotRacing ? (RaceDone ? "race finished" : "not racing") + (cfg.MuteWhenNotRacing ? " (muted)" : "") : "driving";
        Racing = fresh && !Paused && !NotRacing;
        bool playing = fresh && !muted;
        _liveness = Approach(_liveness, playing ? 1 : 0, dt / (playing ? 0.05f : 0.2f));

        float a = wheelAngleDeg / Math.Max(10, lockDeg / 2);        // wheel position, -1..1 at lock
        // Wheel speed (~1 at a fast flick), filtered and capped so a velocity glitch can never become a big damping jolt.
        if (float.IsFinite(wheelVelDps)) _wheelVel = Lerp(_wheelVel, wheelVelDps, LowPassK(dt, 40));
        float w = Math.Clamp(_wheelVel / 500f, -1.5f, 1.5f);

        // Damping follows liveness like everything else: no force at all outside a race. With every other layer
        // faded out too there is nothing for it to stabilise.
        float speedFactor = f is null ? 0 : _speed;
        Set(FfbLayers.Damping, -(0.3f + 0.7f * speedFactor) * w * _liveness);

        if (f is not null && _liveness > 0)
        {
            Wrecked = f[F.CrashTimeRemaining] > 0;
            float k = LowPassK(dt, 25);
            _speed = Lerp(_speed, Math.Clamp(f[F.SpeedKmph] / Math.Max(100, f[F.TopSpeedKmph]), 0, 1.3f), k);
            _slip = Lerp(_slip, f[F.SlipDeg], k);
            _latG = Lerp(_latG, f[F.RelVelX] * f[F.AngVelZ] / 981f, k);
            _drift = Lerp(_drift, f.Has(FrameFlags.Drifting) ? 1 : 0, LowPassK(dt, 4));   // also debounces the flickering flag
            _ground = Lerp(_ground, f[F.OnGroundScale], LowPassK(dt, 8));
            _rpm = Lerp(_rpm, f[F.RpmScale], k);
            Airborne = _ground < 0.5f;
            _distance += _ground * f[F.SpeedKmph] / 3.6 * dt;

            float live = _liveness;
            float grip = Lerp(cfg.AirCentering, 1, _ground);
            float light = 1 - cfg.DriftLightening * _drift;

            if (Wrecked)
            {
                Set(FfbLayers.Centering, -cfg.WreckCentering * a * live);
            }
            else
            {
                Set(FfbLayers.Centering, -(0.15f + 0.85f * _speed) * a * grip * light * live);

                // Aligning torque points the wheel toward the direction of travel (countersteer), peaks at
                // AligningPeakSlipDeg and fades beyond it, like pneumatic trail collapsing.
                float s = _slip / cfg.AligningPeakSlipDeg;
                float sat = s * MathF.Exp(1 - MathF.Abs(s));
                if (MathF.Abs(_slip) > 90) sat = 0;                // spinning backwards: no meaningful aligning force
                Set(FfbLayers.Aligning, sat * Math.Min(1, _speed * 2) * _ground * live);

                // Load resists the turn: right turn (positive yaw) pushes the wheel left.
                Set(FfbLayers.Load, -MathF.Tanh(_latG / cfg.LoadReference) * _ground * light * live);

                // Lean gives a leaning vehicle (the speeder bike) a little cornering weight: holding a lean pushes
                // the wheel back toward upright. Keep it light. The lean trails the steering by ~0.2 s, so this
                // spring acts late: strong, it keeps pushing after you start to unwind and the wheel runs past
                // center. (A lean-in "kick" on the lean rate made that worse while unwinding; removed 2026-10-16.)
                _lean = Lerp(_lean, _leanRaw, LowPassK(dt, 10));
                Set(FfbLayers.Lean, -MathF.Tanh(_lean / cfg.LeanReference) * Math.Min(1, _speed * 2) * _ground * live);

                // Road texture: two incommensurate sines phased by distance travelled.
                double d = _distance;
                float road = (float)(Math.Sin(d * 2.1) * 0.6 + Math.Sin(d * 5.3 + 1.3) * 0.4);
                Set(FfbLayers.Road, road * _speed * _ground * live);

                // Scrape: 38 Hz buzz while any contact has lasted 3+ frames.
                float scrape = 0;
                foreach (var (_, (frames, pvd, _)) in _contacts)
                    if (frames >= 3) scrape = Math.Max(scrape, Math.Clamp(pvd / 1500f, 0.2f, 1));
                Set(FfbLayers.Scrape, scrape * (float)Math.Sin(now * 2 * Math.PI * 38) * live);

                // Engine hum: 25–65 Hz with RPM, scaled by throttle.
                Set(FfbLayers.Engine, _rpm * f[F.Throttle] * (float)Math.Sin(now * 2 * Math.PI * (25 + 40 * _rpm)) * live);

                // Boost: afterburner rumble, Ramjet buzz rising with heat, pulsing when critical / overheated.
                byte ramjet = f[B.RamjetState], boost = f[B.BoostState];
                float buzz = 0;
                if (ramjet is 2) buzz = 0.4f * (float)Math.Sin(now * 2 * Math.PI * 45);
                else if (ramjet is 3) buzz = (0.4f + 0.6f * f[F.RamjetHeat]) * (float)Math.Sin(now * 2 * Math.PI * 55);
                else if (ramjet is 4 or 5 or 6)
                    buzz = (Math.Sin(now * 2 * Math.PI * 6) > 0 ? 1f : 0.2f) * (float)Math.Sin(now * 2 * Math.PI * 70);
                else if (boost is 2) buzz = 0.5f * (float)Math.Sin(now * 2 * Math.PI * 30);
                Set(FfbLayers.Boost, buzz * live);
            }

            // Impacts: 8 ms attack, exponential decay.
            float kick = 0;
            float tau = cfg.ImpactDecayMs / 1000f;
            _impacts.RemoveAll(i => now - i.Start > tau * 6);
            foreach (var i in _impacts)
            {
                float t = (float)(now - i.Start);
                kick += i.Amplitude * Math.Min(1, t / 0.008f) * MathF.Exp(-t / tau);
            }
            Set(FfbLayers.Impacts, Math.Clamp(kick, -1.5f, 1.5f) * live);

            // Landing: damped 12 Hz oscillation over ~250 ms.
            float lt = (float)(now - _landingStart);
            if (lt is >= 0 and < 0.3f)
                Set(FfbLayers.Landing, _landingAmp * MathF.Sin(lt * 2 * MathF.PI * 12) * MathF.Exp(-lt / 0.08f) * live);
        }

        // The game's own rumble (only while it is fresh): large motor low and heavy, small motor high and light.
        // Gated by liveness like every other effect, so menu / pause-screen rumble never reaches the wheel.
        if (rumble.age < 0.5)
            Set(FfbLayers.Rumble, (rumble.large * (float)Math.Sin(now * 2 * Math.PI * 20)
                                 + 0.5f * rumble.small * (float)Math.Sin(now * 2 * Math.PI * 50)) * _liveness);

        float total = 0;
        foreach (var v in Contributions) total += v;
        if (!float.IsFinite(total))
        {
            // Never send NaN to the motor, and don't let it live on in the filters: start them over.
            Array.Clear(Contributions);
            _speed = _slip = _latG = _drift = _ground = _rpm = _wheelVel = _lean = 0;
            _distance = 0;
            return 0;
        }
        return Math.Clamp(total, -1, 1);
    }

    private void Set(string layer, float raw)
    {
        int i = Array.IndexOf(LayerOrder, layer);
        var l = cfg.Layers.GetValueOrDefault(layer);
        Contributions[i] = l is { Enabled: true } ? raw * l.Gain : 0;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float Approach(float v, float target, float step) => v < target ? Math.Min(target, v + step) : Math.Max(target, v - step);
    private static float LowPassK(float dt, float cutoffHz) => 1 - MathF.Exp(-dt * 2 * MathF.PI * cutoffHz);
}
