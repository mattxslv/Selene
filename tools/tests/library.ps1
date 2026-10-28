$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$output = Join-Path $PSScriptRoot 'obj\library'
New-Item -ItemType Directory -Force -Path $output | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'SeleneCore\bin\SeleneCore.dll') -Destination $output -Force
$runtime = Join-Path $env:USERPROFILE 'selene-fresh\Playnite'
$sdk = Join-Path $runtime 'Playnite.SDK.dll'
Copy-Item -LiteralPath $sdk -Destination $output -Force
$refs = @('System.dll','System.Core.dll','System.Xaml.dll','System.Web.Extensions.dll',"$framework\WPF\WindowsBase.dll","$framework\WPF\PresentationCore.dll","$framework\WPF\PresentationFramework.dll",$sdk,(Join-Path $output 'SeleneCore.dll')) | ForEach-Object { "/r:$_" }
& "$framework\csc.exe" /nologo /target:exe /platform:x86 "/out:$output\Library.Check.exe" $refs (Join-Path $PSScriptRoot 'Library.Check.cs')
if ($LASTEXITCODE -ne 0) { throw 'Library checks did not compile' }
& (Join-Path $output 'Library.Check.exe') $runtime (Join-Path $repo 'theme\DerivedStyles\ListGameItemStyle.xaml')
if ($LASTEXITCODE -ne 0) { throw 'Library checks failed' }
