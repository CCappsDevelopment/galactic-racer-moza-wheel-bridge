# SWGR Wheel Bridge 0.1 (beta)

> **NOTE:** This is a beta release. Report problems on the GitHub issues page of the project.

## 1. Description

The SWGR Wheel Bridge lets you use a MOZA wheelbase in STAR WARS: Galactic Racer.

- The bridge sends the steering, pedals and buttons to the game as a virtual Xbox controller.
- The bridge makes force feedback (FFB) from game data. The game does not have FFB.
- The bridge has one profile for each vehicle type: land speeder, speeder bike, skim speeder and podracer.
- The bridge loads the correct profile when you enter a vehicle.

The bridge is portable. You do not install it. You extract the zip file and start `WheelBridge.exe`.

## 2. Safety

> **WARNING:** A direct-drive wheelbase can turn the wheel quickly and with large force. This can cause injury
> to hands, wrists and thumbs. Obey these rules:
>
> - Keep your hands, other persons, pets and objects away from the wheel when you start the bridge, when you
>   do the motor test and when you use **Force center**.
> - Learn the position of **KILL FFB** (Alt+K) before you drive. It stops all forces immediately.
> - Do not hold the wheel with your thumbs inside the rim.

> **WARNING:** The bridge was tested only with a MOZA R3 (3.9 Nm). It was NOT tested with the R5, R9, R12, R16,
> R21 or other wheelbases. On these wheelbases the bridge decreases the force automatically (see section 6.1).
> The forces can still be stronger or feel different from the R3. You use the bridge on these wheelbases at your
> own risk. Start with a low **Master** value and increase it in small steps.

The authors give no warranty (see `LICENSE.txt`).

## 3. Requirements

You must have these items:

- Windows 10 or Windows 11, 64-bit.
- A MOZA wheelbase.
- MOZA Pit House. Pit House must run when you use the bridge.
- The MOZA SDK. The bridge downloads it from MOZA when you start it for the first time (see section 5).
- The ViGEmBus driver, version 1.22: <https://github.com/nefarius/ViGEmBus/releases>
- UE4SS for Galactic Racer ("Galactic Racer Edition", on Nexus Mods).
- An internet connection for the first start only (to download the MOZA SDK).

You do not need to install .NET. It is in `WheelBridge.exe`.

## 4. Installation

1. Install the ViGEmBus driver. If Windows tells you to restart the computer, restart it.
2. Install UE4SS for Galactic Racer. Use the UE4SS instructions.
3. Find the game folder `Griffin\Binaries\Win64\ue4ss\Mods`. The usual location is:
   `C:\Program Files (x86)\Steam\steamapps\common\STAR WARS Galactic Racer\Griffin\Binaries\Win64\ue4ss\Mods`
4. Copy the folder `UE4SS Mod\WheelBridgeExporter` from the zip file into the `Mods` folder.
5. Open the file `Mods\mods.txt` in a text editor.
6. Add this line to the file: `WheelBridgeExporter : 1`
7. Save the file.
8. Extract the zip file to a folder that you can write to. For example, use your Desktop or your Documents folder.

> **NOTE:** Do not extract the bridge into `C:\Program Files`. Windows does not let the bridge save its settings
> there. If you do this, the bridge saves its settings in `%LOCALAPPDATA%\SWGR Wheel Bridge`.

## 5. First start

1. Start MOZA Pit House.
2. Turn on the wheelbase.
3. Double-click `WheelBridge.exe`. The bridge window opens.
4. The bridge asks for the MOZA SDK. Click **Yes** to download it from MOZA.
   - If the download is not possible, download `MOZA_SDK.zip` from <https://mozaracing.com/pages/sdk>
     ("Racing SDK"). Then click **No** and select the zip file.
   - The bridge keeps only three files from the zip file, in the folder `MOZA SDK`.
