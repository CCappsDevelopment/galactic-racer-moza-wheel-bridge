using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WheelBridge;

/// <summary>Runs <see cref="FfbModel"/> at a fixed rate and sends the result to the wheelbase.</summary>
public sealed class FfbEngine : IDisposable
{
    private readonly BridgeConfig _cfg;
    private readonly WheelInput _wheel;
    private readonly VirtualPad _pad;
    private readonly FfbModel _model;
    private FfbDevice _device = new();
    /// <summary>Wheel data older than this is not trusted: a spring pushing against a frozen angle would wind the wheel to lock.</summary>
    private const double WheelStaleSeconds = 0.1;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly Thread _thread;
    private volatile bool _running = true;
    private volatile float _override = float.NaN;   // motor test torque (already in motor sign); NaN = none

    private volatile bool _killed;
    public bool Killed { get => _killed; set => _killed = value; }

    private volatile bool _centerLock;
    private bool _lockActive;
    private float _lockTarget, _lockRamp, _lockVel, _lockRunaway;
    /// <summary>Pin the wheel at 0°. Switches itself off as soon as a race starts (see <see cref="CenterLockAutoReleased"/>).</summary>
    public bool CenterLock { get => _centerLock; set { _centerLock = value; if (value) CenterLockAutoReleased = false; } }
    /// <summary>The last time CenterLock went off, it was because a race started.</summary>
    public bool CenterLockAutoReleased { get; private set; }
    /// <summary>Set when force center let go because the wheel ran away from center (see <see cref="CenterLockTorque"/>); the UI clears it.</summary>
    public string? CenterLockFault { get; set; }
    /// <summary>No force is sent until the motor test has found which way this motor turns.</summary>
    public bool NeedsMotorTest => !_cfg.Ffb.ForceSignTested;
    public bool Racing => _model.Racing;
    public float LastTorque { get; private set; }
    public double LoopHz { get; private set; }
    public string DeviceStatus => _device.Status;
    public float[] Contributions { get; } = new float[FfbModel.LayerOrder.Length];
    public bool Wrecked => _model.Wrecked;
    public bool Airborne => _model.Airborne;
    public float Liveness => _model.Liveness;
    public string ModelState => _model.State;
    public bool Ready => _device.Ready;
    /// <summary>The detected base (null until the FFB device has started once).</summary>
    public Wheelbase? Base => _device.Base ?? _lastBase;
    private Wheelbase? _lastBase;
    /// <summary>
    /// The fraction of the base's full torque that Master 1.0 maps to: the user's fixed choice, or automatic from
    /// the model (1 on the R3, about R3-equivalent newton-metres with a safety margin elsewhere, minimal if unknown).
    /// </summary>
    public float BaseScale => _cfg.Ffb.BaseScale > 0 ? _cfg.Ffb.BaseScale : (Base ?? Wheelbase.Unknown()).AutoScale;

