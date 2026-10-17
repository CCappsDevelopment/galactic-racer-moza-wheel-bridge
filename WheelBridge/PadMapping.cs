namespace WheelBridge;

/// <summary>
/// Wheel state → virtual Xbox pad values, from the bindings in the config. Pure (no I/O), so the pad thread, the
/// Controls tab preview and the tests all use the same code.
/// </summary>
public static class PadMapping
{
    /// <summary>Bindable Xbox buttons, in display order (Guide is left out: it opens the Xbox overlay).</summary>
    public static readonly string[] Buttons = ["A", "B", "X", "Y", "LB", "RB", "LS", "RS", "Back", "Start", "Up", "Down", "Left", "Right"];
    public static readonly string[] Triggers = ["RT", "LT"];
    public static readonly string[] Pedals = ["Throttle", "Brake", "Clutch"];

    /// <summary>What the game does with each control (game defaults), for the Controls tab.</summary>
    public static readonly Dictionary<string, string> GameFunction = new()
    {
        ["RT"] = "Accelerate", ["LT"] = "Brake / reverse",
        ["A"] = "Boost", ["B"] = "Malfunction recovery", ["X"] = "Drift / maneuver", ["Y"] = "",
        ["LB"] = "Ability", ["RB"] = "Camera", ["LS"] = "", ["RS"] = "Ramjet mode",
        ["Back"] = "", ["Start"] = "Pause", ["Up"] = "Ability select", ["Down"] = "Ability select",
        ["Left"] = "Ability select", ["Right"] = "Ability select",
    };

    public static float PedalValue(string? which, WheelState s, BridgeConfig cfg) => which switch
    {
        "Throttle" => cfg.Throttle?.Normalize(s.RawThrottle, cfg.PedalDeadzone) ?? 0,
        "Brake" => cfg.Brake?.Normalize(s.RawBrake, cfg.PedalDeadzone) ?? 0,
        "Clutch" => cfg.Clutch?.Normalize(s.RawClutch, cfg.PedalDeadzone) ?? 0,
        _ => 0,
    };

    private static bool AnyPressed(Dictionary<string, int[]> map, string control, WheelState s)
    {
        if (!map.TryGetValue(control, out var indices) || indices is null) return false;
        foreach (int i in indices) if (s.Pressed.Contains(i)) return true;
        return false;
    }

    /// <summary>
    /// Fills <paramref name="down"/> (indexed like <see cref="Buttons"/>) and returns stick X (-1..1) and the triggers (0..1).
    /// A trigger takes the larger of its pedal and its buttons (a held button = fully pressed), so a wheel without
    /// pedals can accelerate and brake on paddles.
    /// </summary>
    public static (float Stick, float Rt, float Lt) Map(WheelState s, BridgeConfig cfg, bool[] down)
    {
        // Read each collection once: the GUI replaces them (never edits them in place) while this runs on the pad thread.
        var buttons = cfg.Buttons;
        var triggers = cfg.TriggerButtons;
        var pedalButtons = cfg.PedalButtons;

        float stick = VirtualPad.SteeringToStick(s.AngleDegrees, cfg);
        float rt = Math.Max(PedalValue(cfg.RightTriggerPedal, s, cfg), AnyPressed(triggers, "RT", s) ? 1 : 0);
        float lt = Math.Max(PedalValue(cfg.LeftTriggerPedal, s, cfg), AnyPressed(triggers, "LT", s) ? 1 : 0);
        for (int b = 0; b < Buttons.Length; b++)
        {
            bool d = AnyPressed(buttons, Buttons[b], s);
            if (!d)
                foreach (var pb in pedalButtons)
                    if (string.Equals(pb.Button, Buttons[b], StringComparison.OrdinalIgnoreCase) && PedalValue(pb.Pedal, s, cfg) >= pb.Threshold) { d = true; break; }
            down[b] = d;
        }
        return (stick, rt, lt);
    }