5. Make sure that the status line shows the name of your wheelbase (for example "Wheelbase: MOZA R3").
6. The **Wheel Bridge setup** window opens. It has three steps:
   1. **Motor test.** Remove your hands from the wheel. Click **Run motor test**. The wheel turns a small
      distance. The bridge records the motor direction.
   2. **Pedals.** Push a pedal to make sure that the bridge detects it. Release all the pedals, click
      **Calibrate pedals**, then in 8 seconds push each pedal fully one time. If you have no pedals, click **No pedals**.
   3. **Controls.** The window shows the game functions one at a time. Push the wheel button for each function.
      Click **Skip** to keep a function as it is.
7. Click **Finish**.

> **NOTE:** The bridge sends no force to the wheel until the motor test is done. If the motor direction is
> incorrect, all the forces push the wheel away from the center.

If you click **Set up later**, the setup window opens again at the next start. To do the setup again at a later
time, click **Run setup…** in the **Controls** tab.

## 6. Strength

### 6.1 Automatic strength for each wheelbase

The bridge finds the model of your wheelbase. It then changes the force so that **Master 1.00** gives
approximately the force of the R3 (3.9 Nm), less a safety margin of 15 %.

| Wheelbase | Tested | Base strength (auto) | Maximum force at Master 1.00 |
|---|---|---|---|
| R3 | Yes | ×1.00 | 3.9 Nm |
| R5 | No | ×0.60 | 3.3 Nm |
| R9 | No | ×0.37 | 3.3 Nm |
| R12 | No | ×0.28 | 3.3 Nm |
| R16 | No | ×0.21 | 3.3 Nm |
| R21 | No | ×0.16 | 3.3 Nm |
| Other or not found | No | ×0.16 | 3.3 Nm or less |

The status line shows the wheelbase that the bridge found and the base strength.

> **CAUTION:** If the status line shows the wrong wheelbase, do not drive. Close the bridge and report the problem.

### 6.2 Change the strength

- To make all forces weaker or stronger, change **Master**. The profiles do not change Master.
- If the forces are too weak at Master 1.00, you can set **Base strength** to a value other than 0 (auto).
  The bridge asks you to confirm a value that is higher than the automatic value.

> **CAUTION:** A high Base strength on a strong wheelbase can cause very strong impacts. Increase the value in
> steps of 0.05. Keep your hands away from the wheel when you test a new value.

The FFB strength setting in Pit House also changes the force.

## 7. Controls

Use the **Controls** tab to set the controls for your wheel and your play style. The first-run setup (section 5)
sets all the controls one at a time. Use the steps below to change one control.

### 7.1 Bind a button

1. Find the Xbox control in the list. The "In game" column shows the function of the control.
2. Click **Bind…** on the line of the control.
3. In 6 seconds, push a wheel button. You can also push a pedal past half of its travel.
4. Make sure that the "Bound to" column shows the new input.
5. To add more inputs to the same control, do steps 2 to 4 again.

To remove the inputs from a control, click **Clear**.

### 7.2 Throttle and brake

- **RT** is the throttle. **LT** is the brake.
- In the "Pedal" column, select the pedal for each trigger. If you have no pedals, select **None**.
- You can also bind wheel buttons (for example the paddles) to RT and LT. A pushed button gives full throttle
  or full brake.

The game uses these controls:

| Xbox control | Game function |
|---|---|
| RT | Accelerate |
| LT | Brake and reverse |
| A | Boost |
| RS | Ramjet |
| X | Drift and maneuver |
| B | Malfunction recovery |
| LB | Ability |
| RB | Camera |
| D-pad | Ability selection |
| Start | Pause |

The bindings save automatically. The bindings stay with your wheel. A profile does not change them.

## 8. Operation

1. Start MOZA Pit House.
2. Start `WheelBridge.exe`.
3. Click **▶ Launch game**. You can also start the game from Steam.
4. Make sure that the monitor shows `Pipe: CONNECTED`. This tells you that the mod sends data to the bridge.

### 8.1 Buttons in the bridge window

