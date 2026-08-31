# Compare Host vs Guest battle sync traces (NDJSON from BattleSyncTrace).
#
#   powershell -ExecutionPolicy Bypass -File .\LanMp\tools\Compare-SyncTrace.ps1
#   powershell -ExecutionPolicy Bypass -File .\LanMp\tools\Compare-SyncTrace.ps1 -HostLog path -GuestLog path
#
# Default: pick newest sync-trace-Host-*.ndjson under game root and Guest sandbox.

param(
    [string]$HostRoot = "E:\SteamLibrary\steamapps\common\Tactical Annihilation",
    [string]$GuestRoot = "E:\SteamLibrary\steamapps\common\Tactical Annihilation.Guest",
    [string]$HostLog = "",
    [string]$GuestLog = ""
)

$ErrorActionPreference = "Stop"

function Get-NewestTrace([string]$root, [string]$role) {
    $dir = Join-Path $root "LanMp\logs"
    if (-not (Test-Path $dir)) { return $null }
    Get-ChildItem $dir -Filter "sync-trace-$role-*.ndjson" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

if (-not $HostLog) {
    $h = Get-NewestTrace $HostRoot "Host"
    if (-not $h) { throw "No Host sync-trace under $HostRoot\LanMp\logs" }
    $HostLog = $h.FullName
}
if (-not $GuestLog) {
    $g = Get-NewestTrace $GuestRoot "Guest"
    if (-not $g) {
        $g = Get-NewestTrace $HostRoot "Guest"
    }
    if (-not $g) { throw "No Guest sync-trace under Guest/Host LanMp\logs" }
    $GuestLog = $g.FullName
}

Write-Host "Host : $HostLog"
Write-Host "Guest: $GuestLog"
Write-Host ""

function Read-Ndjson([string]$path) {
    Get-Content $path -Encoding UTF8 | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object {
        try { $_ | ConvertFrom-Json } catch { $null }
    } | Where-Object { $_ -ne $null }
}

$hostRows = @(Read-Ndjson $HostLog)
$guestRows = @(Read-Ndjson $GuestLog)

Write-Host "=== Counts ==="
Write-Host ("Host events={0}  Guest events={1}" -f $hostRows.Count, $guestRows.Count)

function Show-Timeline($rows, $label) {
    Write-Host ""
    Write-Host "=== $label timeline (Intent/Cmd/Cursor) ==="
    $rows | Where-Object {
        $_.ev -match 'Intent|Cmd|Cursor|Watch|Nack|Match|TraceOpen|Broadcast|Accept|Apply'
    } | ForEach-Object {
        $cur = if ($null -ne $_.curPlayer) { $_.curPlayer } else { "-" }
        $next = if ($null -ne $_.nextPlayer) { " ->$($_.nextPlayer)" } else { "" }
        $turn = if ($null -ne $_.turn) { "t$($_.turn)" } else { "t?" }
        $kind = if ($_.kind) { $_.kind } else { "" }
        $unit = if ($null -ne $_.unitId) { " u$($_.unitId)" } else { "" }
        $tgt = if ($_.target) { " @$($_.target)" } else { "" }
        $id = if ($_.cmdId) { $_.cmdId.Substring(0, [Math]::Min(8, $_.cmdId.Length)) }
              elseif ($_.intentId) { $_.intentId.Substring(0, [Math]::Min(8, $_.intentId.Length)) }
              else { "" }
        "{0,-18} {1,-10} {2} cur={3}{4}{5}{6} {7}" -f $_.ev, $kind, $turn, $cur, $next, $unit, $tgt, $id
    }
}

Show-Timeline $hostRows "HOST"
Show-Timeline $guestRows "GUEST"

# Align EndTurn by sequence order
$hostEnds = @($hostRows | Where-Object { $_.ev -match 'Broadcast|EndTurnReady' -and $_.kind -eq 'EndTurn' })
$guestEnds = @($guestRows | Where-Object { $_.ev -match 'Apply|Cursor' -and ($_.kind -eq 'EndTurn' -or $_.ev -eq 'CursorSet') })

Write-Host ""
Write-Host "=== EndTurn / Cursor alignment ==="
$n = [Math]::Max($hostEnds.Count, $guestEnds.Count)
$mismatches = 0
for ($i = 0; $i -lt $n; $i++) {
    $h = if ($i -lt $hostEnds.Count) { $hostEnds[$i] } else { $null }
    $g = if ($i -lt $guestEnds.Count) { $guestEnds[$i] } else { $null }
    $hNext = if ($h) { $h.nextPlayer } else { "missing" }
    $gNext = if ($g) {
        if ($null -ne $g.nextPlayer) { $g.nextPlayer } elseif ($null -ne $g.curPlayer) { $g.curPlayer } else { "?" }
    } else { "missing" }
    $hTurn = if ($h -and $null -ne $h.turnsAfter) { $h.turnsAfter } elseif ($h) { $h.turn } else { "?" }
    $gTurn = if ($g -and $null -ne $g.turnsAfter) { $g.turnsAfter } elseif ($g) { $g.turn } else { "?" }
    $ok = ($hNext -eq $gNext) -and ("$hTurn" -eq "$gTurn")
    if (-not $ok) { $mismatches++ }
    $mark = if ($ok) { "OK " } else { "DIFF" }
    Write-Host ("[{0}] #{1} Host next={2} turn={3} | Guest next/cur={4} turn={5}" -f $mark, $i, $hNext, $hTurn, $gNext, $gTurn)
}

Write-Host ""
Write-Host "=== DoAction counts by cate ==="
function Cate-Histogram($rows) {
    $rows | Where-Object { $_.kind -eq 'DoAction' } |
        Group-Object cate | Sort-Object Name |
        ForEach-Object { "  cate=$($_.Name) count=$($_.Count)" }
}
Write-Host "Host:"
Cate-Histogram $hostRows
Write-Host "Guest:"
Cate-Histogram $guestRows

Write-Host ""
if ($mismatches -eq 0 -and $hostEnds.Count -eq $guestEnds.Count -and $hostEnds.Count -gt 0) {
    Write-Host "RESULT: EndTurn/cursor sequences aligned ($($hostEnds.Count) transitions)."
} elseif ($hostEnds.Count -eq 0 -and $guestEnds.Count -eq 0) {
    Write-Host "RESULT: No EndTurn events found — fight may not have started or tracing off."
} else {
    Write-Host "RESULT: $mismatches cursor mismatch(es) and/or count Host=$($hostEnds.Count) Guest=$($guestEnds.Count)."
    Write-Host "Paste both NDJSON files (or this script output) when reporting sync bugs."
}
