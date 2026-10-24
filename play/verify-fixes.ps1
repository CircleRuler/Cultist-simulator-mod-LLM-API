# Verify the three fixes that were still unproven:
#   1. auto-placement  - collected cards get DISTINCT positions (no heap at the origin)
#   2. collect         - reports what it actually took (count vs candidates)
#   3. conclude        - actually closes a verb window (isOpenAfter == false)
#
# The game must already be running with LLM_API enabled. This starts its own game.
#
#   powershell -ExecutionPolicy Bypass -File verify-fixes.ps1 [-Legacy detective] [-Cycles 20]

param(
    [string]$Legacy = 'detective',
    [int]$Cycles = 20
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

# Tracks when each verb window was first seen open, so the script can wait a beat before
# closing it (a window is closed with /action/conclude, the X in its corner).
$script:seen = @{}
$script:pendingConclude = @{}

function Say { param([string]$m, [string]$c = 'Gray'); Write-Host $m -ForegroundColor $c }

$probe = Invoke-Api GET /ping
if ($probe.Status -ne 200) { Say $probe.OneLine 'Red'; exit 1 }

# --- start a fresh game, proven by the board changing --------------------------------------
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
$null = Invoke-Api POST /action/speed '{"speed":"VeryFast"}'

# --- findings ------------------------------------------------------------------------------
$concludeResults = @()
$collectResults = @()
$placementResults = @()

for ($c = 1; $c -le $Cycles; $c++) {
    $board = Get-Board
    if (-not $board) { break }
    if ((Get-Playback $board).paused) { $null = Invoke-Api POST /action/unpause '{}' }

    # ---- 3. conclude: close any window, but only once the verb ALLOWS it ------------------
    # Situation.Conclude() starts with "if (!State.IsConcludable) return;" - a verb that is
    # still running cannot be concluded and Conclude() silently does nothing. So wait for
    # concludable, ask, then confirm on a later poll (the commands are queued, not immediate).
    foreach ($w in @($board.openWindows)) {
        $wid = "$w"
        if (-not $script:seen.ContainsKey($wid)) { $script:seen[$wid] = Get-Date; continue }

        $sit = $board.tokens | Where-Object { $_.id -eq $wid }
        if (-not $sit.concludable) {
            $elapsed = ((Get-Date) - $script:seen[$wid]).TotalSeconds
            if ($elapsed -gt 60) {
                Say "[$c] $wid still not concludable after $([int]$elapsed)s (state=$($sit.situationState))" 'Yellow'
                $concludeResults += [pscustomobject]@{ Token = $wid; Outcome = "never concludable (state=$($sit.situationState))" }
                $script:seen.Remove($wid)
            }
            continue
        }
        if (((Get-Date) - $script:seen[$wid]).TotalSeconds -lt 5) { continue }

        $r = Invoke-Api POST /action/conclude (@{ token = $wid } | ConvertTo-Json -Compress)
        $script:pendingConclude[$wid] = $c
        Say "[$c] conclude $wid (state=$($sit.situationState) concludable=$($sit.concludable)) -> $($r.Status) queued=$($r.Json.queued)"
        $script:seen.Remove($wid)
    }

    # did any previously-concluded window actually disappear from openWindows?
    foreach ($wid in @($script:pendingConclude.Keys)) {
        if (@($board.openWindows) -contains $wid) { continue }
        $askedAt = $script:pendingConclude[$wid]
        $concludeResults += [pscustomobject]@{ Token = $wid; AskedAtCycle = $askedAt; ClosedByCycle = $c; Outcome = 'closed' }
        Say "    $wid window is GONE as of cycle $c (asked at $askedAt)" 'Green'
        $script:pendingConclude.Remove($wid)
    }

    # ---- 2. collect: compare what it says it took with what was there ---------------------
    $outs = @(Get-OutputTokens $board)
    if ($outs.Count -gt 0) {
        $r = Invoke-Api POST /action/collect '{}'
        $collected = @($r.Json.collected).Count
        $candidates = $r.Json.candidates
        $collectResults += [pscustomobject]@{ Waiting = $outs.Count; Candidates = $candidates; Collected = $collected; Success = $r.Json.success }
        Say "[$c] collect: waiting=$($outs.Count) candidates=$candidates collected=$collected success=$($r.Json.success)"
        if ($r.Json.failed) { Say "    failed: $($r.Json.failed -join ', ')" 'Yellow' }
        Start-Sleep -Seconds 3

        # ---- 1. placement: do tabletop cards occupy distinct positions? -------------------
        $after = Get-Board
        $coords = @($after.tokens | Where-Object { $_.spherePath -eq '~/tabletop' -and $_.position } |
            ForEach-Object { "{0}|{1}" -f [math]::Round($_.position.x), [math]::Round($_.position.y) })
        $cards = $coords.Count
        $distinct = ($coords | Sort-Object -Unique).Count
        $origin = @($coords | Where-Object { $_ -eq '0|0' }).Count
        $placementResults += [pscustomobject]@{ Cards = $cards; Distinct = $distinct; AtOrigin = $origin }
        Say "    placement: tabletop cards=$cards distinct positions=$distinct at-origin=$origin"
    }

    # ---- give a verb something to do ------------------------------------------------------
    $pair = Find-Pairing $board
    if ($pair) {
        $r = Invoke-Api POST /action/place (@{ token = $pair.Card; slot = $pair.Slot } | ConvertTo-Json -Compress)
        if ($r.Json.accepted) {
            Start-Sleep -Seconds 2
            $r2 = Invoke-Api POST /action/start (@{ token = $pair.VerbId } | ConvertTo-Json -Compress)
            Say "[$c] place $($pair.Card) + start $($pair.VerbId) changed=$($r2.Json.changed)"
        }
    }

    Start-Sleep -Seconds 5
}

# --- verdict --------------------------------------------------------------------------------
Say ''
Say '=========== VERDICT ===========' 'Cyan'

if ($placementResults.Count -gt 0) {
    $best = $placementResults | Sort-Object -Property { $_.Cards - $_.Distinct } | Select-Object -First 1
    Say "1) AUTO-PLACEMENT  cards=$($best.Cards) distinct=$($best.Distinct) at-origin=$($best.AtOrigin)"
    if ($best.Distinct -eq $best.Cards) { Say '   PASS - every card has its own position' 'Green' }
    else { Say "   FAIL - $($best.Cards - $best.Distinct) card(s) share a position" 'Red' }
} else { Say '1) AUTO-PLACEMENT  no samples' 'Yellow' }

if ($collectResults.Count -gt 0) {
    $honest = @($collectResults | Where-Object { $_.Candidates -eq $_.Collected -or $_.Success -eq $false })
    $mismatch = @($collectResults | Where-Object { $_.Candidates -ne $_.Collected -and $_.Success -ne $false })
    Say "2) COLLECT         $($collectResults.Count) call(s)"
    foreach ($x in $collectResults) { Say "   waiting=$($x.Waiting) candidates=$($x.Candidates) collected=$($x.Collected) success=$($x.Success)" }
    if ($mismatch.Count -eq 0) { Say '   PASS - count always matches candidates, or it reports failure' 'Green' }
    else { Say "   FAIL - $($mismatch.Count) call(s) claimed success while not taking everything" 'Red' }
} else { Say '2) COLLECT         no samples' 'Yellow' }

if ($concludeResults.Count -gt 0) {
    $closed = @($concludeResults | Where-Object { $_.Outcome -eq 'closed' })
    $never = @($concludeResults | Where-Object { $_.Outcome -ne 'closed' })
    Say "3) CONCLUDE        $($closed.Count) window(s) confirmed closed by a later poll"
    foreach ($x in $closed) { Say "   $($x.Token): asked at cycle $($x.AskedAtCycle), gone by cycle $($x.ClosedByCycle)" }
    foreach ($x in $never) { Say "   $($x.Token): $($x.Outcome)" 'Yellow' }
    if ($never.Count -eq 0) { Say '   PASS - every window left openWindows after Conclude' 'Green' }
    else { Say "   PARTIAL - $($closed.Count) closed, $($never.Count) were never concludable while watched" 'Yellow' }
} else {
    Say '3) CONCLUDE        no window disappeared from openWindows' 'Red'
    if ($script:pendingConclude.Count -gt 0) {
        Say "   still listed as open: $(($script:pendingConclude.Keys) -join ', ')" 'Red'
    }
}
