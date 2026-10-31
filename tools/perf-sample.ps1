# Dev: samples one running process from outside (Windows performance counters), once a second, and writes a CSV plus a summary.
# Works on any Playnite (Selene, Solaris, Aniki Remake...), so runs are comparable. Nothing is added to the app being measured.
#   CPU %       share of ALL logical processors (like Task Manager's Details tab)
#   GPU 3D %    sum of the process's 3D engines; GPU max % is the busiest single engine (what Task Manager's GPU column shows)
#   GPU video % sum of the decode / video processing engines (hardware video decoding)
#   VRAM MB     dedicated GPU memory of the process; WS = working set; Private = private bytes
# Usage: perf-sample.ps1 -Name Playnite.FullscreenApp [-Match <path filter>] [-Seconds 60] [-Warmup 10] [-Out <csv>] [-Label idle-spotlight]
#   -Warmup  seconds ignored in the summary (start-up animations, first image loads)
param([string]$Name = 'Playnite.FullscreenApp', [string]$Match = '', [int]$Seconds = 60, [int]$Warmup = 10, [string]$Out = '', [string]$Label = 'run', [int]$WaitForStart = 120)
$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = Join-Path $env:TEMP ("selene-perf-$Label-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.csv') }

$deadline = (Get-Date).AddSeconds($WaitForStart)
$proc = $null
while (-not $proc -and (Get-Date) -lt $deadline) {
    $proc = Get-Process -Name $Name -ErrorAction SilentlyContinue | Where-Object { -not $Match -or $_.Path -like "*$Match*" } | Select-Object -First 1
    if (-not $proc) { Start-Sleep -Milliseconds 500 }
}
if (-not $proc) { throw "no process '$Name' matching '$Match' started within $WaitForStart s" }
$procId = $proc.Id
$cores = [Environment]::ProcessorCount

# direct counter objects (a wildcard Get-Counter walks every process and takes seconds); instances are looked up again every 10 s
$engCat = New-Object Diagnostics.PerformanceCounterCategory('GPU Engine')
$memCat = New-Object Diagnostics.PerformanceCounterCategory('GPU Process Memory')
$engCounters = @(); $memCounters = @(); $lookedUp = -100.0
function Refresh-Counters([int]$id, [double]$now) {
    foreach ($e in @($script:engCounters) + @($script:memCounters)) { $e.c.Dispose() }
    $script:engCounters = @(); $script:memCounters = @()
    try {
        foreach ($n in $engCat.GetInstanceNames() | Where-Object { $_ -like "pid_${id}_*" }) {
            $c = New-Object Diagnostics.PerformanceCounter('GPU Engine', 'Utilization Percentage', $n, $true); $null = $c.NextValue()
            $script:engCounters += [pscustomobject]@{ name = $n; c = $c }
        }
        foreach ($n in $memCat.GetInstanceNames() | Where-Object { $_ -like "pid_${id}_*" }) {
            $script:memCounters += [pscustomobject]@{ name = $n; c = (New-Object Diagnostics.PerformanceCounter('GPU Process Memory', 'Dedicated Usage', $n, $true)) }
        }
    } catch { }
    $script:lookedUp = $now
}
function Read-Gpu([int]$id, [double]$now) {
    $r = @{ d3 = 0.0; video = 0.0; max = 0.0; vram = 0.0 }
    if ($now - $script:lookedUp -ge 10) { Refresh-Counters $id $now }
    foreach ($e in $script:engCounters) {
        try { $v = [double]$e.c.NextValue() } catch { continue }
        if ($v -gt $r.max) { $r.max = $v }
        if ($e.name -match 'engtype_3D') { $r.d3 += $v } elseif ($e.name -match 'engtype_Video') { $r.video += $v }
    }
    foreach ($m in $script:memCounters) { try { $r.vram += [double]$m.c.NextValue() / 1MB } catch { } }
    if ($script:engCounters.Count -eq 0) { $r.d3 = $null; $r.video = $null; $r.max = $null }
    if ($script:memCounters.Count -eq 0) { $r.vram = $null }
    return $r
}

$rows = New-Object System.Collections.Generic.List[object]
$clock = [Diagnostics.Stopwatch]::StartNew()
$prevCpu = $proc.TotalProcessorTime.TotalSeconds; $prevT = 0.0
"sampling $Name pid $procId for $Seconds s -> $Out"
while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
    $tickStart = $clock.Elapsed.TotalSeconds
    try { $proc.Refresh(); if ($proc.HasExited) { "process exited at {0:N0} s" -f $tickStart; break } } catch { break }
    $gpu = Read-Gpu $procId $clock.Elapsed.TotalSeconds
    $now = $clock.Elapsed.TotalSeconds; $cpu = $proc.TotalProcessorTime.TotalSeconds
    $wall = [Math]::Max(0.001, $now - $prevT)
    $rows.Add([pscustomobject]@{
        t = [Math]::Round($now, 1); cpuPct = [Math]::Round(($cpu - $prevCpu) / $wall / $cores * 100, 2)
        gpu3dPct = $(if ($null -ne $gpu.d3) { [Math]::Round($gpu.d3, 2) } else { $null }); gpuMaxPct = $(if ($null -ne $gpu.max) { [Math]::Round($gpu.max, 2) } else { $null }); gpuVideoPct = $(if ($null -ne $gpu.video) { [Math]::Round($gpu.video, 2) } else { $null }); vramMB = $(if ($null -ne $gpu.vram) { [Math]::Round($gpu.vram, 1) } else { $null })
        wsMB = [Math]::Round($proc.WorkingSet64 / 1MB, 1); privateMB = [Math]::Round($proc.PrivateMemorySize64 / 1MB, 1); threads = $proc.Threads.Count; handles = $proc.HandleCount
    })
    $prevCpu = $cpu; $prevT = $now
    $sleep = 1000 - ($clock.Elapsed.TotalSeconds - $tickStart) * 1000
    if ($sleep -gt 0) { Start-Sleep -Milliseconds ([int]$sleep) }
}
$rows | Export-Csv $Out -NoTypeInformation
$use = @($rows | Where-Object { $_.t -ge $Warmup })
if ($use.Count -eq 0) { $use = @($rows) }
foreach ($counter in @($engCounters) + @($memCounters)) { $counter.c.Dispose() }
$proc.Dispose()
function Stat($sel) { $v = @($use | ForEach-Object $sel | Where-Object { $null -ne $_ } | Sort-Object); if ($v.Count -eq 0) { return '-' }; '{0,8:N2} mean {1,8:N2} p95 {2,8:N2} max' -f (($v | Measure-Object -Average).Average), $v[[Math]::Min($v.Count - 1, [int][Math]::Floor($v.Count * 0.95))], $v[-1] }
"--- $Label ($($use.Count) samples after $Warmup s warm-up; $cores logical CPUs)"
"CPU %        " + (Stat { $_.cpuPct })
"GPU 3D %     " + (Stat { $_.gpu3dPct })
"GPU max eng% " + (Stat { $_.gpuMaxPct })
"GPU video %  " + (Stat { $_.gpuVideoPct })
"VRAM MB      " + (Stat { $_.vramMB })
"Working set  " + (Stat { $_.wsMB })
"Private MB   " + (Stat { $_.privateMB })
"csv: $Out"
