namespace WheelBridge;

/// <summary>
/// [C] calibration: start with all pedals released, then press each pedal fully once within the window.
/// Released = reading at start; Pressed = reading furthest from it. Pedals that never moved are left as they were.
/// </summary>
public sealed class PedalCalibrator
{
    public const double WindowSeconds = 8;
    private const int MinTravel = 2000;

    private DateTime _start;
    private int[] _released = new int[3];
    private int[] _extreme = new int[3];

    public bool Active { get; private set; }
    public double SecondsLeft => Math.Max(0, WindowSeconds - (DateTime.UtcNow - _start).TotalSeconds);

    public void Begin(WheelState s)
    {
        _released = [s.RawThrottle, s.RawBrake, s.RawClutch];
        _extreme = [.. _released];
        _start = DateTime.UtcNow;
        Active = true;
    }

    /// <summary>Feeds a sample; returns a summary once the window closes and the config has been updated.</summary>
    public string? Update(WheelState s, BridgeConfig cfg)
    {
        if (!Active) return null;
        int[] raw = [s.RawThrottle, s.RawBrake, s.RawClutch];
        for (int i = 0; i < 3; i++)
            if (Math.Abs(raw[i] - _released[i]) > Math.Abs(_extreme[i] - _released[i])) _extreme[i] = raw[i];
        if (SecondsLeft > 0) return null;

        Active = false;
        var done = new List<string>();
        PedalRange? Make(int i, string name)
        {
            if (Math.Abs(_extreme[i] - _released[i]) < MinTravel) return null;
            done.Add($"{name} {_released[i]}→{_extreme[i]}");
            return new PedalRange { Released = _released[i], Pressed = _extreme[i] };
        }
        cfg.Throttle = Make(0, "throttle") ?? cfg.Throttle;
        cfg.Brake = Make(1, "brake") ?? cfg.Brake;
        cfg.Clutch = Make(2, "clutch") ?? cfg.Clutch;
        return done.Count == 0 ? "Calibration: no pedal moved enough, nothing changed" : "Calibrated " + string.Join(", ", done);
    }
}
