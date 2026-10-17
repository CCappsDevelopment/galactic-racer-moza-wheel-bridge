using System.Text;

namespace WheelBridge.Gui;

/// <summary>
/// Hover help for every setting: what it does and when to change it. Plain text, wrapped for the tooltip.
/// Layer entries are keyed by FfbLayers names.
/// </summary>
public static class Tips
{
    public static readonly Dictionary<string, string> Layer = new()
    {
        [FfbLayers.Centering] = """
            Centering: a spring that pulls the wheel back to center. Gets stronger with speed, weaker while airborne or drifting.
            This is the main "weight" of the wheel.
            Raise it: the wheel feels loose or vague on straights at speed.
            Lower it: the wheel fights you, or turning in feels heavy and tiring.
            """,
        [FfbLayers.Aligning] = """
            Aligning: pulls the wheel toward the direction the vehicle is actually moving, based on slip angle (how sideways you are).
            When you slide, it pulls toward countersteer; past the peak slip it fades, so the wheel goes light. That lightness tells you grip is gone.
            Raise it: slides and drifts are hard to feel or catch.
            Lower it: the wheel snaps hard into countersteer when the rear steps out.
            """,
        [FfbLayers.Load] = """
            Load: resists steering in proportion to cornering force (sideways acceleration). Makes the wheel heavier through fast corners.
            Raise it: long sweeping corners feel the same as straights.
            Lower it: holding a corner is tiring, or the wheel pushes back too much mid-turn.
            Tip: the Load reference slider sets how hard a corner must be for full load.
            """,
        [FfbLayers.Damping] = """
            Damping: resists fast wheel movement, like turning through thick oil. Stronger at speed. Works even without game data, so the wheel never oscillates on its own.
            Raise it: the wheel wobbles or oscillates after a quick move, or springs back too fast.
            Lower it: quick corrections feel slow or syrupy.
            """,
        [FfbLayers.Impacts] = """
            Impacts: a short kick toward the side that was hit, when the vehicle hits a wall, object or another racer. Front hits kick harder than rear hits.
            Raise it: collisions are hard to notice.
            Lower it: hits jerk the wheel out of your hands.
            Tip: Impact reference and Impact decay (other tab) change how small hits scale and how long kicks last.
            """,
        [FfbLayers.Scrape] = """
            Scrape: a 38 Hz buzz while the vehicle stays in contact with something (sliding along a wall). Stronger for harder contact.
            Raise it: you don't notice when you're grinding a wall.
            Lower it: the buzz is annoying or masks other effects.
            """,
        [FfbLayers.Road] = """
            Road: a light texture that rises with speed, so the ground doesn't feel perfectly smooth. Synthetic: it isn't read from the real track surface.
            Raise it: high speed feels lifeless.
            Lower it: you feel a constant buzz or hum while driving. This is the first one to lower for "ambient rumble".
            """,
        [FfbLayers.Engine] = """
            Engine: a fine vibration whose pitch follows engine RPM, only while on the throttle.
            Raise it: you want to feel the engine or the speeder's drive.
            Lower it: there's a constant hum under throttle. A good one to lower for "ambient rumble".
            """,
        [FfbLayers.Boost] = """
            Boost: vibration for afterburner and Ramjet. The buzz rises with Ramjet heat and pulses when it's critical or overheated.
            Use it as a heat warning you can feel.
            Raise it: you keep overheating or blowing up the Ramjet without noticing.
            Lower it: boosting feels like constant noise.
            """,
        [FfbLayers.Landing] = """
            Landing: a short thump when the game grades a landing. Bad landings thump hardest, perfect landings softest.
            Raise it: landings are hard to feel.
            Lower it: the thump is jarring.
            """,
        [FfbLayers.Rumble] = """
            Rumble: replays the game's own controller vibration on the wheel (big motor low and heavy, small motor high and light).
            Picks up effects we don't model ourselves, but it is generic pad rumble, not directional.
            Raise it: you want everything the game would send to a pad.
            Lower or turn off: it adds a constant rumble that hides the other effects.
            """,
        [FfbLayers.Lean] = """
            Lean: light weight from how far the vehicle leans. Holding a lean pushes the wheel back toward upright.
            Made for the speeder bike, which corners with little sideways force, so Load alone feels light. Off (0) in the other profiles.
            Keep it low (around 0.1). The lean follows your steering about 0.2 s late, so a strong Lean force keeps pushing after you start to straighten up and the wheel runs away from you.
            Raise it a little: the bike feels light and vague in corners.
            Lower it: the wheel pulls or slips away while leaning, or holding the bike in a lean is tiring.
            """,
    };

