# Build and run every local suite in a separate process; never launch the user's Playnite.
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$results = @()
$shell = (Get-Process -Id $PID).Path
function Check([string]$Name, [string]$File) {
    & $shell -NoProfile -ExecutionPolicy Bypass -File $File | Out-Host
    $script:results += [pscustomobject]@{ Check = $Name; Result = $(if ($LASTEXITCODE -eq 0) { 'PASS' } else { 'FAIL' }) }
    return ($LASTEXITCODE -eq 0)
}
if (-not $SkipBuild) {
    $ok = Check 'Windows build' (Join-Path $PSScriptRoot 'build.ps1')
    if (-not $ok) { $results | Format-Table; exit 1 }
}
foreach ($suite in 'check', 'bugs', 'interaction', 'library', 'security', 'backgroundchanger') {
    $null = Check $suite (Join-Path $PSScriptRoot "tests/$suite.ps1")
}
& python -m unittest discover -s (Join-Path $PSScriptRoot 'tests') -p 'test_*.py' -v
$results += [pscustomobject]@{ Check = 'Maintainer tooling'; Result = $(if ($LASTEXITCODE -eq 0) { 'PASS' } else { 'FAIL' }) }
& node --test (Join-Path $PSScriptRoot 'tests/release-notify.test.cjs')
$results += [pscustomobject]@{ Check = 'Release notifications'; Result = $(if ($LASTEXITCODE -eq 0) { 'PASS' } else { 'FAIL' }) }
$results | Format-Table -AutoSize
if (@($results | Where-Object Result -eq 'FAIL').Count) { exit 1 }
Write-Host 'All checks passed.'
