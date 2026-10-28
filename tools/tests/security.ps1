# Runs on Linux with pwsh, or Windows PowerShell. Does not elevate or control processes.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
foreach ($name in 'build.ps1', 'package.ps1', 'verify-package.ps1', 'collect-security-evidence.ps1', 'remove-legacy-elevation.ps1') {
    $tokens = $null; $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root "tools/$name"), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw ($errors | Out-String) }
}
$typeOptions = @{}
if ($PSVersionTable.PSVersion.Major -le 5) { $typeOptions.ReferencedAssemblies = @('System.dll', 'System.Core.dll', 'System.Xml.dll') }
Add-Type @typeOptions -Path @((Join-Path $root 'SeleneCore/helper/SeleneHelper.cs'), (Join-Path $root 'SeleneCore/src/LegacyElevation.cs'), (Join-Path $PSScriptRoot 'Security.Check.cs'))
"Elevation regression checks: $([SecurityCheck]::Run()) passed"

# ---- source guards: the elevated command helper must not come back ----
# Nothing in the add-on or helper may register a scheduled task, run anything with another user's rights, or name the old request
# file, except the one place that removes the old tasks (LegacyTasks.cs / LegacyElevation.cs).
$guards = 0
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'SeleneCore') -Filter '*.cs' -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' })
$forbidden = @(
    @{ Pattern = 'HighestAvailable'; Reason = 'a scheduled task with administrator rights'; Allow = @() },
    @{ Pattern = 'RunLevel'; Reason = 'a scheduled task run level'; Allow = @() },
    @{ Pattern = '/create\b'; Reason = 'schtasks /create'; Allow = @() },
    @{ Pattern = 'Microsoft\.Win32\.TaskScheduler|ITaskService|\bTaskService\b|Schedule\.Service'; Reason = 'the Task Scheduler API'; Allow = @() },
    @{ Pattern = 'RequestElevated|PauseViaHelper|WatchForAdmin|SetUpAdmin|DevSetUpHelper'; Reason = 'the old elevated entry points'; Allow = @() },
    @{ Pattern = 'elevated\.txt'; Reason = 'the old request file'; Allow = @('LegacyTasks.cs') },
    @{ Pattern = '\bVerb\s*=\s*"runas"'; Reason = 'an elevation prompt'; Allow = @('LegacyTasks.cs') },
    @{ Pattern = 'schtasks'; Reason = 'schtasks'; Allow = @('LegacyTasks.cs', 'LegacyElevation.cs') }
)
foreach ($rule in $forbidden) {
    foreach ($file in $sources) {
        if ($rule.Allow -contains $file.Name) { continue }
        $hit = Select-String -LiteralPath $file.FullName -Pattern $rule.Pattern -CaseSensitive:$false | Select-Object -First 1
        if ($hit) { throw "Source guard: $($file.Name):$($hit.LineNumber) mentions $($rule.Reason) ($($rule.Pattern))" }
        $guards++
    }
}
# the only elevation prompt is Windows' own schtasks.exe deleting an old \Selene\ task by a validated name
$tasks = Get-Content -LiteralPath (Join-Path $root 'SeleneCore/src/LegacyTasks.cs') -Raw
if ($tasks -notmatch 'LegacyElevation\.DeleteArguments\(') { throw 'Source guard: LegacyTasks must build its schtasks command with LegacyElevation.DeleteArguments' }
if (([regex]::Matches($tasks, 'Verb\s*=\s*"runas"')).Count -ne 1) { throw 'Source guard: LegacyTasks must have exactly one elevation prompt' }
if ($tasks -match 'schtasks[^\r\n]*"\s*/(create|change|run)\b') { throw 'Source guard: LegacyTasks may only delete tasks' }
$guards += 3
# the cleanup script deletes files only through Remove-PlainFile (no links, never elevated) and tasks only through Unregister-ScheduledTask
$cleanup = Get-Content -LiteralPath (Join-Path $root 'tools/remove-legacy-elevation.ps1') -Raw
if (([regex]::Matches($cleanup, 'Remove-Item')).Count -ne 1 -or $cleanup -notmatch '(?s)function Remove-PlainFile.*?Remove-Item') { throw 'Source guard: remove-legacy-elevation.ps1 may delete files only through Remove-PlainFile' }
if ($cleanup -notmatch 'if \(\$CleanFiles -and \$isAdmin\) \{ throw') { throw 'Source guard: -CleanFiles must refuse to run elevated' }
$onlySelene = '$_.TaskPath -ieq ''\Selene\'''
if (([regex]::Matches($cleanup, 'Unregister-ScheduledTask')).Count -ne 1 -or -not $cleanup.Contains($onlySelene)) { throw 'Source guard: the cleanup script must unregister only tasks under \Selene\' }
$guards += 3
# no manifest asking for administrator rights, and no executable other than the stub in the add-on sources
$manifests = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { ($_.Name -like '*.manifest' -or $_.Name -eq 'app.config') -and $_.FullName -notmatch '[\\/](obj|bin|\.git|linux-check)[\\/]' })
foreach ($m in $manifests) { if ((Get-Content -LiteralPath $m.FullName -Raw) -match 'requireAdministrator|highestAvailable') { throw "Source guard: $($m.Name) asks for administrator rights" } }
$guards++
$helperSources = @(Get-ChildItem -LiteralPath (Join-Path $root 'SeleneCore/helper') -File)
if ($helperSources.Count -ne 1 -or $helperSources[0].Name -ne 'SeleneHelper.cs') { throw 'Source guard: SeleneCore/helper may contain only the stub SeleneHelper.cs' }
if ((Get-Content -LiteralPath $helperSources[0].FullName).Count -gt 20) { throw 'Source guard: the helper stub has grown; it must stay a few lines' }
$guards += 2
"Source guards: $guards passed"

