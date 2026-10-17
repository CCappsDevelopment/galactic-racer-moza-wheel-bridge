using System.Text;

namespace WheelBridge;

/// <summary>Dashboard block for the wheel → virtual pad path.</summary>
public static class WheelPanel
{
    public static string Render(WheelInput wheel, VirtualPad pad, BridgeConfig cfg, PedalCalibrator cal, float? gameSteer)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Wheel   {wheel.Status}   poll {wheel.PollHz,5:F0} Hz");
        var s = wheel.State;
        if (s is not null)
        {
            float half = cfg.SteeringLockDegrees / 2;
            sb.AppendLine($"Angle   {Dashboard.Bar(s.AngleDegrees, half)} {s.AngleDegrees,7:F1}°  (lock {cfg.SteeringLockDegrees:F0}° = ±{half:F0}°)");
            sb.AppendLine($"Stick   {Dashboard.Bar(pad.Sent.StickX, 1)} {pad.Sent.StickX,6:F2}   anti-deadzone {cfg.SteeringAntiDeadzone:F2}   game steer {(gameSteer is { } g ? $"{g,6:F2}" : "   n/a")}");
            sb.AppendLine($"Pedals  raw thr {s.RawThrottle,6}  brk {s.RawBrake,6}  clu {s.RawClutch,6}   → RT {Dashboard.Meter(pad.Sent.Rt)} LT {Dashboard.Meter(pad.Sent.Lt)}");
            if (cfg.Throttle is null || cfg.Brake is null)
                sb.AppendLine("        pedals not calibrated: release all pedals, press [C], then press each pedal fully once");
            sb.AppendLine($"Buttons {(s.Pressed.Count == 0 ? "-" : string.Join(' ', s.Pressed.Order()))}   → pad {(pad.Sent.Buttons.Length == 0 ? "-" : pad.Sent.Buttons)}");
        }
        if (cal.Active) sb.AppendLine($"CALIBRATING: press each pedal fully once… {cal.SecondsLeft:F0}s");
        sb.AppendLine($"Pad     {pad.Status}{(!cfg.PadOutputEnabled ? "  (output disabled in config)" : pad.OnFootPaused ? "  (paused: on foot in the hub)" : "")}");
        var r = pad.Rumble;
        bool recent = (DateTime.UtcNow - r.Time).TotalSeconds < 1;
        sb.AppendLine($"Rumble  large {Dashboard.Meter(recent ? r.Large : 0)}  small {Dashboard.Meter(recent ? r.Small : 0)}");
        return sb.ToString();
    }
}
