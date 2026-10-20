param(
    [guid]$AdapterId = [guid]::Empty,
    [string]$ExpectedSsid,
    [string]$ExpectedMac,
    [string]$AssignedIPv6,
    [string]$Prefix,
    [string]$Gateway,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [ValidateSet('Roundtrip','Prepare','Resume','Restore')][string]$Stage = 'Roundtrip',
    [string]$SecondSsid
)
$ErrorActionPreference = 'Stop'
if ($Stage -in @('Roundtrip','Prepare')) {
    if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Use fresh evidence directory.' }
    if ($AdapterId -eq [guid]::Empty -or @($ExpectedSsid,$ExpectedMac,$AssignedIPv6,$Prefix,$Gateway).Where({[string]::IsNullOrWhiteSpace($_)}).Count) { throw 'Explicit adapter, SSID, MAC and approved currently assigned IPv6/prefix/gateway required.' }
    $mode = if ($Stage -eq 'Prepare') { '--ssid-manual-v6-boot-prepare' } else { '--ssid-manual-v6-roundtrip' }
    $auditArguments = @($mode, $AdapterId.ToString('D'), $ExpectedSsid, $ExpectedMac, $AssignedIPv6, $Prefix, $Gateway, $EvidenceDirectory)
    if ($SecondSsid) { $auditArguments += $SecondSsid }
} else {
    if (-not (Test-Path -LiteralPath (Join-Path $EvidenceDirectory 'checkpoint.json'))) { throw 'No existing checkpoint.' }
    if ($AdapterId -ne [guid]::Empty -or $ExpectedSsid -or $ExpectedMac -or $AssignedIPv6 -or $Prefix -or $Gateway -or $SecondSsid) { throw 'Resume/Restore only use the original checkpoint, not new settings.' }
    $auditArguments = @(('--ssid-manual-v6-boot-' + $Stage.ToLowerInvariant()), $EvidenceDirectory)
}
$exe = Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows\NetworkSettings.Tests.exe'
$log = $EvidenceDirectory + '.' + $Stage + '-' + [guid]::NewGuid().ToString('N') + '.process.txt'
if (Test-Path -LiteralPath $log) { throw 'Process log already exists.' }
try {
    & $exe @auditArguments 2>&1 | Tee-Object -FilePath $log
    $auditExit = $LASTEXITCODE
} catch {
    $_ | Out-String | Add-Content -LiteralPath $log
    $auditExit = 1
}
$auditExit | Set-Content -LiteralPath ($log + '.exit-code.txt')
exit $auditExit
