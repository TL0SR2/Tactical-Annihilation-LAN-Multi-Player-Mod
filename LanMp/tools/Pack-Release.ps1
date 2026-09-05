# Pack AnnW.LanMp + BepInEx into a game-root distributable zip.
# Usage: powershell -File LanMp\tools\Pack-Release.ps1 [-Version 0.16.12]
param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$GameRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$LanMpRoot = Join-Path $GameRoot "LanMp"
$DistRoot = Join-Path $LanMpRoot "dist"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $pluginCs = Join-Path $LanMpRoot "src\AnnW.LanMp\Plugin.cs"
    $m = Select-String -Path $pluginCs -Pattern 'PluginVersion\s*=\s*"([^"]+)"' | Select-Object -First 1
    if (-not $m) { throw "Cannot read PluginVersion from Plugin.cs" }
    $Version = $m.Matches[0].Groups[1].Value
}

Write-Host "Building Release $Version ..."
dotnet build (Join-Path $LanMpRoot "src\AnnW.LanMp\AnnW.LanMp.csproj") -c Release | Out-Host
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$stage = Join-Path $DistRoot "stage-$Version"
$zipName = "AnnW.LanMp-$Version-with-BepInEx.zip"
$zipPath = Join-Path $DistRoot $zipName

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
New-Item -ItemType Directory -Force -Path $DistRoot | Out-Null

# Doorstop bootstrap (game root)
foreach ($f in @("winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt")) {
    $src = Join-Path $GameRoot $f
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $stage $f) -Force
    }
}

# BepInEx core (no logs/cache/config clutter)
$bepDst = Join-Path $stage "BepInEx"
New-Item -ItemType Directory -Force -Path $bepDst | Out-Null
Copy-Item (Join-Path $GameRoot "BepInEx\core") (Join-Path $bepDst "core") -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $bepDst "plugins") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $bepDst "patchers") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $bepDst "config") | Out-Null

# Plugin DLLs (no PDBs)
$plugSrc = Join-Path $GameRoot "BepInEx\plugins\AnnW.LanMp"
$plugDst = Join-Path $bepDst "plugins\AnnW.LanMp"
New-Item -ItemType Directory -Force -Path $plugDst | Out-Null
Get-ChildItem $plugSrc -Filter "*.dll" | Copy-Item -Destination $plugDst -Force

$patcher = Join-Path $GameRoot "BepInEx\patchers\AnnW.LanMp.EarlyPatcher.dll"
if (Test-Path $patcher) {
    Copy-Item $patcher (Join-Path $bepDst "patchers\AnnW.LanMp.EarlyPatcher.dll") -Force
}

# Install notes
@"
AnnW LAN Multiplayer $Version
==============================

Install
-------
1. Close the game.
2. Extract this zip into the game root (same folder as AnnW.exe),
   overwriting if asked. Typical Steam path:
   ...\steamapps\common\Tactical Annihilation\
3. Launch AnnW.exe once. BepInEx will generate config under BepInEx\config\.
4. Main menu -> Skirmish -> LAN Multiplayer lobby.

Contents
--------
- BepInEx 5.4.x (win_x64 Doorstop)
- AnnW.LanMp plugin + Protocol + Newtonsoft.Json
- AnnW.LanMp.EarlyPatcher (optional dual-instance / Steam bypass aid)

Notes
-----
- Join uses Host IP:Port (default 127.0.0.1:24555). No public matchmaking.
- Host and Guest must use the same plugin version.
- Source: https://github.com/TL0SR2/Tactical-Annihilation-LAN-Multi-Player-Mod
"@ | Set-Content -Path (Join-Path $stage "INSTALL.txt") -Encoding UTF8

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)

Remove-Item $stage -Recurse -Force

$len = (Get-Item $zipPath).Length
Write-Host "OK: $zipPath ($([math]::Round($len/1MB, 2)) MB)"
