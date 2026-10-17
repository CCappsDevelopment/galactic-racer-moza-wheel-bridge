using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace WheelBridge;

/// <summary>
/// Offline check: feeds a Recorder TSV through <see cref="FfbModel"/> with the wheel angle simulated from the
/// game's processed steering, and prints per-layer statistics. Usage: WheelBridge --replay file.tsv
/// </summary>
public static class Replay
{
    public static void Run(string path, BridgeConfig cfg)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) { Console.WriteLine($"{path}: no frames recorded"); return; }
        var header = lines[0].TrimStart('﻿').Split('\t');
        int Col(string n) => Array.IndexOf(header, n);
        var ci = CultureInfo.InvariantCulture;

        var model = new FfbModel(cfg.Ffb);
        const int hz = 500;
        int n = FfbModel.LayerOrder.Length;
        var sumSq = new double[n]; var peak = new float[n];
        double totalSq = 0; int ticks = 0, clipped = 0;
        float half = cfg.SteeringLockDegrees / 2, lastAngle = 0;
        double now = 0;
        string lastState = "";
        var stateLog = new List<string>();

        for (int row = 1; row < lines.Length; row++)
        {
            var c = lines[row].Split('\t');
            float V(string name) => c[Col(name)] is { Length: > 0 } s ? float.Parse(s, ci) : 0;
            var frame = TelemetryFrame.Parse(Pack(c, header, ci), out var err) ?? throw new InvalidDataException(err);
            float dt = Math.Max(0.001f, V("dt"));
            model.OnFrame(frame, now);

            int steps = Math.Max(1, (int)Math.Round(dt * hz));
            float startAngle = lastAngle, targetAngle = frame[F.SteerInput] * half;
            for (int i = 0; i < steps; i++)
            {
                now += 1.0 / hz;
                float angle = startAngle + (targetAngle - startAngle) * (i + 1) / steps;   // a real wheel moves smoothly
                float vel = (angle - lastAngle) * hz;
                lastAngle = angle;
                float total = model.Step(now, 1f / hz, angle, vel, cfg.SteeringLockDegrees, (0, 0, 99));
                if (model.State != lastState) { stateLog.Add($"  {now,7:F2}s  {model.State}"); lastState = model.State; }
                for (int k = 0; k < n; k++)
                {
                    float v = model.Contributions[k];
                    sumSq[k] += v * v;
                    peak[k] = Math.Max(peak[k], Math.Abs(v));
                }
                totalSq += total * total;
                if (Math.Abs(total) >= 0.999f) clipped++;
                ticks++;
            }
        }

        Console.WriteLine($"Replayed {lines.Length - 1} frames → {ticks} FFB ticks ({ticks / (double)hz:F1} s) at {hz} Hz");
        Console.WriteLine($"{"layer",-10} {"rms",6} {"peak",6}");
        for (int k = 0; k < n; k++)
            Console.WriteLine($"{FfbModel.LayerOrder[k],-10} {Math.Sqrt(sumSq[k] / ticks),6:F3} {peak[k],6:F3}");
        Console.WriteLine($"{"TOTAL",-10} {Math.Sqrt(totalSq / ticks),6:F3}   clipped {clipped * 100.0 / ticks:F2}% of ticks");
        Console.WriteLine($"State changes ({stateLog.Count}):");
        foreach (var line in stateLog.Take(30)) Console.WriteLine(line);
    }

    /// <summary>Rebuilds the binary wire frame from one TSV row (same layout the exporter sends).</summary>
    private static byte[] Pack(string[] c, string[] header, IFormatProvider ci)
    {
        string Get(string name) => c[Array.IndexOf(header, name)];
        float Fl(string name) => Get(name) is { Length: > 0 } s ? float.Parse(s, ci) : 0;
        var b = new byte[TelemetryFrame.Size];
        var s = b.AsSpan();
        "SWGR"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], TelemetryFrame.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], (ushort)TelemetryFrame.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], uint.Parse(Get("seq"), ci));
        BinaryPrimitives.WriteDoubleLittleEndian(s[12..], double.Parse(Get("simTime"), ci));
        BinaryPrimitives.WriteSingleLittleEndian(s[20..], Fl("dt"));
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], uint.Parse(Get("flags"), ci));
        s[28] = byte.Parse(Get("class"), ci);
        int nColl = int.Parse(Get("nColl"), ci);
        s[29] = (byte)nColl;
        int o = TelemetryFrame.HeaderSize;
        foreach (var name in Enum.GetNames<F>()) { BinaryPrimitives.WriteSingleLittleEndian(s[o..], Fl(name)); o += 4; }
        foreach (var name in Enum.GetNames<B>()) { s[o++] = byte.Parse(Get(name), ci); }
        for (int i = 0; i < Math.Min(nColl, TelemetryFrame.MaxCollisions); i++)
        {
            int p = o + i * TelemetryFrame.CollisionSize;
            string[] fl = ["DirX", "DirY", "DirZ", "LocX", "LocY", "LocZ", "Pvd"];
            for (int k = 0; k < fl.Length; k++) BinaryPrimitives.WriteSingleLittleEndian(s[(p + k * 4)..], Fl($"c{i}{fl[k]}"));
            BinaryPrimitives.WriteInt32LittleEndian(s[(p + 28)..], int.Parse(Get($"c{i}Id"), ci));
            s[p + 32] = byte.Parse(Get($"c{i}Veh"), ci);
        }
        return b;
    }
}
