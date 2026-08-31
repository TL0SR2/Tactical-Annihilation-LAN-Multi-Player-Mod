# Wipe LanMp sync traces and/or BepInEx text logs (manual — never on battle end).
#
#   powershell -ExecutionPolicy Bypass -File .\LanMp\tools\Clear-LanMpLogs.ps1
#   powershell -ExecutionPolicy Bypass -File .\LanMp\tools\Clear-LanMpLogs.ps1 -IncludeBepInEx

param(
    [string]$HostRoot = "E:\SteamLibrary\steamapps\common\Tactical Annihilation",
    [string]$GuestRoot = "E:\SteamLibrary\steamapps\common\Tactical Annihilation.Guest",
    [switch]$IncludeBepInEx,
    [switch]$GuestOnly
)

$ErrorActionPreference = "Stop"

function Clear-SyncTraces($root) {
    $dir = Join-Path $root "LanMp\logs"
    if (-not (Test-Path $dir)) { return 0 }
    $files = Get-ChildItem $dir -Filter "sync-trace-*.ndjson" -ErrorAction SilentlyContinue
    foreach ($f in $files) { Remove-Item $f.FullName -Force }
    return @($files).Count
}

function Clear-BepInExLog($root) {
    $log = Join-Path $root "BepInEx\LogOutput.log"
    if (Test-Path $log) { Remove-Item $log -Force; return 1 }
    return 0
}

$roots = if ($GuestOnly) { @($GuestRoot) } else { @($HostRoot, $GuestRoot) }

$nTrace = 0
$nBep = 0
foreach ($r in $roots) {
    if (-not (Test-Path $r)) { continue }
    $nTrace += Clear-SyncTraces $r
    if ($IncludeBepInEx) { $nBep += Clear-BepInExLog $r }
}

Write-Host "Cleared sync-trace: $nTrace file(s)"
if ($IncludeBepInEx) { Write-Host "Cleared BepInEx LogOutput.log: $nBep file(s)" }
