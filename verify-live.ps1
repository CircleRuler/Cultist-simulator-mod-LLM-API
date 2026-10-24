# Live verification of the cs_gameapi endpoints against a real running game.
#
#   powershell -ExecutionPolicy Bypass -File verify-live.ps1 [-Base http://127.0.0.1:8710]
#
# Uses a raw socket rather than Invoke-RestMethod: PowerShell's HTTP client buries the JSON
# error bodies that matter most here, and we need the status line plus the body verbatim.

[CmdletBinding()]
param(
    [string]$Base = '127.0.0.1:8710'
)

$ErrorActionPreference = 'Stop'
$script:pass = 0
$script:fail = 0

function Invoke-Api {
    param([string]$Method, [string]$Path, [string]$Json)

    $parts = $Base.Split(':')
    $client = New-Object System.Net.Sockets.TcpClient
    $client.Connect($parts[0], [int]$parts[1])
    $stream = $client.GetStream()

    $head = "$Method $Path HTTP/1.1`r`nHost: $Base`r`nAccept: application/json`r`nConnection: close`r`n"
    $bodyBytes = $null
    if ($Json) {
        $bodyBytes = [Text.Encoding]::UTF8.GetBytes($Json)
        $head += "Content-Type: application/json`r`nContent-Length: $($bodyBytes.Length)`r`n"
    }
    $head += "`r`n"

    $headBytes = [Text.Encoding]::ASCII.GetBytes($head)
    $stream.Write($headBytes, 0, $headBytes.Length)
    if ($bodyBytes) { $stream.Write($bodyBytes, 0, $bodyBytes.Length) }
    $stream.Flush()

    Start-Sleep -Milliseconds 500
    $buffer = New-Object byte[] 65536
    $ms = New-Object System.IO.MemoryStream
    while (($n = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) { $ms.Write($buffer, 0, $n) }
    $client.Close()

    $raw = [Text.Encoding]::UTF8.GetString($ms.ToArray())
    $split = $raw.IndexOf("`r`n`r`n")
    $statusLine = $raw.Substring(0, $raw.IndexOf("`r`n"))
    $status = [int]($statusLine.Split(' ')[1])
    $bodyText = if ($split -ge 0) { $raw.Substring($split + 4) } else { '' }
    $parsed = $null
    try { $parsed = $bodyText | ConvertFrom-Json } catch { }

    [pscustomobject]@{ Status = $status; Body = $bodyText; Json = $parsed }
}

function Check {
    param([string]$What, [bool]$Ok, [string]$Detail)
    if ($Ok) { $script:pass++; Write-Host "  PASS  $What" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  FAIL  $What" -ForegroundColor Red; if ($Detail) { Write-Host "        $Detail" -ForegroundColor DarkGray } }
}

function Get-State { (Invoke-Api -Method GET -Path '/state').Json }

function Condensed($state) {
    "spheres=$($state.sphereCount) tokens=$($state.tokenCount)"
}

Write-Host ''
Write-Host '=== 0. liveness ===' -ForegroundColor Cyan
$ping = Invoke-Api -Method GET -Path '/ping'
Check 'GET /ping -> 200' ($ping.Status -eq 200) $ping.Body
Check '/ping reports the service' ($ping.Json.service -eq 'LLM_API') $ping.Body

Write-Host ''
Write-Host '=== 1. read state ===' -ForegroundColor Cyan
$before = Get-State
Check 'GET /state -> readable' ($before -ne $null) 'no JSON returned'
Write-Host "        $(Condensed $before)"

$tabletop = $before.spheres | Where-Object { $_.path -eq '~/tabletop' }
Check 'a tabletop sphere exists' ($tabletop -ne $null)

$cards = @($before.tokens | Where-Object { $_.spherePath -eq '~/tabletop' -and $_.payloadType -eq 'ElementStack' })
Check 'tabletop holds at least one card' ($cards.Count -ge 1) "found $($cards.Count)"

# Match a card to a slot the way the game itself does: the card has to carry at least one of
# the slot's required aspects and none of its forbidden ones. Placing an aspect-invalid card is
# correctly refused by the game, so testing with one would prove nothing.
function Get-AspectSet($aspects) {
    $set = @{}
    if ($aspects) { $aspects.PSObject.Properties | ForEach-Object { $set[$_.Name] = [int]$_.Value } }
    return $set
}

$emptySlot = $null
$slotOwner = $null
$chosenCard = $null
$anyEmptySlot = $false
foreach ($s in ($before.tokens | Where-Object { $_.payloadType -eq 'Situation' -and $_.slots })) {
    foreach ($slot in $s.slots) {
        if (-not $slot.empty -or $slot.tokenIds.Count -gt 0) { continue }
        $sphere = $before.spheres | Where-Object { $_.path -eq $slot.path }
        if (-not $sphere -or $sphere.type -ne 'ThresholdSphere') { continue }

        $required = Get-AspectSet $sphere.required
        $forbidden = Get-AspectSet $sphere.forbidden
        if ($required.Count -eq 0) { continue }
        $anyEmptySlot = $true

        foreach ($candidate in $cards) {
            $aspects = Get-AspectSet $candidate.aspects
            $hasRequired = $false
            foreach ($r in $required.Keys) { if ($aspects.ContainsKey($r)) { $hasRequired = $true; break } }
            $hasForbidden = $false
            foreach ($f in $forbidden.Keys) { if ($aspects.ContainsKey($f)) { $hasForbidden = $true; break } }
            if ($hasRequired -and -not $hasForbidden) {
                $emptySlot = $slot.path; $slotOwner = $s.id; $chosenCard = $candidate.id
                break
            }
        }
        if ($emptySlot) { break }
    }
    if ($emptySlot) { break }
}

# An open slot that nothing can legally fill is a legal board state (a dream slot wanting
# "lore" with no lore card in play), so it is reported, not failed. What would be a genuine
# fault is the matcher itself finding nothing while a card plainly does satisfy a slot - that
# is covered by the offline harness, which controls the board.
if ($anyEmptySlot -and -not $emptySlot) {
    Write-Host '  INFO  an empty slot is open but no card on the board satisfies it' -ForegroundColor DarkGray
    foreach ($s in ($before.tokens | Where-Object { $_.payloadType -eq 'Situation' -and $_.slots })) {
        foreach ($slot in $s.slots) {
            if (-not $slot.empty -or $slot.tokenIds.Count -gt 0) { continue }
            $sphere = $before.spheres | Where-Object { $_.path -eq $slot.path }
            if ($sphere -and $sphere.type -eq 'ThresholdSphere') {
                $rq = ($sphere.required.PSObject.Properties | ForEach-Object { $_.Name }) -join '|'
                Write-Host "        slot $($slot.path) wants [$rq]" -ForegroundColor DarkGray
            }
        }
    }
    $inPlay = ($cards | ForEach-Object { $_.id + ':[' + (($_.aspects.PSObject.Properties | ForEach-Object { $_.Name }) -join ',') + ']' }) -join '  '
    Write-Host "        in play: $inPlay" -ForegroundColor DarkGray
}

$script:skipped = @()
if (-not $emptySlot -or -not $chosenCard) {
    Write-Host ''
    Write-Host '  SKIP  place/start/move: this board has no empty slot that any available card satisfies.' -ForegroundColor Yellow
    Write-Host '        (Not a fault: e.g. a dream "lore=1|waywood=1" slot with no lore card on the board.)' -ForegroundColor DarkGray
    $script:skipped += 'place', 'start', 'move-settle'
} else {
    $card = $chosenCard
    Write-Host "        placing '$card' into '$emptySlot' (verb $slotOwner), aspects matched against the slot"

    Write-Host ''
    Write-Host '=== 2. PLACE a card into a card slot ===' -ForegroundColor Cyan
    $place = Invoke-Api -Method POST -Path '/action/place' -Json (@{ token = $card; slot = $emptySlot } | ConvertTo-Json -Compress)
    Check 'POST /action/place -> 200' ($place.Status -eq 200) "$($place.Status) $($place.Body)"
    Check 'place reports accepted=true' ($place.Json.accepted -eq $true) $place.Body
    Check 'place did not hand back a NullElementStack token' ($place.Json.error -notmatch 'NullElementStack') $place.Body

    $afterPlace = Get-State
    $moved = $afterPlace.tokens | Where-Object { $_.id -eq $card }
    Check 'the card really moved into the slot' ($moved.spherePath -eq $emptySlot) "card is at $($moved.spherePath), expected $emptySlot"

    $slotNow = $null
    foreach ($s in ($afterPlace.tokens | Where-Object { $_.id -eq $slotOwner })) {
        foreach ($slot in $s.slots) { if ($slot.path -eq $emptySlot) { $slotNow = $slot } }
    }
    Check 'the slot now reports the card as its occupant' ($slotNow -and ($slotNow.tokenIds -contains $card)) "slot tokenIds=$(if($slotNow){$slotNow.tokenIds -join ','}else{'<slot missing>'})"

    Write-Host ''
    Write-Host '=== 3. START a verb ===' -ForegroundColor Cyan
    $target = $before.tokens | Where-Object { $_.payloadType -eq 'Situation' -and $_.id -eq $slotOwner }
    Check "the target is a Situation ($slotOwner)" ($target -ne $null) "payloadType=$($target.payloadType)"

    $start = Invoke-Api -Method POST -Path '/action/start' -Json (@{ token = $slotOwner } | ConvertTo-Json -Compress)
    Check 'POST /action/start -> 200' ($start.Status -eq 200) "$($start.Status) $($start.Body)"
    Check 'start resolved a real Situation (not NullElementStack)' ($start.Json.error -notmatch 'NullElementStack') $start.Body
    Check 'start reported the verb id' ($start.Json.verbId -eq $target.verbId) "verbId=$($start.Json.verbId) expected=$($target.verbId)"
    Check 'start returned a clean state name' ($start.Json.stateAfter -notmatch 'SecretHistories') "stateAfter=$($start.Json.stateAfter)"
    Check 'start reports a valid situation state' ($start.Json.stateAfter -in @('Unstarted', 'Ongoing', 'Complete')) "stateAfter=$($start.Json.stateAfter)"
    Check 'the game gave the started verb a recipe' ($start.Json.recipeAfter -ne 'nullrecipe') "recipeAfter=$($start.Json.recipeAfter)"

    Write-Host ''
    Write-Host '=== 4. MOVE a card ===' -ForegroundColor Cyan
    $moveAway = Get-State
    $targetCard = ($moveAway.tokens | Where-Object { $_.id -eq $card }).spherePath
    $destination = if ($targetCard -eq '~/tabletop') { $emptySlot } else { '~/tabletop' }
    $move = Invoke-Api -Method POST -Path '/action/move' -Json (@{ token = $card; to = $destination } | ConvertTo-Json -Compress)
    Check 'POST /action/move -> 200' ($move.Status -eq 200) "$($move.Status) $($move.Body)"
    Check 'move reports success' ($move.Json.success -eq $true) $move.Body
    Check 'move resolved a real card (not NullElementStack)' ($move.Json.error -notmatch 'NullElementStack') $move.Body

    $afterMove = Get-State
    $back = $afterMove.tokens | Where-Object { $_.id -eq $card }

    # A moved token travels through the en-route sphere while its animation plays, so give it a
    # moment to settle rather than asserting on a mid-flight position.
    $waited = 0
    while ($back.spherePath -eq '~/enroute' -and $waited -lt 15) {
        Start-Sleep -Seconds 1
        $waited++
        $afterMove = Get-State
        $back = $afterMove.tokens | Where-Object { $_.id -eq $card }
    }
    Check "the card settled at $destination" ($back.spherePath -eq $destination) "card is at $($back.spherePath) after ${waited}s"

    Write-Host ''
    Write-Host '=== 5. CLICK a card ===' -ForegroundColor Cyan
    $click = Invoke-Api -Method POST -Path '/action/click' -Json (@{ token = $card } | ConvertTo-Json -Compress)
    Check 'POST /action/click -> 200' ($click.Status -eq 200) "$($click.Status) $($click.Body)"
    Check 'click resolved a real card (not NullElementStack)' ($click.Json.payloadType -ne 'NullElementStack') $click.Body
}

Write-Host ''
Write-Host '=== 6. /state shape ===' -ForegroundColor Cyan
$shape = Get-State
Check '/state reports the root sphere path' ($shape.rootSpherePath -eq '~') "rootSpherePath=$($shape.rootSpherePath)"
Check '/state reports in-transit tokens explicitly' ($shape.PSObject.Properties.Name -contains 'tokensInTransit') 'tokensInTransit missing'

Write-Host ''
Write-Host '=== 7. error handling ===' -ForegroundColor Cyan
$bad = Invoke-Api -Method POST -Path '/action/start' -Json '{"token":"!definitely_not_real"}'
Check 'unknown token -> 404' ($bad.Status -eq 404) "$($bad.Status) $($bad.Body)"

$nonVerb = Invoke-Api -Method POST -Path '/action/start' -Json '{"token":"!funds_12"}'
Check 'starting a non-verb -> 409' ($nonVerb.Status -eq 409) "$($nonVerb.Status) $($nonVerb.Body)"

# The catalogue answers an unknown path with a fallback sphere, so this must be caught by
# comparing the resolved path rather than by a null check.
$nonSphere = Invoke-Api -Method POST -Path '/action/place' -Json '{"token":"!funds_12","slot":"~/no_such_place"}'
Check 'unknown sphere path -> 404' ($nonSphere.Status -eq 404) "$($nonSphere.Status) $($nonSphere.Body)"
Check 'the 404 explains which sphere it resolved to' ($nonSphere.Body -match 'resolved it to') $nonSphere.Body

$nested = Invoke-Api -Method POST -Path '/action/move' -Json '{"token":"!funds_12","to":"~/tabletop!no_such_verb/nowhere"}'
Check 'unknown nested sphere path -> 404' ($nested.Status -eq 404) "$($nested.Status) $($nested.Body)"

$badJson = Invoke-Api -Method POST -Path '/action/place' -Json '{oops'
Check 'malformed JSON -> 400' ($badJson.Status -eq 400) "$($badJson.Status) $($badJson.Body)"

Write-Host ''
if ($script:skipped.Count -gt 0) {
    Write-Host "skipped (board-dependent): $($script:skipped -join ', ')" -ForegroundColor Yellow
}
if ($script:fail -eq 0) { Write-Host "ALL LIVE CHECKS PASSED (passed=$script:pass failed=0 skipped=$($script:skipped.Count))" -ForegroundColor Green }
else { Write-Host "LIVE CHECKS: passed=$script:pass failed=$script:fail" -ForegroundColor Red }
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
