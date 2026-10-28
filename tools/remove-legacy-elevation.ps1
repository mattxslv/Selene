# Windows-only migration. Default: report. Run task removal and file cleanup in separate sessions.
# -Remove: elevated, unregister reviewed tasks directly inside \Selene\; never access/delete user files.
# -CleanFiles: UNELEVATED, remove old request/log files in this user's profile.
# -CleanFiles -DeleteOldHelpers: also remove helper files marked as legacy by diagnostic heuristics.
# No tasks or samples are ever started. Defender and UAC settings are never changed.
param([switch]$Remove, [switch]$CleanFiles, [switch]$DeleteOldHelpers)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Run this on Windows.' }
if ($Remove -and $CleanFiles) { throw 'Run -Remove and -CleanFiles in separate PowerShell sessions.' }
if ($DeleteOldHelpers -and -not $CleanFiles) { throw '-DeleteOldHelpers requires unelevated -CleanFiles.' }
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($CleanFiles -and $isAdmin) { throw 'File cleanup must run from a normal, unelevated PowerShell.' }
if ($Remove -and -not $isAdmin) { throw 'Task removal requires an elevated PowerShell. Report first from your normal account.' }

# Only invoked from unelevated file-cleanup mode. Check the file AND all parent directories for links.
# This is defense in depth, not an atomic protection against races; no elevated token is used for deletion.
function Remove-PlainFile([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $cursor = $full
    while ($cursor) {
        try { $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop }
        catch { Write-Warning "Cannot inspect $cursor; skipping cleanup."; return }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Write-Warning "Skipping a linked path: $Path"; return
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
    if (Test-Path -LiteralPath $full -PathType Leaf) {
        Remove-Item -LiteralPath $full -Force
        Write-Host "Removed $full"
    }
}

# A failed enumeration is an error, never 'no tasks found'. Deletion uses structured cmdlet parameters.
$allTasks = @(Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.TaskPath.StartsWith('\Selene\', [StringComparison]::OrdinalIgnoreCase) })
$tasks = @($allTasks | Where-Object { $_.TaskPath -ieq '\Selene\' })
foreach ($nested in @($allTasks | Where-Object { $_.TaskPath -ine '\Selene\' })) {
    Write-Warning ("Nested task requires manual review and will not be removed: {0}{1}" -f $nested.TaskPath, $nested.TaskName)
}
Write-Host 'Scheduled tasks directly inside \Selene\ (review before using -Remove)'
foreach ($task in $tasks) {
    Write-Host ("  {0}{1} runs as {2}, level {3}" -f $task.TaskPath, $task.TaskName, $task.Principal.UserId, $task.Principal.RunLevel)
    foreach ($action in @($task.Actions)) { Write-Host ("      {0} {1} (working directory: {2})" -f $action.Execute, $action.Arguments, $action.WorkingDirectory) }
}
$runningHelpers = @(Get-Process -Name SeleneHelper -ErrorAction SilentlyContinue)
if ($runningHelpers.Count -gt 0) {
    Write-Warning ("Helper processes are still running (PIDs: {0}). Return to paused games, save, close games/Playnite normally, and verify these processes exit. Task removal does not stop them." -f (($runningHelpers | ForEach-Object { $_.Id }) -join ', '))
}
if ($CleanFiles -and $runningHelpers.Count -gt 0) { throw 'Close old helper sessions normally before file cleanup; do not terminate a helper while games are suspended.' }
if ($Remove) {
    foreach ($task in $tasks) {
        if ($task.TaskPath -ine '\Selene\') { throw 'Refusing a task outside the legacy folder.' }
        Unregister-ScheduledTask -TaskPath $task.TaskPath -TaskName $task.TaskName -Confirm:$false -ErrorAction Stop
        Write-Host ("Removed task {0}{1}" -f $task.TaskPath, $task.TaskName)
    }
    Write-Host 'Task cleanup complete. Use a normal PowerShell for -CleanFiles; restore direct Play actions in Playnite.'
    return
}
if ($isAdmin) { throw 'Use a normal PowerShell for file diagnostics and cleanup so this script uses the correct user profile.' }

# Names the old helper carried as text or imports; the retired stub has none. Keep in step with tools\verify-package.ps1.
$OldHelperMarkers = @('elevated.txt', 'helper.log', 'schtasks', '/create', 'freeze', 'thaw', 'OpenProcess', 'NtSuspendProcess',
    'TerminateProcess', 'SendInput', 'AttachThreadInput', 'System.IO', 'InteropServices')

function Get-HelperKind([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return 'missing' }
    if ((Get-Item -LiteralPath $Path).Length -gt 1048576) { return 'unknown (too large for helper inspection)' }
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -gt 10240) { $big = $true } else { $big = $false }
    $texts = @([Text.Encoding]::GetEncoding(28591).GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))
    if ($bytes.Length -gt 1) { $texts += [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1 - (($bytes.Length - 1) % 2)) }
    if ($texts[0].IndexOf('SeleneHelper', [StringComparison]::Ordinal) -lt 0) { return 'not a Selene helper' }
    foreach ($marker in $OldHelperMarkers) {
        foreach ($text in $texts) { if ($text.IndexOf($marker, [StringComparison]::Ordinal) -ge 0) { return 'OLD HELPER (can act on request files)' } }
    }
    if ($big) { return 'unknown (larger than the retired stub)' }
    return 'stub-like (heuristic only; compare its hash with the verified package)'
}

# Conservative ACL diagnostic only: ignores deny precedence, ownership, group nesting and more distant ancestors.
# A positive result is a reason to inspect effective access, not proof of exploitability; false does not prove safety.
function Test-NonAdminWritable([string]$Path) {
    $writers = [Security.AccessControl.FileSystemRights]'WriteData, CreateFiles, AppendData, Modify, FullControl, Write, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $broad = @('S-1-5-32-545', 'S-1-5-11', 'S-1-1-0', $me.Value)   # Users, Authenticated Users, Everyone, the account that set it up
    foreach ($target in @($Path, [IO.Path]::GetDirectoryName($Path))) {
        if (-not $target -or -not (Test-Path -LiteralPath $target)) { continue }
        foreach ($rule in (Get-Acl -LiteralPath $target).Access) {
            if ($rule.AccessControlType -ne 'Allow') { continue }
            try { $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { continue }
            if (($broad -contains $sid) -and (($rule.FileSystemRights -band $writers) -ne 0)) { return $true }
        }
    }
    return $false
}

$helperFiles = @{}
foreach ($task in $tasks) {
    foreach ($action in @($task.Actions)) {
        $exe = [Environment]::ExpandEnvironmentVariables([string]$action.Execute).Trim('"')
        if ($exe -and [IO.Path]::GetFileName($exe) -ieq 'SeleneHelper.exe') { $helperFiles[$exe] = Get-HelperKind $exe }
        if ($exe -and (Test-Path -LiteralPath $exe) -and (Test-NonAdminWritable $exe)) {
            Write-Warning "Potential writable task target (ACL heuristic, not an effective-access check): $exe"
        }
    }
}
Write-Host "SeleneHelper.exe files"
$roots = @((Join-Path $env:APPDATA 'Playnite\Extensions'), (Join-Path $env:LOCALAPPDATA 'Playnite\Extensions'), (Join-Path $env:LOCALAPPDATA 'Selene'))
foreach ($rootPath in $roots) {
    if (Test-Path -LiteralPath $rootPath) {
        foreach ($f in Get-ChildItem -LiteralPath $rootPath -Filter 'SeleneHelper.exe' -Recurse -File -ErrorAction SilentlyContinue) { if (-not $helperFiles.ContainsKey($f.FullName)) { $helperFiles[$f.FullName] = Get-HelperKind $f.FullName } }
    }
}
if ($helperFiles.Count -eq 0) { Write-Host '  none found in the usual folders (Playnite portable installs and custom folders are not searched)' }
foreach ($path in $helperFiles.Keys) {
    Write-Host ("  {0}  {1}" -f $path, $helperFiles[$path])
    if (Test-Path -LiteralPath $path -PathType Leaf) { Write-Host ("      sha256 {0}" -f (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()) }
}

$leftovers = @('elevated.txt', 'helper.log') | ForEach-Object { Join-Path (Join-Path $env:LOCALAPPDATA 'Selene') $_ } | Where-Object { Test-Path -LiteralPath $_ }
Write-Host "Old request and log files"
if (@($leftovers).Count -eq 0) { Write-Host '  none found' }
foreach ($f in $leftovers) { Write-Host "  $f" }

if (-not $CleanFiles) {
    Write-Host 'Report only. Review task actions, then use elevated -Remove for tasks, and normal -CleanFiles for this profile.'
    return
}
foreach ($f in $leftovers) { Remove-PlainFile $f }
if ($DeleteOldHelpers) {
    foreach ($path in $helperFiles.Keys) {
        if ($helperFiles[$path] -like 'OLD HELPER*') { Remove-PlainFile $path }
    }
}
Write-Host 'Unelevated file cleanup complete. Portable or custom installations may require manual inspection.'
