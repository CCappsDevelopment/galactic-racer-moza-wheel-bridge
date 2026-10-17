using System.Numerics;
using System.Text;

namespace WheelBridge;

/// <summary>Live console view of the telemetry stream, redrawn in place at ~10 Hz.</summary>
public sealed class Dashboard
{
    private const double DriftHoldSeconds = 0.2;
    private const double ImpactHoldSeconds = 2.0;

    private readonly object _gate = new();
    private TelemetryFrame? _latest;
    private uint _lastSeq;
    private long _seqGaps;
    private double _lastDriftTime = double.NegativeInfinity;
    private readonly Dictionary<int, (double time, Collision c, Vector3 localDir)> _impacts = [];
    private int _framesThisSecond;
    private double _rateHz;
    private DateTime _rateWindow = DateTime.UtcNow;

    public void OnFrame(TelemetryFrame f)
    {
        lock (_gate)
        {
            if (_latest is not null && f.Seq != _lastSeq + 1) _seqGaps++;
            _lastSeq = f.Seq;
            _latest = f;
            _framesThisSecond++;
            if (f.Has(FrameFlags.Drifting)) _lastDriftTime = f.SimTime;
            foreach (var c in f.Collisions)
                _impacts[c.Id] = (f.SimTime, c, f.ToLocal(c.WorldDirection));
            // Also pruned here, not only when the monitor is drawn: the log-only console mode never draws it.
            if (_impacts.Count > 64)
                foreach (var id in _impacts.Keys)
                    if (f.SimTime - _impacts[id].time > ImpactHoldSeconds || f.SimTime < _impacts[id].time) _impacts.Remove(id);
        }
    }

    /// <summary>The game's processed steering input from the latest frame, or null when not driving.</summary>
    public float? GameSteer
    {
        get { lock (_gate) return _latest is { } f && f.Has(FrameFlags.HasVehicle) ? f[F.SteerInput] : null; }
    }

    public string Summary()
    {
        lock (_gate)
        {
            var f = _latest;
            if (f is null) return "no frames";
            if (!f.Has(FrameFlags.HasVehicle)) return "no vehicle";
            return $"seq={f.Seq} gaps={_seqGaps} {f.VehicleClass} {f[F.SpeedKmph]:F0}km/h slip={f[F.SlipDeg]:F1} steer={f[F.SteerInput]:F2} " +
                   $"ramjet={GameEnums.Name(GameEnums.RamjetState, f[B.RamjetState])} contacts={f.CollisionCount}";
        }
    }

