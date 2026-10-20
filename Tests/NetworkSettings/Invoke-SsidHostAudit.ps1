param([Parameter(Mandatory)][string]$EvidenceDirectory, [switch]$Live,
    [guid]$AdapterId, [string]$ExpectedSsid, [switch]$Reconnect, [string]$SecondSsid)
$ErrorActionPreference = 'Stop'
if ($Live -and ($AdapterId -eq [guid]::Empty -or [string]::IsNullOrWhiteSpace($ExpectedSsid))) { throw 'Live audit requires an explicit AdapterId and ExpectedSsid. Obtain consent before running.' }
if ($Reconnect -and (-not $Live -or [string]::IsNullOrWhiteSpace($SecondSsid))) { throw 'Reconnect audit requires Live and an explicitly authorized SecondSsid.' }
$auditExe = Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows\NetworkSettings.Tests.exe'
if (-not (Test-Path -LiteralPath $auditExe)) { throw 'Build the explicit audit harness first.' }
New-Item -ItemType Directory -Path $EvidenceDirectory -ErrorAction Stop | Out-Null
try {
    [string[]]$auditArguments = if ($Reconnect) { @('--ssid-reconnect-roundtrip', $AdapterId.ToString('D'), $ExpectedSsid, $SecondSsid, $EvidenceDirectory) } elseif ($Live) { @('--ssid-live-roundtrip', $AdapterId.ToString('D'), $ExpectedSsid, $EvidenceDirectory) } else { @('--read-only-probe') }
    & $auditExe @auditArguments 2>&1 |
        Tee-Object -FilePath (Join-Path $EvidenceDirectory 'process.txt')
    $auditExit = $LASTEXITCODE
} catch {
    $_ | Out-String | Add-Content -LiteralPath (Join-Path $EvidenceDirectory 'process.txt')
    $auditExit = 1
}
$auditExit | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'exit-code.txt')
exit $auditExit
