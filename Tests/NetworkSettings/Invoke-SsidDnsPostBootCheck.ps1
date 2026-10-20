param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Resume')][string]$Stage,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][string]$PythonPath,
    [guid]$AdapterId = [guid]::Empty,
    [string]$ExpectedSsid,
    [string]$ExpectedMac
)
# Read-only network verification. Never applies settings, reconnects or reboots.
$ErrorActionPreference = 'Stop'
if ($Stage -eq 'Prepare' -and ($AdapterId -eq [guid]::Empty -or [string]::IsNullOrWhiteSpace($ExpectedSsid) -or [string]::IsNullOrWhiteSpace($ExpectedMac))) { throw 'Prepare requires the explicit authorized GUID, SSID and MAC.' }
$manifest = Join-Path $EvidenceDirectory 'dns-boot-checkpoint.json'
$probe = Join-Path $PSScriptRoot 'probes\wifi_traffic_probe.py'
$resolverProbe = Join-Path $PSScriptRoot 'probes\wifi_dns_cloudflare_probe.py'
if (-not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) { throw 'Python executable missing.' }
$boot = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime.ToUniversalTime()
$checkpoint = $null
if ($Stage -eq 'Resume') {
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf) -or (Get-Item -LiteralPath $manifest).Length -gt 65536) { throw 'Missing/oversized checkpoint.' }
    $checkpoint = Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json
    if ($checkpoint.Schema -ne 1 -or $checkpoint.Machine -ne $env:COMPUTERNAME -or $checkpoint.Phase -ne 'AwaitBoot') { throw 'Wrong machine or finalized checkpoint.' }
    # PowerShell 7 may deserialize ISO dates as DateTime; Windows PowerShell 5 keeps strings.
    $previousBoot = if ($checkpoint.Boot -is [datetime]) { [DateTimeOffset]$checkpoint.Boot.ToUniversalTime() }
        else { [DateTimeOffset]::ParseExact([string]$checkpoint.Boot, 'O', [Globalization.CultureInfo]::InvariantCulture) }
    if (([DateTimeOffset]$boot - $previousBoot).TotalSeconds -lt 1) { throw 'No clearly newer Windows boot. Restart manually before Resume; no network changes made.' }
    $AdapterId = [guid]$checkpoint.AdapterId
    $ExpectedSsid = $checkpoint.Ssid
    $ExpectedMac = $checkpoint.Mac
    if ((Get-FileHash -LiteralPath $probe).Hash -ne $checkpoint.ProbeHash -or (Get-FileHash -LiteralPath $resolverProbe).Hash -ne $checkpoint.ResolverProbeHash) { throw 'Probe source changed; review before a new audit.' }
} elseif (Test-Path -LiteralPath $manifest) { throw 'Checkpoint already exists; do not overwrite prior evidence.' }

