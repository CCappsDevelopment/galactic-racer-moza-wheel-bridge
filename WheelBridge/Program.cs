using WheelBridge;

// Galactic Racer Wheel Bridge:
//  - receives telemetry from the in-game exporter, mirrors it to shared memory, shows it live (Phase 1)
//  - reads the MOZA R3 and drives the game through a virtual Xbox pad (input)
//  - synthesizes force feedback from the telemetry and sends it to the R3 (FFB)

string configPath = BridgeHost.ConfigPath;

// Only one bridge may own the pipe, the virtual pad and the wheelbase at a time.
using var instance = new Mutex(false, @"Local\SWGR_WheelBridge_Instance");
bool AlreadyRunning()
{
    try { return !instance.WaitOne(0); }
    catch (AbandonedMutexException) { return false; }   // previous instance crashed; we own it now
}

if (args is ["--version"]) { Console.WriteLine($"SWGR Wheel Bridge {typeof(BridgeHost).Assembly.GetName().Version?.ToString(3)}"); return; }

// Double-clicking the exe opens the GUI; --console gives the text dashboard (also used when output is redirected).
if (args is [] && !Console.IsOutputRedirected) args = ["--gui"];

if (args is ["--gui"])
{
    // The GUI owns the window; drop the console this exe was started with so no empty terminal hangs around.
    FreeConsole();
    if (AlreadyRunning())
    {
        System.Windows.Forms.MessageBox.Show("The Wheel Bridge is already running (console or GUI). Close it first.",
            "SWGR Wheel Bridge", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
        return;
    }
    WheelBridge.Gui.MainForm.Run();
    return;
}

if (args is ["--replay", var replayPath, ..var rest])
{
    // Optional profile: replay with that profile's feel settings (the live config file is not changed).
    var cfg = BridgeConfig.Load(configPath);
    if (rest is [var profile]) cfg.ApplyProfile(BridgeConfig.ReadFile(profile));
    Replay.Run(replayPath, cfg);
    return;
}

if (args is ["--ffb-selftest"])
{
    // Acquire the wheelbase and hold a zero-force effect for 3 s: proves the FFB path without moving the wheel.
    var cfg = BridgeConfig.Load(configPath);
    using var dev = new FfbDevice();
    bool ok = dev.Start(cfg.Ffb.Device);
    Console.WriteLine($"start: {ok}: {dev.Status}");
    for (int i = 0; i < 300 && ok; i++) { dev.Set(0); Thread.Sleep(10); }
    Console.WriteLine($"after 3 s at zero force: {dev.Status}");
    return;
}

if (args is ["--idle-probe"])
{
    // Read the wheel at rest for 5 s: shows how noisy the SDK angle/velocity are (drives the Damping layer).
    using var w = new WheelInput();
    var vel = new List<float>(); var ang = new List<float>(); var times = new List<double>();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    WheelState? prev = null; int changes = 0;
    while (sw.Elapsed.TotalSeconds < 6)
    {
        var s = w.State;
        if (s is not null && sw.Elapsed.TotalSeconds > 1)
        {
            if (prev is not null && s.AngleDegrees != prev.AngleDegrees) { changes++; times.Add(sw.Elapsed.TotalMilliseconds); }
            vel.Add(s.VelocityDps); ang.Add(s.AngleDegrees); prev = s;
        }
        Thread.Sleep(2);
    }
    Console.WriteLine($"status {w.Status}, samples {vel.Count}, angle changes {changes}");
    if (vel.Count > 0)
    {
        Console.WriteLine($"angle min {ang.Min():F3} max {ang.Max():F3}");
        Console.WriteLine($"vel   min {vel.Min():F1} max {vel.Max():F1} rms {Math.Sqrt(vel.Average(v => v * v)):F1} nonzero {vel.Count(v => v != 0)}");
        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
        if (gaps.Count > 0) Console.WriteLine($"angle update gap ms: median {gaps.Order().ElementAt(gaps.Count / 2):F1}");
        Console.WriteLine("distinct vel values: " + string.Join(" ", vel.Distinct().Take(20).Select(v => v.ToString("F1"))));
    }
    return;
}

if (AlreadyRunning())
{
    Console.WriteLine("The Wheel Bridge is already running (console or GUI). Close it first.");
    return;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var host = new BridgeHost();
var config = host.Config;
var ffb = host.Ffb;
string? note = config.LoadWarning ?? $"Config: {configPath}";

// Without an interactive console (e.g. output piped to a log), print one status line per second instead.
if (Console.IsOutputRedirected)
{
    Console.WriteLine(note);
    try
    {
        while (!cts.IsCancellationRequested)
        {
            await Task.Delay(1000, cts.Token);
            if (host.PollAutoProfile() is { } switched) Console.WriteLine($"{DateTime.Now:HH:mm:ss} {switched}");
            var w = host.Wheel.State;
            var rx = host.Receiver;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} pipe={rx.Connected} frames={rx.FramesReceived} bad={rx.BadFrames} {host.Dashboard.Summary()}");
            Console.WriteLine($"         ffb: {ffb.DeviceStatus} {ffb.LoopHz:F0}Hz out={ffb.LastTorque:F2} state={ffb.ModelState}");
            Console.WriteLine($"         wheel: {host.Wheel.Status} {host.Wheel.PollHz:F0}Hz angle={w?.AngleDegrees:F1} thr={w?.RawThrottle} brk={w?.RawBrake} | pad: {host.Pad.Status}");
        }
    }
    catch (OperationCanceledException) { }
    return;
}

string? lastError = null;
Console.CursorVisible = false;
Console.Clear();
try
{
    while (!cts.IsCancellationRequested)
    {
        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true).Key;
            if (key == ConsoleKey.Q) cts.Cancel();
            if (key == ConsoleKey.R) note = host.ToggleRecording();
            if (key is ConsoleKey.Oem4 or ConsoleKey.Oem6)   // [ and ]
            {
                float step = key == ConsoleKey.Oem6 ? 0.01f : -0.01f;
                config.SteeringAntiDeadzone = MathF.Round(Math.Clamp(config.SteeringAntiDeadzone + step, 0f, 0.6f), 2);
                host.SaveConfig();
                note = $"Anti-deadzone {config.SteeringAntiDeadzone:F2} (saved)";
            }
            if (key is ConsoleKey.OemMinus or ConsoleKey.OemPlus)   // - and =
            {
                float step = key == ConsoleKey.OemPlus ? 30f : -30f;
                config.SteeringLockDegrees = Math.Clamp(config.SteeringLockDegrees + step, 90f, 1080f);
                host.SaveConfig();
                note = $"Steering lock {config.SteeringLockDegrees:F0}° (saved)";
            }
            if (key == ConsoleKey.K)
            {
                ffb.Killed = !ffb.Killed;
                note = ffb.Killed ? "FFB KILLED. Press K to resume" : "FFB resumed";
            }
            if (key == ConsoleKey.L)
            {
                if (!ffb.CenterLock && ffb.Racing) note = "Force center is only available outside a race";
                else if (!ffb.CenterLock && ffb.NeedsMotorTest) note = "Force center needs the motor direction: take your hands off the wheel and press [T] first";
                else { ffb.CenterLock = !ffb.CenterLock; note = ffb.CenterLock ? "Force center on: wheel pinned at 0°, stick held at 0 ([L] to release)" : "Force center off"; }
            }
            if (key == ConsoleKey.T)
            {
                note = "Motor test running. Hands off the wheel…";
                var (ok, msg) = await ffb.RunMotorTestAsync();
                if (ok) host.SaveConfig();
                note = ok ? msg + " (saved)" : msg;
            }
            if (key is ConsoleKey.OemComma or ConsoleKey.OemPeriod)   // , and .
            {
                float step = key == ConsoleKey.OemPeriod ? 0.05f : -0.05f;
                config.Ffb.MasterGain = MathF.Round(Math.Clamp(config.Ffb.MasterGain + step, 0f, 1f), 2);
                host.SaveConfig();
                note = $"FFB master {config.Ffb.MasterGain:P0} (saved)";
            }
            if (key >= ConsoleKey.F1 && key < ConsoleKey.F1 + FfbModel.LayerOrder.Length)
            {
                string name = FfbModel.LayerOrder[key - ConsoleKey.F1];
                if (config.Ffb.Layers.TryGetValue(name, out var layer))
                {
                    layer.Enabled = !layer.Enabled;
                    host.SaveConfig();
                    note = $"FFB layer {name} {(layer.Enabled ? "on" : "off")} (saved)";
                }
            }
            if (key == ConsoleKey.C) note = host.BeginCalibration();
        }
        if (host.PollCalibration() is { } result) note = result;
        if (host.PollAutoProfile() is { } switched) note = switched;
        if ((host.FrameError ?? ffb.LastError) is { } err && err != lastError) { lastError = err; note = err; }
        if (ffb.CenterLockFault is { } fault) { ffb.CenterLockFault = null; note = fault; }
        if (host.Pad.SlotWarning is { } slot) note = slot;
        host.Dashboard.Draw(host.Receiver, host.Recorder, note,
            WheelPanel.Render(host.Wheel, host.Pad, config, host.Calibrator, host.Dashboard.GameSteer) + Environment.NewLine + FfbPanel.Render(ffb, config.Ffb));
        await Task.Delay(50);
    }
}
finally
{
    Console.CursorVisible = true;
}

[System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool FreeConsole();
