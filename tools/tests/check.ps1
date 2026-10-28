$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$testExe = Join-Path ([IO.Path]::GetTempPath()) ('Selene.Check-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    $references = @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Xml.dll', "$framework\WPF\WindowsBase.dll", "$framework\WPF\PresentationCore.dll", "$framework\WPF\PresentationFramework.dll") | ForEach-Object { "/r:$_" }
    $references += (Join-Path $repo 'SeleneCore/src/GameTitleMatch.cs')
    & "$framework\csc.exe" /nologo /target:exe "/out:$testExe" $references (Join-Path $PSScriptRoot 'Localization.Check.cs') (Join-Path $repo 'SeleneCore\src\Lang.cs') (Join-Path $repo 'SeleneCore\src\BackgroundFit.cs') (Join-Path $repo 'SeleneCore\src\NewsFeeds.cs') (Join-Path $repo 'SeleneCore\src\StartupWork.cs') (Join-Path $repo 'SeleneCore\src\GameRowOrder.cs') (Join-Path $repo 'SeleneCore\src\InputHints.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Check compilation failed' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed' }
} finally { if (Test-Path -LiteralPath $testExe) { Remove-Item -LiteralPath $testExe } }
