@echo off
title SWGR Wheel Bridge
mode con: cols=120 lines=48
rem Run from a copy so the build output is never locked and can be rebuilt while the bridge is open.
robocopy "%~dp0bin\Release\net10.0-windows" "%~dp0bin\run" /MIR /NJH /NJS /NFL /NDL /NP >nul
"%~dp0bin\run\WheelBridge.exe" --console