    public FfbEngine(BridgeConfig cfg, WheelInput wheel, VirtualPad pad)
    {
        _cfg = cfg;
        _wheel = wheel;
        _pad = pad;
        _model = new FfbModel(cfg.Ffb);
        _thread = new Thread(Run) { IsBackground = true, Name = "FFB", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void OnFrame(TelemetryFrame f)
    {
        lock (_gate) _model.OnFrame(f, _clock.Elapsed.TotalSeconds);
    }

    private void Run()
    {
        timeBeginPeriod(1);
        try
        {
            while (_running)
            {
                // Wait for the SDK (started by WheelInput) to see the wheelbase before creating the effect.
                while (_running && _wheel.State is null) Thread.Sleep(100);
                if (!_running) return;
                if (!_device.Ready && !_device.Start(_cfg.Ffb.Device))
                {
                    // No FFB device yet (base off, Pit House holding it, unplugged): try again rather than give up for the session.
                    _device.Dispose();
                    for (int i = 0; i < 30 && _running; i++) Thread.Sleep(100);
                    _device = new FfbDevice();
                    continue;
                }
                try { Loop(); }
                catch (Exception ex)
                {
                    // A bug in the model must never crash the bridge with force applied: zero it and start over.
                    LastError = $"{DateTime.Now:HH:mm:ss} FFB loop error: {ex.GetType().Name}: {ex.Message}";
                    try { _device.Set(0); } catch { }
                    Thread.Sleep(200);
                }
                if (_device.Lost)
                {
                    _lastBase = _device.Base ?? _lastBase;
                    _device.Dispose();
                    _device = new FfbDevice();
                }
            }
        }
        finally
        {
            _device.Dispose();
            timeEndPeriod(1);
        }
    }

    /// <summary>The last exception the FFB loop recovered from (null if none).</summary>
    public string? LastError { get; private set; }

    /// <summary>Runs ticks until the bridge stops or the device is lost for good (then <see cref="Run"/> recreates it).</summary>
    private void Loop()
    {
        double last = _clock.Elapsed.TotalSeconds, window = last;
        float lastAngle = _wheel.State?.AngleDegrees ?? 0;
        int ticks = 0;
        while (_running && !_device.Lost)
        {
            double now = _clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Clamp(now - last, 0.0005, 0.05);
            last = now;

            var s = _wheel.State;
            if (s is not null && (DateTime.UtcNow - s.Time).TotalSeconds > WheelStaleSeconds) s = null;
            float torque = 0, pin = float.NaN;
            if (s is not null)
            {
                float vel = float.IsNaN(s.VelocityDps) ? (s.AngleDegrees - lastAngle) / dt : s.VelocityDps;
                lastAngle = s.AngleDegrees;
                var r = _pad.Rumble;
                lock (_gate)
                {
                    torque = _model.Step(now, dt, s.AngleDegrees, vel, _cfg.SteeringLockDegrees,
                        (r.Large, r.Small, (DateTime.UtcNow - r.Time).TotalSeconds));
                    _model.Contributions.CopyTo(Contributions, 0);
                }
                float baseScale = BaseScale;
                torque *= _cfg.Ffb.MasterGain * baseScale;
                if (_centerLock && _model.Racing) { _centerLock = false; CenterLockAutoReleased = true; }
                pin = CenterLockTorque(s.AngleDegrees, vel, dt) * baseScale;
            }
            else _lockActive = false;

            float ov = _override;
            float output = Killed ? 0 : !float.IsNaN(ov) ? ov
                : NeedsMotorTest ? 0
                : !float.IsNaN(pin) ? pin * _cfg.Ffb.ForceSign
                : !_cfg.Ffb.Enabled ? 0 : torque * _cfg.Ffb.ForceSign;
            if (!float.IsFinite(output)) output = 0;
            _device.Set(output);
            LastTorque = output;

            ticks++;
            if (now - window >= 1) { LoopHz = ticks / (now - window); ticks = 0; window = now; }
            SleepUntil(now + 1.0 / Math.Max(50, _cfg.Ffb.UpdateHz));
        }
    }

    /// <summary>
    /// Force-center torque (positive = right), or NaN when the pin is off. The target sweeps from where the wheel
    /// was to 0° and the force limit ramps in over 0.5 s, so engaging never whips the wheel across; after that a
    /// soft-saturating spring and a damper on filtered wheel speed hold it at center.
    /// </summary>
    private float CenterLockTorque(float angle, float vel, float dt)
    {
        var c = _cfg.Ffb;
        if (!_centerLock) { _lockActive = false; return float.NaN; }
        if (!_lockActive) { _lockActive = true; _lockTarget = angle; _lockRamp = 0; _lockVel = 0; _lockRunaway = 0; }
        float sweep = Math.Max(1, c.CenterLockSweepDps), step = sweep * dt;
        bool sweeping = Math.Abs(_lockTarget) > step;
        float targetVel = sweeping ? -MathF.Sign(_lockTarget) * sweep : 0;
        _lockTarget = sweeping ? _lockTarget - MathF.Sign(_lockTarget) * step : 0;
        _lockRamp = Math.Min(1, _lockRamp + dt / 0.5f);
        if (float.IsFinite(vel)) _lockVel += (vel - _lockVel) * (1 - MathF.Exp(-dt * 2 * MathF.PI * 25));
        float err = _lockTarget - angle;
        err = MathF.CopySign(Math.Max(0, Math.Abs(err) - Math.Max(0, c.CenterLockDeadbandDeg)), err);
        float t = MathF.Tanh(err / Math.Max(1, c.CenterLockFullAtDeg))
                + (targetVel - _lockVel) / Math.Max(10, c.CenterLockDampingDps);
        float limit = Math.Clamp(c.CenterLockForce, 0, 1) * _lockRamp;

        float hold = Math.Clamp(t, -limit, limit);

        // Runaway guard: the hold always pushes toward the target, so the wheel racing away from it, or sitting far
        // off center at full force without coming back (against an end stop), means the motor turns the other way
        // (wrong ForceSign) or someone is holding it there. Either way let go rather than slam it from stop to stop.
        float toward = MathF.Sign(err) * _lockVel;   // deg/s toward the target; negative = moving away
        bool fleeing = Math.Abs(err) > 25 && toward < -120;
        bool stuck = Math.Abs(err) > 45 && toward < 20 && limit > 0 && Math.Abs(hold) >= 0.9f * limit;
        _lockRunaway = fleeing ? _lockRunaway + dt : stuck ? _lockRunaway + dt / 3 : Math.Max(0, _lockRunaway - dt);
        if (_lockRunaway > 0.25f)
        {
            _centerLock = false;
            _lockActive = false;
            CenterLockFault = "Force center let go: the wheel kept turning away from center. If you didn't turn it yourself, " +
                              "the motor direction is wrong. Run the Motor test with your hands off the wheel.";
            return float.NaN;
        }
        return hold;
    }

    private void SleepUntil(double t)
    {
        while (true)
        {
            double remaining = t - _clock.Elapsed.TotalSeconds;
            if (remaining <= 0) return;
            if (remaining > 0.0015) Thread.Sleep(1); else Thread.SpinWait(50);
        }
    }

    /// <summary>
    /// Applies a small push for 300 ms and watches which way the wheel turns, then sets ForceSign so that
    /// positive torque means "turn right" and marks it tested (no force is sent before that). Hands off the wheel while it runs.
    /// </summary>
    public async Task<(bool Ok, string Message)> RunMotorTestAsync()
    {
        if (!_device.Ready) return (false, "Motor test: FFB device not running: " + _device.Status);
        if (Killed) return (false, "Motor test: FFB is KILLED. Resume it first");
        var before = _wheel.State?.AngleDegrees;
        if (before is null) return (false, "Motor test: no wheel data");
        _override = 0.12f;
        await Task.Delay(300);
        var after = _wheel.State?.AngleDegrees;
        _override = 0f;
        await Task.Delay(150);
        _override = float.NaN;
        if (after is null) return (false, "Motor test: lost wheel data");
        float moved = after.Value - before.Value;
        if (Math.Abs(moved) < 1) return (false, $"Motor test: the wheel only moved {moved:F1}°. Were your hands on it? Nothing changed");
        _cfg.Ffb.ForceSign = moved > 0 ? 1 : -1;
        _cfg.Ffb.ForceSignTested = true;
        return (true, $"Motor test: +torque moved the wheel {moved:+0.0;-0.0}°, ForceSign {_cfg.Ffb.ForceSign}");
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(1000);
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
}
