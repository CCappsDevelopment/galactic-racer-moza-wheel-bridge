using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using WheelBridge;

// Offline regression suite for the Wheel Bridge. No wheel, ViGEm or game needed.
// Usage: dotnet run -c Release [-- filter]

var tests = new List<(string Name, Action Body)>
{
    ("frame: round trip of every field", FrameTests.RoundTrip),
    ("frame: NaN/∞ floats are read as 0", FrameTests.NonFinite),
    ("frame: bad magic / version / short buffer rejected", FrameTests.Rejects),
    ("frame: fuzzed frames never throw, always finite", FrameTests.Fuzz),
    ("pipe: resyncs after garbage, split writes and a wrong-version frame", PipeTests.Resync),
    ("pipe: survives a throwing frame handler", PipeTests.HandlerThrows),
    ("config: corrupt file → defaults, .bad copy, warning", ConfigTests.Corrupt),
    ("config: nulls and out-of-range values are repaired", ConfigTests.Sanitize),
    ("config: save is atomic and round-trips", ConfigTests.AtomicSave),
    ("profile: never changes master, sign, device, pedals or buttons", ConfigTests.ProfileKeepsHardware),
    ("profile: shipped defaults load and cover every layer", ConfigTests.DefaultProfiles),
    ("profile: auto switch per vehicle, never over unsaved tuning", ConfigTests.AutoSwitch),
    ("profile: seeding respects existing and deleted profiles", ConfigTests.Seeding),
    ("model: deterministic over recorded laps", ModelTests.Deterministic),
    ("model: NaN wheel input never reaches the motor and doesn't stick", ModelTests.NaNInput),
    ("model: muted to exactly 0 after the finish", ModelTests.MutedAfterFinish),
    ("model: pause detection on frozen telemetry", ModelTests.Pause),
    ("model: impact queue stays bounded while muted", ModelTests.ImpactsBounded),
    ("model: no vehicle clears wrecked / airborne", ModelTests.NoVehicleClears),
    ("steering: mapping symmetric, monotonic, ±1 at lock", InputTests.Steering),
    ("pedals: normalize safe for any deadzone and either axis direction", InputTests.Pedals),
    ("perf: model step, frame parse and allocations", PerfTests.Budget),
    ("wheelbase: model detection from DirectInput names", BaseTests.Detection),
    ("wheelbase: strength never above R3-equivalent on untested bases", BaseTests.Scaling),
    ("lean: pushes back toward upright, finite, silent at gain 0", ModelTests.Lean),
    ("lean: light, and never pushes toward the lean while unwinding", ModelTests.LeanUnwind),
    ("force center: holds center, lets go when the motor turns the wrong way", ForceTests.CenterLock),
    ("config: no force until the motor test; profiles never set the direction", ConfigTests.ForceSignTested),
    ("bindings: capture, bind / replace / clear", InputTests.Capture),
    ("bindings: buttons, pedal buttons, paddle triggers, no pedals", InputTests.Bindings),
    ("bindings: descriptions and duplicate detection", InputTests.BindingText),
    ("moza sdk: zip install checks paths, hashes and asks before using unknown files", SdkTests.InstallFromZip),
};

// Keep the tests' data (MOZA SDK test installs) out of any real data folder.
Environment.SetEnvironmentVariable("SWGR_WHEELBRIDGE_HOME", Path.Combine(Path.GetTempPath(), "wbtest_home_" + Environment.ProcessId));

string? filter = args.FirstOrDefault();
int pass = 0, fail = 0;
foreach (var (name, body) in tests)
{
    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
    var sw = Stopwatch.StartNew();
    try { body(); pass++; Console.WriteLine($"PASS  {name}  ({sw.ElapsedMilliseconds} ms)"); }
    catch (Exception ex) { fail++; Console.WriteLine($"FAIL  {name}\n      {ex.GetType().Name}: {ex.Message}"); }
}
Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;

static class Check
{
    public static void True(bool cond, string what) { if (!cond) throw new Exception(what); }
    public static void Near(double a, double b, double tol, string what) { if (Math.Abs(a - b) > tol) throw new Exception($"{what}: {a} vs {b}"); }
}

/// <summary>Builds wire frames the way the Lua exporter does.</summary>
static class Frames
{
    public static byte[] Build(Action<float[]>? floats = null, byte[]? bytes = null, FrameFlags flags = FrameFlags.HasVehicle,
                               VehicleClass cls = VehicleClass.Car, uint seq = 1, double simTime = 0, float dt = 1 / 60f,
                               (float x, float y, float z, float lx, float ly, float lz, float pvd, int id, bool veh)[]? colls = null,
                               ushort version = TelemetryFrame.Version)
    {
        var f = new float[TelemetryFrame.FloatCount];
        f[(int)F.QuatW] = 1;
        floats?.Invoke(f);
        var b = new byte[TelemetryFrame.Size];
        var s = b.AsSpan();
        "SWGR"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], version);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], (ushort)TelemetryFrame.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], seq);
        BinaryPrimitives.WriteDoubleLittleEndian(s[12..], simTime);
        BinaryPrimitives.WriteSingleLittleEndian(s[20..], dt);
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)flags);
        s[28] = (byte)cls;
        colls ??= [];
        s[29] = (byte)colls.Length;
        int o = TelemetryFrame.HeaderSize;
        foreach (var v in f) { BinaryPrimitives.WriteSingleLittleEndian(s[o..], v); o += 4; }
        for (int i = 0; i < TelemetryFrame.ByteCount; i++) s[o++] = bytes is not null && i < bytes.Length ? bytes[i] : (byte)0;
        for (int i = 0; i < Math.Min(colls.Length, TelemetryFrame.MaxCollisions); i++)
        {
            var c = colls[i];
            int p = o + i * TelemetryFrame.CollisionSize;
            float[] cv = [c.x, c.y, c.z, c.lx, c.ly, c.lz, c.pvd];
            for (int k = 0; k < 7; k++) BinaryPrimitives.WriteSingleLittleEndian(s[(p + k * 4)..], cv[k]);
            BinaryPrimitives.WriteInt32LittleEndian(s[(p + 28)..], c.id);
            s[p + 32] = (byte)(c.veh ? 1 : 0);
        }
        return b;
    }

    public static TelemetryFrame Parse(byte[] b) => TelemetryFrame.Parse(b, out var e) ?? throw new Exception("parse failed: " + e);

    /// <summary>A racing frame at speed (InputMode Racing, on the ground).</summary>
    public static byte[] Racing(uint seq, double t, float speed = 250, float slip = 3, float yaw = 0.3f, FrameFlags extra = 0,
                                (float, float, float, float, float, float, float, int, bool)[]? colls = null) =>
        Build(f =>
        {
            f[(int)F.SpeedKmph] = speed; f[(int)F.TopSpeedKmph] = 346; f[(int)F.RelVelX] = speed * 27.78f;
            f[(int)F.SlipDeg] = slip; f[(int)F.AngVelZ] = yaw; f[(int)F.OnGroundScale] = 1; f[(int)F.Throttle] = 1;
            f[(int)F.RpmScale] = 0.7f;
            // a moving vehicle never repeats its orientation bit for bit
            float a = (float)(t * 0.37);
            f[(int)F.QuatZ] = MathF.Sin(a / 2); f[(int)F.QuatW] = MathF.Cos(a / 2);
        }, bytes: [0, 0, 0, 0, 0, 3, 0, 0], flags: FrameFlags.HasVehicle | FrameFlags.CollisionsOk | extra, seq: seq, simTime: t, colls: colls);

    public static IEnumerable<string> Recordings()
    {
        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Dumps", "recordings"));
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.tsv").Where(p => new FileInfo(p).Length > 10_000) : [];
    }

    /// <summary>TSV recording → wire frames, through the bridge's own (private) Replay.Pack.</summary>
    public static List<(TelemetryFrame frame, float dt)> Load(string tsv)
    {
        var pack = typeof(Replay).GetMethod("Pack", BindingFlags.NonPublic | BindingFlags.Static)!;
        var lines = File.ReadAllLines(tsv);
        var header = lines[0].TrimStart('﻿').Split('\t');
        int dtCol = Array.IndexOf(header, "dt");
        var list = new List<(TelemetryFrame, float)>();
        foreach (var line in lines.Skip(1))
        {
            var c = line.Split('\t');
            var bytes = (byte[])pack.Invoke(null, [c, header, CultureInfo.InvariantCulture])!;
            list.Add((Parse(bytes), float.Parse(c[dtCol], CultureInfo.InvariantCulture)));
        }
        return list;
    }
}

