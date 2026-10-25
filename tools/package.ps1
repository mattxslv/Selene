# Builds Selene Core and packs both parts into installable files with Playnite's Toolbox:
#   dist\Selene_<id>_<version>.pthm   (theme)       dist\SeleneCore_<id>_<version>.pext   (add-on)
#   plus copies named Selene.pthm and SeleneCore.pext for the GitHub release
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$toolbox = Join-Path $env:LOCALAPPDATA 'Playnite\Toolbox.exe'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Get-ChildItem $dist -Include *.pthm, *.pext -Recurse | Remove-Item

& (Join-Path $PSScriptRoot 'build.ps1')
$stage = Join-Path $root 'SeleneCore\obj\stage'   # always the fresh build (bin\ may be held open by a running Playnite)

& $toolbox pack (Join-Path $root 'theme') $dist
& $toolbox pack $stage $dist
# Toolbox skips a theme's top-level Fonts folder (it treats fonts as Playnite's template files), so the icon and button
# fonts never reached users: add them to the package, then check that the parts the theme can't do without are in it
Add-Type -AssemblyName System.IO.Compression.FileSystem
$pthm = (Get-ChildItem $dist -Filter 'Selene_*.pthm' | Select-Object -First 1).FullName
$zip = [System.IO.Compression.ZipFile]::Open($pthm, 'Update')
try {
    $fonts = Join-Path $root 'theme\Fonts'
    foreach ($f in Get-ChildItem $fonts -File) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, "Fonts\$($f.Name)", 'Optimal') | Out-Null
    }
    $names = @($zip.Entries | ForEach-Object { $_.FullName })
} finally { $zip.Dispose() }
foreach ($need in 'Fonts\MaterialIcons-Regular.ttf', 'Fonts\PlayStation4.ttf', 'Fonts\XBOXONE.ttf', 'Fonts\icofont.ttf', 'Fonts\TitilliumWeb-Regular.ttf',
                  'Audio\navigation.wav', 'Views\Main.xaml', 'Constants.xaml', 'Palettes\selene.xaml') {
    if ($names -notcontains $need) { throw "theme package is missing $need" }
}
"theme package: $($names.Count) files, fonts added"
# fixed names for GitHub releases: the installer manifests (manifests\*.yaml) link to Selene.pthm and SeleneCore.pext
Get-ChildItem $dist -Filter 'Selene_*.pthm' | Copy-Item -Destination (Join-Path $dist 'Selene.pthm')
Get-ChildItem $dist -Filter 'SeleneCore_*.pext' | Copy-Item -Destination (Join-Path $dist 'SeleneCore.pext')
Get-ChildItem $dist | ForEach-Object { "{0}  ({1:N0} KB)" -f $_.Name, ($_.Length / 1KB) }
