$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$output = Join-Path $PSScriptRoot 'obj\interaction'
New-Item -ItemType Directory -Force -Path $output | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'SeleneCore\bin\SeleneCore.dll') -Destination $output -Force
$sdk = Join-Path $env:LOCALAPPDATA 'Playnite\Playnite.SDK.dll'
Copy-Item -LiteralPath $sdk -Destination $output -Force
$references = @('System.dll','System.Core.dll','System.Xaml.dll',"$framework\WPF\WindowsBase.dll","$framework\WPF\PresentationCore.dll","$framework\WPF\PresentationFramework.dll",$sdk,(Join-Path $output 'SeleneCore.dll')) | ForEach-Object { "/r:$_" }
& "$framework\csc.exe" /nologo /target:exe "/out:$output\Interaction.Check.exe" $references (Join-Path $PSScriptRoot 'Interaction.Check.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI checks did not compile' }
& (Join-Path $output 'Interaction.Check.exe') $output (Join-Path $repo 'theme\Views\Main.xaml')
if ($LASTEXITCODE -ne 0) { throw 'UI checks failed' }
