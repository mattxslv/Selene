# Builds Selene Core and packs both parts into installable files with Playnite's Toolbox:
#   dist\Selene_<id>_<version>.pthm   (theme)       dist\SeleneCore_<id>_<version>.pext   (add-on)
#   plus copies named Selene.pthm and SeleneCore.pext for the GitHub release
#   -AllowDirty  a local test build from a working tree with uncommitted changes (never for a release)
param([switch]$AllowDirty)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'verify-package.ps1')
# A release is built from a clean, committed tree, so its BUILDINFO.txt names the exact source it came from.
$commit = $null
if (Get-Command git -ErrorAction SilentlyContinue) {
    $commit = & git -C $root rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the source commit' }
    $commit = $commit.Trim()
    $dirty = @(& git -C $root status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify whether the source tree is clean' }
    if ($dirty.Count -gt 0 -and -not $AllowDirty) { throw "The working tree has uncommitted changes ($($dirty.Count)). Commit or stash them, or pass -AllowDirty for a local test build." }
    if ($dirty.Count -gt 0) { $commit += ' (uncommitted changes)' }
} elseif (-not $AllowDirty) { throw 'git was not found, so the source commit cannot be recorded. Install git, or pass -AllowDirty for a local test build.' }
else { $commit = 'unknown (local test build without git)' }
$toolbox = Join-Path $env:LOCALAPPDATA 'Playnite\Toolbox.exe'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Get-ChildItem $dist -Include *.pthm, *.pext -Recurse | Remove-Item
foreach ($old in 'SHA256SUMS', 'BUILDINFO.txt') {
    if (Test-Path -LiteralPath (Join-Path $dist $old)) { Remove-Item -LiteralPath (Join-Path $dist $old) }
}

& (Join-Path $PSScriptRoot 'build.ps1')
$stage = Join-Path $root 'SeleneCore\obj\stage'   # always the fresh build (bin\ may be held open by a running Playnite)

& $toolbox pack (Join-Path $root 'theme') $dist
if ($LASTEXITCODE -ne 0) { throw 'Theme packaging failed' }
& $toolbox pack $stage $dist
if ($LASTEXITCODE -ne 0) { throw 'Add-on packaging failed' }
# Toolbox skips a theme's top-level Fonts folder (it treats fonts as Playnite's template files), so the icon and button
# fonts never reached users: add them to the package, then check that the parts the theme can't do without are in it
Add-Type -AssemblyName System.IO.Compression.FileSystem
$themes = @(Get-ChildItem $dist -Filter 'Selene_*.pthm')
if ($themes.Count -ne 1) { throw 'Expected exactly one theme package' }
$pthm = $themes[0].FullName
$zip = [System.IO.Compression.ZipFile]::Open($pthm, 'Update')
try {
    $fonts = Join-Path $root 'theme\Fonts'
    foreach ($f in Get-ChildItem $fonts -File) {
        # Toolbox versions that already include fonts must not create duplicate ZIP paths.
        foreach ($entry in @($zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -ieq "Fonts/$($f.Name)" })) { $entry.Delete() }
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, "Fonts\$($f.Name)", 'Optimal') | Out-Null
    }
    $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
} finally { $zip.Dispose() }
Test-SeleneThemePackage -Names $names
foreach ($need in 'Fonts\MaterialIcons-Regular.ttf', 'Fonts\PlayStation4.ttf', 'Fonts\XBOXONE.ttf', 'Fonts\icofont.ttf', 'Fonts\TitilliumWeb-Regular.ttf', 'Fonts\Lato-Regular.ttf', 'Fonts\Lato-Black.ttf',
                  'Audio\navigation.wav', 'Views\Main.xaml', 'Constants.xaml', 'Palettes\selene.xaml') {
    if ($names -notcontains $need.Replace('\', '/')) { throw "theme package is missing $need" }
}
"theme package: $($names.Count) files, fonts added"
# Keep the inert helper replacement in updates to overwrite the old elevated interpreter.
$addons = @(Get-ChildItem $dist -Filter 'SeleneCore_*.pext')
if ($addons.Count -ne 1) { throw 'Expected exactly one add-on package' }
$pext = $addons[0].FullName
Test-SeleneCorePackage -Package $pext -Stage $stage -Localization (Join-Path $root 'SeleneCore\Localization')
# fixed names for GitHub releases: the installer manifests (manifests\*.yaml) link to Selene.pthm and SeleneCore.pext
Get-ChildItem $dist -Filter 'Selene_*.pthm' | Copy-Item -Destination (Join-Path $dist 'Selene.pthm')
Get-ChildItem $dist -Filter 'SeleneCore_*.pext' | Copy-Item -Destination (Join-Path $dist 'SeleneCore.pext')
# Hash the final upload files, after all package modifications.
@('Selene.pthm', 'SeleneCore.pext') | ForEach-Object {
    $hash = Get-FileHash -LiteralPath (Join-Path $dist $_) -Algorithm SHA256
    '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), $_
} | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS') -Encoding ASCII
# What this build came from, to upload beside SHA256SUMS. The compiler and SDK are whatever is installed on this PC: record
# them (the legacy csc.exe cannot build deterministically, so a rebuild differs byte for byte even from the same source).
$csc = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sdkDll = Join-Path $env:LOCALAPPDATA 'Playnite\Playnite.SDK.dll'
$info = @(
    "Selene build information",
    "Source commit: $commit",
    "Local test mode: $([bool]$AllowDirty)",
    "Built: $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm')) UTC",
    "Compiler: csc.exe $((Get-Item -LiteralPath $csc).VersionInfo.ProductVersion)  sha256 $((Get-FileHash -LiteralPath $csc -Algorithm SHA256).Hash.ToLowerInvariant())",
    "Playnite SDK: $((Get-Item -LiteralPath $sdkDll).VersionInfo.FileVersion)  sha256 $((Get-FileHash -LiteralPath $sdkDll -Algorithm SHA256).Hash.ToLowerInvariant())",
    "Toolbox: $((Get-Item -LiteralPath $toolbox).VersionInfo.FileVersion)  sha256 $((Get-FileHash -LiteralPath $toolbox -Algorithm SHA256).Hash.ToLowerInvariant())",
    "Staged binaries:")
foreach ($f in 'SeleneCore.dll', 'SeleneHelper.exe') {
    $item = Get-Item -LiteralPath (Join-Path $stage $f)
    $info += "  $f  $($item.Length) bytes  sha256 $((Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
}
$info | Set-Content -LiteralPath (Join-Path $dist 'BUILDINFO.txt') -Encoding ASCII
Get-ChildItem $dist | ForEach-Object { "{0}  ({1:N0} KB)" -f $_.Name, ($_.Length / 1KB) }
