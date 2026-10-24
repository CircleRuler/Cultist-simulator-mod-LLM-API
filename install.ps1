# Install a built mod DLL into Cultist Simulator's local mods folder.
#
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Name <modid> [-Dll <path>]
#            [-DisplayName ".."] [-GatekeeperId 2901287611] [-Remove]
#
# Layout the game's loader requires (MODDING_README.txt "CUSTOM DLL LOADING"):
#   <LocalLow>/mods/<modid>/synopsis.json     name must equal the mod id
#   <LocalLow>/mods/<modid>/dll/<modid>.dll   dll file name == name with non-alphanumerics stripped
#   <LocalLow>/mods/<modid>/README.md         shipped for whoever finds the folder
#   <LocalLow>/mods.txt                       the game owns this (loaded order / enablement)

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Dll,
    [string]$DisplayName,
    # Workshop category tags. The game hands these straight to SteamUGC.SetItemTags with no
    # validation, and Steam shows a category filter per distinct tag. The five presets the game
    # documents are the only ones anybody browses by:
    #   Art | Balance Changes/Tweaks | Community Translation | Expansion | New Legacy
    # Community-invented tags exist (e.g. "THE HIGHER I RISE THE MORE I SEE") but nobody filters
    # by them, so a value like "Tools" simply makes the mod unfindable in the category list.
    [string[]]$Tags = @('Expansion'),
    # Left empty on purpose. See the dependency note below: a dependency the game judges
    # unsatisfiable does not just warn, it loads the "UH O" crash scene.
    [string]$GatekeeperId = '',
    # The mod's own version, shown in the mod list and in the game log. Keep it in step with
    # GameApiMod.Version, which is what the log and /ping report.
    [string]$Version = '1.0.2',
    [switch]$Disable,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$LocalLow = Join-Path $env:USERPROFILE 'AppData\LocalLow\Weather Factory\Cultist Simulator'
$ModsDir  = Join-Path $LocalLow 'mods'
$ModDir   = Join-Path $ModsDir $Name
$ModsTxt  = Join-Path $LocalLow 'mods.txt'

if (-not (Test-Path $LocalLow)) { throw "game data folder not found: $LocalLow" }
if (-not $Dll) { $Dll = Join-Path $PSScriptRoot "build\$Name\$Name.dll" }

# --- removing a mod ---------------------------------------------------------------------
# Deletes the folder outright. Note that mods.txt is deliberately left alone: the game rewrites
# it from the in-game mod menu, and editing it from outside was measured to desync the enabled
# list from the catalogue.
if ($Remove) {
    if (Test-Path $ModDir) {
        Remove-Item $ModDir -Recurse -Force
        Write-Host "removed mod folder $ModDir" -ForegroundColor Green
    } else {
        Write-Host "mod folder not present: $ModDir" -ForegroundColor Yellow
    }
    if ((Test-Path $ModsTxt) -and ((Get-Content $ModsTxt) -contains $Name)) {
        Write-Host "note: '$Name' is still listed in mods.txt; turn it off in the game's mod menu" -ForegroundColor Yellow
    }
    return
}