    public const string LayerBar = """
        Live output of this layer right now (after its gain). Blue = pushing right, orange = pushing left, red = at the meter's limit.
        Use it to find which layer makes a feeling you like or dislike.
        """;

    public const string Master = """
        Master: scales all force feedback together. 1.00 = the strength the profiles were tuned for (on a 3.9 Nm R3).
        Lower it to make everything weaker without changing the balance between layers. Profiles never change Master.
        On a stronger base the bridge already reduces the force automatically (see Base strength).
        The bar on the right is the total force being sent to the wheel (blue = right, orange = left, red = clipping at 100%).
        If it is often red, the layers add up to more than the motor can give: lower the strongest layers or the master.
        """;

    public const string FfbEnabled = """
        Turns force feedback on or off and saves that choice.
        For a quick temporary stop, use KILL FFB instead.
        """;

    public const string AligningPeak = """
        Aligning peak slip (degrees): the slip angle where the aligning pull is strongest. Past it, the pull fades and the wheel goes light.
        Lower it: the wheel goes light sooner. Good for vehicles that rarely slide much (the speeder bike mostly stays under 10°).
        Raise it: the pull keeps building in big slides before letting go.
        """;

    public const string LoadReference = """
        Load reference (g): how hard a corner must be to give full Load force. Calculated from speed × turn rate.
        Lower it: load builds in gentler corners. Good for vehicles that corner with less sideways force (the speeder bike reaches about half of what the car does).
        Raise it: only the hardest corners feel heavy.
        """;

    public const string ImpactReference = """
        Impact reference: how hard a hit must be (the game's impact speed) to give a full-strength kick.
        Lower it: small taps and scrapes kick harder.
        Raise it: only big crashes kick hard. Typical hits measured 100–2200, the biggest 3000–5700.
        """;

    public const string ImpactDecay = """
        Impact decay (ms): how long an impact kick lasts before fading out.
        Lower it: short, sharp knocks.
        Raise it: longer, heavier shoves.
        """;

    public const string AirCentering = """
        Air centering: how much of the centering spring stays while airborne (0 = none, 1 = all).
        Lower it: the wheel goes loose in the air, so you can feel the jump.
        Raise it: the wheel stays steady in the air, and landings feel less sudden.
        """;

    public const string DriftLightening = """
        Drift lightening: how much centering and load drop while the game says you are drifting (0 = no change, 1 = fully weightless).
        Raise it: drifts feel looser and easier to hold.
        Lower it: the wheel keeps its weight through drifts.
        Note: the speeder bike never sets the drift flag, so this does nothing on the bike.
        """;

    public const string WreckCentering = """
        Wreck centering: a gentle centering spring while the vehicle is wrecked and respawning, when all other effects stop.
        Raise it: the wheel drifts off-center during respawns.
        Lower it: you'd rather the wheel go limp after a crash.
        """;

    public const string StaleMs = """
        Stale telemetry (ms): if no game data arrives for this long, forces fade out. Protects against stuck forces when the game freezes or closes.
        Raise it: forces cut out briefly during game stutters (hitches reach ~185 ms).
        Lower it: forces linger too long after the game freezes or you alt-tab.
        """;

    public const string CenterLockForce = """
        Force center strength: how hard the Force center button holds the wheel. 1 = the wheelbase's full torque (not scaled by Master).
        """;

