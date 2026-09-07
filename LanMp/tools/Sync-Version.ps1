# Sync README to LanMpVersion.Current.
# Single source: LanMp/src/AnnW.LanMp.Protocol/LanMpVersion.cs
# Usage:
#   powershell -File LanMp\tools\Sync-Version.ps1
#   powershell -File LanMp\tools\Sync-Version.ps1 -Version 0.19.0
param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$script:Utf8 = New-Object System.Text.UTF8Encoding $false
$LanMpRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$VersionCs = Join-Path $LanMpRoot "src\AnnW.LanMp.Protocol\LanMpVersion.cs"
$Readme = Join-Path $LanMpRoot "README.md"

function Get-LanMpVersion([string]$path) {
    $text = [System.IO.File]::ReadAllText($path, $script:Utf8)
    $m = [regex]::Match($text, 'Current\s*=\s*"([^"]+)"')
    if (-not $m.Success) { throw "Cannot read LanMpVersion.Current from $path" }
    return $m.Groups[1].Value
}

function Set-LanMpVersion([string]$path, [string]$ver) {
    $text = [System.IO.File]::ReadAllText($path, $script:Utf8)
    $updated = [regex]::Replace(
        $text,
        'Current\s*=\s*"[^"]+"',
        ('Current = "' + $ver + '"'),
        1)
    if ($updated -notmatch [regex]::Escape('Current = "' + $ver + '"')) {
        throw "Failed to update LanMpVersion.Current in $path"
    }
    [System.IO.File]::WriteAllText($path, $updated, $script:Utf8)
}

function Sync-Readme([string]$path, [string]$ver) {
    if (-not (Test-Path $path)) { return }
    $text = [System.IO.File]::ReadAllText($path, $script:Utf8)
    # ASCII-safe: **x.y.z** on the line that mentions LanMpVersion (UTF-8 README OK).
    $pattern = '(\*\*)(\d+\.\d+\.\d+)(\*\*[^\r\n]*LanMpVersion)'
    if (-not [regex]::IsMatch($text, $pattern)) {
        Write-Warning "README version token not found; leaving unchanged"
        return
    }
    $updated = [regex]::Replace($text, $pattern, ('${1}' + $ver + '${3}'), 1)
    if ($updated -ne $text) {
        [System.IO.File]::WriteAllText($path, $updated, $script:Utf8)
        Write-Host ("README synced to **" + $ver + "**")
    } else {
        Write-Host ("README already at **" + $ver + "**")
    }
}

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    Write-Host ("Bumping LanMpVersion.Current -> " + $Version)
    Set-LanMpVersion $VersionCs $Version.Trim()
}

$resolved = Get-LanMpVersion $VersionCs
Write-Host ("Syncing docs to " + $resolved + " ...")
Sync-Readme $Readme $resolved
Write-Host ("OK: version=" + $resolved)