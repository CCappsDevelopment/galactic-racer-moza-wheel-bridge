using System.Globalization;
using System.Text;

namespace WheelBridge;

/// <summary>Writes every frame to a tab-separated file for offline FFB tuning.</summary>
public sealed class Recorder : IDisposable
{
    private readonly StreamWriter _w;
    public string Path { get; }
    public long Rows { get; private set; }

    public Recorder(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"telemetry_{DateTime.Now:yyyyMMdd_HHmmss}.tsv");
        _w = new StreamWriter(Path, false, new UTF8Encoding(false));
        var cols = new List<string> { "seq", "simTime", "dt", "flags", "class", "nColl" };
        cols.AddRange(Enum.GetNames<F>());
        cols.AddRange(Enum.GetNames<B>());
        for (int i = 0; i < TelemetryFrame.MaxCollisions; i++)
            cols.AddRange([$"c{i}DirX", $"c{i}DirY", $"c{i}DirZ", $"c{i}LocX", $"c{i}LocY", $"c{i}LocZ", $"c{i}Pvd", $"c{i}Id", $"c{i}Veh"]);
        _w.WriteLine(string.Join('\t', cols));
    }

    public void Write(TelemetryFrame f)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(1024);
        sb.Append(f.Seq).Append('\t').Append(f.SimTime.ToString("F4", ci)).Append('\t')
          .Append(f.Dt.ToString("F5", ci)).Append('\t').Append((uint)f.Flags).Append('\t')
          .Append((int)f.VehicleClass).Append('\t').Append(f.CollisionCount);
        foreach (var v in f.Floats) sb.Append('\t').Append(v.ToString("G6", ci));
        foreach (var v in f.Bytes) sb.Append('\t').Append(v);
        for (int i = 0; i < TelemetryFrame.MaxCollisions; i++)
        {
            if (i < f.Collisions.Length)
            {
                var c = f.Collisions[i];
                foreach (var v in new[] { c.WorldDirection.X, c.WorldDirection.Y, c.WorldDirection.Z,
                                          c.LocalLocation.X, c.LocalLocation.Y, c.LocalLocation.Z, c.PointVelocityDelta })
                    sb.Append('\t').Append(v.ToString("G6", ci));
                sb.Append('\t').Append(c.Id).Append('\t').Append(c.OtherIsVehicle ? 1 : 0);
            }
            else sb.Append('\t', 9);
        }
        _w.WriteLine(sb);
        Rows++;
    }

    public void Dispose() => _w.Dispose();
}
