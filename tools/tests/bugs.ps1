$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$output = Join-Path $PSScriptRoot 'obj\bugs'
New-Item -ItemType Directory -Force $output | Out-Null
Copy-Item (Join-Path $repo 'SeleneCore\bin\SeleneCore.dll') $output -Force
$sdk = Join-Path $env:LOCALAPPDATA 'Playnite\Playnite.SDK.dll'
Copy-Item $sdk $output -Force
$refs = @('System.dll','System.Core.dll','System.Xaml.dll',"$framework\WPF\WindowsBase.dll","$framework\WPF\PresentationCore.dll","$framework\WPF\PresentationFramework.dll",$sdk,(Join-Path $output 'SeleneCore.dll')) | ForEach-Object { "/r:$_" }
& "$framework\csc.exe" /nologo /target:exe "/out:$output\Bugs.Check.exe" $refs (Join-Path $PSScriptRoot 'Bugs.Check.cs')
if ($LASTEXITCODE -ne 0) { throw 'Bug checks did not compile' }
& (Join-Path $output 'Bugs.Check.exe') $output (Join-Path $repo 'theme\Views\Main.xaml') (Join-Path $repo 'theme\Views\GameDetails.xaml')
if ($LASTEXITCODE -ne 0) { throw 'Bug checks failed' }
