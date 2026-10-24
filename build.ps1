# Build a Cultist Simulator mod DLL against the game's own assemblies.
#
#   pwsh -File build.ps1 -Source <file.cs> -Name <modid> [-OutputDir <dir>] [-ExtraRefs a.dll,b.dll]
#
# The mod id must match the mod folder name, the DLL file name and the entry class name
# (see MODDING_README.txt "CUSTOM DLL LOADING").

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Source,
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$OutputDir,
    [string[]]$ExtraRefs = @(),
    [switch]$TargetExe
)

$ErrorActionPreference = 'Stop'

$GameManaged = 'D:\SteamLibrary\steamapps\common\Cultist Simulator\cultistsimulator_Data\Managed'
$MonoRoot    = 'C:\Program Files\UnityEditor\2022.3.62f1c1\Editor\Data\MonoBleedingEdge'
$Csc         = Join-Path $MonoRoot 'lib\mono\4.5\csc.exe'
$Mono        = Join-Path $MonoRoot 'bin\mono.exe'

# A source argument may be a file or a directory; directories expand to every .cs inside.
$sources = @()
foreach ($s in $Source) {
    if (-not (Test-Path $s)) { throw "source not found: $s" }
    if ((Get-Item $s).PSIsContainer) {
        $sources += (Get-ChildItem -Path $s -Filter '*.cs' -Recurse | ForEach-Object { $_.FullName })
    } else {
        $sources += (Get-Item $s).FullName
    }
}
if ($sources.Count -eq 0) { throw "no .cs sources found in: $($Source -join ', ')" }

foreach ($p in @($Csc, $Mono, $GameManaged)) {
    if (-not (Test-Path $p)) { throw "not found: $p" }
}

if (-not $OutputDir) { $OutputDir = Join-Path $PSScriptRoot "build\$Name" }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# Referenced through the game's own Managed folder so the compile matches the shipping
# assemblies exactly. netstandard.dll must be present: the game assemblies are
# netstandard2.1 and csc rejects them otherwise.
$refNames = @(
    'mscorlib.dll'
    'System.dll'
    'System.Core.dll'
    'netstandard.dll'
    'UnityEngine.dll'
    'UnityEngine.CoreModule.dll'
    'UnityEngine.JSONSerializeModule.dll'
    'SecretHistories.Main.dll'
    'SecretHistories.Interfaces.dll'
    'SecretHistories.Enums.dll'
    'SecretHistories.Constants.dll'
    'Newtonsoft.Json.dll'
)
$refs = $refNames | ForEach-Object { Join-Path $GameManaged $_ }
$missing = $refs | Where-Object { -not (Test-Path $_) }
if ($missing) { throw "missing reference assemblies:`n  $($missing -join "`n  ")" }

$outFile = Join-Path $OutputDir "$Name.dll"
$target = if ($TargetExe) { 'exe' } else { 'library' }

# No /debug: this Mono csc cannot emit PDBs on Windows ("Unexpected error writing debug information").
$cscArgs = @('/nologo', "/target:$target", "/out:$outFile", '/optimize+', '/langversion:7.3')
$cscArgs += $refs | ForEach-Object { "/r:$_" }
foreach ($r in $ExtraRefs) {
    if (-not (Test-Path $r)) { throw "extra reference not found: $r" }
    $cscArgs += "/r:$r"
}
$cscArgs += $sources

Write-Host "compiling $Name ($($sources.Count) file(s)) -> $outFile" -ForegroundColor Cyan
$output = & $Mono $Csc @cscArgs 2>&1
$code = $LASTEXITCODE
$output | Where-Object { "$_".Trim() } | ForEach-Object { Write-Host $_ }
if ($code -ne 0) { throw "csc failed with exit code $code" }

$dll = Get-Item $outFile
Write-Host ("OK  {0}  ({1:N0} bytes)" -f $dll.Name, $dll.Length) -ForegroundColor Green
return $outFile
