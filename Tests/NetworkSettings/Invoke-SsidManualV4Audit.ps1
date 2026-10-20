param(
    [Parameter(Mandatory)][guid]$AdapterId,
    [Parameter(Mandatory)][string]$ExpectedSsid,
    [Parameter(Mandatory)][string]$ExpectedMac,
    [Parameter(Mandatory)][string]$ReservedIPv4,
    [Parameter(Mandatory)][string]$SubnetMask,
    [Parameter(Mandatory)][string]$Gateway,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][string]$SecondSsid
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Use a fresh evidence directory.' }
$exe = Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows\NetworkSettings.Tests.exe'
$processLog = $EvidenceDirectory + '.process.txt'
if (Test-Path -LiteralPath $processLog) { throw 'Process log already exists.' }
try {
    & $exe '--ssid-manual-v4-roundtrip' $AdapterId.ToString('D') $ExpectedSsid $ExpectedMac $ReservedIPv4 $SubnetMask $Gateway $EvidenceDirectory $SecondSsid 2>&1 | Tee-Object -FilePath $processLog
    $auditExit = $LASTEXITCODE
} catch {
    $_ | Out-String | Add-Content -LiteralPath $processLog
    $auditExit = 1
}
$auditExit | Set-Content -LiteralPath ($EvidenceDirectory + '.exit-code.txt')
exit $auditExit
