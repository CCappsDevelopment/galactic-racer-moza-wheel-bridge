namespace WheelBridge;

/// <summary>
/// Everything the bridge runs (telemetry pipe, shared memory, wheel input, virtual pad, FFB), wired together.
/// Used by both the console dashboard and the GUI.
/// </summary>
public sealed class BridgeHost : IDisposable
{
    public const string PipeName = "swgr_wheelbridge";

    /// <summary>Where wheelbridge.json, profiles and recordings live (see <see cref="ResolveDataDir"/>).</summary>
    public static readonly string DataDir = ResolveDataDir();
    public static readonly string ConfigPath = Path.Combine(DataDir, "wheelbridge.json");
    public static readonly string ProfileDir = Path.Combine(DataDir, "profiles");
    public static readonly string RecordDir = ResolveRecordDir();

    private readonly CancellationTokenSource _cts = new();
    private readonly SharedMemoryPublisher _shm;
    private readonly Task _rxTask;
    private readonly object _recLock = new();
    private Recorder? _recorder;

    public BridgeConfig Config { get; }
    public Dashboard Dashboard { get; } = new();
    public WheelInput Wheel { get; }
    public VirtualPad Pad { get; }
    public FfbEngine Ffb { get; }
    public PedalCalibrator Calibrator { get; } = new();
    public PipeReceiver Receiver { get; }
    public ProfileManager Profiles { get; }
    public Recorder? Recorder { get { lock (_recLock) return _recorder; } }
    /// <summary>Last error from handling a frame (recording, publishing); the stream itself keeps going.</summary>
    public string? FrameError { get; private set; }

    public BridgeHost()
    {
        ProfileManager.SeedDefaults(Path.Combine(AppContext.BaseDirectory, "defaults", "profiles"), ProfileDir);
        Config = BridgeConfig.Load(ConfigPath);
        Profiles = new ProfileManager(Config, ProfileDir, SaveConfig);
        _shm = new SharedMemoryPublisher();
        Wheel = new WheelInput();
        Pad = new VirtualPad(Wheel, Config);
        Ffb = new FfbEngine(Config, Wheel, Pad);
        Pad.HoldStickCentered = () => Ffb.CenterLock;
        Receiver = new PipeReceiver(PipeName, (frame, raw) =>
        {
            // One failing consumer (a full disk under the recorder, say) must not stop telemetry for the others.
            try { _shm.Publish(raw); } catch (Exception ex) { FrameError = "shared memory: " + ex.Message; }
            Dashboard.OnFrame(frame);
            Ffb.OnFrame(frame);
            Pad.Telemetry = (frame.Has(FrameFlags.OnFoot), DateTime.UtcNow);
            lock (_recLock)
            {
                try { _recorder?.Write(frame); }
                catch (Exception ex)
                {
                    FrameError = $"recording stopped: {ex.Message}";
                    try { _recorder?.Dispose(); } catch { }
                    _recorder = null;
                }
            }
        });
        _rxTask = Task.Run(() => Receiver.RunAsync(_cts.Token));
    }

    /// <summary>Saves wheelbridge.json. Never throws (it runs from timers and key handlers): a failure is reported through <see cref="FrameError"/>.</summary>
    public void SaveConfig()
    {
        try { Config.Save(ConfigPath); }
        catch (Exception ex) { FrameError = $"couldn't save settings: {ex.Message}"; }
    }

    /// <summary>Call regularly (~1–20 Hz) on the UI/console thread: switches profile when the vehicle type changes.</summary>
    public string? PollAutoProfile() => Profiles.OnVehicle(Dashboard.CurrentVehicle);

    // ───────────────────────────── data folder ─────────────────────────────

    /// <summary>
    /// SWGR_WHEELBRIDGE_HOME if set; else the source folder when running a dev build from its bin\ folder (so the
    /// existing config keeps working); else next to the exe (portable install); else %LOCALAPPDATA% when the exe
    /// folder is read-only (e.g. installed under Program Files).
    /// </summary>
    private static string ResolveDataDir()
    {
        if (Environment.GetEnvironmentVariable("SWGR_WHEELBRIDGE_HOME") is { Length: > 0 } env) return Ensure(env);
        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        // Dev builds run from <project>\bin\Release\net10.0-windows or <project>\bin\run.
        var d = new DirectoryInfo(exeDir);
        for (int up = 0; up < 3 && d.Parent is not null; up++)
        {
            d = d.Parent;
            if (File.Exists(Path.Combine(d.FullName, "WheelBridge.csproj"))) return d.FullName;
        }
        if (IsWritable(exeDir)) return exeDir;
        return Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SWGR Wheel Bridge"));
    }

    /// <summary>Dev builds keep recording into the workspace's Dumps\recordings; installs use a recordings folder in the data folder.</summary>
    private static string ResolveRecordDir()
    {
        string dumps = Path.Combine(Path.GetDirectoryName(DataDir) ?? DataDir, "Dumps");
        return File.Exists(Path.Combine(DataDir, "WheelBridge.csproj")) && Directory.Exists(dumps)
            ? Path.Combine(dumps, "recordings")
            : Path.Combine(DataDir, "recordings");
    }

    private static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }

    private static bool IsWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // ───────────────────────────── recording / calibration ─────────────────────────────

    /// <summary>Starts or stops a telemetry recording; returns a status message.</summary>
    public string ToggleRecording()
    {
        lock (_recLock)
        {
            if (_recorder is null)
            {
                try { _recorder = new Recorder(RecordDir); }
                catch (Exception ex) { return "Couldn't start recording: " + ex.Message; }
                FrameError = null;
                return $"Recording to {_recorder.Path}";
            }
            var msg = $"Saved {_recorder.Rows} rows to {_recorder.Path}";
            _recorder.Dispose();
            _recorder = null;
            return msg;
        }
    }

    /// <summary>Starts pedal calibration; returns a status message.</summary>
    public string BeginCalibration()
    {
        if (Calibrator.Active) return "Calibration already running";
        if (Wheel.State is not { } ws) return "Can't calibrate: no wheel data";
        Calibrator.Begin(ws);
        return "Calibrating: press each pedal fully once…";
    }

    /// <summary>Call regularly (~10–20 Hz). Returns a message when a running calibration finishes (config saved).</summary>
    public string? PollCalibration()
    {
        if (Calibrator.Active && Wheel.State is { } s && Calibrator.Update(s, Config) is { } result)
        {
            SaveConfig();
            return result + " (saved)";
        }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        // FFB first so the force is zeroed while the wheel is still being read, then the pad, then the SDK.
        Ffb.Dispose();
        Pad.Dispose();
        Wheel.Dispose();
        lock (_recLock) { _recorder?.Dispose(); _recorder = null; }
        try { _rxTask.Wait(1000); } catch { }
        _shm.Dispose();
    }
}