static class FrameTests
{
    public static void RoundTrip()
    {
        var b = Frames.Build(f => { for (int i = 0; i < f.Length; i++) f[i] = i * 1.5f - 7; },
            bytes: [1, 2, 3, 4, 5, 6, 7, 8], flags: FrameFlags.HasVehicle | FrameFlags.RaceDone | FrameFlags.Drifting,
            cls: VehicleClass.Pod, seq: 0xDEADBEEF, simTime: 1234.5678, dt: 0.0125f,
            colls: [(1, 2, 3, 4, 5, 6, 777, 42, true), (0, 0, 0, 0, -50, 0, 10, -3, false)]);
        var fr = Frames.Parse(b);
        Check.True(fr.Seq == 0xDEADBEEF && fr.SimTime == 1234.5678 && fr.Dt == 0.0125f, "header");
        Check.True(fr.VehicleClass == VehicleClass.Pod && fr.Has(FrameFlags.RaceDone) && fr.Has(FrameFlags.Drifting) && !fr.Has(FrameFlags.OnFoot), "flags/class");
        for (int i = 0; i < TelemetryFrame.FloatCount; i++) Check.True(fr.Floats[i] == i * 1.5f - 7, $"float {i}");
        for (int i = 0; i < TelemetryFrame.ByteCount; i++) Check.True(fr.Bytes[i] == i + 1, $"byte {i}");
        Check.True(fr.CollisionCount == 2 && fr.Collisions.Length == 2, "collision count");
        Check.True(fr.Collisions[0].PointVelocityDelta == 777 && fr.Collisions[0].Id == 42 && fr.Collisions[0].OtherIsVehicle, "collision 0");
        Check.True(fr.Collisions[1].LocalLocation.Y == -50 && fr.Collisions[1].Id == -3, "collision 1");
        Check.True(TelemetryFrame.Size == 318, $"frame size {TelemetryFrame.Size} (exporter sends 318)");
    }

    public static void NonFinite()
    {
        var b = Frames.Build(f => { f[(int)F.SlipDeg] = float.NaN; f[(int)F.SpeedKmph] = float.PositiveInfinity; f[(int)F.Roll] = float.NegativeInfinity; },
            colls: [(float.NaN, 0, 0, 0, float.PositiveInfinity, 0, float.NaN, 1, false)]);
        var fr = Frames.Parse(b);
        Check.True(fr.Floats.All(float.IsFinite), "floats finite");
        var c = fr.Collisions[0];
        Check.True(float.IsFinite(c.WorldDirection.X) && float.IsFinite(c.LocalLocation.Y) && float.IsFinite(c.PointVelocityDelta), "collision finite");
    }

    public static void Rejects()
    {
        var good = Frames.Build();
        var bad = (byte[])good.Clone(); bad[0] = (byte)'X';
        Check.True(TelemetryFrame.Parse(bad, out _) is null, "bad magic accepted");
        Check.True(TelemetryFrame.Parse(Frames.Build(version: 2), out var e) is null && e!.Contains("v2"), "wrong version accepted");
        Check.True(TelemetryFrame.Parse(good.AsSpan(0, 100), out _) is null, "short buffer accepted");
        var many = (byte[])good.Clone(); many[29] = 255;   // more contacts than the frame carries
        Check.True(Frames.Parse(many).Collisions.Length == TelemetryFrame.MaxCollisions, "collision count not capped");
    }

    public static void Fuzz()
    {
        var rng = new Random(1234);
        var buf = new byte[TelemetryFrame.Size];
        var model = new FfbModel(new FfbConfig());
        double now = 0;
        for (int i = 0; i < 50_000; i++)
        {
            rng.NextBytes(buf);
            if (i % 2 == 0) { Frames.Build().AsSpan(0, 8).CopyTo(buf); }   // valid header, random body
            var fr = TelemetryFrame.Parse(buf, out _);
            if (fr is null) continue;
            Check.True(fr.Floats.All(float.IsFinite), "non-finite float survived");
            model.OnFrame(fr, now);
            for (int k = 0; k < 4; k++)
            {
                now += 0.002;
                float t = model.Step(now, 0.002f, (float)(rng.NextDouble() * 900 - 450), (float)(rng.NextDouble() * 4000 - 2000), 180, (1, 1, 0));
                Check.True(float.IsFinite(t) && Math.Abs(t) <= 1, $"torque {t}");
            }
        }
    }
}

static class PipeTests
{
    private static (PipeReceiver rx, CancellationTokenSource cts, Task task, List<uint> seqs) Start(string name, Action<TelemetryFrame>? extra = null)
    {
        var seqs = new List<uint>();
        var rx = new PipeReceiver(name, (f, raw) => { lock (seqs) seqs.Add(f.Seq); extra?.Invoke(f); });
        var cts = new CancellationTokenSource();
        var task = Task.Run(() => rx.RunAsync(cts.Token));
        return (rx, cts, task, seqs);
    }

