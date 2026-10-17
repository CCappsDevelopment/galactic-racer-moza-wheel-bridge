using System.Text.RegularExpressions;

namespace WheelBridge;

/// <summary>
/// Which MOZA wheelbase is connected and how much to scale the forces for it.
/// The layers were tuned on a 3.9 Nm R3, the only base tested. On a stronger base the same fraction of full torque
/// is far more force, so the output is scaled down to roughly R3-equivalent newton-metres, with an extra safety
/// margin because none of the other bases have been tried.
/// </summary>
public sealed record Wheelbase(string ProductName, string Model, float PeakNm, bool Known, bool Tested)
{
    public const int MozaVendorId = 0x346E;
    public const float ReferenceNm = 3.9f;       // the R3 the profiles were tuned on
    public const float UntestedMargin = 0.85f;   // extra cut for every base other than the R3
    public const float UnknownNm = 21f;          // unrecognised base: assume the strongest MOZA makes

    // Rated peak torque. A model missing here but named "R<n>" is taken as n Nm (MOZA names follow their torque).
    private static readonly Dictionary<int, float> PeakTorque = new() { [3] = 3.9f, [5] = 5.5f, [9] = 9f, [12] = 12f, [16] = 16f, [21] = 21f };

    /// <summary>Default output scale: R3 = 1, stronger bases ≈ R3-equivalent torque × the safety margin.</summary>
    public float AutoScale => Tested ? 1f : Math.Clamp(ReferenceNm / PeakNm * UntestedMargin, 0.05f, 1f);

    public static Wheelbase Unknown(string productName = "") => new(productName, "unknown", UnknownNm, false, false);

    /// <summary>From the DirectInput product name, e.g. "R3 Racing Wheel and Pedals" (VID 346E, PID 0005 on the R3).</summary>
    public static Wheelbase FromProductName(string productName)
    {
        var m = Regex.Match(productName ?? "", @"(?<![A-Za-z0-9])R\s?(\d{1,2})(?![0-9])", RegexOptions.IgnoreCase);
        if (!m.Success) return Unknown(productName ?? "");
        int n = int.Parse(m.Groups[1].Value);
        bool listed = PeakTorque.TryGetValue(n, out float nm);
        if (!listed) nm = Math.Max(n, ReferenceNm);
        return new Wheelbase(productName!, $"R{n}", nm, listed, n == 3);
    }

    public string Describe(float scale) =>
        $"{(Known ? $"MOZA {Model}" : ProductName.Length > 0 ? $"\"{ProductName}\"" : "unknown base")}, " +
        $"{(Known ? $"{PeakNm:0.#} Nm" : $"assumed {PeakNm:0.#} Nm")}, strength ×{scale:0.00}{(Tested ? "" : " (untested base)")}";
}
