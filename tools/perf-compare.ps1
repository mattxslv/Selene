# Dev: the same scenarios on the REAL desktop for two builds, one after the other, and a table of the results.
#   GPU load, frame pacing and the cost of staying minimized only mean something on a screen that is really drawn: the hidden
#   desktop of hidden-test.ps1 throttles WPF to ~10 frames a second, so perf-bench.ps1 without -Visible is only good for memory,
#   CPU and what the UI thread does (-Frames -Flag census).
#   THIS TAKES THE SCREEN: the test Playnite opens full screen for ~1 minute per run, 20-30 minutes in all. Start it, then keep
#   hands off keyboard and mouse (any click or key leaves the test copy's window) and look at the table when it is done.
#
# Usage: perf-compare.ps1 -Baseline <folder with Selene.pthm + SeleneCore.pext> -Candidate <folder> [-Seconds 55] [-Quick]
#   -Quick   only idle + hold + minimized (about 8 minutes)
# Every build runs each scenario once, in this order, so slow drifts (thermals, background updates) hit both alike:
#   baseline idle, candidate idle, baseline idle (again, to see how steady the numbers are), candidate ... and so on.
#   -Frames  separate diagnostic pass: the Rendering observer changes idle cadence; leave off for resource comparisons.
# Output: <folder>\compare-<time>\table.md (also printed), and each run's CSV + selene.log beside it.
param([Parameter(Mandatory = $true)][string]$Baseline, [Parameter(Mandatory = $true)][string]$Candidate, [int]$Seconds = 55, [int]$Warmup = 15,
      [switch]$Quick, [switch]$Frames, [int]$Countdown = 20, [string[]]$Extra = @())
$ErrorActionPreference = 'Stop'
foreach ($p in $Baseline, $Candidate) { foreach ($f in 'Selene.pthm', 'SeleneCore.pext') { if (-not (Test-Path (Join-Path $p $f))) { throw "$p has no $f" } } }
$scenarios = if ($Quick) { @('idle', 'hold', 'minimized') } else { @('idle', 'collection', 'hold', 'filters', 'minimized', 'cycle') }
$bench = Join-Path $PSScriptRoot 'perf-bench.ps1'
$out = Join-Path (Split-Path $Candidate -Parent) ('compare-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $out | Out-Null
"This will open the test Playnite full screen on this PC, $($scenarios.Count * 3) times, about $([int](($scenarios.Count * 3 * ($Seconds + 55)) / 60)) minutes in all."
"Keep your hands off the keyboard and mouse. Starting in $Countdown s (Ctrl+C to cancel)..."
Start-Sleep -Seconds $Countdown

$rows = @()
foreach ($scenario in $scenarios) {
    foreach ($round in 'baseline', 'candidate', 'baseline-2') {
        $pack = if ($round -eq 'candidate') { $Candidate } else { $Baseline }
        $label = "$scenario-$round"
        $secs = if ($scenario -eq 'cycle') { $Seconds + 15 } else { $Seconds }
        "=== $label"
        $text = & $bench -Pack $pack -Scenario $scenario -Seconds $secs -Warmup $(if ($scenario -eq 'minimized') { 25 } else { $Warmup }) -Visible -Frames:$Frames -Label $label @Extra 2>&1 | Out-String
        $text | Set-Content (Join-Path $out "$label.txt")
        if ($text -match 'UNVERIFIED lifecycle scenario') {
            $rows += [pscustomobject]@{ Scenario = $scenario; Run = $round; Validation = 'UNVERIFIED window commands; see run log' }
            $rows | Export-Csv (Join-Path $out 'table.csv') -NoTypeInformation
            continue
        }
        $num = { param($name) if ($text -match "(?m)^$name\s+([\d.]+) mean\s+([\d.]+) p95\s+([\d.]+) max") { [pscustomobject]@{ Mean = [double]$Matches[1]; P95 = [double]$Matches[2]; Max = [double]$Matches[3] } } else { $null } }
        $cpu = & $num 'CPU %'; $gpu = & $num 'GPU 3D %'; $gmax = & $num 'GPU max eng%'; $vram = & $num 'VRAM MB'; $ws = & $num 'Working set'; $priv = & $num 'Private MB'
        $fr = if ($text -match 'frames: (\d+) s logged \| ([\d.]+) frames/s mean \| worst gap (\d+) ms \(p95 (\d+) ms\) \| late frames ([\d.]+)/s') { $Matches } else { $null }
        $rows += [pscustomobject]@{
            Scenario = $scenario; Run = $round; Validation = 'Recorded; inspect run log'
            'CPU % mean' = if ($cpu) { $cpu.Mean } else { '' }; 'GPU 3D % mean' = if ($gpu) { $gpu.Mean } else { '' }; 'GPU 3D % p95' = if ($gpu) { $gpu.P95 } else { '' }
            'GPU busiest engine % mean' = if ($gmax) { $gmax.Mean } else { '' }; 'VRAM MB' = if ($vram) { $vram.Mean } else { '' }
            'Working set MB' = if ($ws) { $ws.Mean } else { '' }; 'Private MB' = if ($priv) { $priv.Mean } else { '' }
            'WPF callbacks/s' = if ($fr) { $fr[2] } else { '' }; 'Worst gap ms' = if ($fr) { $fr[3] } else { '' }; 'Late frames/s' = if ($fr) { $fr[5] } else { '' }
        }
        $rows | Export-Csv (Join-Path $out 'table.csv') -NoTypeInformation
    }
}
$md = @('| Scenario | Run | Validation | CPU % | GPU 3D % | GPU 3D p95 | Busiest GPU engine % | VRAM MB | Working set MB | Private MB | WPF callbacks/s | Worst gap ms | Late frames/s |', '|---|---|---|---|---|---|---|---|---|---|---|---|---|')
foreach ($r in $rows) { $md += "| $($r.Scenario) | $($r.Run) | $($r.Validation) | $($r.'CPU % mean') | $($r.'GPU 3D % mean') | $($r.'GPU 3D % p95') | $($r.'GPU busiest engine % mean') | $($r.'VRAM MB') | $($r.'Working set MB') | $($r.'Private MB') | $($r.'WPF callbacks/s') | $($r.'Worst gap ms') | $($r.'Late frames/s') |" }
$md | Set-Content (Join-Path $out 'table.md')
$md
"table: $(Join-Path $out 'table.md')"
