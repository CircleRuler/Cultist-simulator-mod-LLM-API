# An autonomous session driver for LLM_API - the mod exists so an external agent can play
# Cultist Simulator, so this is the reference loop such an agent would run.
#
#   powershell -ExecutionPolicy Bypass -File play-session.ps1                  # 10 minutes
#   powershell -ExecutionPolicy Bypass -File play-session.ps1 -Minutes 3
#   powershell -ExecutionPolicy Bypass -File play-session.ps1 -NewGame detective
#
# Each cycle it:
#   1. reads the board
#   2. resumes the clock if it is paused   (continue/load leaves the game paused)
#   3. collects cards sitting in a verb's output frame
#   4. makes one legal place/start
#   5. reports anything the API could not express - that is the point of the exercise

param(
    [int]$Minutes = 10,
    [string]$NewGame,
    [int]$CycleSeconds = 20,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

$deadline = (Get-Date).AddMinutes($Minutes)
$acts = @()
$gaps = New-Object System.Collections.Generic.List[string]
$script:windowSeen = @{}

function Say { param([string]$m, [string]$c = 'Gray'); Write-Host $m -ForegroundColor $c }

function Record { param($Board, [string]$Step, $Result)
    $script:acts += [pscustomobject]@{ At = (Get-Date -Format 'HH:mm:ss'); Step = $Step; Status = $Result.Status; Note = $Result.OneLine }
}

# In dry-run mode every mutating call is reported instead of sent, so a board can be inspected
# without changing it.
function Act {
    param([string]$Method, [string]$Path, [string]$Body)
    if ($DryRun) {
        Say "    [dry-run] $Method $Path $(if ($Body) { $Body })" 'DarkGray'
        return [pscustomobject]@{ Status = 0; Body = ''; Json = $null; OneLine = '(dry run)' }
    }
    return Invoke-Api -Method $Method -Path $Path -Body $Body
}

$probe = Invoke-Api GET /ping
if ($probe.Status -ne 200) {
    Say $probe.OneLine 'Red'
    exit 1
}

Say "=== LLM_API autonomous session: $Minutes minute(s), cycle ${CycleSeconds}s ===" 'Cyan'
Say "deadline $($deadline.ToString('HH:mm:ss'))"
Say ''

# Optionally start a fresh game first.
if ($NewGame) {
    $sc = (Invoke-Api GET /screen).Json
    if ($sc.tabletopHasCards) {
        Say 'leaving the current game to start a new one...' 'Yellow'
        $r = Act POST /action/menu '{}'
        Record $null 'menu' $r
        for ($i = 0; $i -lt 10; $i++) { Start-Sleep -Seconds 3; if (-not (Invoke-Api GET /screen).Json.tabletopHasCards) { break } }
    }

    # A fresh game must be proven by the board CHANGING, not by "a tabletop exists": a game
    # already loaded also has a tabletop, so presence alone proves nothing. Capture the old
    # signature and require it to differ.
    $before = @()
    $pre = (Invoke-Api GET /state).Json
    if ($pre) { $before = @($pre.tokens | ForEach-Object { $_.id } | Sort-Object) }

    $r = Act POST /action/newgame (@{ legacy = $NewGame } | ConvertTo-Json -Compress)
    Record $null 'newgame' $r
    Say "newgame $NewGame -> $($r.Status)"

    $confirmed = $false
    for ($i = 0; $i -lt 15; $i++) {
        Start-Sleep -Seconds 3
        $b = (Invoke-Api GET /state).Json
        if (-not $b -or -not $b.tabletop.exists) { continue }
        $now = @($b.tokens | ForEach-Object { $_.id } | Sort-Object)
        if ($before.Count -gt 0 -and (($now -join ',') -eq ($before -join ','))) {
            Say "    [$((($i + 1) * 3))s] still the same board ($($now.Count) tokens); waiting" 'DarkGray'
            continue
        }
        $confirmed = $true
        Say "    [$((($i + 1) * 3))s] fresh game confirmed: $($before.Count) -> $($now.Count) tokens" 'Green'
        break
    }
    if (-not $confirmed) { $gaps.Add('newgame did not visibly replace the board within 45s') }
}

$cycle = 0
while ((Get-Date) -lt $deadline) {
    $cycle++
    $board = Get-Board
    if (-not $board) { Say "[$cycle] no board; waiting" 'DarkGray'; Start-Sleep -Seconds $CycleSeconds; continue }

    $pb = Get-Playback $board
    $verbs = @($board.tokens | Where-Object { $_.payloadType -eq 'Situation' })
    $cards = @(Get-TabletopCards $board)
    $running = @($verbs | Where-Object { $_.timeRemaining -gt 0 })
    Say ("[{0}] cards={1} verbs={2} running={3} paused={4} speed={5}" -f `
        $cycle, $cards.Count, $verbs.Count, $running.Count, $pb.paused, $pb.gameSpeed)

    # 2. keep the clock running - a paused game looks exactly like a slow one
    if ($pb.paused) {
        $r = Act POST /action/unpause '{}'
        Record $board 'unpause' $r
        Say "    unpaused -> $($r.Status) $(if ($r.Json) { $r.Json.gameSpeed })" 'Yellow'
        $board = Get-Board
    }

    # 3. clear the board of windows EVERY cycle. The game re-opens a default situation window by
    # itself once a work verb finishes, so closing one window once is not enough - that is how a
    # window ends up permanently covering the tabletop.
    if (@($board.openWindows).Count -gt 0) {
        $r = Act POST /action/closewindows '{}'
        Record $board 'closewindows' $r
        Say "    closewindows: wereOpen=[$($r.Json.wereOpen -join ', ')] closed=$($r.Json.closedCount)"
        $board = Get-Board
    }

    # 3b. also conclude finished verbs, which additionally evicts their output frames
    foreach ($w in @($board.openWindows)) {
        $wid = "$w"
        $sit = $board.tokens | Where-Object { $_.id -eq $wid }
        if (-not $sit.concludable) { continue }
        $r = Act POST /action/conclude (@{ token = $wid } | ConvertTo-Json -Compress)
        Record $board "conclude $wid" $r
        Say "    conclude $wid (state=$($sit.situationState)) -> $($r.Status) queued=$($r.Json.queued)"
        $board = Get-Board
    }

    # 4. collect anything left in an output frame
    foreach ($o in @(Get-OutputTokens $board)) {
        $r = Act POST /action/move (@{ token = $o.TokenId; to = '~/tabletop' } | ConvertTo-Json -Compress)
        Record $board "collect $($o.TokenId)" $r
        Say "    collect $($o.TokenId) from $($o.VerbId) -> $($r.Status) via=$($r.Json.via) grouped=$($r.Json.groupedStacks)"
    }

    # 5. one legal place/start
    $board = Get-Board
    $pair = Find-Pairing $board
    if ($pair) {
        $r = Act POST /action/place (@{ token = $pair.Card; slot = $pair.Slot } | ConvertTo-Json -Compress)
        Record $board "place $($pair.Card)" $r
        Say "    place $($pair.Card) [$($pair.Label)] -> $($pair.Slot) : $($r.Status) accepted=$($r.Json.accepted)"
        if ($r.Json.accepted) {
            Start-Sleep -Seconds 2
            $r2 = Act POST /action/start (@{ token = $pair.VerbId } | ConvertTo-Json -Compress)
            Record $board "start $($pair.VerbId)" $r2
            Say "    start $($pair.VerbId) : $($r2.Status) $(if ($r2.Json) { "$($r2.Json.stateBefore) -> $($r2.Json.stateAfter) recipe=$($r2.Json.recipeAfter)" })"
        }
    } else {
        $openSlots = @(Get-OpenSlots $board)
        if ($openSlots.Count -eq 0) {
            Say '    nothing to do: no open card frame' 'DarkGray'
        } else {
            Say '    nothing to do: open frame(s) but no card fits them' 'DarkGray'
            foreach ($s in $openSlots) { Say "      $($s.Path) wants [$($s.Required -join ',')]" 'DarkGray' }
            if ($cards.Count -eq 0) { $gaps.Add('a frame is open but no card on the tabletop can fill it, and nothing invites new cards') }
        }
    }

    # 5. surface anything the API could not express
    $b2 = Get-Board
    if ($b2) {
        if ($b2.openWindows -and @($b2.openWindows).Count -gt 0) { Say "    open windows: $($b2.openWindows -join ', ')" 'DarkGray' }
        if ($b2.tokensInTransit -and @($b2.tokensInTransit).Count -gt 0) { Say "    in transit: $($b2.tokensInTransit -join ', ')" 'DarkGray' }
    }

    Start-Sleep -Seconds $CycleSeconds
}

Say ''
Say '=== session summary ===' 'Cyan'
$verbs = @((Get-Board).tokens | Where-Object { $_.payloadType -eq 'Situation' })
Say "actions taken: $($acts.Count)"
$acts | Group-Object { $_.Step -replace '\s.*', '' } | ForEach-Object { Say ("  {0,-10} x{1}" -f $_.Name, $_.Count) }
Say ''
Say 'verbs at the end:'
foreach ($v in $verbs) { Say ("  {0,-14} {1,-10} recipe={2}" -f $v.id, $v.situationState, $v.currentRecipeId) }
if ($gaps.Count -gt 0) {
    Say ''
    Say 'possible gaps noticed:' 'Yellow'
    $gaps | Sort-Object -Unique | ForEach-Object { Say "  - $_" 'Yellow' }
}
