# Build and run the offline harness for cs_gameapi.
#
#   powershell -ExecutionPolicy Bypass -File run-harness.ps1 [-SkipBuild]
#
# Outputs into tools/harness/bin/:
#   UnityEngineStubs.dll  stand-ins for the UnityEngine types the mod and the fakes touch
#   FakeGame.dll          a fake Cultist Simulator board, in the real game's namespaces
#   Harness.exe           the mod's sources compiled in, driven over real HTTP
#
# The stubs live in their own assembly so that UnityEngine.Vector3 has a single identity
# across the harness and the fake game; otherwise the two copies would be different types.

[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$Repo     = $PSScriptRoot
$MonoRoot = 'C:\Program Files\UnityEditor\2022.3.62f1c1\Editor\Data\MonoBleedingEdge'
$Csc      = Join-Path $MonoRoot 'lib\mono\4.5\csc.exe'
$Mono     = Join-Path $MonoRoot 'bin\mono.exe'
$ApiDir   = Join-Path $MonoRoot 'lib\mono\4.5-api'
$GameManaged = 'D:\SteamLibrary\steamapps\common\Cultist Simulator\cultistsimulator_Data\Managed'

foreach ($p in @($Csc, $Mono, $ApiDir, $GameManaged)) {
    if (-not (Test-Path $p)) { throw "not found: $p" }
}

$Bin = Join-Path $Repo 'tools\harness\bin'
New-Item -ItemType Directory -Force -Path $Bin | Out-Null

function Invoke-Csc {
    param([string]$Label, [string[]]$Arguments)
    $out = & $Mono $Csc @Arguments 2>&1
    $code = $LASTEXITCODE
    $out | Where-Object { "$_".Trim() } | ForEach-Object { Write-Host "  $_" }
    if ($code -ne 0) { throw "$Label build failed (exit $code)" }
}

# Reference the *runtime* BCL (lib/mono/4.5) rather than the 4.5-api reference assemblies:
# mono resolves BCL types against its installed implementation assemblies, and mixing the two
# led to System.Net failing at runtime with a missing Console.IsRunningOnAndroid field.
$bcl = Join-Path $MonoRoot 'lib\mono\4.5'
$netstandard = Join-Path $GameManaged 'netstandard.dll'
$coreRefs = @(
    (Join-Path $bcl 'mscorlib.dll')
    (Join-Path $bcl 'System.dll')
    (Join-Path $bcl 'System.Core.dll')
    $netstandard
)

$stubsOut = Join-Path $Bin 'UnityEngineStubs.dll'
# Deliberately named like the real game assembly: cs_gameapi's Reflect.FindType() only probes
# the game's own assemblies, so the fakes have to arrive under that name to be found at all.
$fakeOut  = Join-Path $Bin 'SecretHistories.Main.dll'
$harnessOut = Join-Path $Bin 'Harness.exe'

if (-not $SkipBuild) {
    Write-Host '--- mod DLL ---' -ForegroundColor Cyan
    & (Join-Path $Repo 'build.ps1') -Source (Join-Path $Repo 'src\main') -Name LLM_API | Out-Null

    Write-Host '--- UnityEngineStubs.dll ---' -ForegroundColor Cyan
    Invoke-Csc -Label 'UnityEngineStubs.dll' -Arguments (@(
        '/nologo', '/target:library', "/out:$stubsOut", '/optimize+', '/langversion:7.3'
    ) + ($coreRefs | ForEach-Object { "/r:$_" }) + @(
        (Join-Path $Repo 'tools\harness\UnityStubs.cs')
    ))
    Write-Host ("  OK  {0:N0} bytes" -f (Get-Item $stubsOut).Length) -ForegroundColor Green

    Write-Host '--- SecretHistories.Main.dll (fakes) ---' -ForegroundColor Cyan
    Invoke-Csc -Label 'SecretHistories.Main.dll' -Arguments (@(
        '/nologo', '/target:library', "/out:$fakeOut", '/optimize+', '/langversion:7.3'
    ) + ($coreRefs | ForEach-Object { "/r:$_" }) + @(
        "/r:$stubsOut"
        (Join-Path $Repo 'tools\harness\FakeGame.cs')
    ))
    Write-Host ("  OK  {0:N0} bytes" -f (Get-Item $fakeOut).Length) -ForegroundColor Green

    Write-Host '--- Harness.exe ---' -ForegroundColor Cyan
    $modSources = Get-ChildItem (Join-Path $Repo 'src\main\*.cs') | ForEach-Object { $_.FullName }

    Invoke-Csc -Label 'Harness.exe' -Arguments (@(
        '/nologo', '/target:exe', "/out:$harnessOut", '/langversion:7.3'
    ) + ($coreRefs | ForEach-Object { "/r:$_" }) + @(
        "/r:$(Join-Path $GameManaged 'Newtonsoft.Json.dll')"
        "/r:$stubsOut"
        "/r:$fakeOut"
        (Join-Path $Repo 'tools\harness\Harness.cs')
    ) + $modSources)
    Write-Host ("  OK  {0:N0} bytes" -f (Get-Item $harnessOut).Length) -ForegroundColor Green
}

Copy-Item (Join-Path $GameManaged 'Newtonsoft.Json.dll') $Bin -Force

Write-Host ''
Write-Host '--- running harness ---' -ForegroundColor Cyan
& $Mono $harnessOut (Join-Path $Repo 'build\LLM_API\LLM_API.dll') $fakeOut
$code = $LASTEXITCODE

Write-Host ''
if ($code -eq 0) { Write-Host 'harness PASSED' -ForegroundColor Green }
else { Write-Host "harness FAILED (exit $code)" -ForegroundColor Red }
exit $code
