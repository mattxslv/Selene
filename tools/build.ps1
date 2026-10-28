# Builds Selene Core with the C# compiler that ships with Windows (.NET Framework 4.x), no Visual Studio needed.
# Output: SeleneCore\bin\ (SeleneCore.dll + extension.yaml + icon + localization) - Playnite loads it from there in dev
# (Settings > For developers > custom extensions), or `Toolbox.exe pack` turns it into an installable .pext.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$core = Join-Path $root 'SeleneCore'
$out = Join-Path $core 'bin'
$stage = Join-Path $core 'obj\stage'  # Playnite keeps bin\SeleneCore.dll open while running
$fw = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319'
$sdk = Join-Path $env:LOCALAPPDATA 'Playnite\Playnite.SDK.dll'

# A release must not inherit binaries or resources left by an older build.
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force $out, $stage | Out-Null
$versionText = Get-Content -LiteralPath (Join-Path $core 'extension.yaml') -Raw
if ($versionText -notmatch '(?m)^Version:\s*(\d+\.\d+\.\d+)\s*$') { throw 'Invalid extension version' }
$version = $Matches[1]
$metadata = Join-Path $core 'obj\ReleaseMetadata.cs'
@"
using System.Reflection;
[assembly: AssemblyCompany("Selene contributors")]
[assembly: AssemblyProduct("Selene Core")]
[assembly: AssemblyVersion("$version.0")]
[assembly: AssemblyFileVersion("$version.0")]
[assembly: AssemblyInformationalVersion("$version")]
"@ | Set-Content -LiteralPath $metadata -Encoding UTF8
$sources = @(Get-ChildItem $core -Filter *.cs) + @(Get-ChildItem (Join-Path $core 'src') -Filter *.cs) | ForEach-Object { $_.FullName }
$refs = @('System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Xaml.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll',
          'Microsoft.CSharp.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll', "$fw\WPF\WindowsBase.dll", "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll", $sdk) |
        ForEach-Object { "/r:$_" }
& "$fw\csc.exe" /nologo /target:library /optimize+ /nowarn:1701,1702 "/out:$stage\SeleneCore.dll" $refs $sources $metadata
if ($LASTEXITCODE -ne 0) { throw "build failed" }
# Retired helper: ship the inert replacement so old task paths cannot retain the command interpreter.
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu "/out:$stage\SeleneHelper.exe" /r:System.dll /r:System.Core.dll (Join-Path $core 'helper\SeleneHelper.cs') $metadata
if ($LASTEXITCODE -ne 0) { throw "helper build failed" }
Copy-Item (Join-Path $core 'extension.yaml'), (Join-Path $core 'icon.png') $stage -Force
Copy-Item (Join-Path $core 'Localization') $stage -Recurse -Force
# Playnite keeps the loaded DLL open, but a file in use can still be renamed: move it aside and put the new one in its
# place, so the next Playnite start loads this build however Playnite is restarted
$dll = Join-Path $out 'SeleneCore.dll'
Get-ChildItem $out -Filter 'SeleneCore.dll.old-*' -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
try { Copy-Item "$stage\*" $out -Recurse -Force -ErrorAction Stop; "built and installed $dll" }
catch {
    Rename-Item $dll ('SeleneCore.dll.old-' + (Get-Date -Format 'HHmmss')) -ErrorAction Stop
    Copy-Item "$stage\*" $out -Recurse -Force
    "built and installed $dll (Playnite is running: it loads this build the next time it starts)"
}