    /// <summary>Human-readable list of everything bound to a control, e.g. "wheel 14, wheel 22, clutch ≥ 50%".</summary>
    public static string Describe(string control, BridgeConfig cfg)
    {
        var parts = new List<string>();
        if (control == "RT" && cfg.RightTriggerPedal is "Throttle" or "Brake" or "Clutch") parts.Add(cfg.RightTriggerPedal.ToLowerInvariant() + " pedal");
        if (control == "LT" && cfg.LeftTriggerPedal is "Throttle" or "Brake" or "Clutch") parts.Add(cfg.LeftTriggerPedal.ToLowerInvariant() + " pedal");
        var map = control is "RT" or "LT" ? cfg.TriggerButtons : cfg.Buttons;
        if (map.TryGetValue(control, out var idx) && idx is not null) parts.AddRange(idx.Select(i => $"wheel {i}"));
        if (control is not ("RT" or "LT"))
            parts.AddRange(cfg.PedalButtons.Where(p => string.Equals(p.Button, control, StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{p.Pedal.ToLowerInvariant()} ≥ {p.Threshold:P0}"));
        return parts.Count == 0 ? "not bound" : string.Join(", ", parts);
    }

    /// <summary>Every control a wheel button is bound to (to warn about one button doing two things).</summary>
    public static List<string> ControlsUsing(int button, BridgeConfig cfg) =>
        cfg.Buttons.Concat(cfg.TriggerButtons).Where(kv => kv.Value?.Contains(button) == true).Select(kv => kv.Key).ToList();

    // The pad thread reads these collections while the GUI edits them, so every change builds a new collection and
    // swaps the reference in one assignment (never edits in place).

    /// <summary>Binds a wheel button to a control, added to what is there or (replace) instead of it. Returns a status message.</summary>
    public static string BindButton(BridgeConfig cfg, string control, int button, bool replace = false)
    {
        var others = ControlsUsing(button, cfg).Where(c => c != control).ToList();
        bool trigger = control is "RT" or "LT";
        var map = trigger ? cfg.TriggerButtons : cfg.Buttons;
        var old = map.GetValueOrDefault(control) ?? [];
        int[] list = replace ? [button] : old.Contains(button) ? old : [.. old, button];
        var copy = new Dictionary<string, int[]>(map) { [control] = list };
        if (trigger) cfg.TriggerButtons = copy;
        else
        {
            cfg.Buttons = copy;
            if (replace) cfg.PedalButtons = cfg.PedalButtons.Where(p => !string.Equals(p.Button, control, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return $"{control}: {(replace ? "set to" : "added")} wheel button {button}" + (others.Count > 0 ? $" (note: it is also bound to {string.Join(", ", others)})" : "");
    }

    /// <summary>Binds a pedal (pressed past 50%) to a button control, added or (replace) instead of its other inputs. Returns a status message.</summary>
    public static string BindPedal(BridgeConfig cfg, string control, string pedal, bool replace = false)
    {
        bool has = cfg.PedalButtons.Any(p => p.Button == control && p.Pedal == pedal);
        if (has && !replace) return $"{control}: the {pedal.ToLowerInvariant()} pedal is already bound";
        var keep = replace ? cfg.PedalButtons.Where(p => !string.Equals(p.Button, control, StringComparison.OrdinalIgnoreCase)) : cfg.PedalButtons;
        cfg.PedalButtons = [.. keep, new PedalButton { Button = control, Pedal = pedal, Threshold = 0.5f }];
        if (replace) cfg.Buttons = new Dictionary<string, int[]>(cfg.Buttons) { [control] = [] };
        string usedBy = cfg.RightTriggerPedal == pedal ? "RT" : cfg.LeftTriggerPedal == pedal ? "LT" : "";
        return $"{control}: {(replace ? "set to" : "added")} the {pedal.ToLowerInvariant()} pedal (pressed past 50%)" + (usedBy.Length > 0 ? $" (note: it also drives {usedBy})" : "");
    }

    /// <summary>Removes the wheel buttons (and, for a button, the pedals) bound to a control. A trigger's own pedal stays.</summary>
    public static void ClearBinding(BridgeConfig cfg, string control)
    {
        if (control is "RT" or "LT")
            cfg.TriggerButtons = new Dictionary<string, int[]>(cfg.TriggerButtons) { [control] = [] };
        else
        {
            cfg.Buttons = new Dictionary<string, int[]>(cfg.Buttons) { [control] = [] };
            cfg.PedalButtons = cfg.PedalButtons.Where(p => !string.Equals(p.Button, control, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }
}

/// <summary>
/// Waits for the input to bind to a control: a wheel button that wasn't already held when the capture started, or
/// (for a button, with calibrated pedals) a pedal going from released (&lt; 20%) at the start to past 60%.
/// </summary>
public sealed class BindCapture
{
    private readonly HashSet<int> _held;
    private readonly float[] _startPedals;

    public string Control { get; }
    /// <summary>When the capture gives up, or null to wait until it is dropped.</summary>
    public DateTime? Until { get; }
    public bool TimedOut => Until is { } u && DateTime.UtcNow > u;

    public BindCapture(string control, WheelState s, BridgeConfig cfg, TimeSpan? timeout)
    {
        Control = control;
        _held = [.. s.Pressed];
        _startPedals = PedalValues(s, cfg);
        Until = timeout is { } t ? DateTime.UtcNow + t : null;
    }

    private static float[] PedalValues(WheelState s, BridgeConfig cfg) => [.. PadMapping.Pedals.Select(p => PadMapping.PedalValue(p, s, cfg))];

    /// <summary>Feeds a sample; returns the new button or pedal once one is pressed.</summary>
    public (int? Button, string? Pedal) Poll(WheelState s, BridgeConfig cfg)
    {
        foreach (int b in s.Pressed) if (!_held.Contains(b)) return (b, null);
        if (Control is not ("RT" or "LT"))
        {
            var now = PedalValues(s, cfg);
            for (int i = 0; i < now.Length; i++)
                if (now[i] >= 0.6f && _startPedals[i] < 0.2f) return (null, PadMapping.Pedals[i]);
        }
        // Buttons let go during the wait can be used again.
        _held.IntersectWith(s.Pressed);
        return (null, null);
    }
}
