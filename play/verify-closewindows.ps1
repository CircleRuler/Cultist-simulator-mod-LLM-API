# Does /action/closewindows actually keep the tabletop clear?
#
# The user reported that closing a verb window is not enough: once a work verb finishes, the game
# opens a *default* work window by itself and it sits there covering the board. So the question is
# not "does one close work" but "does calling it repeatedly suppress the re-opening".
#
#   powershell -ExecutionPolicy Bypass -File verify-closewindows.ps1 [-Legacy detective] [-Cycles 24]

param(
    [string]$Legacy = 'detective',
    [int]$Cycles = 24
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

function Say { param([string]$m, [string]$c = 'Gray'); Write-Host $m -ForegroundColor $c }

$probe = Invoke-Api GET /ping
if ($probe.Status -ne 200) { Say $probe.OneLine 'Red'; exit 1 }

# --- get into a game -----------------------------------------------------------------------
$sc = (Invoke-Api GET /screen).Json
if ($sc.tabletopHasCards) {
    Say 'leaving the current game...' 'Yellow'
    $null = Invoke-Api POST /action/menu '{}'
    for ($i = 0; $i -lt 12; $i++) { Start-Sleep -Seconds 3; if (-not (Invoke-Api GET /screen).Json.tabletopHasCards) { break } }
}
$before = @((Invoke-Api GET /state).Json.tokens | ForEach-Object { $_.id } | Sort-Object)
$null = Invoke-Api POST /action/newgame (@{ legacy = $Legacy } | ConvertTo-Json -Compress)
for ($i = 0; $i -lt 15; $i++) {
    Start-Sleep -Seconds 3
    $b = (Invoke-Api GET /state).Json
    if (-not $b -or -not $b.tabletop.exists) { continue }
    $now = @($b.tokens | ForEach-Object { $_.id } | Sort-Object)
    if (($now -join ',') -ne ($before -join ',')) { break }
}
$b = (Invoke-Api GET /state).Json
Say "in game: tokens=$($b.tokenCount) paused=$($b.playback.paused)" 'Green'
$null = Invoke-Api POST /action/speed '{"speed":"Normal"}'

$reopens = 0
$cleared = 0
$samples = @()

for ($c = 1; $c -le $Cycles; $c++) {
    $board = Get-Board
    if (-not $board) { break }
    if ((Get-Playback $board).paused) { $null = Invoke-Api POST /action/unpause '{}' }

    $openBefore = @($board.openWindows)

    # --- THE TEST: clear every window, then see whether the game puts one back -------------
    $r = Clear-Windows
    if ($openBefore.Count -gt 0) {
        Say "[$c] closewindows: wereOpen=[$($openBefore -join ', ')] closed=$($r.Json.closedCount)"
        $cleared++
    }

    Start-Sleep -Seconds 2
    $after = Get-Board
    $openAfter = @($after.openWindows)
    $samples += [pscustomobject]@{ Cycle = $c; Before = ($openBefore -join ','); After = ($openAfter -join ',') }

    if ($openBefore.Count -gt 0 -and $openAfter.Count -gt 0) {
        $reopens++
        $newOnes = @($openAfter | Where-Object { $openBefore -notcontains $_ })
        Say "    STILL OPEN / RE-OPENED: [$($openAfter -join ', ')]$(if ($newOnes.Count -gt 0) { "  (new: $($newOnes -join ', '))" })" 'Yellow'
    } elseif ($openBefore.Count -gt 0) {
        Say "    clear now: openWindows=[]" 'Green'
    }

    # feed a verb so work runs and finishes, which is what made the game re-open a window
    $pair = Find-Pairing $board
    if ($pair) {
        $p = Invoke-Api POST /action/place (@{ token = $pair.Card; slot = $pair.Slot } | ConvertTo-Json -Compress)
        if ($p.Json.accepted) {
            Start-Sleep -Seconds 2
            $null = Invoke-Api POST /action/start (@{ token = $pair.VerbId } | ConvertTo-Json -Compress)
        }
    }

    Start-Sleep -Seconds 3
}

Say ''
Say '=========== VERDICT ===========' 'Cyan'
$withBefore = @($samples | Where-Object { $_.Before -ne '' })
$stillOpen = @($withBefore | Where-Object { $_.After -ne '' })
Say "closewindows called with windows open: $($withBefore.Count) time(s)"
Say "of those, a window was still open 2s later: $($stillOpen.Count) time(s)"
if ($stillOpen.Count -eq 0) { Say 'PASS - every clear emptied openWindows' 'Green' }
else {
    Say 'FAIL - the game put a window back' 'Red'
    foreach ($x in $stillOpen) { Say "   cycle $($x.Cycle): before=[$($x.Before)] after=[$($x.After)]" 'Red' }
}
$final = Get-Board
Say "final openWindows = [$($final.openWindows -join ', ')]"
$final.tokens | Where-Object { $_.payloadType -eq 'Situation' } | ForEach-Object {
    Say ("  {0,-14} state={1,-10} isOpen={2,-6} concludable={3}" -f $_.id, $_.situationState, $_.isOpen, $_.concludable)
}
