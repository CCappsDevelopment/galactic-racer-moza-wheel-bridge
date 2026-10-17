using System.Runtime.InteropServices;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace WheelBridge;

/// <summary>
/// Feeds the wheel into the game as a virtual Xbox 360 pad (ViGEmBus) at ~500 Hz,
/// and captures the game's rumble requests for use as FFB cues.
/// The game only listens to player 1 (XInput slot 0). A real controller plugged in before the bridge starts takes
/// that slot, so the wheel is ignored; the pad moves itself to player 1 as soon as the slot is free.
/// </summary>
public sealed class VirtualPad : IDisposable
{
    private static readonly Dictionary<string, Xbox360Button> ButtonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = Xbox360Button.A, ["B"] = Xbox360Button.B, ["X"] = Xbox360Button.X, ["Y"] = Xbox360Button.Y,
        ["LB"] = Xbox360Button.LeftShoulder, ["RB"] = Xbox360Button.RightShoulder,
        ["Back"] = Xbox360Button.Back, ["Start"] = Xbox360Button.Start, ["Guide"] = Xbox360Button.Guide,
        ["LS"] = Xbox360Button.LeftThumb, ["RS"] = Xbox360Button.RightThumb,
        ["Up"] = Xbox360Button.Up, ["Down"] = Xbox360Button.Down, ["Left"] = Xbox360Button.Left, ["Right"] = Xbox360Button.Right,
    };

    private readonly WheelInput _wheel;
    private readonly BridgeConfig _cfg;
    private readonly ViGEmClient? _client;
    private IXbox360Controller? _pad;   // replaced (on the pad thread) when it moves to player 1
    private readonly Thread? _thread;
    private volatile bool _running = true;

    public string Status { get; private set; }
    /// <summary>XInput slot of the virtual pad (0 = player 1), or null while unknown.</summary>
    public int? PlayerSlot { get; private set; }
    /// <summary>Set while the wheel can't drive the game because another controller is player 1; null otherwise.</summary>
    public string? SlotWarning { get; private set; }
    private DateTime _nextSlotCheck, _nextReconnect;
    /// <summary>Last values sent to the game: stick X (-1..1), RT, LT (0..1), Xbox buttons held.</summary>
    public (float StickX, float Rt, float Lt, string Buttons) Sent { get; private set; } = (0, 0, 0, "");
    /// <summary>Game rumble request (0..1 each), and when it arrived.</summary>
    public (float Large, float Small, DateTime Time) Rumble { get; private set; }
    /// <summary>Set from telemetry: true while the player is on foot (hub). Stale after 1 s so a closed game never blocks the pad.</summary>
    public (bool OnFoot, DateTime Time) Telemetry { get; set; }
    /// <summary>While this returns true the stick is held at 0 (force center: no stick drift from a pinned wheel's last few tenths of a degree).</summary>
    public Func<bool>? HoldStickCentered { get; set; }
    /// <summary>The pad is held neutral because Shade is on foot.</summary>
    public bool OnFootPaused => _cfg.PausePadOnFoot && Telemetry.OnFoot && (DateTime.UtcNow - Telemetry.Time).TotalSeconds < 1;

    public VirtualPad(WheelInput wheel, BridgeConfig cfg)
    {
        _wheel = wheel;
        _cfg = cfg;
        try
        {
            _client = new ViGEmClient();
            _pad = NewPad();
            Status = "virtual Xbox pad connected";
            _thread = new Thread(Run) { IsBackground = true, Name = "Virtual pad", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
        catch (Exception ex)
        {
            Status = ex.GetType().Name.Contains("BusNotFound")
                ? "ViGEmBus driver not installed. Install ViGEmBus (see the README), then restart the bridge"
                : "virtual pad failed: " + ex.Message;
        }
    }

    private IXbox360Controller NewPad()
    {
        var pad = _client!.CreateXbox360Controller();
        pad.AutoSubmitReport = false;
        pad.FeedbackReceived += (_, e) => Rumble = (e.LargeMotor / 255f, e.SmallMotor / 255f, DateTime.UtcNow);
        pad.Connect();
        return pad;
    }

    /// <summary>Wheel angle → stick X (-1..1) using lock, linearity and anti-deadzone from config.</summary>
    public static float SteeringToStick(float angleDeg, BridgeConfig cfg)
    {
        float half = Math.Max(10f, cfg.SteeringLockDegrees / 2f);
        float x = Math.Clamp(angleDeg / half, -1f, 1f);
        if (cfg.InvertSteering) x = -x;
        float mag = MathF.Pow(Math.Abs(x), Math.Max(0.1f, cfg.SteeringLinearity));
        if (mag > 0.001f) mag = cfg.SteeringAntiDeadzone + (1 - cfg.SteeringAntiDeadzone) * mag;
        return MathF.CopySign(Math.Min(mag, 1f), x);
    }

    private void Run()
    {
        var names = PadMapping.Buttons;
        var xbox = names.Select(n => ButtonNames[n]).ToArray();
        var down = new bool[names.Length];
        while (_running)
        {
            try
            {
                // After a failed reconnect: keep trying to get a pad back.
                _pad ??= NewPad();
                var s = _wheel.State;
                bool live = s is not null && _cfg.PadOutputEnabled && !OnFootPaused && (DateTime.UtcNow - s.Time).TotalMilliseconds < 200;
                float stick = 0, rt = 0, lt = 0;
                Array.Clear(down);
                if (live) (stick, rt, lt) = PadMapping.Map(s!, _cfg, down);
                if (HoldStickCentered?.Invoke() == true) stick = 0;

                _pad.SetAxisValue(Xbox360Axis.LeftThumbX, (short)Math.Round(stick * 32767));
                _pad.SetSliderValue(Xbox360Slider.RightTrigger, (byte)Math.Round(rt * 255));
                _pad.SetSliderValue(Xbox360Slider.LeftTrigger, (byte)Math.Round(lt * 255));
                // Every button is written each tick, so one dropped from the map can never stay held.
                bool any = false;
                for (int b = 0; b < names.Length; b++) { _pad.SetButtonState(xbox[b], down[b]); any |= down[b]; }
                _pad.SubmitReport();
                Sent = (stick, rt, lt, any ? string.Join(' ', names.Where((_, b) => down[b])) : "");
                if (Status.StartsWith("virtual pad error")) Status = "virtual Xbox pad connected";
                if (DateTime.UtcNow >= _nextSlotCheck) { _nextSlotCheck = DateTime.UtcNow.AddSeconds(1); CheckSlot(); }
            }
            catch (Exception ex)
            {
                // ViGEmBus gone (driver updated/removed) or a bad map entry: report it and keep trying, never kill the bridge.
                Status = "virtual pad error: " + ex.Message;
                Thread.Sleep(500);
            }
            Thread.Sleep(2);
        }
    }

    /// <summary>
    /// Keeps the wheel on player 1. If the pad got a later slot (a controller was already connected) and slot 0 is
    /// free now, reconnect it: like replugging a controller, it takes the lowest free slot and the game sees it again.
    /// </summary>
    private void CheckSlot()
    {
        int slot;
        try { slot = _pad!.UserIndex; }
        catch { PlayerSlot = null; return; }   // not assigned yet right after Connect
        PlayerSlot = slot;
        if (slot == 0) { SlotWarning = null; return; }
        if (XInputConnected(0))
        {
            SlotWarning = $"Another controller is player 1, so the game ignores the wheel (it is player {slot + 1}). " +
                          "Unplug or turn off the other controller: the wheel takes over by itself.";
            return;
        }
        if (DateTime.UtcNow < _nextReconnect) return;
        _nextReconnect = DateTime.UtcNow.AddSeconds(5);
        // A fresh controller rather than re-adding the old one: a new device is what the game and Windows expect.
        var old = _pad!;
        _pad = null;
        try { old.Disconnect(); } catch { }
        (old as IDisposable)?.Dispose();
        Thread.Sleep(200);
        _pad = NewPad();
        Status = "virtual Xbox pad reconnected to take player 1";
        _nextSlotCheck = DateTime.UtcNow.AddMilliseconds(500);
    }

    private static bool XInputConnected(int slot)
    {
        try { return XInputGetState(slot, out _) == 0; }   // ERROR_DEVICE_NOT_CONNECTED (1167) when empty
        catch { return false; }                            // no XInput on this system: nothing can hold the slot
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState { public uint Packet; public ushort Buttons; public byte Lt, Rt; public short Lx, Ly, Rx, Ry; }

    [DllImport("xinput1_4.dll")] private static extern int XInputGetState(int userIndex, out XInputState state);

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
        try { _pad?.Disconnect(); } catch { }
        _client?.Dispose();
    }
}
