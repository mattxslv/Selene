# Windows-only, read-only collection. Writes diagnostics locally; never executes or uploads a sample.
param(
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('Selene-security-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string[]]$FilePath = @()
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Run this collector on Windows.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory to preserve existing evidence.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
function Save-Evidence([string]$Name, [scriptblock]$Read) {
    try {
        $result = & $Read
        ConvertTo-Json -InputObject @($result) -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory ($Name + '.json')) -Encoding UTF8
    } catch {
        $_.Exception.Message | Set-Content -LiteralPath (Join-Path $OutputDirectory ($Name + '.error.txt')) -Encoding UTF8
    }
}
Save-Evidence 'defender-status' {
    Get-MpComputerStatus | Select-Object AMEngineVersion, AMProductVersion, AMRunningMode, AntivirusEnabled,
        RealTimeProtectionEnabled, AntivirusSignatureVersion, AntivirusSignatureLastUpdated
}
Save-Evidence 'defender-threats' { Get-MpThreat | Select-Object ThreatID, ThreatName, IsActive, DidThreatExecute, Resources }
Save-Evidence 'defender-detections' {
    Get-MpThreatDetection | Select-Object ThreatID, InitialDetectionTime, LastThreatStatusChangeTime,
        ActionSuccess, DetectionSourceTypeID, ProcessName, Resources
}
Save-Evidence 'selene-tasks' {
    Get-ScheduledTask | Where-Object { $_.TaskPath -ieq '\Selene\' } | ForEach-Object {
        [pscustomobject]@{
            TaskPath = $_.TaskPath; TaskName = $_.TaskName; State = [string]$_.State
            Xml = Export-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath
        }
    }
}
$index = 0
foreach ($file in $FilePath) {
    $index++
    # Immediate invocation avoids persisting closures over a later loop iteration.
    Save-Evidence "file-$index" {
        $item = Get-Item -LiteralPath $file
        if ($item.PSIsContainer) { throw 'Supply a file, not a directory.' }
        $signature = Get-AuthenticodeSignature -LiteralPath $item.FullName
        [pscustomobject]@{
            Path = $item.FullName; Bytes = $item.Length
            SHA256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
            SignatureStatus = [string]$signature.Status
            SignerSubject = $(if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null })
            FileAcl = (Get-Acl -LiteralPath $item.FullName).Sddl
            DirectoryAcl = (Get-Acl -LiteralPath $item.DirectoryName).Sddl
        }
    }
}
'Local evidence only. Review and redact private paths before sharing. No samples were executed or uploaded.' |
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'README.txt') -Encoding UTF8
"Evidence saved to $OutputDirectory"