. (Join-Path $root 'tools/verify-package.ps1')
$checks = 0
function Assert-True($condition, $message) { if (!$condition) { throw $message }; $script:checks++ }
function Assert-Throws([scriptblock]$code, $message) {
    $threw = $false
    try { & $code | Out-Null } catch { $threw = $true }
    if (!$threw) { throw "Expected a rejection: $message" }
    $script:checks++
}

# ---- the cleanup script and the package check must look for the same old-helper names ----
function Get-MarkerList($file, $variable) {
    $text = Get-Content -LiteralPath (Join-Path $root $file) -Raw
    $m = [regex]::Match($text, [regex]::Escape($variable) + '\s*=\s*@\((?<list>.*?)\)', 'Singleline')
    if (!$m.Success) { throw "No marker list $variable in $file" }
    @([regex]::Matches($m.Groups['list'].Value, "'([^']+)'") | ForEach-Object { $_.Groups[1].Value } | Sort-Object)
}
$packageMarkers = Get-MarkerList 'tools/verify-package.ps1' '$script:RetiredHelperForbidden'
$cleanupMarkers = Get-MarkerList 'tools/remove-legacy-elevation.ps1' '$OldHelperMarkers'
Assert-True ($packageMarkers.Count -ge 10 -and (($packageMarkers -join '|') -ceq ($cleanupMarkers -join '|'))) 'verify-package.ps1 and remove-legacy-elevation.ps1 list different old-helper names'

# ---- the cleanup script deletes through links only never: Remove-PlainFile skips a path that passes through a junction or symbolic link ----
$cleanupText = Get-Content -LiteralPath (Join-Path $root 'tools/remove-legacy-elevation.ps1') -Raw
$function = [regex]::Match($cleanupText, '(?s)function Remove-PlainFile.*?\r?\n}\r?\n').Value
if (!$function) { throw 'Remove-PlainFile not found' }
. ([scriptblock]::Create($function))
$linkRoot = Join-Path ([IO.Path]::GetTempPath()) ('Selene-links-' + [guid]::NewGuid().ToString('N'))
try {
    $real = Join-Path $linkRoot 'real'; $link = Join-Path $linkRoot 'link'
    New-Item -ItemType Directory -Path $real -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $real 'elevated.txt'), 'x')
    [IO.File]::WriteAllText((Join-Path $real 'plain.txt'), 'x')
    $type = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $type -Path $link -Target $real | Out-Null
    Remove-PlainFile (Join-Path $link 'elevated.txt') *> $null
    Assert-True (Test-Path -LiteralPath (Join-Path $real 'elevated.txt')) 'A file reached through a link must not be deleted'
    Remove-PlainFile (Join-Path $real 'plain.txt') *> $null
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $real 'plain.txt'))) 'A plain file is deleted'
} finally { Remove-Item -LiteralPath $linkRoot -Recurse -Force }

