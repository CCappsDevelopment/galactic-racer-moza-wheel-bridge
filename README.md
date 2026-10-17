# Galactic Racer MOZA Wheel Bridge

Play **STAR WARS: Galactic Racer** with a MOZA direct-drive wheel, with force feedback.

The game has no wheel support and no force feedback (only controller rumble). The bridge adds both:

- **Input:** your wheel, pedals and wheel buttons drive the game through a virtual Xbox controller.
- **Force feedback:** a small UE4SS mod sends live vehicle data (speed, slip, cornering load, collisions, landings,
  boost and Ramjet state) to the bridge, which turns it into force feedback on the wheelbase.
- **Profiles** for the land speeder, speeder bike, skim speeder and podracer, loaded automatically when you get in.
- **First-run setup** finds your motor direction, calibrates the pedals and binds every control.
- A tuning window with live meters for each force layer, and hover help for every setting.

> **Status: beta (v0.1).** Tested on a MOZA R3 with all four vehicle types. Other MOZA bases (R5 to R21) get an
> automatic, conservative strength limit but are untested.

## Download and install

Get the zip from the [Releases](../../releases) page. The full instructions are in the `README.md` inside the zip
([`WheelBridge/release/README.md`](WheelBridge/release/README.md) in this repo). In short:

1. Install [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) and
   [UE4SS Galactic Racer Edition](https://www.nexusmods.com/starwarsgalacticracer/mods/2).
2. Copy `UE4SS Mod\WheelBridgeExporter` into the game's `ue4ss\Mods` folder and add `WheelBridgeExporter : 1` to `mods.txt`.
3. Start MOZA Pit House, then `WheelBridge.exe`. It downloads the MOZA SDK from MOZA on first start and walks you
   through setup.

> **Safety:** a direct-drive base can turn the wheel quickly and with large force. Keep your hands clear during the
> motor test and Force center, and learn where **KILL FFB** (Alt+K) is before you drive.

## How it works

```
Galactic Racer + UE4SS ──(Lua exporter, named pipe, ~60 Hz)──▶ WheelBridge.exe ──(DirectInput constant force, 500 Hz)──▶ wheelbase
                       ◀──────────────(virtual Xbox pad, ViGEmBus)──────────────    ◀──(MOZA SDK via Pit House)── wheel, pedals, buttons
```

- `Mods/WheelBridgeExporter`: UE4SS Lua mod. Reads the player vehicle's reflected state once per frame and writes a
  fixed binary frame to a named pipe. Read-only: it changes nothing in the game.
- `WheelBridge`: the bridge (C#, .NET 10, WinForms). The force model (`FfbModel.cs`) is deterministic: the same
  telemetry and wheel motion always give the same torque, so recorded laps can be replayed offline.
- `WheelBridge.Tests`: offline regression suite (no wheel, game or driver needed), plus `exporter_test.py`, which
  runs the Lua mod in a real Lua 5.4 VM with UE4SS stubbed.
- `Dumps/recordings`: recorded laps of each vehicle, used by the tests and by `WheelBridge.exe --replay`.

## Building from source

Requirements: Windows 10/11 x64, the [.NET 10 SDK](https://dotnet.microsoft.com/download), and Python 3 with
[lupa](https://pypi.org/project/lupa/) for the exporter test.

```powershell
# The MOZA SDK can't be redistributed: this downloads MOZA's official zip into Tools\ (needed to compile).
powershell -ExecutionPolicy Bypass -File WheelBridge\tools\get-moza-sdk.ps1

dotnet build WheelBridge -c Release
dotnet run -c Release --project WheelBridge.Tests      # C# regression tests
python WheelBridge.Tests\exporter_test.py              # Lua exporter tests

# Release zip in dist\ (runs both test suites first; never includes the MOZA SDK or your settings)
powershell -ExecutionPolicy Bypass -File WheelBridge\tools\package.ps1
```

Useful while developing:

- `WheelBridge.exe --console`: text dashboard instead of the window.
- `WheelBridge.exe --replay <recording.tsv> [profile.json]`: replays a recording through the force model and
  prints per-layer statistics.
- `WheelBridge\tools\simulate-first-run.ps1`: starts a published build with an empty settings folder, as a new
  user would see it.
- `WheelBridge\tools\fake_exporter.py`: sends synthetic telemetry frames without the game.

## Licence

MIT (see [LICENSE](LICENSE)). Third-party components are listed in
[`WheelBridge/release/THIRD-PARTY-NOTICES.txt`](WheelBridge/release/THIRD-PARTY-NOTICES.txt). The MOZA SDK is
MOZA Racing's property and is not part of this project; users download it from MOZA.

Not affiliated with or endorsed by Lucasfilm, the game's developer or publisher, or MOZA Racing.