    private static void WaitFor(Func<bool> cond, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!cond()) { if (sw.ElapsedMilliseconds > 3000) throw new Exception("timed out waiting for " + what); Thread.Sleep(10); }
    }

    public static void Resync()
    {
        string name = "swgr_wheelbridge_test_" + Environment.ProcessId;
        var (rx, cts, task, seqs) = Start(name);
        using (var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly))
        {
            client.Connect(2000);
            client.Write("garbage"u8);
            client.Write(Frames.Build(seq: 1));
            var f2 = Frames.Build(seq: 2);
            client.Write(f2.AsSpan(0, 100)); client.Flush(); Thread.Sleep(30);
            client.Write(f2.AsSpan(100));
            client.Write(Frames.Build(seq: 99, version: 7));
            client.Write("SW"u8);   // a false magic start
            client.Write(Frames.Build(seq: 3));
            client.Flush();
            WaitFor(() => { lock (seqs) return seqs.Count >= 3; }, "3 frames");
        }
        WaitFor(() => !rx.Connected, "disconnect");
        // The bridge must accept the next connection too (the exporter reconnects after a bridge restart).
        using (var again = new NamedPipeClientStream(".", name, PipeDirection.Out))
        {
            again.Connect(2000);
            again.Write(Frames.Build(seq: 4)); again.Flush();
            WaitFor(() => { lock (seqs) return seqs.Count >= 4; }, "frame after reconnect");
        }
        cts.Cancel();
        task.Wait(2000);
        lock (seqs) Check.True(seqs.SequenceEqual([1u, 2u, 3u, 4u]), "frames: " + string.Join(",", seqs));
        Check.True(rx.BadFrames >= 2, $"bad frames counted: {rx.BadFrames}");
    }

    public static void HandlerThrows()
    {
        string name = "swgr_wheelbridge_test2_" + Environment.ProcessId;
        int n = 0;
        var (rx, cts, task, seqs) = Start(name, f => { if (Interlocked.Increment(ref n) == 1) throw new IOException("disk full"); });
        using (var client = new NamedPipeClientStream(".", name, PipeDirection.Out))
        {
            client.Connect(2000);
            for (uint i = 1; i <= 3; i++) client.Write(Frames.Build(seq: i));
            client.Flush();
            WaitFor(() => { lock (seqs) return seqs.Count >= 3; }, "frames after a throwing handler");
        }
        cts.Cancel();
        task.Wait(2000);
        Check.True(rx.LastError?.Contains("disk full") == true, "error reported: " + rx.LastError);
    }
}

