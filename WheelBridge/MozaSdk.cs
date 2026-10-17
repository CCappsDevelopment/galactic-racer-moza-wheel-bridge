using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace WheelBridge;

/// <summary>
/// The MOZA SDK (MOZA_API_CSharp.dll, MOZA_API_C.dll, MOZA_SDK.dll) is MOZA's proprietary software with no
/// redistribution licence, so it is not shipped with the bridge. Each user downloads it from MOZA, from inside the
/// app or by hand, and this class finds and loads it at run time. Nothing touches a mozaAPI type before
/// <see cref="Available"/> is true.
/// </summary>
public static class MozaSdk
{
    /// <summary>The official download linked from https://mozaracing.com/pages/sdk ("Racing SDK").</summary>
    public const string DownloadUrl = "https://cdn.gudsen.vip/simulation_game/rs21repository/installer/MOZA_SDK.zip";
    public const string SdkPage = "https://mozaracing.com/pages/sdk";
    public const string ZipFolder = "MOZA_SDK/SDK_CSharp/x64/";
    public static readonly string[] Files = ["MOZA_API_CSharp.dll", "MOZA_API_C.dll", "MOZA_SDK.dll"];

    /// <summary>
    /// SHA-256 of the 64-bit files in MOZA SDK 1.0.1.8, the version the bridge was built and tested against.
    /// A download that doesn't match (a newer SDK, or a tampered file) is only used after the user agrees.
    /// </summary>
    public static readonly Dictionary<string, string> KnownSha256 = new()
    {
        ["MOZA_API_CSharp.dll"] = "F48DA18AA2C261144DE619A2E9D3931584812E336FB0301B59477B96269DE775",
        ["MOZA_API_C.dll"] = "80E75D2B0B664DA1557A2FB8B5AA0DD9BA3CFB0C50CEE46CCD925364A38EF2AE",
        ["MOZA_SDK.dll"] = "5C4B9253F8A291B02C5C08522C73D16FF51EF9BF2C51294AA5CDBCEEB1EED2C6",
    };

    /// <summary>Where a downloaded SDK goes: a "MOZA SDK" folder in the data folder (next to the exe for a portable install).</summary>
    public static string DownloadDir => Path.Combine(BridgeHost.DataDir, "MOZA SDK");

    private static string? _dir;
    private static bool _resolverInstalled;

    /// <summary>The folder holding all three files (next to the exe, or the download folder), or null.</summary>
    public static string? Locate()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, DownloadDir })
            if (Files.All(f => File.Exists(Path.Combine(dir, f)))) return dir;
        return null;
    }

    /// <summary>True once the SDK is found and set up for loading. Cheap to call repeatedly.</summary>
    public static bool Available
    {
        get
        {
            if (_dir is not null) return true;
            if (Locate() is not { } dir) return false;
            Prepare(dir);
            return true;
        }
    }

    private static void Prepare(string dir)
    {
        _dir = dir;
        // MOZA_API_C.dll loads MOZA_SDK.dll by name; let Windows find it in the SDK folder too.
        SetDllDirectory(dir);
        if (_resolverInstalled) return;
        _resolverInstalled = true;
        // The managed wrapper isn't in the published exe: load it from the SDK folder when first needed.
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            name.Name == "MOZA_API_CSharp" && _dir is { } d ? ctx.LoadFromAssemblyPath(Path.Combine(d, "MOZA_API_CSharp.dll")) : null;
    }

    public sealed record InstallResult(bool Ok, string Message, bool Unverified = false);

    /// <summary>
    /// Downloads MOZA's SDK zip (≈57 MB) and extracts only the three 64-bit files into <see cref="DownloadDir"/>.
    /// Files that don't match <see cref="KnownSha256"/> are installed only if <paramref name="confirmUnverified"/> returns true.
    /// </summary>
    public static async Task<InstallResult> DownloadAsync(Func<string, bool>? confirmUnverified, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"MOZA_SDK_{Guid.NewGuid():N}.zip");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SWGR-Wheel-Bridge/" + typeof(MozaSdk).Assembly.GetName().Version?.ToString(3));
                using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                var buf = new byte[1 << 16];
                long done = 0, lastReport = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (done - lastReport > 1 << 20)
                    {
                        lastReport = done;
                        progress?.Report(total is > 0 ? $"Downloading the MOZA SDK… {done * 100 / total.Value}%" : $"Downloading the MOZA SDK… {done >> 20} MB");
                    }
                }
            }
            progress?.Report("Checking the MOZA SDK files…");
            return InstallFromZip(tmp, confirmUnverified);
        }
        catch (OperationCanceledException) { return new(false, "Download cancelled"); }
        catch (Exception ex) { return new(false, $"Download failed: {ex.Message}. You can download MOZA_SDK.zip from {SdkPage} and use \"Install from zip…\"."); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>Installs the three 64-bit files from a MOZA_SDK.zip the user downloaded (same checks as the download).</summary>
    public static InstallResult InstallFromZip(string zipPath, Func<string, bool>? confirmUnverified)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entries = new Dictionary<string, ZipArchiveEntry>();
            foreach (var f in Files)
            {
                // Exact path match only: never extract anything else, and no path from the archive is used on disk.
                var e = zip.Entries.FirstOrDefault(x => x.FullName.Replace('\\', '/').EndsWith(ZipFolder + f, StringComparison.OrdinalIgnoreCase));
                if (e is null) return new(false, $"{Path.GetFileName(zipPath)} is not the MOZA SDK: {ZipFolder}{f} is missing");
                if (e.Length > 64 << 20) return new(false, $"{f} in the zip is unexpectedly large, not installed");
                entries[f] = e;
            }
            var mismatched = new List<string>();
            var data = new Dictionary<string, byte[]>();
            foreach (var (f, e) in entries)
            {
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                data[f] = ms.ToArray();
                string sha = Convert.ToHexString(SHA256.HashData(data[f]));
                if (!string.Equals(sha, KnownSha256[f], StringComparison.OrdinalIgnoreCase)) mismatched.Add(f);
            }
            if (mismatched.Count > 0)
            {
                string what = $"These files differ from MOZA SDK 1.0.1.8, the version the bridge was tested with: {string.Join(", ", mismatched)}.";
                if (confirmUnverified?.Invoke(what) != true) return new(false, what + " Not installed.", Unverified: true);
            }

            Directory.CreateDirectory(DownloadDir);
            foreach (var (f, bytes) in data)
            {
                string target = Path.Combine(DownloadDir, f);
                File.WriteAllBytes(target + ".tmp", bytes);
                File.Move(target + ".tmp", target, overwrite: true);
            }
            _dir = null;
            bool ok = Available;
            return new(ok, ok ? $"MOZA SDK installed in {DownloadDir}" + (mismatched.Count > 0 ? " (a version other than the tested 1.0.1.8)" : "")
                              : "MOZA SDK files written but not found. Restart the bridge");
        }
        catch (InvalidDataException) { return new(false, $"{Path.GetFileName(zipPath)} is not a valid zip file"); }
        catch (Exception ex) { return new(false, "Couldn't install the MOZA SDK: " + ex.Message); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);
}
