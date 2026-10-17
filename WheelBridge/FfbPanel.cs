using System.Text;

namespace WheelBridge;

/// <summary>Dashboard block for force feedback: output, state and each layer's live contribution.</summary>
public static class FfbPanel
{
    public static string Render(FfbEngine ffb, FfbConfig cfg)
    {
        var sb = new StringBuilder();
        string state = ffb.Killed ? "KILLED ([K] to resume)" : !cfg.Enabled ? "disabled in config" : ffb.NeedsMotorTest ? "OFF until the motor test ([T], hands off)" : "ON";
        sb.AppendLine($"FFB     {state}   {ffb.DeviceStatus}   {ffb.LoopHz,4:F0} Hz   master {cfg.MasterGain:P0}   sign {cfg.ForceSign:+0;-0}");
        string flags = $"  {ffb.ModelState}" + (ffb.Airborne ? " AIRBORNE" : "") + (ffb.Wrecked ? " WRECKED" : "");
        sb.AppendLine($"Output  {Dashboard.Bar(ffb.LastTorque, 1, 20)} {ffb.LastTorque,6:F2}{flags}");
        for (int i = 0; i < FfbModel.LayerOrder.Length; i++)
        {
            string name = FfbModel.LayerOrder[i];
            var layer = cfg.Layers.GetValueOrDefault(name);
            bool on = layer?.Enabled ?? false;
            sb.AppendLine($"  F{i + 1,-2} {name,-10} {(on ? "on " : "off")} gain {layer?.Gain ?? 0,4:F2}  {Dashboard.Bar(ffb.Contributions[i], 0.5f, 10)} {ffb.Contributions[i],6:F2}");
        }
        return sb.ToString();
    }
}