static class ConfigTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "wbtest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static void Corrupt()
    {
        string d = TempDir(), p = Path.Combine(d, "wheelbridge.json");
        File.WriteAllText(p, "{ \"SteeringLockDegrees\": 180, oops");
        var c = BridgeConfig.Load(p);
        Check.True(c.LoadWarning is not null, "no warning");
        Check.True(File.ReadAllText(p + ".bad").Contains("oops"), "bad copy missing");
        Check.True(c.SteeringLockDegrees == 360, "not defaults");
        Check.True(BridgeConfig.Load(p).LoadWarning is null, "rewritten file still unreadable");
    }

    public static void Sanitize()
    {
        string d = TempDir(), p = Path.Combine(d, "wheelbridge.json");
        File.WriteAllText(p, """
            { "Buttons": null, "PedalButtons": [null, {"Button":"X","Pedal":null,"Threshold":"NaN"}],
              "SteeringLockDegrees": 0, "PedalDeadzone": 0.5, "VehicleProfiles": null,
              "Ffb": { "Layers": { "Centering": null, "Road": { "Gain": 99 } }, "AligningPeakSlipDeg": 0, "LoadReference": -1,
                       "MasterGain": 7, "ForceSign": 0, "UpdateHz": 0, "StaleMs": -5, "CenterLockFullAtDeg": 0 } }
            """);
        BridgeConfig c;
        try { c = BridgeConfig.Load(p); } catch (Exception ex) { throw new Exception("load threw: " + ex.Message); }
        // "NaN" as a string isn't valid JSON for float without AllowNamedFloatingPointLiterals: then it's the corrupt path.
        if (c.LoadWarning is not null)
        {
            File.WriteAllText(p, File.ReadAllText(p + ".bad").Replace("\"NaN\"", "0"));
            c = BridgeConfig.Load(p);
            Check.True(c.LoadWarning is null, "still unreadable: " + c.LoadWarning);
        }
        Check.True(c.Buttons is not null && c.PedalButtons.Count == 1 && c.PedalButtons[0].Pedal == "Clutch", "buttons/pedal buttons");
        Check.True(c.SteeringLockDegrees >= 90 && c.PedalDeadzone <= 0.4f, "steering/pedal ranges");
        Check.True(c.VehicleProfiles.Count == 4, "vehicle profiles restored");
        var f = c.Ffb;
        Check.True(f.Layers.Count == FfbModel.LayerOrder.Length && f.Layers.Values.All(l => l is not null), "layers restored");
        Check.True(f.Layers["Road"].Gain <= 2, "layer gain clamp");
        Check.True(f.AligningPeakSlipDeg >= 0.5f && f.LoadReference >= 0.5f && f.MasterGain <= 1 && f.ForceSign == 1
                   && f.UpdateHz >= 50 && f.StaleMs >= 30 && f.CenterLockFullAtDeg >= 1, "ffb ranges");
        // And the repaired config must drive the model without NaN.
        var m = new FfbModel(f);
        m.OnFrame(Frames.Parse(Frames.Racing(1, 0)), 0);
        for (int i = 1; i < 200; i++) Check.True(float.IsFinite(m.Step(i * 0.002, 0.002f, 30, 100, c.SteeringLockDegrees, (0, 0, 9))), "NaN torque");
    }

    public static void AtomicSave()
    {
        string d = TempDir(), p = Path.Combine(d, "wheelbridge.json");
        var c = BridgeConfig.Load(p);
        c.Ffb.Layers["Road"].Gain = 0.77f;
        c.Save(p);
        Check.True(!File.Exists(p + ".tmp"), "temp file left behind");
        Check.True(BridgeConfig.Load(p).Ffb.Layers["Road"].Gain == 0.77f, "value not saved");
        // Saving while another handle reads the file (the GUI autosave racing an editor) must not throw.
        using (var reader = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) c.Save(p);
    }

    public static void ProfileKeepsHardware()
    {
        var c = new BridgeConfig { Throttle = new PedalRange { Released = -100, Pressed = 9000 } };
        c.Buttons["A"] = [14];
        c.Ffb.MasterGain = 0.3f; c.Ffb.ForceSign = -1; c.Ffb.Device = "MyBase"; c.Ffb.UpdateHz = 333; c.Ffb.CenterLockForce = 0.4f;
        var p = new BridgeConfig { SteeringLockDegrees = 200 };
        p.Ffb.MasterGain = 1; p.Ffb.ForceSign = 1; p.Ffb.Device = "Other"; p.Ffb.UpdateHz = 1000; p.Ffb.CenterLockForce = 1;
        p.Ffb.Layers["Engine"].Gain = 0.99f; p.Ffb.LoadReference = 3;
        c.ApplyProfile(p);
        Check.True(c.SteeringLockDegrees == 200 && c.Ffb.Layers["Engine"].Gain == 0.99f && c.Ffb.LoadReference == 3, "feel not applied");
        Check.True(c.Ffb.MasterGain == 0.3f && c.Ffb.ForceSign == -1 && c.Ffb.Device == "MyBase" && c.Ffb.UpdateHz == 333
                   && c.Ffb.CenterLockForce == 0.4f, "hardware/user settings changed by a profile");
        Check.True(c.Throttle!.Pressed == 9000 && c.Buttons["A"][0] == 14, "pedals/buttons changed");
    }

    public static void ForceSignTested()
    {
        string d = TempDir(), p = Path.Combine(d, "wheelbridge.json");
        var c = BridgeConfig.Load(p);
        Check.True(!c.Ffb.ForceSignTested && !c.SetupDone, "a new config claims a tested motor direction or a finished setup");
        c.Ffb.ForceSign = -1; c.Ffb.ForceSignTested = true; c.SetupDone = true;
        c.Save(p);
        var back = BridgeConfig.Load(p);
        Check.True(back.Ffb.ForceSign == -1 && back.Ffb.ForceSignTested && back.SetupDone, "not saved");
        // A profile (which may have been saved on another wheel) never changes it.
        var fresh = new BridgeConfig();
        fresh.ApplyProfile(new BridgeConfig { Ffb = { ForceSignTested = true, ForceSign = -1 } });
        Check.True(!fresh.Ffb.ForceSignTested && fresh.Ffb.ForceSign == 1, "profile set the motor direction");
        back.ApplyProfile(new BridgeConfig());
        Check.True(back.Ffb.ForceSignTested && back.Ffb.ForceSign == -1, "profile cleared the motor direction");
    }

    private static string DefaultsDir()
    {
        string a = Path.Combine(AppContext.BaseDirectory, "defaults", "profiles");
        return Directory.Exists(a) ? a : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "WheelBridge", "defaults", "profiles"));
    }

    public static void DefaultProfiles()
    {
        var files = Directory.GetFiles(DefaultsDir(), "*.json");
        var names = files.Select(Path.GetFileNameWithoutExtension).ToHashSet();
        foreach (var v in BridgeConfig.DefaultVehicleProfiles().Values) Check.True(names.Contains(v), $"default profile {v} missing");
        foreach (var file in files)
        {
            var p = BridgeConfig.ReadFile(file);
            Check.True(p.Ffb.Layers.Count == FfbModel.LayerOrder.Length, $"{file}: layers");
            string raw = File.ReadAllText(file);
            foreach (var hw in new[] { "MasterGain", "ForceSign", "Buttons", "Throttle", "Device" })
                Check.True(!raw.Contains($"\"{hw}\""), $"{Path.GetFileName(file)} ships hardware setting {hw}");
            // Each one must produce sane forces on a recorded lap.
            var rec = Frames.Recordings().FirstOrDefault();
            if (rec is null) continue;
            var m = new FfbModel(p.Ffb);
            double now = 0; int clipped = 0, ticks = 0;
            foreach (var (fr, dt) in Frames.Load(rec).Take(3000))
            {
                m.OnFrame(fr, now);
                for (int i = 0; i < Math.Max(1, (int)Math.Round(dt * 500)); i++)
                {
                    now += 0.002;
                    float t = m.Step(now, 0.002f, fr[F.SteerInput] * p.SteeringLockDegrees / 2, 0, p.SteeringLockDegrees, (0, 0, 9));
                    Check.True(float.IsFinite(t), "NaN");
                    if (Math.Abs(t) >= 0.999f) clipped++;
                    ticks++;
                }
            }
            Check.True(clipped < ticks * 0.3, $"{Path.GetFileName(file)} clips {clipped * 100 / Math.Max(1, ticks)}% of ticks");
        }
    }

    public static void AutoSwitch()
    {
        string d = TempDir(), prof = Path.Combine(d, "profiles");
        Directory.CreateDirectory(prof);
        var a = new BridgeConfig { SteeringLockDegrees = 300 }; a.Save(Path.Combine(prof, "Land Speeder.json"));
        var b = new BridgeConfig { SteeringLockDegrees = 150 }; b.Save(Path.Combine(prof, "Speeder Bike.json"));
        var cfg = BridgeConfig.Load(Path.Combine(d, "wheelbridge.json"));
        int saves = 0;
        var pm = new ProfileManager(cfg, prof, () => saves++);

        Check.True(pm.OnVehicle(null) is null && pm.OnVehicle(VehicleClass.Unknown) is null, "switched on no vehicle");
        Check.True(pm.OnVehicle(VehicleClass.Car)?.Contains("Land Speeder") == true && cfg.SteeringLockDegrees == 300, "car profile");
        Check.True(pm.OnVehicle(VehicleClass.Car) is null, "re-switched to the same vehicle");
        cfg.SteeringLockDegrees = 310;   // unsaved tuning
        Check.True(pm.Modified, "modified not detected");
        var msg = pm.OnVehicle(VehicleClass.Bike);
        Check.True(msg?.Contains("unsaved") == true && cfg.SteeringLockDegrees == 310 && cfg.ActiveProfile == "Land Speeder", "unsaved tuning discarded: " + msg);
        Check.True(pm.OnVehicle(VehicleClass.Bike) is null, "refusal message repeated every poll");
        pm.Save("Land Speeder");
        Check.True(!pm.Modified && BridgeConfig.ReadFile(pm.PathOf("Land Speeder")).SteeringLockDegrees == 310, "save");
        Check.True(pm.OnVehicle(VehicleClass.Bike)?.Contains("Speeder Bike") == true && cfg.SteeringLockDegrees == 150, "bike profile");
        Check.True(pm.OnVehicle(VehicleClass.Pod)?.Contains("no profile") == true && cfg.SteeringLockDegrees == 150, "missing profile");
        cfg.AutoSwitchProfiles = false;
        Check.True(pm.OnVehicle(VehicleClass.Car) is null && cfg.SteeringLockDegrees == 150, "switched while auto is off");
        Check.True(pm.Save("bad:name").Contains("can't") && pm.Save("  ").Contains("can't") && pm.Save("..").Contains("can't"), "bad names accepted");
        Check.True(saves > 0, "config not saved after switching");
    }

    public static void Seeding()
    {
        string d = TempDir(), src = Path.Combine(d, "src"), dst = Path.Combine(d, "profiles");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "Podracer.json"), "{\"SteeringLockDegrees\": 200}");
        File.WriteAllText(Path.Combine(src, "Speeder Bike.json"), "{\"SteeringLockDegrees\": 180}");
        ProfileManager.SeedDefaults(src, dst);
        Check.True(File.Exists(Path.Combine(dst, "Podracer.json")), "not seeded");
        File.WriteAllText(Path.Combine(dst, "Speeder Bike.json"), "{\"SteeringLockDegrees\": 123}");   // user's own tuning
        var cfg = new BridgeConfig();
        var pm = new ProfileManager(cfg, dst, () => { });
        pm.Delete("Podracer");
        ProfileManager.SeedDefaults(src, dst);
        Check.True(!File.Exists(Path.Combine(dst, "Podracer.json")), "deleted default came back");
        Check.True(File.ReadAllText(Path.Combine(dst, "Speeder Bike.json")).Contains("123"), "user profile overwritten");
        Check.True(!pm.Names().Contains("Podracer.json") && pm.Names().SequenceEqual(["Speeder Bike"]), "names: " + string.Join(",", pm.Names()));
    }
}

