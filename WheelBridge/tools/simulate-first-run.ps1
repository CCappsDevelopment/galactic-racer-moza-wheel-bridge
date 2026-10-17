<#
  Shows the bridge exactly as a new user gets it: a published build (no MOZA SDK next to the exe) with an empty
  settings folder. Your own wheelbridge.json, profiles and bindings are not used or changed.

  Usage:  powershell -ExecutionPolicy Bypass -File tools\simulate-first-run.ps1 [-KeepSdk]
    -KeepSdk  keep the MOZA SDK downloaded in an earlier simulation (default: start without it every time)
#>
param([switch]$KeepSdk)
$ErrorActionPreference = 'Stop'

$project = Split-Path -Parent $PSScriptRoot
$root = Join-Path (Split-Path -Parent $project) 'dist\first-run'
$app = Join-Path $root 'app'
$dataDir = Join-Path $root 'home'

if (Get-Process WheelBridge -ErrorAction SilentlyContinue) { throw 'Close the running Wheel Bridge first (only one can own the wheel and the pipe).' }

Write-Host '== Publishing a release build (without the MOZA SDK)'
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
dotnet publish (Join-Path $project 'WheelBridge.csproj') -c Release -r win-x64 --self-contained true -o $app -nologo -v q `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=true `
    -p:SatelliteResourceLanguages=en -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
$moza = Get-ChildItem $app -Filter 'MOZA_*' -Recurse
if ($moza) { throw "The build contains MOZA files ($($moza.Name -join ', ')): it would not show the first-run download." }

Write-Host '== Fresh settings folder'
if (Test-Path $dataDir) {
    $sdk = Join-Path $dataDir 'MOZA SDK'
    Get-ChildItem $dataDir | Where-Object { -not ($KeepSdk -and $_.FullName -eq $sdk) } | Remove-Item -Recurse -Force
} else { New-Item -ItemType Directory $dataDir | Out-Null }

Write-Host "== Starting the bridge as a new user (settings in $dataDir)"
$env:SWGR_WHEELBRIDGE_HOME = $dataDir
Start-Process (Join-Path $app 'WheelBridge.exe') -WorkingDirectory $app
