<#
  Downloads MOZA's official SDK zip and extracts the 64-bit C# SDK into ..\Tools\MOZA_SDK_1.0.1.8\SDK_CSharp\x64,
  where WheelBridge.csproj expects it. Needed once per checkout to build the bridge. The SDK is MOZA's proprietary
  software: it is not in this repository and must never be committed.

  Usage:  powershell -ExecutionPolicy Bypass -File tools\get-moza-sdk.ps1
#>
$ErrorActionPreference = 'Stop'
$url = 'https://cdn.gudsen.vip/simulation_game/rs21repository/installer/MOZA_SDK.zip'   # linked from https://mozaracing.com/pages/sdk
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$target = Join-Path $workspace 'Tools\MOZA_SDK_1.0.1.8\SDK_CSharp\x64'
$files = 'MOZA_API_CSharp.dll', 'MOZA_API_C.dll', 'MOZA_SDK.dll'

if (($files | ForEach-Object { Test-Path (Join-Path $target $_) }) -notcontains $false) { Write-Host "MOZA SDK already in $target"; return }

$tmp = Join-Path ([IO.Path]::GetTempPath()) "MOZA_SDK_$([guid]::NewGuid().ToString('N')).zip"
try {
    Write-Host "Downloading $url"
    Invoke-WebRequest $url -OutFile $tmp -UseBasicParsing
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($tmp)
    try {
        New-Item -ItemType Directory -Force $target | Out-Null
        foreach ($f in $files) {
            $e = $zip.Entries | Where-Object { $_.FullName -eq "MOZA_SDK/SDK_CSharp/x64/$f" } | Select-Object -First 1
            if (-not $e) { throw "MOZA_SDK/SDK_CSharp/x64/$f not found in the download" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path $target $f), $true)
            Write-Host ("  {0}  sha256 {1}" -f $f, (Get-FileHash (Join-Path $target $f) -Algorithm SHA256).Hash)
        }
    } finally { $zip.Dispose() }
    Write-Host "MOZA SDK extracted to $target"
} finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
