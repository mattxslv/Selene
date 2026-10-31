# Selene as a stranger gets it: a brand-new portable Playnite (the real 10.63 download), Selene's two packages installed the way
# Playnite's installer does it, no settings, no profiles, no library, no developer switches, no add-ons of yours.
# It is a folder of its own (%USERPROFILE%\selene-public-test): your own Playnite, the selene-fresh test copy and the
# first-setup preview are never touched.
#
# Usage (or double-click tools\public-test.cmd):
#   public-test.ps1                 wipe the test folder, unpack a pristine Playnite, install dist\ and start Fullscreen
#   public-test.ps1 -Keep           keep what is there (library, Selene's settings and profiles), only replace the two packages
#   public-test.ps1 -Release        install the REAL public release from GitHub instead of dist\ (to compare with what people have)
#   public-test.ps1 -Desktop        start Playnite's Desktop mode instead, to add games, or to set a theme yourself
#   public-test.ps1 -Wizard         write no config at all: Playnite's own first-run wizard shows first (Desktop mode)
#   public-test.ps1 -PrepareOnly    do everything but start it
# Selene's log of the run: <folder>\temp\selene.log
param([switch]$Keep, [switch]$Release, [switch]$Desktop, [switch]$Wizard, [switch]$PrepareOnly,
      [string]$Root = (Join-Path $env:USERPROFILE 'selene-public-test'),
      [string]$Playnite = (Join-Path $env:USERPROFILE 'selene-fresh\dl-playnite\10.63.7z'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$themeId = 'Selene_a98e0027-fa6a-4f5e-a087-bad8d69f2443'
$coreId = 'SeleneCore_cd424c29-97f4-4dd0-aa66-4d8e01cc8724'
$root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$pn = Join-Path $root 'Playnite'
# the only folder this script ever deletes: inside the user's profile and named like a test folder
if (-not $root.StartsWith($env:USERPROFILE + '\', [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $root -Leaf) -notmatch 'selene' -or $root -match '(?i)\\selene$|\\selene-fresh$') { throw "Refusing to use $root as the test folder." }

# 1. close a previous run of this test folder (and only that one)
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 800

# 2. the packages to test
if ($Release) {
    $dl = Join-Path $root 'download'
    New-Item -ItemType Directory -Force $dl | Out-Null
    & gh release download --repo mattxslv/Selene --pattern 'Selene.pthm' --pattern 'SeleneCore.pext' --dir $dl --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Could not download the public release (is gh installed and online?).' }
    $theme = Join-Path $dl 'Selene.pthm'; $core = Join-Path $dl 'SeleneCore.pext'
    $what = 'the public release from GitHub'
} else {
    $theme = Join-Path $repo 'dist\Selene.pthm'; $core = Join-Path $repo 'dist\SeleneCore.pext'
    if (-not (Test-Path $theme) -or -not (Test-Path $core)) { throw 'No packages in dist\. Run tools\package.ps1 first (or use -Release).' }
    $what = 'dist\ (' + ((Get-Content (Join-Path $repo 'dist\BUILDINFO.txt') -ErrorAction SilentlyContinue | Select-String 'Source commit').Line) + ')'
    # stale only if the add-on or theme changed since the commit dist\ was built from (a docs-only commit does not matter)
    $built = ((Get-Content (Join-Path $repo 'dist\BUILDINFO.txt') -ErrorAction SilentlyContinue | Select-String 'Source commit: (\w+)') | ForEach-Object { $_.Matches[0].Groups[1].Value })
    if ($built) {
        $changed = @(& git -C $repo diff --name-only $built HEAD -- SeleneCore theme 2>$null)
        if ($LASTEXITCODE -ne 0) { Write-Warning 'Could not compare dist\ with the current code.' }
        elseif ($changed.Count -gt 0) { Write-Warning "dist\ is older than the code ($($changed.Count) files changed since it was built). Run tools\package.ps1 for the newest." }
        elseif (@(& git -C $repo status --porcelain -- SeleneCore theme).Count -gt 0) { Write-Warning 'There are uncommitted code changes that dist\ does not have.' }
    }
}

# 3. a pristine Playnite
if (-not $Keep -and (Test-Path $root)) { Remove-Item -LiteralPath $root -Recurse -Force }
if (-not (Test-Path (Join-Path $pn 'Playnite.FullscreenApp.exe'))) {
    $sevenZip = @('C:\Program Files\7-Zip\7z.exe', 'C:\Program Files (x86)\7-Zip\7z.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $sevenZip) { $sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source }
    if (-not $sevenZip) { throw 'Needs 7-Zip to unpack the Playnite download.' }
    if (-not (Test-Path $Playnite)) { throw "Playnite's portable download not found: $Playnite (get the .7z from playnite.link and pass -Playnite <file>)." }
    New-Item -ItemType Directory -Force $pn | Out-Null
    & $sevenZip x $Playnite "-o$pn" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unpacking Playnite failed.' }
}

# 4. install the two packages (what Playnite's own installer does with them: unzip into the theme / extension folder)
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Install-Package([string]$archive, [string]$target) {
    $targetRoot = [IO.Path]::GetFullPath($target).TrimEnd('\')
    if (-not $targetRoot.StartsWith($pn + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package target left the test folder.' }
    if (Test-Path $targetRoot) { Remove-Item -LiteralPath $targetRoot -Recurse -Force }
    New-Item -ItemType Directory -Force $targetRoot | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            if (-not $entry.Name) { continue }
            $file = [IO.Path]::GetFullPath((Join-Path $targetRoot $entry.FullName))
            if (-not $file.StartsWith($targetRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package path.' }
            New-Item -ItemType Directory -Force (Split-Path $file -Parent) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $file, $true)
        }
    } finally { $zip.Dispose() }
}
Install-Package $theme (Join-Path $pn "Themes\Fullscreen\$themeId")
Install-Package $core (Join-Path $pn "Extensions\$coreId")

# 5. settings of a new install: the wizard done (-Wizard: not even that), Selene chosen as the fullscreen theme
if (-not $Wizard -and -not (Test-Path (Join-Path $pn 'config.json'))) {
    '{ "FirstTimeWizardComplete": true, "DatabasePath": "{PlayniteDir}/library", "StartInFullscreen": false }' | Set-Content (Join-Path $pn 'config.json') -Encoding UTF8
}
if (-not (Test-Path (Join-Path $pn 'fullscreenConfig.json'))) { "{ `"Theme`": `"$themeId`" }" | Set-Content (Join-Path $pn 'fullscreenConfig.json') -Encoding UTF8 }
Remove-Item (Join-Path $pn 'safestart.flag') -ErrorAction SilentlyContinue

# 6. its own temp folder, so Selene's log is easy to find (no developer switch is ever written here)
$temp = Join-Path $root 'temp'
New-Item -ItemType Directory -Force $temp | Out-Null
Get-ChildItem $temp -Filter 'selene-dev-*' -ErrorAction SilentlyContinue | Remove-Item -Force
Remove-Item (Join-Path $temp 'selene.log') -ErrorAction SilentlyContinue
$env:TEMP = $temp; $env:TMP = $temp

"Selene as a new user gets it: $what"
"Folder: $root$(if ($Keep) { '  (kept: your earlier library and Selene settings are still there)' } else { '  (brand new)' })"
"Log:    $(Join-Path $temp 'selene.log')"
if ($PrepareOnly) { 'Prepared. Not started.'; return }
$exe = if ($Desktop -or $Wizard) { 'Playnite.DesktopApp.exe' } else { 'Playnite.FullscreenApp.exe' }
# --masterinstance: Playnite normally allows one copy per PC; this lets it run beside your own
Start-Process -FilePath (Join-Path $pn $exe) -ArgumentList '--masterinstance' -WorkingDirectory $pn
"Started $exe."
"Add a few games from Desktop mode (Add game > Manually; any .exe will do), then open Fullscreen: public-test.cmd keep"