    public const string CenterLockFullAt = """
        Force center softness (degrees): how far you can turn the pinned wheel before it pushes back at about three-quarters strength (nearly full at twice this). Lower is stiffer.
        Raise it if the wheel buzzes or shakes while pinned. Below ~12° the hold can start to vibrate, because every correction reaches the motor a few milliseconds late.
        """;

    public const string CenterLockDamping = """
        Force center damping (°/s): wheel speed that gets full braking force while pinned. This is a speed, so a HIGHER number means LESS damping.
        Raise it if the pinned wheel buzzes. Lower it if the wheel swings past center and wobbles before settling.
        """;

    public const string MutePaused = """
        Mute while paused: the game keeps sending data in the pause menu, but frozen. Without this, the wheel keeps playing road and engine effects as if you were still driving.
        Leave on unless you want force in the pause menu.
        """;

    public const string PausePadOnFoot = """
        Pause wheel on foot: while Shade is walking the hub (talking to characters, shops), the wheel sends nothing to the game, so your controller or keyboard drives her. The wheel takes over again as soon as you are back in a vehicle or a menu.
        Turn off if you want to walk Shade around with the wheel.
        """;

    public const string MuteNotRacing = """
        Mute when not racing: fades all forces while the game isn't letting you drive (menus, intro fly-bys, the run-out after the finish line, results screens). This also blocks menu rumble from the game.
        Leave on unless you want force outside of races.
        """;

    public const string SteeringLock = """
        Steering lock (degrees, lock to lock): how far you turn the wheel for full steering. 180 = 90° each way.
        Lower it: quicker, twitchier steering with less hand movement.
        Raise it: slower, more precise steering.
        Turning past this does nothing in game. Set the R3's own max angle in Pit House to the same value for a real hard stop.
        """;

    public const string AntiDeadzone = """
        Anti-deadzone: the game ignores small stick movements (about the first 33%). This adds that amount the moment the wheel leaves center, so the vehicle responds to small turns.
        Raise it: the first few degrees of wheel do nothing.
        Lower it: the vehicle darts or twitches as soon as you touch the wheel. The graph on the right shows the jump at center.
        """;

    public const string Linearity = """
        Linearity: the response curve. 1 = linear (wheel angle maps straight to steering).
        Above 1: softer around center, sharper near lock. Good for stability at high speed.
        Below 1: sharper around center, softer near lock.
        """;

    public const string PedalDeadzone = """
        Pedal deadzone: the share of pedal travel ignored at the top and bottom, so a resting foot doesn't apply throttle and a full press reaches 100%.
        Raise it: the vehicle creeps or brakes with your feet resting, or never reaches full throttle.
        Lower it: the pedals feel dead at the start of their travel.
        """;

    public const string Invert = "Invert steering: flips left and right. Only needed if the vehicle steers the wrong way.";

    public const string PadOutput = """
        Send wheel to game: when off, the virtual Xbox pad stays connected but sends nothing (neutral stick, no buttons).
        Useful while changing settings or using a real controller.
        """;

    public const string Launch = "Starts STAR WARS: Galactic Racer through Steam, so DRM, the Steam overlay and the telemetry mod all load as normal.";
    public const string Kill = "KILL FFB (Alt+K): instantly zeroes all force. Click again to resume. Not saved: the next start has FFB on.";
    public const string ForceCenter = """
        Force center (Alt+C): turns the wheel back to 0° and holds it there with full force, so it fights you if you try to turn it. Handy when you put the wheel down or between sessions.
        While it is on, the steering sent to the game is held at exactly 0, so the vehicle doesn't drift.
        Click again to release. It also releases by itself the moment a race starts, or if the wheel keeps turning away from center (then run the Motor test). KILL FFB overrides it.
        The first time, it runs the Motor test so it knows which way your motor turns.
        """;

