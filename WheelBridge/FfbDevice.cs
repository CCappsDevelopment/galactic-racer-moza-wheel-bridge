using System.Runtime.InteropServices;
using Vortice.DirectInput;

namespace WheelBridge;

/// <summary>
/// Drives the wheelbase with a DirectInput constant-force effect in background + exclusive mode, so force keeps
/// flowing while the game has focus. (The MOZA SDK's own effects acquire in foreground mode: they fail with
/// "Access is denied" whenever its window isn't active and then throw a native exception that kills the process.)
/// The effect is bound to a hidden window owned by this process; the MOZA SDK is still used for reading the wheel.
/// </summary>
public sealed class FfbDevice : IDisposable
{
    private IDirectInput8? _di;
    private IDirectInputDevice8? _device;
    private IDirectInputEffect? _effect;
    private HiddenWindow? _hidden;
    private readonly ConstantForce _force = new() { Magnitude = 0 };
    private EffectParameters? _params;
    private int _lastMagnitude = int.MinValue;
    private DateTime _nextRecovery;

    private DateTime _failingSince = DateTime.MaxValue;
    /// <summary>How long re-acquiring may keep failing before the device is given up and recreated (e.g. after a replug).</summary>
    private static readonly TimeSpan LostAfter = TimeSpan.FromSeconds(3);

    public string Status { get; private set; } = "not started";
    public bool Ready => _effect is not null;
    /// <summary>Recovery has failed for a while: the owner should dispose this device and start a new one.</summary>
    public bool Lost { get; private set; }
    /// <summary>The base being driven (null until started).</summary>
    public Wheelbase? Base { get; private set; }

    /// <summary>DirectInput product GUIDs start with PID&lt;&lt;16 | VID.</summary>
    private static int VendorId(Guid productGuid) => BitConverter.ToInt32(productGuid.ToByteArray(), 0) & 0xFFFF;

