# Shared helpers for driving LLM_API: HTTP, board reading, and a policy for legal moves.
#
# Everything here is written for an external agent to use, so the pieces are deliberately
# explicit: read the board, find legal pairings using the game's own aspect rule, act, and
# verify. Dot-source it:
#
#   . .\lib.ps1
#   $s = Get-Board
#   Invoke-Api POST /action/place (@{token='!x'; slot='~/tabletop!work_0/work'} | ConvertTo-Json -Compress)

$ErrorActionPreference = 'Stop'

$script:ApiBase = '127.0.0.1:8710'

function Invoke-Api {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Body
    )

    $parts = $script:ApiBase.Split(':')
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect($parts[0], [int]$parts[1])
    }
    catch {
        # A driver should be able to probe without crashing when the game is simply not running.
        $client.Close()
        return [pscustomobject]@{
            Status  = 0
            Body    = ''
            Json    = $null
            OneLine = "cannot reach the mod at $($script:ApiBase) - is the game running with LLM_API enabled?"
        }
    }
    $stream = $client.GetStream()

    $payload = $null
    if ($Body) {
        $payload = [Text.Encoding]::UTF8.GetBytes($Body)
        $head = "$Method $Path HTTP/1.1`r`nHost: $($script:ApiBase)`r`nContent-Type: application/json`r`nContent-Length: $($payload.Length)`r`nConnection: close`r`n`r`n"
    } else {
        $head = "$Method $Path HTTP/1.1`r`nHost: $($script:ApiBase)`r`nConnection: close`r`n`r`n"
    }
    $headBytes = [Text.Encoding]::ASCII.GetBytes($head)
    $stream.Write($headBytes, 0, $headBytes.Length)
    if ($payload) { $stream.Write($payload, 0, $payload.Length) }
    $stream.Flush()

    Start-Sleep -Milliseconds 700
    $buffer = New-Object byte[] 262144
    $ms = New-Object System.IO.MemoryStream
    while (($n = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) { $ms.Write($buffer, 0, $n) }
    $client.Close()

    $raw = [Text.Encoding]::UTF8.GetString($ms.ToArray())
    $split = $raw.IndexOf("`r`n`r`n")

    # Split the response explicitly rather than inside one long expression: a nested
    # $(try { ... | ConvertFrom-Json } catch { $null }) misbehaved when this ran from a script
    # file, returning an object with no properties.
    $status = 0
    $body = ''
    $json = $null
    if ($split -ge 0) { $body = $raw.Substring($split + 4) }
    $firstLine = $raw.Substring(0, $raw.IndexOf("`r`n"))
    $bits = $firstLine.Split(' ')
    if ($bits.Length -ge 2) { $status = [int]$bits[1] }
    if ($body.Length -gt 0) {
        try { $json = $body | ConvertFrom-Json } catch { $json = $null }
    }

    [pscustomobject]@{
        Status  = $status
        Body    = $body
        Json    = $json
        OneLine = ($body -replace '\s+', ' ')
    }
}

function Get-Board { (Invoke-Api GET /state).Json }

function Get-Aspects($aspects) {
    $set = @{}
    if ($aspects) { $aspects.PSObject.Properties | ForEach-Object { $set[$_.Name] = [int]$_.Value } }
    return $set
}

# Every empty card frame, with the aspects it will accept. Mirrors what /state already reports
# so a caller never has to guess at a slot's rule.
function Get-OpenSlots($board) {
    $out = @()
    foreach ($t in @($board.tokens | Where-Object { $_.payloadType -eq 'Situation' })) {
        foreach ($slot in @($t.slots)) {
            if ($slot.type -ne 'ThresholdSphere') { continue }
            if (@($slot.tokenIds).Count -gt 0) { continue }
            $sphere = @($board.spheres | Where-Object { $_.path -eq $slot.path })
            if ($sphere.Count -eq 0) { continue }
            $out += [pscustomobject]@{
                VerbId    = $t.id
                Verb      = $t.verbId
                Path      = $slot.path
                SpecId    = $slot.specId
                Required  = @($sphere[0].required.PSObject.Properties | ForEach-Object { $_.Name })
                Forbidden = @($sphere[0].forbidden.PSObject.Properties | ForEach-Object { $_.Name })
            }
        }
    }
    return $out
}

# Cards lying on the tabletop, which are the ones a player can pick up.
function Get-TabletopCards($board) {
    $out = @()
    foreach ($t in @($board.tokens | Where-Object { $_.payloadType -eq 'ElementStack' -and $_.spherePath -eq '~/tabletop' })) {
        $out += [pscustomobject]@{
            Id      = $t.id
            Label   = $t.label
            Entity  = $t.entityId
            Qty     = $t.quantity
            Aspects = (Get-Aspects $t.aspects)
        }
    }
    return $out
}

# The game's own rule: carry at least one required aspect, carry no forbidden one.
function Test-Fits($card, $slot) {
    foreach ($f in $slot.Forbidden) { if ($card.Aspects.ContainsKey($f)) { return $false } }
    foreach ($r in $slot.Required) { if ($card.Aspects.ContainsKey($r)) { return $true } }
    return $false
}

# The first legal (card, slot) pairing available right now, or $null.
function Find-Pairing($board) {
    foreach ($slot in (Get-OpenSlots $board)) {
        foreach ($card in (Get-TabletopCards $board)) {
            if (Test-Fits $card $slot) {
                return [pscustomobject]@{ Card = $card.Id; Label = $card.Label; Slot = $slot.Path; VerbId = $slot.VerbId; Verb = $slot.Verb }
            }
        }
    }
    return $null
}

# Cards parked in a verb's output frame. Moving these to the tabletop is what "collect" means.
function Get-OutputTokens($board) {
    $out = @()
    foreach ($t in @($board.tokens | Where-Object { $_.payloadType -eq 'Situation' })) {
        foreach ($slot in @($t.slots)) {
            if ($slot.type -ne 'OutputSphere') { continue }
            foreach ($id in @($slot.tokenIds)) {
                $out += [pscustomobject]@{ VerbId = $t.id; TokenId = $id; Path = $slot.path }
            }
        }
    }
    return $out
}

function Get-Playback($board) {
    if ($board -and $board.playback) { return $board.playback }
    return $null
}

# Close every situation window in one call. Situation.Close() has no "must be finished" gate
# (unlike Conclude(), which returns early unless the state IsConcludable), and the game re-opens
# a default window by itself once a work verb finishes - so a driver should call this every
# cycle rather than concluding once and assuming the screen is clear.
function Clear-Windows {
    return Invoke-Api POST /action/closewindows '{}'
}