static class ModelTests
{
    private static uint Hash(IEnumerable<float> xs) { uint h = 2166136261; foreach (var x in xs) h = (h ^ BitConverter.SingleToUInt32Bits(x)) * 16777619; return h; }

    private static List<float> Run(List<(TelemetryFrame f, float dt)> frames, FfbConfig cfg)
    {
        var m = new FfbModel(cfg);
        var outp = new List<float>();
        double now = 0; float last = 0;
        foreach (var (fr, dt) in frames)
        {
            m.OnFrame(fr, now);
            int steps = Math.Max(1, (int)Math.Round(Math.Max(0.001f, dt) * 500));
            float target = fr[F.SteerInput] * 90;
            for (int i = 0; i < steps; i++)
            {
                now += 0.002;
                float angle = last + (target - last) * (i + 1) / steps;
                outp.Add(m.Step(now, 0.002f, angle, (angle - last) * 500, 180, (0, 0, 9)));
            }
            last = target;
        }
        return outp;
    }

    public static void Deterministic()
    {
        var recs = Frames.Recordings().ToList();
        Check.True(recs.Count > 0, "no recordings found under Dumps\\recordings");
        foreach (var rec in recs)
        {
            var frames = Frames.Load(rec);
            var a = Run(frames, new FfbConfig());
            var b = Run(frames, new FfbConfig());
            Check.True(a.Count > 100, $"{Path.GetFileName(rec)}: only {a.Count} ticks");
            Check.True(Hash(a) == Hash(b), $"{Path.GetFileName(rec)} not deterministic");
            Check.True(a.All(float.IsFinite) && a.Any(t => Math.Abs(t) > 0.05f), $"{Path.GetFileName(rec)}: no force or NaN");
        }
    }

    public static void NaNInput()
    {
        var m = new FfbModel(new FfbConfig());
        double now = 0;
        for (int i = 0; i < 300; i++)
        {
            m.OnFrame(Frames.Parse(Frames.Racing((uint)i, now)), now);
            for (int k = 0; k < 8; k++)
            {
                now += 0.002;
                float angle = i == 150 && k == 0 ? float.NaN : 20, vel = i == 160 ? float.PositiveInfinity : 10;
                float t = m.Step(now, 0.002f, angle, vel, 180, (0, 0, 9));
                Check.True(float.IsFinite(t), $"NaN torque at frame {i}");
            }
        }
        float after = m.Step(now + 0.002, 0.002f, 20, 0, 180, (0, 0, 9));
        Check.True(float.IsFinite(after) && Math.Abs(after) > 0.01f, $"force didn't come back after a NaN input ({after})");
    }

    public static void MutedAfterFinish()
    {
        var m = new FfbModel(new FfbConfig());
        double now = 0;
        float last = 1;
        for (int i = 0; i < 120; i++)
        {
            m.OnFrame(Frames.Parse(Frames.Racing((uint)i, now, extra: FrameFlags.RaceDone,
                colls: [(0, -1, 0, 100, 60, 0, 2500, 1000 + i, true)])), now);
            for (int k = 0; k < 8; k++) { now += 0.002; last = m.Step(now, 0.002f, 40, 300, 180, (1, 1, 0)); }
        }
        Check.True(m.State.Contains("finished") && !m.Racing, "state: " + m.State);
        Check.True(last == 0, $"force after finish: {last}");
    }

    public static void Pause()
    {
        var m = new FfbModel(new FfbConfig());
        var frozen = Frames.Parse(Frames.Racing(1, 5));
        for (int i = 0; i < 5; i++) m.OnFrame(frozen, i * 0.016);
        Check.True(m.Paused, "frozen frames at speed not detected as paused");
        m.OnFrame(Frames.Parse(Frames.Racing(2, 5.1)), 0.1);
        Check.True(!m.Paused, "still paused after motion");
    }

    public static void ImpactsBounded()
    {
        var m = new FfbModel(new FfbConfig());
        var impacts = (System.Collections.ICollection)typeof(FfbModel).GetField("_impacts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;
        double now = 0;
        for (int i = 0; i < 20_000; i++)
        {
            // 4 new contacts every frame during a long muted run-out; no Step calls with live force.
            var colls = Enumerable.Range(0, 4).Select(k => (0f, -1f, 0f, 100f, 60f, 0f, 3000f, i * 4 + k, false)).ToArray();
            m.OnFrame(Frames.Parse(Frames.Racing((uint)i, now, extra: FrameFlags.RaceDone, colls: colls)), now);
            now += 1 / 60.0;
        }
        Check.True(impacts.Count < 200, $"{impacts.Count} queued impacts");
    }

    public static void Lean()
    {
        float Run(float roll, float gain)
        {
            var cfg = new FfbConfig();
            foreach (var l in cfg.Layers.Values) l.Enabled = false;
            cfg.Layers[FfbLayers.Lean].Enabled = true;
            cfg.Layers[FfbLayers.Lean].Gain = gain;
            var m = new FfbModel(cfg);
            double now = 0; float t = 0;
            for (int i = 0; i < 120; i++)
            {
                var raw = Frames.Racing((uint)i, now);
                BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(TelemetryFrame.HeaderSize + (int)F.Roll * 4), roll);
                m.OnFrame(Frames.Parse(raw), now);
                for (int k = 0; k < 8; k++) { now += 0.002; t = m.Step(now, 0.002f, 0, 0, 180, (0, 0, 9)); }
            }
            return t;
        }
        // Roll < 0 = leaning right (follows right steer) → push left (negative), and the reverse.
        float right = Run(-1.2f, 0.45f), left = Run(1.2f, 0.45f);
        Check.True(right < -0.1f && left > 0.1f, $"lean push right-lean {right}, left-lean {left}");
        Check.Near(right, -left, 1e-4, "lean not symmetric");
        Check.True(Run(-1.2f, 0) == 0 && Run(0, 0.45f) == 0, "lean force with gain 0 or upright");
        Check.True(float.IsFinite(Run(float.NaN, 0.45f)), "NaN roll");
    }