# Exercise the real elevated-mode block with synthetic tasks and a mock unregister command.
$parsedCleanup = [System.Management.Automation.Language.Parser]::ParseInput($cleanupText, [ref]$null, [ref]$null)
$removeBlock = @($parsedCleanup.FindAll({ param($n)
    $n -is [System.Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -eq '$Remove'
}, $true))
Assert-True ($removeBlock.Count -eq 1) 'There must be one isolated task-removal block'
$commands = @($removeBlock[0].FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() })
foreach ($command in $commands) {
    Assert-True ($command -in @('Unregister-ScheduledTask', 'Write-Host')) 'Elevated removal must not call file cleanup or another program'
}
$script:deletedTestTasks = @()
function Unregister-ScheduledTask {
    param($TaskPath, $TaskName, $Confirm, $ErrorAction)
    $script:deletedTestTasks += $TaskPath + $TaskName
}
try {
    $Remove = $true
    $tasks = @([pscustomobject]@{ TaskPath = '\Selene\'; TaskName = 'Helper' })
    & ([scriptblock]::Create($removeBlock[0].Extent.Text)) *> $null
    Assert-True ($script:deletedTestTasks.Count -eq 1 -and $script:deletedTestTasks[0] -eq '\Selene\Helper') 'Remove the explicitly scoped test task'
    $tasks = @([pscustomobject]@{ TaskPath = '\Other\'; TaskName = 'Helper' })
    Assert-Throws { & ([scriptblock]::Create($removeBlock[0].Extent.Text)) } 'Never unregister an unrelated task'
    Assert-True ($script:deletedTestTasks.Count -eq 1) 'Unrelated task was untouched'
} finally { Remove-Item Function:\Unregister-ScheduledTask }

# ---- the helper bytes ----
$stub = [Text.Encoding]::ASCII.GetBytes('BSJB SeleneHelper mscorlib Main System.Diagnostics')   # what a real stub looks like to the check (see docs)
Test-RetiredHelperBytes -Bytes $stub -Label 'stub'; $checks++
foreach ($marker in 'elevated.txt', 'helper.log', 'schtasks', '/create', 'freeze', 'thaw', 'OpenProcess', 'NtSuspendProcess', 'TerminateProcess', 'SendInput', 'AttachThreadInput', 'System.IO', 'InteropServices') {
    $ascii = [Text.Encoding]::ASCII.GetBytes("SeleneHelper $marker")
    Assert-Throws { Test-RetiredHelperBytes -Bytes $ascii -Label 'helper' } "ASCII marker $marker"
    $utf16 = [Text.Encoding]::ASCII.GetBytes('SeleneHelper ') + [Text.Encoding]::Unicode.GetBytes($marker)
    Assert-Throws { Test-RetiredHelperBytes -Bytes $utf16 -Label 'helper' } "UTF-16 marker $marker (even alignment)"
    $odd = [Text.Encoding]::ASCII.GetBytes('SeleneHelper') + [byte[]](0) + [Text.Encoding]::Unicode.GetBytes($marker)
    Assert-Throws { Test-RetiredHelperBytes -Bytes $odd -Label 'helper' } "UTF-16 marker $marker (odd alignment)"
}
Assert-Throws { Test-RetiredHelperBytes -Bytes (New-Object byte[] 20000) -Label 'helper' } 'oversize helper'
Assert-Throws { Test-RetiredHelperBytes -Bytes ([byte[]]@()) -Label 'helper' } 'empty helper'
Assert-Throws { Test-RetiredHelperBytes -Bytes ([Text.Encoding]::ASCII.GetBytes('some other program')) -Label 'helper' } 'not a Selene helper'

# ---- the theme package ----
Test-SeleneThemePackage -Names @('Views/Main.xaml', 'Fonts/Lato-Black.ttf', 'Audio/navigation.wav', 'Avatars/00000-00000.jpg', 'theme.yaml', 'icon.png'); $checks++
foreach ($bad in 'Views//Main.xaml', 'Fonts/a.dll.', 'Scripts/a.ps1 ', '../outside.xaml', '/absolute.xaml', 'C:/drive.xaml', 'Views/../outside.xaml', 'Views/run.exe', 'Fonts/a.DLL', 'x.ps1', 'tools/b.bat', 'c.cmd', 'd.vbs', 'e.js', 'f.msi', 'g.lnk', 'h.zip', 'i.pext', 'Audio/j.scr', 'k.hta') {
    Assert-Throws { Test-SeleneThemePackage -Names @('Views/Main.xaml', $bad) } "theme entry $bad"
}

Assert-Throws { Test-SeleneThemePackage -Names @('Views/Main.xaml', 'views/MAIN.xaml') } 'case-insensitive duplicate theme paths'

# ---- the add-on package ----
$temp = Join-Path ([IO.Path]::GetTempPath()) ('Selene-package-check-' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $temp 'stage'
$localization = Join-Path $stage 'Localization'
New-Item -ItemType Directory -Path $localization -Force | Out-Null
$files = @{
    'SeleneCore.dll' = 'fresh core'; 'SeleneHelper.exe' = 'SeleneHelper retired stub';
    'extension.yaml' = 'Version: 1.3.1'; 'icon.png' = 'icon'; 'Localization/en_US.xaml' = 'localization'
}
foreach ($name in $files.Keys) { [IO.File]::WriteAllText((Join-Path $stage $name), $files[$name]) }
$package = Join-Path $temp 'core.pext'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
function Write-TestPackage($entries) {
    if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package }
    $zip = [IO.Compression.ZipFile]::Open($package, 'Create')
    try {
        foreach ($entry in $entries) {
            $item = $zip.CreateEntry($entry.Name)
            $writer = New-Object IO.StreamWriter($item.Open())
            try { $writer.Write($entry.Content) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
}
function Assert-Rejected($entries, $reason) {
    Write-TestPackage $entries
    $rejected = $false
    try { Test-SeleneCorePackage -Package $package -Stage $stage -Localization $localization | Out-Null }
    catch { $rejected = $true }
    if (!$rejected) { throw "Package was accepted: $reason" }
    $script:checks++
}
try {
    $entries = @($files.Keys | ForEach-Object { @{ Name = $_; Content = $files[$_] } })
    Write-TestPackage $entries
    Test-SeleneCorePackage -Package $package -Stage $stage -Localization $localization | Out-Null; $checks++
    Assert-Rejected @($entries | Where-Object { $_.Name -ne 'SeleneHelper.exe' }) 'missing replacement helper'
    Assert-Rejected ($entries + @{ Name = 'unexpected.exe'; Content = 'extra binary' }) 'unexpected binary'
    Assert-Rejected ($entries + @{ Name = '../outside.exe'; Content = 'traversal' }) 'path traversal'
    Assert-Rejected ($entries + $entries[0]) 'duplicate entry'
    $tampered = @($entries | ForEach-Object { @{ Name = $_.Name; Content = $(if ($_.Name -eq 'SeleneHelper.exe') { 'old unsafe helper' } else { $_.Content }) } })
    Assert-Rejected $tampered 'helper bytes differ from fresh build'
    Write-TestPackage @($entries | ForEach-Object { @{ Name = $_.Name.Replace('/', '\'); Content = $_.Content } })
    Test-SeleneCorePackage -Package $package -Stage $stage -Localization $localization | Out-Null; $checks++

    # an old or oversize helper, even when the fresh build "matches" it byte for byte (a stale or wrong staged file)
    foreach ($bad in 'SeleneHelper elevated.txt freeze thaw', ('SeleneHelper ' + ('x' * 20000)), 'a different program') {
        [IO.File]::WriteAllText((Join-Path $stage 'SeleneHelper.exe'), $bad)
        Assert-Rejected @($entries | ForEach-Object { @{ Name = $_.Name; Content = $(if ($_.Name -eq 'SeleneHelper.exe') { $bad } else { $_.Content }) } }) "helper content: $($bad.Substring(0, [Math]::Min(30, $bad.Length)))"
    }
    [IO.File]::WriteAllText((Join-Path $stage 'SeleneHelper.exe'), $files['SeleneHelper.exe'])
} finally { Remove-Item -LiteralPath $temp -Recurse -Force }
"Package regression checks: $checks passed (helper content, theme contents, valid, missing, unexpected, traversal, duplicate, tampered, Windows separators, stale helper)"
