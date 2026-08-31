# Dual-instance LAN smoke on one PC (Host + Guest).
# Guest uses BepInEx EarlyPatcher + LanMp.ForceNoSteam marker to skip Steam Quit at PreMenu.
#
#   powershell -ExecutionPolicy Bypass -File .\LanMp\tools\DualInstance.ps1
#   powershell -ExecutionPolicy Bypass -File .\LanMp\tools\DualInstance.ps1 -ClearLogs

param(
    [switch]$ClearLogs
)

$ErrorActionPreference = "Stop"
$HostRoot = "E:\SteamLibrary\steamapps\common\Tactical Annihilation"
$GuestRoot = "E:\SteamLibrary\steamapps\common\Tactical Annihilation.Guest"
$AppId = "3345550"

Get-Process AnnW -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

if (-not (Test-Path "$HostRoot\AnnW.exe")) { throw "AnnW.exe missing" }
if (-not (Test-Path "$GuestRoot\AnnW.exe")) {
    Write-Host "Creating Guest sandbox (robocopy)..."
    New-Item -ItemType Directory -Force -Path $GuestRoot | Out-Null
    & robocopy $HostRoot $GuestRoot /E /XD ".git" /NFL /NDL /NJH /NJS /nc /ns /np
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed $LASTEXITCODE" }
}

# Sync plugin + early patcher
New-Item -ItemType Directory -Force -Path "$GuestRoot\BepInEx\plugins\AnnW.LanMp" | Out-Null
New-Item -ItemType Directory -Force -Path "$GuestRoot\BepInEx\patchers" | Out-Null
Copy-Item "$HostRoot\BepInEx\plugins\AnnW.LanMp\*" "$GuestRoot\BepInEx\plugins\AnnW.LanMp\" -Recurse -Force
Copy-Item "$HostRoot\BepInEx\patchers\AnnW.LanMp.EarlyPatcher.dll" "$GuestRoot\BepInEx\patchers\" -Force
# Host must NOT have the ForceNoSteam marker
Remove-Item "$HostRoot\LanMp.ForceNoSteam" -Force -ErrorAction SilentlyContinue
# Guest marker enables IL bypass in EarlyPatcher
Set-Content "$GuestRoot\LanMp.ForceNoSteam" "1" -Encoding ASCII

Set-Content "$HostRoot\steam_appid.txt" $AppId -Encoding ASCII
Set-Content "$GuestRoot\steam_appid.txt" $AppId -Encoding ASCII

New-Item -ItemType Directory -Force -Path "$GuestRoot\BepInEx\config" | Out-Null
@"
[General]
Enabled = true
ForceNoSteam = true

[Network]
HostPort = 24555
JoinAddress = 127.0.0.1:24555
DisplayName = GuestPC
"@ | Set-Content "$GuestRoot\BepInEx\config\annw.lanmp.cfg" -Encoding UTF8

if ($ClearLogs) {
    Write-Host "ClearLogs: wiping sync traces + BepInEx logs (optional -ClearLogs only)..."
    & powershell -ExecutionPolicy Bypass -File "$HostRoot\LanMp\tools\Clear-LanMpLogs.ps1" -IncludeBepInEx
}

Write-Host "Launching Host..."
Start-Process -FilePath "$HostRoot\AnnW.exe" -WorkingDirectory $HostRoot
Start-Sleep -Seconds 10
Write-Host "Launching Guest (EarlyPatcher + ForceNoSteam marker)..."
Start-Process -FilePath "$GuestRoot\AnnW.exe" -WorkingDirectory $GuestRoot
Start-Sleep -Seconds 15

$procs = @(Get-Process AnnW -ErrorAction SilentlyContinue)
$procs | Format-Table Id, Path, MainWindowTitle -AutoSize
if ($procs.Count -lt 2) {
    Write-Host "FAILED — Guest BepInEx log:"
    Get-Content "$GuestRoot\BepInEx\LogOutput.log" -ErrorAction SilentlyContinue
    Write-Host "---- preloader ----"
    Get-ChildItem $GuestRoot -Filter "preloader*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | ForEach-Object { Get-Content $_.FullName -Tail 40 }
    exit 1
}
Write-Host "OK: $($procs.Count) clients up. Host: create room; Guest: join 127.0.0.1:24555"
Write-Host "Sync traces: $HostRoot\LanMp\logs  and  $GuestRoot\LanMp\logs"
Write-Host "After a fight: powershell -ExecutionPolicy Bypass -File $HostRoot\LanMp\tools\Compare-SyncTrace.ps1"
Write-Host "Clear logs manually: powershell -ExecutionPolicy Bypass -File $HostRoot\LanMp\tools\Clear-LanMpLogs.ps1 [-IncludeBepInEx]"