    public static void LeanUnwind()
    {
        var cfg = new FfbConfig();
        foreach (var l in cfg.Layers.Values) l.Enabled = false;
        cfg.Layers[FfbLayers.Lean].Enabled = true;
        cfg.Layers[FfbLayers.Lean].Gain = 1;
        var m = new FfbModel(cfg);
        double now = 0;
        // Lean right (roll -1.4), then straighten up fast (0.25 s): the push must stay toward upright (left, <= 0) the whole
        // time. The old lean-in kick pushed right (toward the lean) while unwinding, so the wheel ran away.
        for (int i = 0; i < 180; i++)
        {
            float roll = i < 90 ? -1.4f : Math.Min(0, -1.4f + (i - 90) * 1.4f / 15);
            var raw = Frames.Racing((uint)i, now);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(TelemetryFrame.HeaderSize + (int)F.Roll * 4), roll);
            m.OnFrame(Frames.Parse(raw), now);
            for (int k = 0; k < 8; k++)
            {
                now += 0.002;
                float t = m.Step(now, 0.002f, 0, 0, 180, (0, 0, 9));
                Check.True(t <= 1e-6f, $"lean pushed toward the lean ({t:F3}) at frame {i}, roll {roll:F2}");
            }
        }
        // The shipped bike profile keeps it light: at most ~0.15 of full torque from a full lean.
        string bike = Path.Combine(AppContext.BaseDirectory, "defaults", "profiles", "Speeder Bike.json");
        if (!File.Exists(bike)) bike = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "WheelBridge", "defaults", "profiles", "Speeder Bike.json"));
        float gain = BridgeConfig.ReadFile(bike).Ffb.Layers[FfbLayers.Lean].Gain;
        Check.True(gain is > 0 and <= 0.15f, $"Speeder Bike lean gain {gain}");
    }

    public static void NoVehicleClears()
    {
        var m = new FfbModel(new FfbConfig());
        var wreck = Frames.Parse(Frames.Build(f => { f[(int)F.CrashTimeRemaining] = 2; f[(int)F.SpeedKmph] = 10; f[(int)F.QuatW] = 1; },
            bytes: [0, 0, 0, 0, 0, 3, 0, 0]));
        m.OnFrame(wreck, 0);
        m.Step(0.002, 0.002f, 0, 0, 180, (0, 0, 9));
        Check.True(m.Wrecked && m.Airborne, "setup: wrecked and airborne");
        m.OnFrame(Frames.Parse(Frames.Build(flags: FrameFlags.OnFoot)), 0.01);
        Check.True(!m.Wrecked && !m.Airborne, "flags kept after leaving the vehicle");
    }
}

static class ForceTests
{
    /// <summary>
    /// Runs the force-center hold against a simulated wheel: 0.03 kg·m² rim, 3.9 Nm at full output, 10 ms from torque
    /// to motor, end stops at ±90°. motorSign -1 is a motor that turns the other way than ForceSign says.
    /// </summary>
    private static (bool Released, float Angle, string? Fault, float MaxAngle) Hold(int motorSign, float startDeg, double seconds = 3)
    {
        var eng = (FfbEngine)RuntimeHelpers.GetUninitializedObject(typeof(FfbEngine));
        typeof(FfbEngine).GetField("_cfg", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(eng, new BridgeConfig());
        var torque = typeof(FfbEngine).GetMethod("CenterLockTorque", BindingFlags.NonPublic | BindingFlags.Instance)!;
        eng.CenterLock = true;
        const float dt = 0.002f, inertia = 0.03f, peakNm = 3.9f;
        var delay = new Queue<float>(Enumerable.Repeat(0f, 5));
        float angle = startDeg, vel = 0, max = 0;
        for (int i = 0; i < seconds / dt; i++)
        {
            float t = (float)torque.Invoke(eng, [angle, vel, dt])!;
            if (float.IsNaN(t)) return (true, angle, eng.CenterLockFault, max);
            delay.Enqueue(t);
            float nm = motorSign * delay.Dequeue() * peakNm - 0.02f * vel / 57.3f;   // plus a little bearing friction
            vel += nm / inertia * 57.3f * dt;
            angle += vel * dt;
            if (Math.Abs(angle) > 90) { angle = Math.Clamp(angle, -90, 90); vel = -0.3f * vel; }
            max = Math.Max(max, Math.Abs(angle));
        }
        return (false, angle, eng.CenterLockFault, max);
    }

    public static void CenterLock()
    {
        foreach (float start in new[] { 60f, -45f, 5f })
        {
            var ok = Hold(+1, start);
            Check.True(!ok.Released && ok.Fault is null, $"correct motor from {start}°: released ({ok.Fault})");
            Check.True(Math.Abs(ok.Angle) < 1.5f, $"correct motor from {start}°: ended at {ok.Angle:F1}°");
            Check.True(ok.MaxAngle <= Math.Abs(start) + 2, $"correct motor from {start}°: swung out to {ok.MaxAngle:F1}°");
        }
        foreach (float start in new[] { 30f, -10f, 0.5f })
        {
            var bad = Hold(-1, start, seconds: 1.5);
            Check.True(bad.Released && bad.Fault?.Contains("Motor test") == true, $"wrong motor direction from {start}°: never let go (max {bad.MaxAngle:F0}°)");
        }
    }
}

static class BaseTests
{
    public static void Detection()
    {
        var r3 = Wheelbase.FromProductName("R3 Racing Wheel and Pedals");   // what the R3 reports (VID 346E PID 0005)
        Check.True(r3.Model == "R3" && r3.Known && r3.Tested && r3.PeakNm == 3.9f, $"R3: {r3}");
        foreach (var (name, model, nm) in new[] { ("MOZA R5 Base", "R5", 5.5f), ("R9 Racing Wheel", "R9", 9f), ("MOZA R12", "R12", 12f),
                                                  ("R16 Base", "R16", 16f), ("MOZA R21 Racing Wheel Base", "R21", 21f) })
        {
            var b = Wheelbase.FromProductName(name);
            Check.True(b.Model == model && b.Known && !b.Tested && b.PeakNm == nm, $"{name}: {b}");
        }
        var r25 = Wheelbase.FromProductName("MOZA R25 Ultra");
        Check.True(r25.Model == "R25" && !r25.Known && r25.PeakNm == 25, $"unlisted model: {r25}");
        foreach (var name in new[] { "", "MOZA Racing Wheel", "CRP Pedals", "FR3000 Racing" })
        {
            var u = Wheelbase.FromProductName(name);
            Check.True(!u.Known && !u.Tested && u.PeakNm == Wheelbase.UnknownNm, $"'{name}' should be unknown: {u}");
        }
    }