# --- mods.txt is the game's file, not ours ---------------------------------------------
# The game owns mods.txt: it rewrites it from the in-game mod menu and then matches the ids
# against its own mod catalogue. Editing it from outside was measured to break that match (the
# game logged "was found in the enabled loading order list, but not in the catalogue"). So this
# script only seeds the file when it does not exist yet, and otherwise leaves enablement alone.
if ($Disable) {
    Write-Host "note: '$Name' is still enabled in $(Split-Path $ModsTxt -Leaf); turn it off in the game's mod menu" -ForegroundColor Yellow
} elseif (-not (Test-Path $ModsTxt)) {
    [System.IO.File]::WriteAllText($ModsTxt, $Name + "`n", (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "seeded mods.txt with '$Name' (enable it in the in-game mod menu)" -ForegroundColor Yellow
} else {
    $current = [System.IO.File]::ReadAllLines($ModsTxt) | Where-Object { $_.Trim() }
    if ($current -notcontains $Name) {
        Write-Host "note: '$Name' is not enabled yet; enable it in the game's mod menu" -ForegroundColor Yellow
    }
}

if (-not $Disable) {
    if (-not (Test-Path $Dll)) { throw "built dll not found: $Dll (build it first)" }

    # Rebuild the mod folder from scratch so nothing from an older layout survives. A stale
    # root-level DLL or half-removed test mod is exactly the kind of thing that makes the
    # loader's silence impossible to diagnose.
    #
    # If the game is running it holds the DLL open, so this Remove-Item fails. Previously the
    # script carried on regardless: it logged "installed ...", wrote the new synopsis and README,
    # and never copied the DLL - leaving a stale module that looked freshly installed. A whole
    # test round was wasted on a build that was never actually deployed. Fail here instead.
    # Steam writes serapeum_catalogue_number.txt into the published mod's own folder. It holds
    # the Workshop item id, and it is how the game knows to UPDATE that item instead of uploading
    # a brand new one. This script rebuilds the folder from scratch, so the file has to be carried
    # across explicitly - deleting it flips the in-game button from "Update" to "Upload" and
    # orphans everything already attached to the published item.
    $steamStamp = Join-Path $ModDir 'serapeum_catalogue_number.txt'
    $steamStampValue = $null
    if (Test-Path $steamStamp) {
        try { $steamStampValue = [System.IO.File]::ReadAllText($steamStamp) } catch { }
    }

    if (Test-Path $ModDir) {
        try {
            Remove-Item $ModDir -Recurse -Force -ErrorAction Stop
        }
        catch {
            Write-Host ''
            Write-Host "REFUSING TO CONTINUE: could not clear $ModDir" -ForegroundColor Red
            Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
            Write-Host ''
            Write-Host "The game is almost certainly still running and holding dll\$Name.dll open." -ForegroundColor Yellow
            Write-Host "Close Cultist Simulator and run this again. Nothing has been changed." -ForegroundColor Yellow
            exit 1
        }
    }
    New-Item -ItemType Directory -Force -Path $ModDir | Out-Null

    if ($steamStampValue -ne $null) {
        [System.IO.File]::WriteAllText($steamStamp, $steamStampValue, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "  preserved serapeum_catalogue_number.txt (uploaded item $($steamStampValue.Trim()))" -ForegroundColor DarkGray
    }
    else {
        Write-Host "  note: this mod folder has no serapeum_catalogue_number.txt yet" -ForegroundColor DarkGray
        Write-Host "        (Steam creates it when you publish; until then the game offers Upload, not Update)" -ForegroundColor DarkGray
    }

    # The DLL must live in <modfolder>/dll/ and be named after the mod's synopsis "name" with
    # every non-alphanumeric character stripped. ModManager.TryLoadDllsForMod builds exactly
    # this path and returns silently if it does not exist:
    #     Path.Combine(mod.ModRootFolder, "dll", mod.GetNameAlphanumericsOnly() + ".dll")
    #     GetNameAlphanumericsOnly() => Regex.Replace(Name, "[^a-zA-Z0-9_]+", "")
    # So the synopsis name has to be the plain mod id; a pretty display name belongs in
    # "description". Getting this wrong produces a mod that loads without a single log line.
    $DllDir = Join-Path $ModDir 'dll'
    New-Item -ItemType Directory -Force -Path $DllDir | Out-Null
    $installedDll = Join-Path $DllDir "$Name.dll"
    Copy-Item $Dll $installedDll -Force

    # Prove the copy landed. Checking this in the SAME run is the point: an earlier round was
    # lost to a byte-comparison performed in a later run, which validated a different install
    # than the one being tested.
    $srcHash = (Get-FileHash $Dll -Algorithm SHA256).Hash
    $dstHash = (Get-FileHash $installedDll -Algorithm SHA256).Hash
    if ($srcHash -ne $dstHash) {
        Write-Host "ERROR: the copied dll does not match the build ($srcHash vs $dstHash)" -ForegroundColor Red
        exit 1
    }

    # Seed config.txt. The mod writes one itself on first run, but shipping it means the file is
    # present in the Workshop package and a downloader can see the options without starting the
    # game first. Values mirror ApiConfig.WriteDefaultConfig exactly; the mod overwrites nothing
    # that already exists, so a player's edits survive.
    $configPath = Join-Path $ModDir 'config.txt'
    if (-not (Test-Path $configPath)) {
        $cfg = @(
            '# LLM_API - external API for Cultist Simulator',
            '# Edit and restart the game to apply.',
            '',
            '# TCP port the HTTP API listens on (localhost only).',
            'port=8710',
            '',
            '# Set to 0 to load the mod without starting the HTTP server.',
            'enabled=1',
            '',
            '# Where LLM_API.log and selftest.json are written.',
            ('directory=' + (Join-Path $env:USERPROFILE 'AppData\LocalLow\Weather Factory\Cultist Simulator\LLM_API')),
            '',
            '# Run the built-in read -> place -> start -> move self-test on the next boot.',
            'selftest=1',
            '',
            '# Slot used by the self-test. Comment out to auto-detect the first open slot.',
            '# selftest_slot_path=~/tabletop!work_0/work',
            '',
            '# Token moved by the self-test. Comment out to move the first draggable card.',
            '# selftest_token_id=!introjob_2',
            '',
            '# How long an HTTP request waits for the game thread.',
            'request_timeout_ms=15000'
        ) -join "`r`n"
        [System.IO.File]::WriteAllText($configPath, $cfg + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
    }

    # The two lines the in-game mod list shows, per ModEntry.Initialise():
    #     title.text       = mod.Name          (read from synopsis "name")
    #     description.text = mod.Description   (read from synopsis "description")
    #
    # "name" is ALSO what the loader turns into the DLL file name
    # (GetNameAlphanumericsOnly strips every non [a-zA-Z0-9_] character), so it can never be a
    # human-readable title: a CJK title reduces to whatever Latin happens to be in it and the
    # loader then hunts for a DLL that does not exist and gives up in silence. The bilingual
    # title therefore lives in "description", which is free text, and "name" stays the bare id.
    #
    # Chinese must be wrapped in the CJK font: the default font has no CJK glyphs and renders
    # tofu boxes. "<font=...>" is a TMP tag that applies only to the text it encloses, so Latin
    # left outside it keeps the normal font. The game's own content does exactly this (see
    # credits.json, and the zh-hans culture declares fontscript "cjk").
    #
    # This script is deliberately ASCII-only: under Windows PowerShell 5.1 a UTF-8 file WITHOUT
    # a BOM is parsed as the system ANSI codepage, which silently turns any non-ASCII literal in
    # here into mojibake. The text lives in UTF-8 data files that are read with an explicit
    # -Encoding UTF8 instead, which is the one path that measured as reliable.
    $cjkFont = 'NotoSansCJKsc-Regular'
    $title = $DisplayName
    if (-not $title) {
        $titleFile = Join-Path $PSScriptRoot 'mod_title.txt'
        if (Test-Path $titleFile) {
            $title = ([System.IO.File]::ReadAllText($titleFile, [System.Text.Encoding]::UTF8)).Trim()
            if ($DisplayName -eq $null -and $title.Length -eq 0) { $title = $null }
        }
    }
    if (-not $title) { $title = '<font=' + $cjkFont + '></font> / LLM plays via API' }
    if ($title -notmatch '<font=') {
        # Wrap only the runs that actually need the CJK font, leaving Latin on the default one.
        $title = [regex]::Replace($title, '([\u3000-\u9fff\uff00-\uffef]+)', ('<font=' + $cjkFont + '>$1</font>'))
    }

    $synopsis = [ordered]@{
        name             = $Name
        author           = 'local'
        version          = $Version
        description      = $title
        description_long = 'External HTTP/JSON API for Cultist Simulator. See README.md in this folder.'
        tags             = @($Tags)
    }

    # description_long is what gets pushed to the Workshop page, verbatim and in one field
    # (SteamUGC.SetItemDescription). Ship the bilingual text from workshop_description.md.
    $workshopText = Join-Path $PSScriptRoot 'workshop_description.md'
    if (Test-Path $workshopText) {
        $synopsis['description_long'] = (Get-Content $workshopText -Raw -Encoding UTF8).Trim()
    } else {
        Write-Host "  note: $workshopText not found, using the short description_long" -ForegroundColor Yellow
    }

    # --- dependencies: deliberately NOT emitted by default --------------------------------
    # Dependency.ModId is matched against the other mod's catalogue id, which the game derives
    # from the folder name; for the Workshop gatekeeper that is its numeric published-file id
    # ("2901287611"), not its display name ("GHIRBI").
    #
    # Writing a dependency the game judges unsatisfiable is NOT a harmless warning. Measured
    # behaviour with a bare ["2901287611"]:
    #     >>>> 02:54: Dependency '2901287611' for '<mod id>' has incompatible version
    #     >>>>>>>> 02:54: CRASH!!! UH O! LOADING UH O SCENE!!!
    #         at ModManager.CatalogueMods()
    # The mod itself still loaded, but the game dropped into its crash scene. Since GHIRBI is
    # already a hard prerequisite for the DLL gate and is documented as such in the mod's
    # README, declaring the dependency buys little and can cost the player their session.
    #
    # Pass -GatekeeperId explicitly to experiment; the value must satisfy the game's grammar
    # "^\s*(\w+)(?:\s*(<=|<|>=|>|==)\s*([\d.]+))?\s*$" and the version comparison it implies.
    if ($GatekeeperId) {
        $synopsis['dependencies'] = @($GatekeeperId)
        Write-Host "  WARNING: declaring dependency '$GatekeeperId' - if the game cannot satisfy it" -ForegroundColor Yellow
        Write-Host "           it will load the UH O crash scene. Test this before shipping." -ForegroundColor Yellow
    }

    # --- write synopsis.json without any \uXXXX escapes -----------------------------------
    # The game parses this with SimpleJsonImporter, a minimal JSON reader that does NOT
    # understand \uXXXX. ConvertTo-Json escapes non-ASCII characters that way, so a synopsis
    # containing Chinese - or merely an apostrophe, which becomes \u0027 - makes the importer
    # fail, CatalogueMod mark the mod invalid, and the game drop into its "UH O" crash scene:
    #     Failed to fully parse JSON: Unknown escaped sequence: '\u'
    #       at SimpleJsonImporter.Import(...) -> ModManager.CatalogueMod(...)
    # So serialise, then turn every \uXXXX back into the real character before writing.
    $json = $synopsis | ConvertTo-Json -Depth 5
    $json = [regex]::Replace($json, '\\u([0-9a-fA-F]{4})', {
        param($m)
        [char][Convert]::ToInt32($m.Groups[1].Value, 16)
    })
    # No BOM: the importer is equally naive about that.
    [System.IO.File]::WriteAllText((Join-Path $ModDir 'synopsis.json'), $json,
        (New-Object System.Text.UTF8Encoding($false)))

    if ($json -match '\\u[0-9a-fA-F]{4}') {
        throw "synopsis.json still contains a \u escape; the game's JSON importer cannot read it"
    }

    # Validate the document too. A synopsis the game cannot parse does not merely fail to load:
    # CatalogueMod marks the mod invalid and the game loads its "UH O" crash scene. Catching it
    # here is far cheaper than discovering it from a player's crash.
    try {
        $null = ConvertFrom-Json $json
    } catch {
        throw "synopsis.json is not valid JSON: $($_.Exception.Message)"
    }

    # Ship the mod's own README inside the mod folder: whoever finds this folder on another
    # machine needs to know what it is and how to drive it, without the dev workspace.
    $modReadme = Join-Path $PSScriptRoot 'MOD_README.md'
    if (Test-Path $modReadme) {
        Copy-Item $modReadme (Join-Path $ModDir 'README.md') -Force
        Write-Host '  installed README.md into the mod folder' -ForegroundColor DarkGray
    } else {
        Write-Host "  note: $modReadme not found, mod folder ships without a README" -ForegroundColor Yellow
    }

    # An icon makes the mod list entry legible and is required for a Workshop upload.
    $icon = Join-Path $PSScriptRoot 'build\cover.png'
    if (Test-Path $icon) { Copy-Item $icon (Join-Path $ModDir 'cover.png') -Force }

    Write-Host "installed $Name -> $DllDir\$Name.dll" -ForegroundColor Green
    Write-Host "  synopsis name = '$Name' (must equal the dll file name)" -ForegroundColor DarkGray
    if ($GatekeeperId) { Write-Host "  dependencies  = '$GatekeeperId' (GHIRBI's catalogue id)" -ForegroundColor DarkGray }

    if (Test-Path $ModsTxt) {
        $check = [System.IO.File]::ReadAllLines($ModsTxt) | Where-Object { $_.Trim() }
        Write-Host "mods.txt currently enables: $($check -join ' | ')" -ForegroundColor Cyan
    }
}
