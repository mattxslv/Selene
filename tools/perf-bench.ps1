# Dev: one measured run of the portable test Playnite (see fresh-test.ps1) for a named scenario, with perf-sample.ps1 reading
# its CPU / GPU / VRAM / memory from outside. The same steps run for a before and an after build, so the numbers compare.
#   -Pack <folder>   folder holding Selene.pthm + SeleneCore.pext to install first (default: dist\ of this repository).
#                    Keep a copy of a release's dist\ to measure it later against a new build.
#   -Scenario        idle | collection | hold | filters | minimized | tray | cycle  (what is done after start-up; see $scenarios)
#   -Visible         on the real desktop (takes the screen, real display refresh, DWM composition). Default is the hidden
#                    desktop of hidden-test.ps1: no screen, so GPU numbers there are NOT what a player sees.
#   -Frames          also writes the app's own per-second frame log ("fps N, worst gap") into selene.log (developer switch selene-dev-frames)
#   -On / -Off       Selene feature switches to turn on / off for the run (keys of Features.All, e.g. -On perfmode -Off glow)
#   -Flag name=value  developer switches written to the run's temp folder as selene-dev-<name> (e.g. -Flag census -Flag leaveout=video)
#   -Shots           seconds after start at which a picture of the window is saved (to <Root>\shots, copied into the results folder)
#   -Keys            a key plan of your own instead of the scenario's ("<ms> <button> ...", see hidden-test.ps1)
#   -Theme / -DisablePlugin  run another fullscreen theme (e.g. -Theme Default -DisablePlugin SeleneCore_cd424c29-97f4-4dd0-aa66-4d8e01cc8724: plain Playnite)
#   -Values          Selene settings for the run, as "key=value;key=value" (see hidden-test.ps1)
# Output: summary lines (also saved as <Out>.txt), the CSV from perf-sample.ps1 and the run's selene.log copy.
param([string]$Pack = '', [ValidateSet('idle', 'collection', 'hold', 'filters', 'minimized', 'tray', 'cycle')][string]$Scenario = 'idle', [int]$Seconds = 70, [int]$Warmup = 20,
      [string]$Keys = '', [string]$Shots = '', [string]$Theme = '', [string[]]$DisablePlugin = @(), [switch]$Visible, [switch]$Frames, [string[]]$On = @(), [string[]]$Off = @(), [string[]]$Flag = @(), [string]$Values = '', [string]$Label = '', [switch]$KeepInstalled, [string]$Root = "$env:USERPROFILE\selene-fresh")
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Pack) { $Pack = Join-Path $repo 'dist' }
if (-not $Label) { $Label = "$Scenario" + $(if ($Visible) { '-visible' } else { '-hidden' }) }
$pn = Join-Path $Root 'Playnite'
$themeId = 'Selene_a98e0027-fa6a-4f5e-a087-bad8d69f2443'; $coreId = 'SeleneCore_cd424c29-97f4-4dd0-aa66-4d8e01cc8724'

# the same input, every run: key plan as "<ms after the previous> <button>" pairs (hidden-test.ps1 / LoginScene.DevKeys)
$hold = ''; for ($i = 0; $i -lt 6; $i++) { $hold += ' 4000 pad:DPadRight 3000 padup:DPadRight 300 pad:DPadLeft 3000 padup:DPadLeft' }
$filters = ''; for ($i = 0; $i -lt 12; $i++) { $filters += ' 1500 filter:1' }
$scenarios = @{
    idle       = ''                                       # Spotlight, nothing pressed
    collection = '9000 collection'                        # Collection, nothing pressed
    hold       = '10000 pad:DPadLeft 300 padup:DPadLeft' + $hold             # hold right / left on Spotlight's row (steady-hold scrolling)
    filters    = '10000 pad:DPadLeft 300 padup:DPadLeft' + $filters          # cycle the row filter (Playnite's filter presets / Selene's tabs)
    minimized  = '12000 minimize'                         # the window minimized and left (a game running in front of it)
    tray       = '12000 hidetray'                         # hidden to the tray
    cycle      = '12000 minimize 5000 restore 5000 minimize 5000 restore 5000 hidetray 5000 showtray 5000 minimize 5000 restore'   # robustness: window state changes in a row
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Install($zip, $dir) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory $dir | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $dir)
}
& (Join-Path $Root 'close-test-copies.ps1'); Start-Sleep -Seconds 2
Install (Join-Path $Pack 'Selene.pthm') (Join-Path $pn "Themes\Fullscreen\$themeId")
Install (Join-Path $Pack 'SeleneCore.pext') (Join-Path $pn "Extensions\$coreId")
"installed packages from $Pack"