    public static void Scaling()
    {
        Check.True(Wheelbase.FromProductName("R3 Racing Wheel and Pedals").AutoScale == 1, "R3 scale");
        foreach (var name in new[] { "R5", "R9", "R12", "R16", "R21", "R25", "unknown" })
        {
            var b = Wheelbase.FromProductName(name);
            float nmAtFull = b.AutoScale * b.PeakNm;
            Check.True(nmAtFull <= Wheelbase.ReferenceNm * Wheelbase.UntestedMargin + 0.01f, $"{name}: {nmAtFull:F2} Nm at Master 1");
        }
        Check.Near(Wheelbase.FromProductName("R9").AutoScale, 3.9 / 9 * 0.85, 1e-4, "R9 scale");
    }
}

static class SdkTests
{
    public static void InstallFromZip()
    {
        string d = Path.Combine(Path.GetTempPath(), "wbsdk_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        string zipPath = Path.Combine(d, "MOZA_SDK.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var f in MozaSdk.Files)
            {
                using var w = new StreamWriter(zip.CreateEntry(MozaSdk.ZipFolder + f).Open());
                w.Write("not the real " + f);
            }
            // Entries that must never be extracted (a path-traversal attempt and an unrelated file).
            using (var w = new StreamWriter(zip.CreateEntry("../../evil.dll").Open())) w.Write("x");
            using (var w = new StreamWriter(zip.CreateEntry("MOZA_SDK/Tools/OpenSSL/Win_x64/bin/libssl-1_1-x64.dll").Open())) w.Write("x");
        }
        bool asked = false;
        var refused = MozaSdk.InstallFromZip(zipPath, _ => { asked = true; return false; });
        Check.True(asked && !refused.Ok && refused.Unverified, "unverified files installed without consent: " + refused.Message);
        Check.True(!MozaSdk.Files.Any(f => File.Exists(Path.Combine(MozaSdk.DownloadDir, f))), "files written although refused");

        var accepted = MozaSdk.InstallFromZip(zipPath, _ => true);
        Check.True(accepted.Ok, "install failed: " + accepted.Message);
        Check.True(MozaSdk.Files.All(f => File.Exists(Path.Combine(MozaSdk.DownloadDir, f))), "files missing");
        var written = Directory.GetFiles(MozaSdk.DownloadDir).Select(Path.GetFileName).ToHashSet();
        Check.True(written.SetEquals(MozaSdk.Files), "extra files extracted: " + string.Join(", ", written));
        Check.True(!File.Exists(Path.GetFullPath(Path.Combine(MozaSdk.DownloadDir, "..", "..", "evil.dll"))), "path traversal");

        string notSdk = Path.Combine(d, "other.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(notSdk, System.IO.Compression.ZipArchiveMode.Create)) zip.CreateEntry("readme.txt");
        Check.True(!MozaSdk.InstallFromZip(notSdk, _ => true).Ok, "a zip without the SDK accepted");
        File.WriteAllText(Path.Combine(d, "broken.zip"), "not a zip");
        Check.True(!MozaSdk.InstallFromZip(Path.Combine(d, "broken.zip"), _ => true).Ok, "a broken zip accepted");
        Check.True(MozaSdk.KnownSha256.Values.All(h => h.Length == 64), "KnownSha256 not filled in for SDK 1.0.1.8");
    }
}

static class InputTests
{
    private static WheelState State(int thr = -32768, int brk = -32768, int clu = -32768, params int[] pressed) =>
        new(0, 0, thr, brk, clu, pressed.ToHashSet(), DateTime.UtcNow);

    private static BridgeConfig Pedalled()
    {
        var c = new BridgeConfig
        {
            Throttle = new PedalRange { Released = -32768, Pressed = 32767 },
            Brake = new PedalRange { Released = -32768, Pressed = 32767 },
            Clutch = new PedalRange { Released = -32768, Pressed = 32767 },
        };
        c.Sanitize();
        return c;
    }

    public static void Bindings()
    {
        var c = Pedalled();
        c.Buttons = new() { ["A"] = [14], ["RB"] = [22, 23] };
        c.PedalButtons = [new PedalButton { Button = "X", Pedal = "Clutch", Threshold = 0.5f }];
        var down = new bool[PadMapping.Buttons.Length];
        int Ix(string b) => Array.IndexOf(PadMapping.Buttons, b);

        var (_, rt, lt) = PadMapping.Map(State(thr: 32767, brk: -32768, clu: 32767, pressed: [14, 23]), c, down);
        Check.True(rt == 1 && lt == 0, $"pedal triggers {rt}/{lt}");
        Check.True(down[Ix("A")] && down[Ix("RB")] && down[Ix("X")] && !down[Ix("B")], "buttons: " + string.Join(",", PadMapping.Buttons.Where((_, i) => down[i])));

        PadMapping.Map(State(clu: -20000), c, down);
        Check.True(!down[Ix("X")], "clutch below threshold pressed X");

        // No pedals at all: triggers on paddles.
        c.RightTriggerPedal = "None"; c.LeftTriggerPedal = "None";
        c.Throttle = c.Brake = c.Clutch = null;
        c.TriggerButtons = new() { ["RT"] = [40], ["LT"] = [41] };
        (_, rt, lt) = PadMapping.Map(State(pressed: [40]), c, down);
        Check.True(rt == 1 && lt == 0, $"paddle RT {rt}/{lt}");
        (_, rt, lt) = PadMapping.Map(State(), c, down);
        Check.True(rt == 0 && lt == 0, "paddles released");

        // A pedal and a button on the same trigger: the larger wins.
        c = Pedalled();
        c.TriggerButtons = new() { ["RT"] = [40], ["LT"] = [] };
        (_, rt, _) = PadMapping.Map(State(thr: 0, pressed: [40]), c, down);
        Check.True(rt == 1, "button didn't override half throttle");
        (_, rt, _) = PadMapping.Map(State(thr: 0), c, down);
        Check.True(rt is > 0.4f and < 0.6f, $"half throttle {rt}");

        // Sanitize fills a missing TriggerButtons section.
        var raw = new BridgeConfig { TriggerButtons = null! };
        raw.Sanitize();
        Check.True(raw.TriggerButtons["RT"] is not null && raw.TriggerButtons["LT"] is not null, "TriggerButtons not repaired");
    }

    public static void BindingText()
    {
        var c = Pedalled();
        c.Buttons = new() { ["A"] = [14], ["RB"] = [14] };
        c.TriggerButtons = new() { ["RT"] = [40], ["LT"] = [] };
        c.PedalButtons = [new PedalButton { Button = "X", Pedal = "Clutch", Threshold = 0.5f }];
        Check.True(PadMapping.Describe("RT", c) == "throttle pedal, wheel 40", "RT: " + PadMapping.Describe("RT", c));
        Check.True(PadMapping.Describe("X", c).Contains("clutch"), "X: " + PadMapping.Describe("X", c));
        Check.True(PadMapping.Describe("Y", c) == "not bound", "Y");
        c.LeftTriggerPedal = "None";
        Check.True(PadMapping.Describe("LT", c) == "not bound", "LT none: " + PadMapping.Describe("LT", c));
        Check.True(PadMapping.ControlsUsing(14, c).Count == 2, "duplicate not found");
        Check.True(PadMapping.Buttons.All(PadMapping.GameFunction.ContainsKey) && PadMapping.Triggers.All(PadMapping.GameFunction.ContainsKey), "game function list incomplete");
    }

