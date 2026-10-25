# Dev: Selene as a new user gets it. Runs a second, portable Playnite (its own folder, library, settings and temp) beside
# the one in use, with the packaged theme and add-on from dist\ installed into it, takes a picture and closes it.
# Playnite's --masterinstance skips its single-instance check, so the Playnite you use is never touched (its pipe
# server fails to start in the test copy, which is harmless).
# Usage: fresh-test.ps1 [-Root <folder>] [-ThemeOnly] [-Wait 25] [-Shot out.png]
#   First time: unpack a portable Playnite (the .7z from Playnite's releases) into <Root>\Playnite. Run package.ps1 first.
param([string]$Root = "$env:USERPROFILE\selene-fresh", [switch]$ThemeOnly, [int]$Wait = 25, [string]$Shot = "$env:TEMP\selene-fresh.png")
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$pn = Join-Path $Root 'Playnite'
if (-not (Test-Path (Join-Path $pn 'Playnite.FullscreenApp.exe'))) { throw "no portable Playnite in $pn (unpack Playnite's .7z there)" }
$themeId = 'Selene_a98e0027-fa6a-4f5e-a087-bad8d69f2443'
$coreId = 'SeleneCore_cd424c29-97f4-4dd0-aa66-4d8e01cc8724'
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Install($zip, $dir) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory $dir | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $dir)   # what Playnite's installer does with the file
}
Install (Join-Path $repo 'dist\Selene.pthm') (Join-Path $pn "Themes\Fullscreen\$themeId")
$ext = Join-Path $pn "Extensions\$coreId"
if ($ThemeOnly) { if (Test-Path $ext) { Remove-Item $ext -Recurse -Force } } else { Install (Join-Path $repo 'dist\SeleneCore.pext') $ext }
# the settings a first run would have made (no wizard): the library next to Playnite, Selene as the fullscreen theme
if (-not (Test-Path (Join-Path $pn 'config.json'))) {
    '{ "FirstTimeWizardComplete": true, "DatabasePath": "{PlayniteDir}/library", "StartInFullscreen": false, "UpdateLibStartup": false }' | Set-Content (Join-Path $pn 'config.json')
}
"{ `"Theme`": `"$themeId`" }" | Set-Content (Join-Path $pn 'fullscreenConfig.json')
# a killed run leaves the flag behind and Playnite would ask about safe mode instead of starting
Remove-Item (Join-Path $pn 'safestart.flag'), (Join-Path $pn 'playnite.log') -ErrorAction SilentlyContinue
$temp = Join-Path $Root 'temp'
New-Item -ItemType Directory -Force $temp | Out-Null
Remove-Item (Join-Path $temp 'selene.log') -ErrorAction SilentlyContinue
$env:TEMP = $temp; $env:TMP = $temp   # its own selene.log, not the one of the Playnite in use
$p = Start-Process -FilePath (Join-Path $pn 'Playnite.FullscreenApp.exe') -ArgumentList '--masterinstance', '--nolibupdate' -WorkingDirectory $pn -PassThru
"started $($p.Id); waiting $Wait s"
Start-Sleep -Seconds $Wait
& (Join-Path $PSScriptRoot 'screenshot.ps1') $Shot 1400 $p.Id
"logs: $(Join-Path $pn 'playnite.log')   $(Join-Path $temp 'selene.log')"
# a normal close (a killed Playnite leaves the safe-mode flag)
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);'
$p.Refresh()
if ($p.MainWindowHandle -ne [IntPtr]::Zero) { [W.U]::PostMessage($p.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
if (-not $p.WaitForExit(12000)) { Stop-Process -Id $p.Id -Force; "closed (forced)" } else { "closed" }