$out = Join-Path $env:TEMP ("selene-bench-$Label-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory $out | Out-Null
$sampler = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'perf-sample.ps1'),
    '-Name', 'Playnite.FullscreenApp', '-Match', $pn, '-Seconds', $Seconds, '-Warmup', $Warmup, '-Label', $Label, '-Out', (Join-Path $out 'samples.csv') `
    -RedirectStandardOutput (Join-Path $out 'summary.txt') -RedirectStandardError (Join-Path $out 'sampler-error.txt') -WindowStyle Hidden -PassThru
$args2 = @{ Keys = $(if ($Keys) { $Keys } else { $scenarios[$Scenario].Trim() }); Wait = $Seconds + 6; Root = $Root; Disable = @('PlayniteAudioSwitcher_708b6ec4-bf96-4c0d-bd9d-fe0aa04d6bf1', 'UniPlaySong.a1b2c3d4-e5f6-7890-abcd-ef1234567890') + @($DisablePlugin) }
if ($Theme) { $args2.Theme = $Theme }
if ($Shots) { $args2.Shots = $Shots }
if ($Values) { $args2.Values = $Values }
if ($Visible) { $args2.Visible = $true }
Get-ChildItem (Join-Path $Root "temp") -Filter "selene-dev-*" -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin "selene-dev-isolated" } | Remove-Item -Force -ErrorAction SilentlyContinue
$featOn = $On; $featOff = $Off; $withFrames = [bool]$Frames; $devFlags = $Flag
# hidden-test.ps1 runs this after it has prepared the test copy's settings (and puts the settings back afterwards)
$args2.Prep = {
    param($pn, $temp)
    if ($withFrames) { New-Item -ItemType File -Force (Join-Path $temp 'selene-dev-frames') | Out-Null }
    foreach ($fl in $devFlags) { $kv = $fl -split '=', 2; Set-Content (Join-Path $temp ('selene-dev-' + $kv[0])) $(if ($kv.Count -gt 1) { $kv[1] } else { '' }) }
    $cfgFile = Join-Path $pn 'ExtensionsData\cd424c29-97f4-4dd0-aa66-4d8e01cc8724\config.json'
    $sel = Get-Content $cfgFile -Raw | ConvertFrom-Json
    $sel | Add-Member -NotePropertyName TurnedOn -NotePropertyValue @((@($sel.TurnedOn) + $featOn) | Where-Object { $_ } | Select-Object -Unique) -Force
    $sel | Add-Member -NotePropertyName TurnedOff -NotePropertyValue @((@($sel.TurnedOff) + $featOff) | Where-Object { $_ } | Select-Object -Unique) -Force
    $sel | ConvertTo-Json -Depth 20 | Set-Content $cfgFile -Encoding UTF8
}.GetNewClosure()
& (Join-Path $PSScriptRoot 'hidden-test.ps1') @args2
$null = $sampler.WaitForExit(30000)
Copy-Item (Join-Path $Root 'temp\selene.log') (Join-Path $out 'selene.log') -ErrorAction SilentlyContinue
if ($Shots) { Copy-Item (Join-Path $Root 'shots\shot-*.png') $out -ErrorAction SilentlyContinue }
Get-Content (Join-Path $out 'summary.txt')
# Older builds may accept the dev-key plan but ignore lifecycle commands. Never silently compare those as minimized runs.
$scenarioLog = Join-Path $out 'selene.log'
if ($Scenario -in 'minimized', 'tray', 'cycle') {
    $markers = if (Test-Path $scenarioLog) { @(Select-String -LiteralPath $scenarioLog -Pattern 'dev lifecycle:') } else { @() }
    $expected = if ($Scenario -eq 'cycle') { 8 } else { 1 }
    if ($markers.Count -lt $expected) { 'UNVERIFIED lifecycle scenario: expected window commands were not confirmed by this build. Use external sampling and manually minimize the baseline.' }
}
# the app's own frame log (-Frames): WPF render callbacks per second, the longest gap between two, and frames that came late (more than 1.25 refreshes)
$logFile = Join-Path $out 'selene.log'
if ($Frames -and (Test-Path $logFile)) {
    $lines = @(Get-Content $logFile)
    $first = if ($lines.Count) { [datetime]::ParseExact($lines[0].Substring(0, 12), 'HH:mm:ss.fff', $null) } else { $null }
    $fps = @(); $late = @(); $gap = @()
    foreach ($l in $lines) {
        if ($l -match '^(\d\d:\d\d:\d\d\.\d{3}) fps (\d+), worst gap (\d+) ms, frames (\d+), late (\d+)') {
            $t = [datetime]::ParseExact($Matches[1], 'HH:mm:ss.fff', $null)
            if ($first -and ($t - $first).TotalSeconds -lt $Warmup + 10) { continue }
            $fps += [int]$Matches[4]; $gap += [int]$Matches[3]; $late += [int]$Matches[5]
        }
    }
    if ($fps.Count) {
        $sg = $gap | Sort-Object
        "frames: {0} s logged | {1:N1} frames/s mean | worst gap {2} ms (p95 {3} ms) | late frames {4:N1}/s" -f $fps.Count, ($fps | Measure-Object -Average).Average, $sg[-1], $sg[[Math]::Min($sg.Count - 1, [int][Math]::Floor($sg.Count * 0.95))], ($late | Measure-Object -Average).Average
    }
}
Get-Content (Join-Path $out 'sampler-error.txt') -ErrorAction SilentlyContinue
"results: $out"