function Read-State {
    $adapter = @(Get-NetAdapter | Where-Object { [guid]$_.InterfaceGuid -eq $AdapterId })
    if ($adapter.Count -ne 1 -or $adapter[0].Status -ne 'Up' -or $adapter[0].MacAddress.Replace('-',':') -ne $ExpectedMac) { throw 'Target adapter/MAC is not connected.' }
    $index = $adapter[0].ifIndex
    $wlan = (& netsh wlan show interfaces | Out-String)
    if ($LASTEXITCODE -ne 0 -or $wlan -notmatch ('(?im)^\s*GUID\s*:\s*' + [regex]::Escape($AdapterId.ToString()) + '\s*$') -or $wlan -notmatch ('(?im)^\s*SSID\s*:\s*' + [regex]::Escape($ExpectedSsid) + '\s*$')) { throw 'Connected SSID differs or cannot be established.' }
    # This bounded host check refuses multi-WLAN ambiguity instead of pairing unrelated sections.
    if (@([regex]::Matches($wlan, '(?im)^\s*GUID\s*:')).Count -ne 1) { throw 'Multiple WLAN interfaces require an identity-scoped reader.' }
    $v4 = Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4
    $v6 = Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv6
    $addresses4 = @(Get-NetIPAddress -InterfaceIndex $index -AddressFamily IPv4 | Where-Object { $_.AddressState -eq 'Preferred' -and $_.PrefixOrigin -eq 'Dhcp' })
    $addresses6 = @(Get-NetIPAddress -InterfaceIndex $index -AddressFamily IPv6 | Where-Object { $_.AddressState -eq 'Preferred' -and $_.PrefixOrigin -eq 'RouterAdvertisement' -and $_.IPAddress -notlike 'fe80:*' })
    if (-not $addresses4.Count -or -not $addresses6.Count) { throw 'Preferred automatic IPv4/IPv6 addresses not available.' }
    [pscustomobject]@{
        Index = $index
        IPv4 = $addresses4[0].IPAddress
        IPv6 = $addresses6[0].IPAddress
        Dhcp4 = $v4.Dhcp.ToString()
        Dhcp6 = $v6.Dhcp.ToString()
        RouterDiscovery = $v6.RouterDiscovery.ToString()
        Dns4 = @((Get-DnsClientServerAddress -InterfaceIndex $index -AddressFamily IPv4).ServerAddresses)
        Dns6 = @((Get-DnsClientServerAddress -InterfaceIndex $index -AddressFamily IPv6).ServerAddresses)
        Gateway4 = @(Get-NetRoute -InterfaceIndex $index -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty NextHop | Sort-Object -Unique)
        Gateway6 = @(Get-NetRoute -InterfaceIndex $index -DestinationPrefix '::/0' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty NextHop | Sort-Object -Unique)
    }
}
function Same-Configuration($left, $right) {
    foreach ($property in @('Dhcp4','Dhcp6','RouterDiscovery','Dns4','Dns6','Gateway4','Gateway6')) {
        if (($left.$property -join '|') -cne ($right.$property -join '|')) { return $false }
    }
    return $true
}
$state = Read-State
if ($state.Dhcp4 -ne 'Enabled' -or $state.Dhcp6 -ne 'Enabled' -or $state.RouterDiscovery -ne 'Enabled' -or ($state.Dns6 -join ',') -ne '2606:4700:4700::1111,2606:4700:4700::1001' -or $state.Dns4.Count -lt 1 -or $state.Gateway4.Count -lt 1 -or $state.Gateway6.Count -lt 1) { throw 'Expected retained DNS / automatic addressing is not effective.' }
if ($Stage -eq 'Prepare') {
    if (-not (Test-Path -LiteralPath $EvidenceDirectory -PathType Container)) { New-Item -ItemType Directory -Path $EvidenceDirectory | Out-Null }
    $checkpoint = [pscustomobject]@{ Schema=1; Machine=$env:COMPUTERNAME; Phase='AwaitBoot'; Boot=([DateTimeOffset]$boot).ToString('O'); AdapterId=$AdapterId.ToString(); Ssid=$ExpectedSsid; Mac=$ExpectedMac; State=$state; ProbeHash=(Get-FileHash -LiteralPath $probe).Hash; ResolverProbeHash=(Get-FileHash -LiteralPath $resolverProbe).Hash }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($checkpoint | ConvertTo-Json -Depth 6))
    $file = [IO.File]::Open($manifest, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $file.Write($bytes, 0, $bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
    Write-Output 'AWAIT BOOT: read-only checkpoint saved. Restart manually when ready, reconnect the original SSID, then Resume. No reboot or network change was requested.'
    return
}
if (-not (Same-Configuration $state $checkpoint.State)) { throw 'Post-boot DNS/addressing modes/gateways changed. No reapply or correction performed.' }
$stamp = Get-Date -Format yyyyMMdd-HHmmss
$traffic = Join-Path $EvidenceDirectory ("postboot-traffic-$stamp.jsonl")
& $PythonPath $probe --index $state.Index --ipv4 $state.IPv4 --ipv6 $state.IPv6 --dns4 $state.Dns4[0] --dns6 $state.Dns6[0] --dns6-source $state.IPv6 | Tee-Object -FilePath $traffic
if ($LASTEXITCODE -ne 0) { throw 'Post-boot bound DNS/HTTPS incomplete; checkpoint remains AwaitBoot.' }
$resolvers = Join-Path $EvidenceDirectory ("postboot-resolvers-$stamp.jsonl")
& $PythonPath $resolverProbe --source $state.IPv6 --index $state.Index | Tee-Object -FilePath $resolvers
if ($LASTEXITCODE -ne 0) { throw 'One or both IPv6 resolvers failed; checkpoint remains AwaitBoot.' }
$after = Read-State
if (-not (Same-Configuration $state $after)) { throw 'Configuration changed during traffic checks; not a pass.' }
# Preserve the original checkpoint; a separate completion record retains the two boot identities.
$completed = Join-Path $EvidenceDirectory ("dns-boot-complete-$stamp.json")
[pscustomobject]@{ Status='VerifiedAfterBoot'; BeforeBoot=$checkpoint.Boot; AfterBoot=([DateTimeOffset]$boot).ToString('O'); State=$after; Traffic=$traffic; Resolvers=$resolvers } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $completed -Encoding UTF8
Write-Output "PASS: DNS persisted across a distinct boot; 12 DNS/HTTPS checks and eight dual-resolver queries passed. Read-only verification. Evidence: $completed"