    public static void Capture()
    {
        var c = Pedalled();
        var cap = new BindCapture("A", State(pressed: [3]), c, TimeSpan.FromSeconds(6));
        Check.True(cap.Poll(State(pressed: [3]), c) == (null, null), "a button held at the start was captured");
        Check.True(cap.Poll(State(), c) == (null, null), "release captured");
        Check.True(cap.Poll(State(pressed: [3]), c) == (3, null), "re-pressed button not captured");
        cap = new BindCapture("X", State(), c, null);
        Check.True(cap.Poll(State(clu: 32767), c) == (null, "Clutch") && !cap.TimedOut, "pedal not captured");
        cap = new BindCapture("RT", State(), c, null);
        Check.True(cap.Poll(State(thr: 32767), c) == (null, null), "a trigger captured a pedal");
        Check.True(new BindCapture("A", State(), c, TimeSpan.FromMilliseconds(-1)).TimedOut, "timeout");

        c.Buttons = new() { ["A"] = [1], ["B"] = [] };
        PadMapping.BindButton(c, "A", 2);
        Check.True(c.Buttons["A"].SequenceEqual([1, 2]), "add: " + string.Join(",", c.Buttons["A"]));
        c.PedalButtons = [new PedalButton { Button = "A", Pedal = "Clutch" }];
        string msg = PadMapping.BindButton(c, "B", 2, replace: true);
        Check.True(msg.Contains("also bound to A") && c.Buttons["B"].SequenceEqual([2]), "replace / duplicate note: " + msg);
        PadMapping.BindButton(c, "A", 7, replace: true);
        Check.True(c.Buttons["A"].SequenceEqual([7]) && c.PedalButtons.Count == 0, "replace kept the old inputs");
        PadMapping.BindPedal(c, "A", "Brake", replace: true);
        Check.True(c.Buttons["A"].Length == 0 && c.PedalButtons.Single().Pedal == "Brake", "pedal replace");
        PadMapping.BindButton(c, "LT", 40);
        Check.True(c.TriggerButtons["LT"].SequenceEqual([40]), "trigger button");
        PadMapping.ClearBinding(c, "LT");
        PadMapping.ClearBinding(c, "A");
        Check.True(c.TriggerButtons["LT"].Length == 0 && c.Buttons["A"].Length == 0 && c.PedalButtons.Count == 0 && c.LeftTriggerPedal == "Brake", "clear");
    }

    public static void Steering()
    {
        var c = new BridgeConfig { SteeringLockDegrees = 180, SteeringAntiDeadzone = 0.31f, SteeringLinearity = 1.3f };
        float prev = -2;
        for (float a = -120; a <= 120; a += 0.5f)
        {
            float s = VirtualPad.SteeringToStick(a, c);
            Check.True(s >= prev - 1e-6f && s is >= -1 and <= 1, $"not monotonic/limited at {a}°");
            Check.Near(s, -VirtualPad.SteeringToStick(-a, c), 1e-6, $"asymmetric at {a}°");
            prev = s;
        }
        Check.True(VirtualPad.SteeringToStick(0, c) == 0, "center not 0");
        Check.Near(VirtualPad.SteeringToStick(90, c), 1, 1e-6, "lock not 1");
        Check.True(VirtualPad.SteeringToStick(1, c) >= 0.31f, "anti-deadzone missing");
        c.InvertSteering = true;
        Check.True(VirtualPad.SteeringToStick(45, c) < 0, "invert");
    }

    public static void Pedals()
    {
        var r = new PedalRange { Released = -32768, Pressed = 32767 };
        var rev = new PedalRange { Released = 32767, Pressed = -32768 };
        foreach (float dz in new[] { 0f, 0.03f, 0.2f, 0.5f, 0.9f, -1f })
            foreach (int raw in new[] { -32768, -1000, 0, 20000, 32767 })
            {
                float v = r.Normalize(raw, dz), w = rev.Normalize(raw, dz);
                Check.True(float.IsFinite(v) && v is >= 0 and <= 1 && float.IsFinite(w) && w is >= 0 and <= 1, $"dz {dz} raw {raw}: {v}/{w}");
            }
        Check.Near(r.Normalize(32767, 0.03f), 1, 1e-6, "full press");
        Check.Near(rev.Normalize(32767, 0.03f), 0, 1e-6, "reversed released");
        Check.True(new PedalRange { Released = 5, Pressed = 5 }.Normalize(100, 0) == 0, "zero span");
    }
}

static class PerfTests
{
    public static void Budget()
    {
        var cfg = new FfbConfig();
        var m = new FfbModel(cfg);
        var raw = Frames.Racing(1, 0, colls: [(0, -1, 0, 100, 60, 0, 2500, 7, true)]);
        var frames = Enumerable.Range(0, 64).Select(i => Frames.Parse(Frames.Racing((uint)i, i / 60.0, colls: [(0, -1, 0, 100, 60, 0, 2500, i % 3, true)]))).ToArray();

        // Warm up, then measure the 500 Hz model step and the per-frame work.
        double now = 0;
        for (int i = 0; i < 20_000; i++) { if (i % 8 == 0) m.OnFrame(frames[i / 8 % 64], now); now += 0.002; m.Step(now, 0.002f, 10, 50, 180, (0.5f, 0.5f, 0)); }

        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        const int N = 200_000;
        for (int i = 0; i < N; i++) { now += 0.002; m.Step(now, 0.002f, 10, 50, 180, (0.5f, 0.5f, 0)); }
        double stepUs = sw.Elapsed.TotalMilliseconds * 1000 / N;
        long stepAlloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / N;

        a0 = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        const int P = 100_000;
        for (int i = 0; i < P; i++) { var f = TelemetryFrame.Parse(raw, out _)!; m.OnFrame(f, now); }
        double frameUs = sw.Elapsed.TotalMilliseconds * 1000 / P;
        long frameAlloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / P;

        Console.WriteLine($"      model step {stepUs:F2} µs ({stepAlloc} B alloc), budget 2000 µs at 500 Hz");
        Console.WriteLine($"      parse + OnFrame {frameUs:F2} µs ({frameAlloc} B alloc) per frame");
        Check.True(stepUs < 50, "model step too slow");
        Check.True(stepAlloc < 64, $"model step allocates {stepAlloc} B per tick (GC pressure at 500 Hz)");
        Check.True(frameUs < 100, "frame handling too slow");
    }
}
