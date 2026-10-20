param([Parameter(Mandatory)][ValidateSet('Prepare','Resume')][string]$Stage,
    [Parameter(Mandatory)][string]$EvidenceDirectory, [guid]$AdapterId, [string]$ExpectedSsid,
    [switch]$ManualIPv4, [string]$ExpectedMac, [string]$ReservedIPv4, [string]$SubnetMask, [string]$Gateway)
$ErrorActionPreference = 'Stop'
$auditExe = Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows\NetworkSettings.Tests.exe'
if (-not (Test-Path -LiteralPath $auditExe)) { throw 'Build the audit harness first.' }
if ($Stage -eq 'Prepare') {
    if ($AdapterId -eq [guid]::Empty -or [string]::IsNullOrWhiteSpace($ExpectedSsid)) { throw 'Prepare requires explicit adapter GUID and authorized SSID.' }
    New-Item -ItemType Directory -Path $EvidenceDirectory -ErrorAction Stop | Out-Null
    [string[]]$auditArguments = @('--ssid-boot-prepare', $AdapterId.ToString('D'), $ExpectedSsid, $EvidenceDirectory)
    if ($ManualIPv4) {
        if ([string]::IsNullOrWhiteSpace($ExpectedMac) -or [string]::IsNullOrWhiteSpace($ReservedIPv4) -or [string]::IsNullOrWhiteSpace($SubnetMask) -or [string]::IsNullOrWhiteSpace($Gateway)) { throw 'Static prepare requires the user-reserved IPv4, MAC, subnet mask and gateway.' }
        $auditArguments = @('--ssid-static-boot-prepare', $AdapterId.ToString('D'), $ExpectedSsid, $ExpectedMac, $ReservedIPv4, $SubnetMask, $Gateway, $EvidenceDirectory)
    }
} else {
    if (-not (Test-Path -LiteralPath (Join-Path $EvidenceDirectory 'checkpoint.json'))) { throw 'No existing checkpoint; Resume never creates one.' }
    [string[]]$auditArguments = @('--ssid-boot-resume', $EvidenceDirectory)
}
$logPath = Join-Path $EvidenceDirectory ('process-' + $Stage + '-' + [guid]::NewGuid().ToString('N') + '.txt')
try {
    & $auditExe @auditArguments 2>&1 | Tee-Object -FilePath $logPath
    $auditExit = $LASTEXITCODE
} catch {
    $_ | Out-String | Add-Content -LiteralPath $logPath
    $auditExit = 1
}
$auditExit | Set-Content -LiteralPath ($logPath + '.exit-code.txt')
exit $auditExit
