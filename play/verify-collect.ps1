# Verify the two fixes that matter here:
#   1. conclude closes a verb window and lets the clock run again
#   2. collected cards land at DISTINCT positions instead of one heap
#
#   powershell -ExecutionPolicy Bypass -File verify-collect.ps1 [-Legacy detective] [-Cycles 40]

param(
    [string]$Legacy = 'detective',
    [int]$Cycles = 40
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

function Say { param([string]$m, [string]$c = 'Gray'); Write-Host $m -ForegroundColor $c }

function Positions($board) {
    $board.tokens | Where-Object { $_.spherePath -eq '~/tabletop' -and $_.position } |
        ForEach-Object { "{0}@{1},{2}" -f $_.id, [math]::Round($_.position.x), [math]::Round($_.position.y) }
}

# --- get into a game -----------------------------------------------------------------------
$sc = (Invoke-Api GET /screen).Json
if ($sc.tabletopHasCards) {
    Say 'leaving the current game...' 'Yellow'
    $null = Invoke-Api POST /action/menu '{}'
    for ($i = 0; $i -lt 12; $i++) { Start-Sleep -Seconds 3; if (-not (Invoke-Api GET /screen).Json.tabletopHasCards) { break } }
}
$before = @((Invoke-Api GET /state).Json.tokens | ForEach-Object { $_.id } | Sort-Object)
Say "newgame $Legacy ..."
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
$null = Invoke-Api POST /action/speed '{"speed":"VeryFast"}'   # hurry the timers along

$collectedCount = 0
$stackCheck = $null
$script:seen = @{}

for ($c = 1; $c -le $Cycles; $c++) {
    $board = Get-Board
    if (-not $board) { break }

    # keep the clock alive
    if ((Get-Playback $board).paused) { $null = Invoke-Api POST /action/unpause '{}' }

    # --- collect anything produced ---------------------------------------------------------
    $outs = @(Get-OutputTokens $board)
    if ($outs.Count -gt 0) {
        $beforePos = Positions $board
        Say "[$c] collecting $($outs.Count) card(s): $(($outs | ForEach-Object { $_.TokenId }) -join ', ')"
        # use the dedicated endpoint: it places each card at a free position and groups stacks
        $r = Invoke-Api POST /action/collect '{}'
        Say "    collect -> $($r.Status) $($r.OneLine)"
        $collectedCount += $outs.Count
        Start-Sleep -Seconds 4
        $after = Get-Board
        $pos = @(Positions $after)
        Say '    positions now:'
        $pos | ForEach-Object { Say "      $_" }

        # the heap test: on a board with several cards, distinct positions mean no piling
        $coords = $after.tokens | Where-Object { $_.spherePath -eq '~/tabletop' -and $_.position } |
            ForEach-Object { "{0}|{1}" -f [math]::Round($_.position.x), [math]::Round($_.position.y) }
        $distinct = ($coords | Sort-Object -Unique).Count
        $total = @($coords).Count
        $stackCheck = [pscustomobject]@{ Cards = $total; DistinctPositions = $distinct }
        Say "    tabletop cards=$total distinct positions=$distinct"
    }

    # --- close any window that has been open a while --------------------------------------
    foreach ($w in @($board.openWindows)) {
        $wid = "$w"
        if (-not $script:seen.ContainsKey($wid)) { $script:seen[$wid] = Get-Date; continue }
        if (((Get-Date) - $script:seen[$wid]).TotalSeconds -lt 10) { continue }
        $r = Invoke-Api POST /action/conclude (@{ token = $wid } | ConvertTo-Json -Compress)
        Say "[$c] conclude $wid -> $($r.Status) stillOpen=$($r.Json.isOpenAfter)"
        $script:seen.Remove($wid)
    }

    # --- feed a verb if we can -------------------------------------------------------------
    $pair = Find-Pairing $board
    if ($pair) {
        $r = Invoke-Api POST /action/place (@{ token = $pair.Card; slot = $pair.Slot } | ConvertTo-Json -Compress)
        if ($r.Json.accepted) {
            Start-Sleep -Seconds 2
            $r2 = Invoke-Api POST /action/start (@{ token = $pair.VerbId } | ConvertTo-Json -Compress)
            Say "[$c] place $($pair.Card) + start $($pair.VerbId) -> changed=$($r2.Json.changed) recipe=$($r2.Json.recipeAfter)"
        }
    }

    Start-Sleep -Seconds 6
}

Say ''
Say '=== result ===' 'Cyan'
$b = Get-Board
Say "collected this session: $collectedCount cards"
if ($stackCheck) { Say "tabletop cards=$($stackCheck.Cards) distinct positions=$($stackCheck.DistinctPositions)" }
Say ''
Say 'tabletop layout:'
$b.tokens | Where-Object { $_.spherePath -eq '~/tabletop' } | ForEach-Object {
    Say ("  {0,-26} {1,-14} qty={2,-3} pos={3}" -f $_.id, $_.label, $_.quantity, ($_.position | ConvertTo-Json -Compress))
}
Say ''
Say "openWindows=[$($b.openWindows -join ',')]  playback=$($b.playback | ConvertTo-Json -Compress)"