    public const string MotorTest = """
        Motor test: pushes the wheel gently for 0.3 s to learn which way the motor turns it, and saves the direction (ForceSign).
        No force feedback is sent until it has run once: with the wrong direction every force would push away from center.
        Needed once per wheelbase, or again if every force feels backwards. Take your hands off the wheel first.
        """;
    public const string Record = """
        Record: saves the game data to the recordings folder as a spreadsheet-style file. Click again to stop.
        Record a lap when something feels wrong, so it can be analysed and replayed offline.
        """;
    public const string Calibrate = """
        Calibrate pedals: release all pedals, click, then press each pedal fully once within 8 seconds.
        Do it if a pedal doesn't reach 100% or isn't at 0% when released.
        """;
    public const string Profiles = """
        Profiles: named sets of steering and FFB settings, for example one per vehicle type.
        Load applies one, Save overwrites the current one, Save as makes a new one.
        Pedal calibration, the button map, the motor direction and the Master strength stay with the wheel and are never changed by a profile.
        With "Auto profile" on, the profile set for each vehicle type loads by itself when you get into that vehicle.
        "modified" means your live settings differ from the saved profile.
        """;
    public const string GetSdk = "The MOZA SDK is not installed. The bridge needs it to read the wheel. Click to download it from MOZA or to install it from a MOZA_SDK.zip you downloaded.";
    public const string BaseScale = """
        Base strength: how much of your wheelbase's full torque Master 1.00 uses. 0 = automatic.
        Automatic is 1.00 on the R3 (the only tested base). On other bases it gives about the R3's force (3.9 Nm) with a 15% safety margin: R5 ×0.60, R9 ×0.37, R12 ×0.28, R16 ×0.21, R21 ×0.16, unknown base ×0.16.
        Set a value only if the automatic strength is too weak. Raise it in small steps, with your hands clear of the wheel on the first try.
        """;
    public const string LeanReference = """
        Lean reference: how far the vehicle must lean to give about three-quarters of the Lean force. The speeder bike leans up to about 1.5 at full lean.
        Lower it: the weight builds early in the lean.
        Raise it: only deep leans feel heavy.
        """;
    public const string BindHint = """
        Click Bind…, then press a wheel button (or, for a button, push a pedal past half way) within 6 seconds. You can bind several inputs to one control.
        RT and LT use the pedal you choose plus any wheel buttons you bind (a held button = full press), so a wheel without pedals can use paddles to accelerate and brake.
        Changes work at once and save automatically. They stay with your wheel and are not part of a profile.
        """;
    public const string TriggerPedal = "The pedal that drives this trigger. Choose None if you have no pedals or want this trigger on buttons only.";
    public const string BindTrigger = "Bind a wheel button that presses this trigger fully while held (for example a shifter paddle).";
    public const string BindButton = "Bind a wheel button or a pedal to this control. Press the input within 6 seconds after you click.";
    public const string ClearBinding = "Removes the wheel buttons (and pedals, for a button) bound to this control.";

    public const string AutoProfile = """
        Auto profile: loads the matching profile by itself when you get into a land speeder, speeder bike, skim speeder or podracer
        (Land Speeder, Speeder Bike, Skim Speeder, Podracer; change the names under VehicleProfiles in wheelbridge.json).
        It never throws away tuning you haven't saved: if the current profile is "modified", it stays loaded and the status bar says so.
        """;
    public const string ResetFfb = "Resets every FFB gain, shape and mute setting to the built-in defaults. Master, steering, pedals and buttons are not touched.";
    public const string RunSetup = """
        Runs the guided setup again: the motor test, pedal calibration (or no pedals), then every control one at a time.
        It opens by itself the first time the bridge sees your wheel.
        """;
    public const string AlwaysOnTop = "Keeps this window above the game. Useful for tuning live on one screen.";

    /// <summary>Joins the indented paragraphs and wraps them to a readable tooltip width.</summary>
    public static string Format(string text, int width = 78)
    {
        var sb = new StringBuilder();
        foreach (var para in text.Trim().Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in para.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + word.Length + 1 > width) { sb.AppendLine(line.ToString()); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            sb.AppendLine(line.ToString());
        }
        return sb.ToString().TrimEnd();
    }
}