    /// <param name="nameHint">Substring of the DirectInput product name to prefer (e.g. "MOZA").</param>
    public bool Start(string nameHint)
    {
        try
        {
            _hidden = new HiddenWindow();
            if (_hidden.Handle == IntPtr.Zero) { Status = "could not create the FFB window"; return false; }

            _di = DInput.DirectInput8Create();
            var candidates = _di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.ForceFeedback | DeviceEnumerationFlags.AttachedOnly);
            // MOZA bases are found by USB vendor ID: their DirectInput names don't contain "MOZA" (the R3 is
            // "R3 Racing Wheel and Pedals"). Never fall back to an arbitrary FFB device: it would get forces tuned for another motor.
            var pick = candidates.FirstOrDefault(d => VendorId(d.ProductGuid) == Wheelbase.MozaVendorId)
                       ?? candidates.FirstOrDefault(d => nameHint.Length > 0
                              && (d.ProductName.Contains(nameHint, StringComparison.OrdinalIgnoreCase)
                                  || d.InstanceName.Contains(nameHint, StringComparison.OrdinalIgnoreCase)));
            if (pick is null)
            {
                Status = candidates.Count == 0 ? "no force-feedback device found" : "no MOZA force-feedback device found";
                return false;
            }
            Base = VendorId(pick.ProductGuid) == Wheelbase.MozaVendorId ? Wheelbase.FromProductName(pick.ProductName.Trim()) : Wheelbase.Unknown(pick.ProductName.Trim());

            _device = _di.CreateDevice(pick.InstanceGuid);
            _device.SetDataFormat<RawJoystickState>().CheckError();
            _device.SetCooperativeLevel(_hidden.Handle, CooperativeLevel.Background | CooperativeLevel.Exclusive).CheckError();
            try { _device.Properties.AutoCenter = false; } catch { /* not all drivers expose it */ }
            _device.Acquire().CheckError();

            _params = new EffectParameters
            {
                Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                Duration = -1,                // INFINITE
                SamplePeriod = 0,
                Gain = 10000,
                TriggerButton = -1,           // DIEB_NOTRIGGER
                TriggerRepeatInterval = 0,
                StartDelay = 0,
                Axes = [(int)JoystickOffset.X],
                Directions = [1],             // single axis: the magnitude's sign gives the direction
                Parameters = _force,
            };
            _effect = _device.CreateEffect(EffectGuid.ConstantForce, _params);
            _effect.Start(1, EffectPlayFlags.None).CheckError();
            Status = $"DirectInput constant force on \"{pick.ProductName.Trim()}\"";
            return true;
        }
        catch (Exception ex)
        {
            Status = "FFB start failed: " + ex.Message;
            Release();
            return false;
        }
    }

    /// <summary>Torque -1..1 (already signed for the motor).</summary>
    public void Set(float torque)
    {
        if (_effect is null || _params is null) return;
        int magnitude = (int)Math.Round(Math.Clamp(torque, -1, 1) * 10000);
        if (magnitude == _lastMagnitude) return;
        try
        {
            _force.Magnitude = magnitude;
            _effect.SetParameters(_params, EffectParameterFlags.TypeSpecificParameters);
            _lastMagnitude = magnitude;
            _failingSince = DateTime.MaxValue;
        }
        catch (Exception ex)
        {
            if (_failingSince == DateTime.MaxValue) _failingSince = DateTime.UtcNow;
            Recover(ex);
            // An unplugged base comes back as a new device instance: re-acquiring the old one never works.
            if (DateTime.UtcNow - _failingSince > LostAfter) { Lost = true; Status = "device lost, reconnecting: " + ex.Message; }
        }
    }

    /// <summary>Re-acquire and restart after the device was lost (unplugged, another app grabbed it, …).</summary>
    private void Recover(Exception ex)
    {
        if (DateTime.UtcNow < _nextRecovery) return;
        _nextRecovery = DateTime.UtcNow.AddSeconds(1);
        try
        {
            _device?.Acquire();
            _effect?.SetParameters(_params!, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);
            Status = "recovered after: " + ex.Message;
            _failingSince = DateTime.MaxValue;
        }
        catch (Exception again) { Status = "device lost: " + again.Message; }
        _lastMagnitude = int.MinValue;
    }

    private void Release()
    {
        try { _effect?.Stop(); } catch { }
        try { _device?.SendForceFeedbackCommand(ForceFeedbackCommand.Reset); } catch { }
        try { _device?.Unacquire(); } catch { }
        _effect?.Dispose(); _effect = null;
        _device?.Dispose(); _device = null;
        _di?.Dispose(); _di = null;
    }

    public void Dispose()
    {
        if (_effect is not null) { try { _force.Magnitude = 0; _effect.SetParameters(_params!, EffectParameterFlags.TypeSpecificParameters); } catch { } }
        Release();
        Status = "stopped";
        _hidden?.Dispose();
    }

    /// <summary>A never-shown top-level window with its own message loop thread.</summary>
    private sealed class HiddenWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private uint _threadId;
        // The window class is registered once per process and outlives any one HiddenWindow (the FFB device is
        // recreated after an unplug), so its WndProc must be a delegate that is never collected.
        private static readonly WndProc Proc = DefWindowProc;
        private static readonly IntPtr ProcPtr = Marshal.GetFunctionPointerForDelegate(Proc);
        public IntPtr Handle { get; private set; }

        public HiddenWindow()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "FFB window" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(2000);
        }

        private void Run()
        {
            _threadId = GetCurrentThreadId();
            var wc = new WNDCLASS { lpfnWndProc = ProcPtr, lpszClassName = "SWGRWheelBridgeFfb", hInstance = GetModuleHandle(null) };
            RegisterClass(ref wc);   // fails harmlessly (class exists) for every window after the first
            Handle = CreateWindowEx(0, wc.lpszClassName, "SWGR Wheel Bridge FFB", 0x00CF0000 /* WS_OVERLAPPEDWINDOW */, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            _ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(500);
        }

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName; public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WNDCLASS wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint id, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    }
}