| Button | Function |
|---|---|
| **KILL FFB** (Alt+K) | Stops all forces immediately. Click again to start the forces again. |
| **Force center** (Alt+C) | Holds the wheel at the center and sends a centered steering value to the game. Use it between races. It stops when a race starts. |
| **Motor test** | Finds the motor direction. Remove your hands from the wheel first. |
| **Load**, **Save**, **Save as…** | Load a profile, save the profile, or save a new profile. |
| **Auto profile** | Loads the correct profile when you enter a vehicle. |
| **● Record** | Records game data to a file. Use this to report a problem. |

To get help for a setting, put the mouse pointer on the setting.

### 8.2 Operation rules

- The forces operate only during a race. The forces stop in menus, when the game is paused, and after the finish line.
- When Shade walks in the hub, the wheel does not control the game. Use your controller or keyboard to move Shade.
- **Auto profile** does not replace a profile that has unsaved changes. Click **Save** to keep the changes.
- The speeder bike profile uses the **Lean** layer at a low value (0.12). This layer adds a small weight when the bike
  leans into a turn. A high value makes the wheel turn away from you while the bike leans.
- The game uses only player 1. If a different controller is connected when the bridge starts, that controller is
  player 1 and the game ignores the wheel. The bridge shows a message. Disconnect or turn off the other controller:
  the wheel then becomes player 1 automatically.
- To start again with the default settings, close the bridge and delete `wheelbridge.json`.
- To use a text window instead of the bridge window, start `WheelBridge.exe --console`.

## 9. Troubleshooting

| Problem | Possible cause | Correction |
|---|---|---|
| The bridge shows `MOZA SDK not installed` | The MOZA SDK is not downloaded | Click **⬇ Get MOZA SDK** (see section 5, step 4). |
| The monitor shows `Pipe: waiting for game…` during a race | The mod did not start | Do steps 4 to 7 of the installation again. Look for `[WheelBridgeExporter] loaded` in `ue4ss\UE4SS.log`. |
| The monitor shows `Pit House not ready` or `no MOZA device found` | Pit House does not run, or the wheelbase is off | Start Pit House. Turn on the wheelbase. Then start the bridge. |
| The bridge shows `ViGEmBus driver not installed` | The driver is not installed | Install the ViGEmBus driver (see section 3). |
| A wheel button does nothing in the game | The button is not bound | Bind the button in the **Controls** tab (see section 7). |
| The status line shows `OFF until the Motor test has run` | The motor test is not done | Remove your hands from the wheel. Click **Motor test**. |
| All forces push in the wrong direction | The motor direction is incorrect | Do the motor test again. Keep your hands away from the wheel. |
| **Force center** stops and shows "the wheel kept turning away from center" | The motor direction is incorrect, or you turned the wheel | Do the motor test again. Keep your hands away from the wheel. |
| The wheel does not control the game | A different controller is player 1 | Disconnect or turn off the other controller. The status bar tells you when the wheel is player 1 again. |
| The wheel turns away from you while the speeder bike leans | **Lean** is too high | Set **Lean** to 0.12 or less, or turn it off. |
| Small wheel movements do not steer the vehicle | The game ignores small controller movements | Increase **Anti-deadzone**. |
| The vehicle turns too quickly from the center | **Anti-deadzone** is too high | Decrease **Anti-deadzone**. |
| The forces are too weak or too strong | Incorrect strength | See section 6.2. |
| Windows shows "Windows protected your PC" | The file has no publisher signature | Click **More info**, then **Run anyway**. |

To report a problem:

1. Click **● Record**.
2. Drive the part of the track where the problem occurs.
3. Click **■ Stop**.
4. Send the `.tsv` file from the `recordings` folder with your report.

## 10. Status

- All four vehicle profiles (land speeder, speeder bike, skim speeder and podracer) were tested in the game with a
  MOZA R3.
- The bridge operates only with MOZA wheelbases. Only the R3 was tested.

## 11. Licence

The bridge and the game mod are free software under the MIT License (see `LICENSE.txt`). The MOZA SDK is not part
of the bridge. It is the property of MOZA Racing.

This tool is not related to Lucasfilm, to the developer or publisher of the game, or to MOZA Racing.
