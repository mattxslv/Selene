# Shared by packaging and cross-platform regression checks. Throws on any mismatch.

# The retired SeleneHelper.exe must stay an inert stub. The old helper (11 KB) carried these names as text or imports; a
# sanity check rejects the known old implementation; it is not proof that arbitrary bytes are inert.
# It complements clean-source builds and package/stage hash comparison. Names are looked for as ASCII and as
# UTF-16 at either byte alignment.
$script:RetiredHelperMaxBytes = 10240
$script:RetiredHelperForbidden = @('elevated.txt', 'helper.log', 'schtasks', '/create', 'freeze', 'thaw', 'OpenProcess', 'NtSuspendProcess',
    'TerminateProcess', 'SendInput', 'AttachThreadInput', 'System.IO', 'InteropServices')
function Test-RetiredHelperBytes {
    param([byte[]]$Bytes, [string]$Label)
    if ($Bytes.Length -eq 0 -or $Bytes.Length -gt $script:RetiredHelperMaxBytes) {
        throw "$Label is $($Bytes.Length) bytes; the retired helper stub is a few KB"
    }
    $texts = @([Text.Encoding]::GetEncoding(28591).GetString($Bytes), [Text.Encoding]::Unicode.GetString($Bytes),
               [Text.Encoding]::Unicode.GetString($Bytes, 1, $Bytes.Length - 1 - (($Bytes.Length - 1) % 2)))
    foreach ($name in $script:RetiredHelperForbidden) {
        foreach ($text in $texts) {
            if ($text.IndexOf($name, [StringComparison]::Ordinal) -ge 0) { throw "$Label contains '$name': it is not the retired helper stub" }
        }
    }
    if ($texts[0].IndexOf('SeleneHelper', [StringComparison]::Ordinal) -lt 0) { throw "$Label is not a Selene helper build" }
}

# The theme package carries no programs, scripts or archives (only XAML, fonts, sounds and pictures).
function Test-SeleneThemePackage {
    param([string[]]$Names)
    $forbidden = '\.(exe|dll|msi|scr|com|bat|cmd|ps1|psm1|vbs|vbe|js|jse|wsf|hta|jar|lnk|sys|cpl|reg|zip|7z|rar|pext|pthm)$'
    $seen = @{}
    foreach ($name in $Names) {
        $relative = $name.Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative.StartsWith('/') -or $relative.Contains(':') -or
            $relative -match '(^|/)\.\.?(/|$)|//|(^|/)[^/]*[. ](/|$)' -or $relative -match '[\x00-\x1f]') { throw "Unsafe theme entry: $name" }
        if ($seen.ContainsKey($relative)) { throw "Duplicate theme entry: $name" }
        $seen[$relative] = $true
        if ($name -imatch $forbidden) { throw "Unexpected program or archive in the theme package: $name" }
    }
}

function Test-SeleneCorePackage {
    param([string]$Package, [string]$Stage, [string]$Localization)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $coreNames = @($zip.Entries | ForEach-Object { $_.FullName })
        foreach ($entry in $zip.Entries) {
            $relative = $entry.FullName.Replace('\', '/')
            if ($relative -notmatch '^(SeleneCore\.dll|SeleneHelper\.exe|extension\.yaml|icon\.png|Localization/[^/]+\.xaml)$') {
                throw "Unexpected add-on entry: $relative"
            }
            $source = Join-Path $Stage $relative
            if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "No staged source for $relative" }
            $stream = $entry.Open()
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ($actual -ne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash) {
                throw "Packaged bytes differ from the fresh build: $relative"
            }
            if ($relative -eq 'SeleneHelper.exe') {
                if ($entry.Length -gt $script:RetiredHelperMaxBytes) { throw "Packaged SeleneHelper.exe is $($entry.Length) bytes; the retired helper stub is a few KB" }
                $buffer = New-Object IO.MemoryStream
                $reader = $entry.Open()
                try { $reader.CopyTo($buffer) } finally { $reader.Dispose() }
                try { Test-RetiredHelperBytes -Bytes $buffer.ToArray() -Label 'packaged SeleneHelper.exe' }
                finally { $buffer.Dispose() }
            }
        }
    } finally { $zip.Dispose() }
    foreach ($need in 'SeleneCore.dll', 'SeleneHelper.exe', 'extension.yaml') {
        if ($coreNames -notcontains $need) { throw "add-on package is missing $need" }
    }
    $expected = @('SeleneCore.dll', 'SeleneHelper.exe', 'extension.yaml', 'icon.png') +
        @(Get-ChildItem -LiteralPath $Localization -Filter '*.xaml' -File | ForEach-Object { 'Localization/' + $_.Name })
    $normalized = @($coreNames | ForEach-Object { $_.Replace('\', '/') })
    if ($normalized.Count -ne $expected.Count -or @($normalized | Select-Object -Unique).Count -ne $normalized.Count -or
        @(Compare-Object $expected $normalized).Count -ne 0) { throw 'Add-on package inventory differs from release sources' }
    "add-on package: $($coreNames.Count) files, replacement helper verified"
}