    /// <summary>Pipe / recording status lines (shared by the console dashboard and the GUI).</summary>
    public string StatusText(PipeReceiver rx, Recorder? rec)
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if ((now - _rateWindow).TotalSeconds >= 1)
            {
                _rateHz = _framesThisSecond / (now - _rateWindow).TotalSeconds;
                _framesThisSecond = 0;
                _rateWindow = now;
            }
            sb.AppendLine($"Pipe: {(rx.Connected ? "CONNECTED" : "waiting for game…")}   rate {_rateHz,6:F1} Hz   frames {rx.FramesReceived}   gaps {_seqGaps}   bad {rx.BadFrames}");
            sb.AppendLine(rec is null
                ? "Rec:  not recording"
                : $"Rec:  ●●● RECORDING ●●●  {rec.Rows} rows → {rec.Path}");
            if (rx.LastError is { } err) sb.AppendLine($"Last error: {err}");
        }
        return sb.ToString();
    }

    /// <summary>The live vehicle telemetry block.</summary>
    public string TelemetryText()
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            var f = _latest;
            if (f is null || !f.Has(FrameFlags.HasVehicle))
            {
                sb.AppendLine(f is null ? "No frames yet." : f.Has(FrameFlags.OnFoot) ? "Connected: on foot in the hub (no vehicle)."
                    : "Connected: no player vehicle (menu / loading).");
            }
            else
            {
                bool drifting = f.SimTime - _lastDriftTime <= DriftHoldSeconds;
                sb.AppendLine($"Vehicle {f.VehicleClass,-8} mode {GameEnums.Name(GameEnums.InputMode, f[B.InputMode]),-12}{(f.Has(FrameFlags.RaceDone) ? " FINISHED" : "")} sim t {f.SimTime,8:F2}s  dt {f.Dt * 1000,5:F1} ms");
                sb.AppendLine($"Speed   {f[F.SpeedKmph],6:F0} km/h  (top {f[F.TopSpeedKmph]:F0})   fwd {f[F.RelVelX] / 27.78f,6:F0}  lat {f[F.RelVelY] / 27.78f,6:F0}  vert {f[F.RelVelZ] / 27.78f,6:F0} km/h");
                sb.AppendLine($"Steer   {Bar(f[F.SteerInput], 1)} {f[F.SteerInput],6:F2}   intent {f[F.SteerIntent],6:F2}");
                sb.AppendLine($"Slip    {Bar(f[F.SlipDeg], 30)} {f[F.SlipDeg],6:F1}°  {(drifting ? "DRIFTING" : "        ")}");
                sb.AppendLine($"Yaw     {Bar(f[F.AngVelZ], 3)} {f[F.AngVelZ],6:F2} rad/s   roll {f[F.Roll],5:F2}  pitch {f[F.Pitch],5:F2}");
                sb.AppendLine($"Pedals  thr {Meter(f[F.Throttle])}  brk {Meter(f[F.Brake])}   rpm {f[F.Rpm],6:F0} ({f[F.RpmScale]:P0})");
                sb.AppendLine($"Ground  {Meter(f[F.OnGroundScale])} {f[F.OnGroundScale]:F2}  air {f[F.TimeInAir],4:F2}s  levitation {f[F.LevitationScale]:F2}  {(f.Has(FrameFlags.UpsideDown) ? "UPSIDE DOWN" : "")}");
                sb.AppendLine($"Landing {GameEnums.Name(GameEnums.LandingState, f[B.LandingState]),-10} grade {GameEnums.Name(GameEnums.PerfectLandingState, f[B.PerfectLandingState]),-10}");
                sb.AppendLine($"Boost   {GameEnums.Name(GameEnums.BoostState, f[B.BoostState]),-13} ramjet {GameEnums.Name(GameEnums.RamjetState, f[B.RamjetState]),-15} heat {Meter(f[F.RamjetHeat])} {f[F.RamjetHeat]:F2}");
                sb.AppendLine($"        afterburner {GameEnums.Name(GameEnums.AfterburnerState, f[B.AfterburnerState]),-10} energy {Meter(f[F.AfterburnerEnergy])}   drafting {GameEnums.Name(GameEnums.DraftingState, f[B.DraftingState])}");
                sb.AppendLine($"Crash   {(f[F.CrashTimeRemaining] > 0 ? $"WRECKED ({f[F.CrashTimeRemaining]:F1}s)" : "-")}");
                sb.AppendLine();
                sb.AppendLine($"Contacts this frame: {f.CollisionCount}   {(f.Has(FrameFlags.CollisionsOk) ? "" : "(collision list unreadable)")}");
                foreach (var id in _impacts.Where(kv => f.SimTime - kv.Value.time > ImpactHoldSeconds).Select(kv => kv.Key).ToList())
                    _impacts.Remove(id);
                foreach (var (id, (t, c, local)) in _impacts.OrderByDescending(kv => kv.Value.time).Take(6))
                {
                    string side = c.LocalLocation.Y > 30 ? "RIGHT" : c.LocalLocation.Y < -30 ? "LEFT" : c.LocalLocation.X > 0 ? "FRONT" : "REAR";
                    sb.AppendLine($"  #{id,-6} {f.SimTime - t,4:F1}s ago  {side,-5} strength {c.PointVelocityDelta,8:F0}  local dir ({local.X,5:F2},{local.Y,5:F2},{local.Z,5:F2}) {(c.OtherIsVehicle ? "vs RACER" : "")}");
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>Vehicle class of the latest frame (null when there is no player vehicle).</summary>
    public VehicleClass? CurrentVehicle
    {
        get { lock (_gate) return _latest is { } f && f.Has(FrameFlags.HasVehicle) ? f.VehicleClass : null; }
    }

    public void Draw(PipeReceiver rx, Recorder? rec, string? note, string? wheelPanel = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SWGR Wheel Bridge   [K] KILL FFB  [L] force center  [T] motor test  , . master  F1-F12 layers  [ ] anti-deadzone  - = lock");
        sb.AppendLine("                    [R] record  [C] calibrate pedals  [Q] quit");
        sb.AppendLine(new string('─', 72));
        sb.Append(StatusText(rx, rec));
        if (note is not null) sb.AppendLine(note);
        sb.AppendLine();
        if (wheelPanel is not null) { sb.Append(wheelPanel); sb.AppendLine(); }
        sb.Append(TelemetryText());

        // Fill exactly the visible window: pad every line (erases leftovers from a longer previous frame)
        // and never write past the last row, which would scroll the console and make the view jump.
        var lines = sb.ToString().Split(Environment.NewLine);
        int width = Math.Max(20, Console.WindowWidth - 1);
        int height = Math.Max(5, Console.WindowHeight - 1);
        var outSb = new StringBuilder();
        for (int i = 0; i < height; i++)
        {
            string l = i < lines.Length ? lines[i] : "";
            if (i == height - 1 && lines.Length > height) l = $"… {lines.Length - height + 1} more lines, enlarge the window";
            outSb.Append(l.Length > width ? l[..width] : l.PadRight(width));
            if (i < height - 1) outSb.Append('\n');
        }
        Console.SetCursorPosition(0, 0);
        Console.Write(outSb.ToString());
    }

    public static string Bar(float v, float range, int half = 12)
    {
        int n = (int)Math.Round(Math.Clamp(v / range, -1, 1) * half);
        var c = new char[half * 2 + 1];
        Array.Fill(c, '·');
        c[half] = '|';
        for (int i = 1; i <= Math.Abs(n); i++) c[half + Math.Sign(n) * i] = '█';
        return "[" + new string(c) + "]";
    }

    public static string Meter(float v, int width = 10)
    {
        int n = (int)Math.Round(Math.Clamp(v, 0, 1) * width);
        return "[" + new string('█', n) + new string('·', width - n) + "]";
    }
}
