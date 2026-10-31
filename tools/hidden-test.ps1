# Dev: runs the portable test Playnite (see fresh-test.ps1) on a hidden Windows desktop, so it can be tested while the PC is
# in use: its windows never appear on screen or take the focus, it reads no controller and plays no sound
# (%TEMP%\selene-dev-isolated), it can't sleep / restart / shut down the PC, and the add-ons that reach outside Playnite
# (audio device switching, music) are off for the run. The test copy's settings are put back afterwards.
# Usage: hidden-test.ps1 [-Keys "<ms> <button> ..."] [-Wait 40] [-Shots 10,20] [-Root <folder>] [-Prep { script }]
#   -Keys   button presses after start (selene-dev-keys: "menu", "A", "B", "DPadDown", "type:abc"...)
#   -Shots  "10,20": seconds after start at which a picture of the test Playnite is saved as <Root>\shots\shot-<s>.png
#   -Values "filtertabs=playnite;shade=1": Selene settings for the run
#   -Theme   the fullscreen theme's folder name (default Selene; 'Default' is Playnite's own, to compare Selene against plain Playnite)
#   -Relaunch <s>: a second, normal start of the test Playnite <s> seconds in (as a user starting it again right after quitting)
#   -Visible on the real desktop instead (it takes the screen!); -Shots are then pictures of the whole main screen
#   -Login  keep the sign-in (and a new copy's first-run setup) instead of skipping it
#   -Wait   seconds to let it run; still running then = closed by PID (the result says so)
param([string]$Keys = "", [string]$Values = "", [string]$Theme = 'Selene_a98e0027-fa6a-4f5e-a087-bad8d69f2443', [switch]$Visible, [int]$Relaunch = 0, [int]$Wait = 40, [string]$Shots = "", [string]$Root = "$env:USERPROFILE\selene-fresh", [scriptblock]$Prep = $null,
      [switch]$Login,
      [string[]]$Disable = @('PlayniteAudioSwitcher_708b6ec4-bf96-4c0d-bd9d-fe0aa04d6bf1', 'UniPlaySong.a1b2c3d4-e5f6-7890-abcd-ef1234567890'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$pn = Join-Path $Root 'Playnite'
$exe = Join-Path $pn 'Playnite.FullscreenApp.exe'
if (-not (Test-Path $exe)) { throw "no portable Playnite in $pn" }
if (Get-Process Playnite.FullscreenApp, Playnite.DesktopApp -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$pn\*" }) { throw "the test copy is already running" }

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class HiddenDesk {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct STARTUPINFO { public int cb; public string lpReserved; public string lpDesktop; public string lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
  [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr sa);
  [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr h);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  public static int Start(string desktop, string app, string cmd, string dir, bool noWindow) {
    var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si); si.lpDesktop = desktop;
    PROCESS_INFORMATION pi;
    if (!CreateProcess(app, cmd, IntPtr.Zero, IntPtr.Zero, false, noWindow ? 0x08000000u : 0u, IntPtr.Zero, dir, ref si, out pi)) throw new System.ComponentModel.Win32Exception();
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    return pi.dwProcessId;
  }
}
"@

# ---- the test copy's settings for the run (put back in finally) ----
$files = @((Join-Path $pn 'config.json'), (Join-Path $pn 'fullscreenConfig.json'), (Join-Path $pn 'ExtensionsData\cd424c29-97f4-4dd0-aa66-4d8e01cc8724\config.json'), (Join-Path $pn 'selene-row-filter.txt'))
foreach ($f in $files) { if (Test-Path $f) { Copy-Item $f "$f.bak-hidden" -Force } }
$desk = [IntPtr]::Zero
$originalTemp = $env:TEMP; $originalTmp = $env:TMP
$procId = 0
try {
    $cfg = Get-Content $files[0] -Raw | ConvertFrom-Json
    $cfg | Add-Member -NotePropertyName DisabledPlugins -NotePropertyValue $Disable -Force
    $cfg | ConvertTo-Json -Depth 20 | Set-Content $files[0] -Encoding UTF8
    $full = if (Test-Path $files[1]) { Get-Content $files[1] -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    foreach ($kv in @{ Theme = $Theme; EnableGameControllerSupport = $false; InterfaceVolume = 0.0; BackgroundVolume = 0.0; MuteInBackground = $true }.GetEnumerator()) {
        $full | Add-Member -NotePropertyName $kv.Key -NotePropertyValue $kv.Value -Force
    }
    $full | ConvertTo-Json -Depth 20 | Set-Content $files[1] -Encoding UTF8
    if (Test-Path $files[2]) {
        $sel = Get-Content $files[2] -Raw | ConvertFrom-Json
        $off = @($sel.TurnedOff) + @('tray', 'trailersound') | Where-Object { $_ } | Select-Object -Unique
        $sel | Add-Member -NotePropertyName TurnedOff -NotePropertyValue @($off) -Force
        if ($Values) {
            if (-not $sel.Values) { $sel | Add-Member -NotePropertyName Values -NotePropertyValue ([pscustomobject]@{}) -Force }
            foreach ($pair in $Values -split ';') { $kv = $pair -split '=', 2; if ($kv.Count -eq 2) { $sel.Values | Add-Member -NotePropertyName $kv[0].Trim() -NotePropertyValue $kv[1].Trim() -Force } }
        }
        $sel | ConvertTo-Json -Depth 20 | Set-Content $files[2] -Encoding UTF8
    }
    $temp = Join-Path $Root 'temp'
    New-Item -ItemType Directory -Force $temp | Out-Null
    Remove-Item (Join-Path $temp 'selene.log'), (Join-Path $temp 'selene-dev-keys'), (Join-Path $pn 'safestart.flag') -ErrorAction SilentlyContinue
    New-Item -ItemType File -Force (Join-Path $temp 'selene-dev-isolated') | Out-Null
    if (-not $Login) { New-Item -ItemType File -Force (Join-Path $temp 'selene-skip-login') | Out-Null }   # (-Login: the sign-in / first-run setup shows)
    if ($Keys) { Set-Content (Join-Path $temp 'selene-dev-keys') $Keys }
    if ($Prep) { & $Prep $pn $temp }
    $shotDir = Join-Path $Root 'shots'
    New-Item -ItemType Directory -Force $shotDir | Out-Null

    # ---- run it on the hidden desktop ----
    $env:TEMP = $temp; $env:TMP = $temp   # inherited: the test copy's own selene.log and dev switches
    if ($Visible) {
        $name = 'Default'
        $procId = [HiddenDesk]::Start("WinSta0\Default", $exe, "`"$exe`" --masterinstance --nolibupdate", $pn, $false)
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        Add-Type -Namespace Dpi -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
        [Dpi.U]::SetProcessDPIAware() | Out-Null
        "started $procId on the real desktop (visible)"
    } else {
        $name = 'SeleneTest' + (Get-Random -Maximum 100000)
        $desk = [HiddenDesk]::CreateDesktop($name, [IntPtr]::Zero, [IntPtr]::Zero, 0, 0x10000000 <# GENERIC_ALL #>, [IntPtr]::Zero)
        if ($desk -eq [IntPtr]::Zero) { throw "CreateDesktop failed" }
        $procId = [HiddenDesk]::Start("WinSta0\$name", $exe, "`"$exe`" --masterinstance --nolibupdate", $pn, $false)
        "started $procId on hidden desktop $name"
    }
    $t0 = Get-Date
    $exitedAt = $null
    $shotQueue = New-Object System.Collections.Generic.Queue[int]
    foreach ($v in ($Shots -split "[,; ]+" | Where-Object { $_ } | ForEach-Object { [int]$_ } | Sort-Object)) { $shotQueue.Enqueue($v) }
    $procId2 = 0
    while (((Get-Date) - $t0).TotalSeconds -lt $Wait) {
        Start-Sleep -Milliseconds 500
        $secs = ((Get-Date) - $t0).TotalSeconds
        if ($Relaunch -gt 0 -and $procId2 -eq 0 -and $secs -ge $Relaunch) {
            $procId2 = [HiddenDesk]::Start("WinSta0\$name", $exe, "`"$exe`" --nolibupdate", $pn, $false)
            "relaunched at {0:N1} s: pid $procId2" -f $secs
        }
        if (-not (Get-Process -Id $procId -ErrorAction SilentlyContinue)) { $exitedAt = $secs; break }
        while ($shotQueue.Count -gt 0 -and $secs -ge $shotQueue.Peek()) {
            $s = $shotQueue.Dequeue()
            $out = Join-Path $shotDir "shot-$s.png"
            if ($Visible) {
                $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
                $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
                $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size); $g.Dispose()
                (New-Object System.Drawing.Bitmap $bmp, 1280, ([int](1280 * $b.Height / $b.Width))).Save($out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
                "screen at $s s -> $out"
                continue
            }
            $ps = (Get-Command powershell).Source
            $slog = Join-Path $shotDir "shot-$s.log"
            $sp = [HiddenDesk]::Start("WinSta0\$name", $ps, "`"$ps`" -NoProfile -ExecutionPolicy Bypass -Command `"& '$PSScriptRoot\screenshot.ps1' '$out' 1280 $procId *> '$slog'`"", $repo, $true)
            $null = (Get-Process -Id $sp -ErrorAction SilentlyContinue) | Wait-Process -Timeout 20 -ErrorAction SilentlyContinue
            "shot at $s s -> $(if (Test-Path $out) { $out } else { 'none' })"
        }
    }
    if ($exitedAt -ne $null) { "test Playnite exited by itself after {0:N1} s" -f $exitedAt }
    else {
        "test Playnite still running after $Wait s -> closed by PID"
        Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    if ($procId2 -ne 0) {
        Start-Sleep -Seconds 12
        $p2 = Get-Process -Id $procId2 -ErrorAction SilentlyContinue
        "second start: " + $(if ($p2) { "running ($($p2.ProcessName), window '$($p2.MainWindowTitle)')" } else { "not running" })
    }
}
finally {
    # anything of the test copy left (Chromium helpers...) goes, then its settings come back
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$pn\*" } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    foreach ($f in $files) { if (Test-Path "$f.bak-hidden") { Move-Item "$f.bak-hidden" $f -Force } elseif ($f -like '*selene-row-filter.txt') { Remove-Item $f -ErrorAction SilentlyContinue } }
    Remove-Item (Join-Path $pn 'safestart.flag') -ErrorAction SilentlyContinue
    foreach ($flag in 'selene-dev-isolated', 'selene-skip-login', 'selene-dev-keys') { Remove-Item (Join-Path $Root "temp\$flag") -ErrorAction SilentlyContinue }
    if ($desk -ne [IntPtr]::Zero) { [HiddenDesk]::CloseDesktop($desk) | Out-Null }
    "log: $(Join-Path $Root 'temp\selene.log')"
    $env:TEMP = $originalTemp; $env:TMP = $originalTmp
}
