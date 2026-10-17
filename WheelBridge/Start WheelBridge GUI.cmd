@echo off
rem Tuning GUI. Runs from a copy so the build output is never locked and can be rebuilt while the bridge is open.
robocopy "%~dp0bin\Release\net10.0-windows" "%~dp0bin\run" /MIR /NJH /NJS /NFL /NDL /NP >nul
start "" "%~dp0bin\run\WheelBridge.exe" --gui
