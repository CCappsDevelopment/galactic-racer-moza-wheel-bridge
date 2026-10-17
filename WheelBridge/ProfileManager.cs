namespace WheelBridge;

/// <summary>
/// Named feel profiles in a folder (one JSON each), the "modified" state of the active one, and switching profile
/// automatically when the player gets into another vehicle type.
/// </summary>
public sealed class ProfileManager
{
    private readonly BridgeConfig _cfg;
    private readonly Action _saveConfig;
    private string? _fingerprint;   // feel fingerprint of the active profile as saved on disk
    private VehicleClass _lastVehicle = VehicleClass.Unknown;
    private VehicleClass? _refusedFor;

    public string Directory { get; }

    public ProfileManager(BridgeConfig cfg, string directory, Action saveConfig)
    {
        _cfg = cfg;
        Directory = directory;
        _saveConfig = saveConfig;
        if (cfg.ActiveProfile is { } active && File.Exists(PathOf(active)))
        {
            try { _fingerprint = BridgeConfig.ReadFile(PathOf(active)).FeelFingerprint(); } catch { }
        }
    }

    public string PathOf(string name) => Path.Combine(Directory, name + ".json");

    public string[] Names()
    {
        System.IO.Directory.CreateDirectory(Directory);
        return System.IO.Directory.GetFiles(Directory, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order().ToArray();
    }

    /// <summary>The live settings differ from the active profile as saved.</summary>
    public bool Modified => _cfg.ActiveProfile is not null && _fingerprint != _cfg.FeelFingerprint();

    /// <summary>Applies a saved profile's feel settings; returns a status message.</summary>
    public string Load(string name)
    {
        try
        {
            var p = BridgeConfig.ReadFile(PathOf(name));
            _cfg.ApplyProfile(p);
            _cfg.ActiveProfile = name;
            _fingerprint = _cfg.FeelFingerprint();
            _saveConfig();
            return $"Loaded profile \"{name}\"";
        }
        catch (Exception ex) { return $"Couldn't load \"{name}\": {ex.Message}"; }
    }

    /// <summary>Saves the live feel settings as a profile; returns a status message.</summary>
    public string Save(string name)
    {
        name = name.Trim();
        if (name.Trim('.').Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "A profile name can't be empty or contain \\ / : * ? \" < > |";
        System.IO.Directory.CreateDirectory(Directory);
        _cfg.ActiveProfile = name;
        _cfg.Save(PathOf(name));
        File.Delete(PathOf(name) + ".deleted");
        _fingerprint = _cfg.FeelFingerprint();
        _saveConfig();
        return $"Saved profile \"{name}\"";
    }

    public string Delete(string name)
    {
        File.Delete(PathOf(name));
        // Remember the deletion so a shipped default of the same name isn't copied back on the next start.
        try { File.WriteAllText(PathOf(name) + ".deleted", ""); } catch { }
        if (_cfg.ActiveProfile == name) { _cfg.ActiveProfile = null; _fingerprint = null; }
        _saveConfig();
        return $"Deleted profile \"{name}\"";
    }

    /// <summary>
    /// Call regularly on the UI/console thread with the current vehicle. When the player gets into another vehicle
    /// type, loads the profile mapped to it. Never discards unsaved tuning: a modified profile stays loaded and the
    /// message says why. Returns a message when something happened.
    /// </summary>
    public string? OnVehicle(VehicleClass? vehicle)
    {
        if (vehicle is not { } v || v == VehicleClass.Unknown || v == _lastVehicle) return null;
        // While off, nothing is marked handled, so turning it on switches right away.
        if (!_cfg.AutoSwitchProfiles) return null;
        if (!_cfg.VehicleProfiles.TryGetValue(v.ToString(), out var name) || string.IsNullOrWhiteSpace(name) || name == _cfg.ActiveProfile)
        {
            _lastVehicle = v;
            return null;
        }
        if (!File.Exists(PathOf(name)))
        {
            _lastVehicle = v;
            return $"{VehicleName(v)}: no profile \"{name}\", keeping the current settings";
        }
        if (Modified)
        {
            // Not marked handled: once the tuning is saved (or another profile loaded) the switch happens. Say so once.
            if (_refusedFor == v) return null;
            _refusedFor = v;
            return $"{VehicleName(v)}: not switching to \"{name}\": \"{_cfg.ActiveProfile}\" has unsaved changes (Save it, or Load a profile)";
        }
        _lastVehicle = v;
        _refusedFor = null;
        return Load(name) + $" for the {VehicleName(v)}";
    }

    public static string VehicleName(VehicleClass v) => v switch
    {
        VehicleClass.Car => "land speeder",
        VehicleClass.Bike => "speeder bike",
        VehicleClass.Skimmer => "skim speeder",
        VehicleClass.Pod => "podracer",
        _ => "vehicle",
    };

    /// <summary>
    /// Copies each shipped default profile into the folder unless a profile of that name already exists or the user
    /// deleted it (a .deleted marker), so updates can add new defaults without ever overwriting someone's tuning.
    /// </summary>
    public static void SeedDefaults(string sourceDir, string profileDir)
    {
        if (!System.IO.Directory.Exists(sourceDir)) return;
        try
        {
            System.IO.Directory.CreateDirectory(profileDir);
            foreach (var file in System.IO.Directory.GetFiles(sourceDir, "*.json"))
            {
                string dst = Path.Combine(profileDir, Path.GetFileName(file));
                if (!File.Exists(dst) && !File.Exists(dst + ".deleted")) File.Copy(file, dst, overwrite: false);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
