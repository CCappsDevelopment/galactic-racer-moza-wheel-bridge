<#
  Builds the release zip: dist\SWGR-Wheel-Bridge-<version>.zip

  Contents: the bridge (self-contained, so players don't need to install .NET), the shipped default profiles,
  the UE4SS exporter mod, README, LICENSE and third-party notices. Never includes your wheelbridge.json, your
  profiles, recordings or the MOZA SDK.

  Usage:  powershell -ExecutionPolicy Bypass -File tools\package.ps1 [-FrameworkDependent] [-SkipTests]
#>
param([switch]$FrameworkDependent, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'

$project = Split-Path -Parent $PSScriptRoot                 # ...\WheelBridge
$workspace = Split-Path -Parent $project                    # ...\Galactic Racer Modding
[xml]$csproj = Get-Content (Join-Path $project 'WheelBridge.csproj')
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$name = "SWGR-Wheel-Bridge-$version"
$dist = Join-Path $workspace 'dist'
$out = Join-Path $dist $name

if (-not $SkipTests) {
    Write-Host '== Regression tests'
    dotnet run -c Release --project (Join-Path $workspace 'WheelBridge.Tests') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'C# regression tests failed: not packaging.' }
    python -I (Join-Path $workspace 'WheelBridge.Tests\exporter_test.py') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Exporter tests failed: not packaging.' }
}

Write-Host "== Publishing $name"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$sc = if ($FrameworkDependent) { 'false' } else { 'true' }
# Portable: one WheelBridge.exe that carries the .NET runtime, nothing to install. The MOZA SDK is not in the
# package (CopyToPublishDirectory=Never); the app downloads it from MOZA on first start.
dotnet publish (Join-Path $project 'WheelBridge.csproj') -c Release -r win-x64 --self-contained $sc -o $out -nologo -v q `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=true `
    -p:SatelliteResourceLanguages=en -p:DebugType=none | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# Nothing personal or dev-only may ship.
foreach ($p in 'wheelbridge.json', 'profiles', 'recordings', '*.pdb') {
    Get-ChildItem $out -Filter $p -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
}

$mod = Join-Path $out 'UE4SS Mod\WheelBridgeExporter\Scripts'
New-Item -ItemType Directory -Force $mod | Out-Null
Copy-Item (Join-Path $workspace 'Mods\WheelBridgeExporter\Scripts\main.lua') $mod
Copy-Item (Join-Path $project 'release\README.md') $out
Copy-Item (Join-Path $project 'release\THIRD-PARTY-NOTICES.txt') $out
Copy-Item (Join-Path $workspace 'LICENSE') (Join-Path $out 'LICENSE.txt')

# The MOZA SDK has no redistribution licence: the zip must not contain any of it (users download it from MOZA).
$moza = Get-ChildItem $out -Recurse -File | Where-Object { $_.Name -like 'MOZA_*' -or $_.Name -like 'libssl*' -or $_.Name -like 'libcrypto*' }
if ($moza) { throw "MOZA SDK files in the package: $($moza.Name -join ', ')" }

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "== $zip ($size MB)"
