using System.Runtime.InteropServices;
using mozaAPI;
using Moza = mozaAPI.mozaAPI;

namespace WheelBridge;

/// <summary>Snapshot of the MOZA wheelbase, pedals and wheel buttons.</summary>
public sealed record WheelState(
    float AngleDegrees, float VelocityDps,
    int RawThrottle, int RawBrake, int RawClutch,
    IReadOnlySet<int> Pressed, DateTime Time);

/// <summary>
/// Polls the MOZA SDK (via Pit House) on a background thread at ~500 Hz.
/// The SDK reports buttons as "changes since last poll", so a press shorter than one poll is still seen.
/// </summary>
public sealed class WheelInput : IDisposable
{
    private const int PollMs = 2;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private volatile WheelState? _state;

    public WheelState? State => _state;
    public string Status { get; private set; } = "starting";
    public double PollHz { get; private set; }

    public WheelInput()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "MOZA poll", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>True when the MOZA SDK isn't installed yet (the GUI offers to download it).</summary>
    public bool SdkMissing { get; private set; }

    private void Run()
    {
        // The SDK isn't shipped (no redistribution licence): wait until the user has downloaded it.
        while (_running && !MozaSdk.Available)
        {
            SdkMissing = true;
            Status = "MOZA SDK not installed. Use \"Get MOZA SDK\" (or see the README)";
            Thread.Sleep(1000);
        }
        SdkMissing = false;
        if (!_running) return;
        timeBeginPeriod(1);
        try { RunSdk(); }
        catch (Exception ex) { Status = "SDK failure: " + ex.Message; }   // includes a missing or broken DLL
        finally { timeEndPeriod(1); }
    }

    // Kept out of Run: a method that uses mozaAPI types needs MOZA_API_CSharp.dll to be compiled at all, so its
    // failure must happen inside Run's try, not when the thread starts.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void RunSdk()
    {
        try
        {
            Moza.installMozaSDK();
            // The SDK needs a moment after install to discover devices through Pit House.
            Thread.Sleep(1000);
            int polls = 0;
            var window = DateTime.UtcNow;
            while (_running)
            {
                Poll();
                polls++;
                var now = DateTime.UtcNow;
                if ((now - window).TotalSeconds >= 1)
                {
                    PollHz = polls / (now - window).TotalSeconds;
                    polls = 0;
                    window = now;
                }
                Thread.Sleep(PollMs);
            }
        }
        finally
        {
            try { Moza.removeMozaSDK(); } catch { }
        }
    }

    private void Poll()
    {
        var err = ERRORCODE.NORMAL;
        HIDData d;
        try { d = Moza.getHIDData(ref err); }
        catch (Exception ex)
        {
            Status = "no data (" + ex.GetType().Name + "). Is Pit House running and the wheelbase on?";
            _state = null;
            Thread.Sleep(250);
            return;
        }
        if (err != ERRORCODE.NORMAL && err != ERRORCODE.COLLECTIONCYCLEDATALOSS)
        {
            Status = err switch
            {
                ERRORCODE.NOINSTALLSDK => "SDK not installed",
                ERRORCODE.NODEVICES => "no MOZA device found. Is the wheelbase powered and connected?",
                ERRORCODE.PITHOUSENOTREADY => "Pit House not ready. Start MOZA Pit House",
                _ => err.ToString(),
            };
            _state = null;
            Thread.Sleep(250);
            return;
        }
        if (float.IsNaN(d.fSteeringWheelAngle))
        {
            Status = "connected, waiting for wheelbase angle";
            return;
        }

        var pressed = new HashSet<int>();
        if (d.buttons is not null)
            for (int i = 0; i < d.buttons.Length; i++)
                if (d.buttons[i].IsPressed()) pressed.Add(i);

        Status = err == ERRORCODE.COLLECTIONCYCLEDATALOSS ? "ok (some HID reports dropped)" : "ok";
        _state = new WheelState(d.fSteeringWheelAngle, d.fSteeringWheelVelocity,
            d.throttle, d.brake, d.clutch, pressed, DateTime.UtcNow);
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(1000);
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
}
