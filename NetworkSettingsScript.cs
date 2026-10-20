namespace Naufal_Windows_Tech_s_Powertoys;

// Fixed Windows cmdlets only. User data is decoded JSON, never executable text.
internal static class NetworkSettingsScript
{
    internal const string Common = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
        function Read-FamilyCore($a, $family) {
            $ipif = @(Get-NetIPInterface -InterfaceIndex $a.ifIndex -AddressFamily $family -ErrorAction Stop)
            if ($ipif.Count -ne 1) { throw 'Cannot determine adapter IP configuration.' }
            $service = if ($family -eq 'IPv4') { 'Tcpip' } else { 'Tcpip6' }
            $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $service + '\Parameters\Interfaces\{' + ([guid]$a.InterfaceGuid).ToString() + '}'
            $reg = Get-ItemProperty -LiteralPath $key -ErrorAction Stop
            $dns = Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily $family
            $addresses = @(Get-NetIPAddress -InterfaceIndex $a.ifIndex -AddressFamily $family)
            $routes = @(Get-NetRoute -InterfaceIndex $a.ifIndex -AddressFamily $family | Where-Object { $_.DestinationPrefix -in @('0.0.0.0/0','::/0') })
            $persistentAddresses = @(Get-NetIPAddress -PolicyStore PersistentStore | Where-Object { $_.InterfaceIndex -eq $a.ifIndex -and [string]$_.AddressFamily -eq $family -and $_.PrefixOrigin -eq 'Manual' -and $_.IPAddress -notlike 'fe80:*' } | ForEach-Object { $_.IPAddress + '/' + $_.PrefixLength } | Sort-Object)
            $activeAddresses = @($addresses | Where-Object { $_.PrefixOrigin -eq 'Manual' -and $_.IPAddress -notlike 'fe80:*' } | ForEach-Object { $_.IPAddress + '/' + $_.PrefixLength } | Sort-Object)
            $persistentRoutes = @(Get-NetRoute -PolicyStore PersistentStore | Where-Object { $_.InterfaceIndex -eq $a.ifIndex -and [string]$_.AddressFamily -eq $family -and $_.DestinationPrefix -in @('0.0.0.0/0','::/0') } | ForEach-Object { $_.NextHop } | Sort-Object)
            $activeRoutes = @($routes | Where-Object { [string]$_.Protocol -eq 'NetMgmt' } | ForEach-Object { $_.NextHop } | Sort-Object)
            [ordered]@{
                # Automatic default routes can be reported as NetMgmt too. They
                # are learned again by DHCP/RA, not persisted as static routes.
                PersistenceConsistent = (($persistentAddresses -join ',') -eq ($activeAddresses -join ',') -and $(if ($activeAddresses.Count -eq 0) { $persistentRoutes.Count -eq 0 } else { ($persistentRoutes -join ',') -eq ($activeRoutes -join ',') }))
                Dhcp = [string]$ipif[0].Dhcp; RouterDiscovery = [string]$ipif[0].RouterDiscovery
                Addresses = @($addresses | Where-Object { $_.IPAddress -notlike 'fe80:*' } | ForEach-Object {
                    [ordered]@{ Address = $_.IPAddress; Prefix = [int]$_.PrefixLength; Origin = [string]$_.PrefixOrigin; State = [string]$_.AddressState }
                })
                Gateways = @($routes | Select-Object -ExpandProperty NextHop)
                Dns = @($dns.ServerAddresses); DnsAutomatic = [string]::IsNullOrWhiteSpace([string]$reg.NameServer)
            }
        }
        function Read-Family($a, $family) {
            try { Read-FamilyCore $a $family }
            catch { [ordered]@{ ReadError = $_.Exception.Message; Dhcp = ''; RouterDiscovery = ''; Addresses = @(); Gateways = @(); Dns = @(); DnsAutomatic = $false } }
        }
        function Read-Adapters {
            foreach ($a in @(Get-NetAdapter -Physical | Where-Object { $_.InterfaceType -in @(6,71) } | Sort-Object Name)) {
                $category = @(Get-NetConnectionProfile -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue)
                [ordered]@{
                    Id = ([guid]$a.InterfaceGuid).ToString(); Index = [int]$a.ifIndex; Name = $a.Name
                    InterfaceType = [int]$a.InterfaceType
                    Description = $a.InterfaceDescription; Status = [string]$a.Status; Mac = $a.MacAddress; LinkSpeed = [string]$a.LinkSpeed
                    Category = if ($category.Count -eq 1) { [string]$category[0].NetworkCategory } else { 'Unavailable' }
                    IPv4 = Read-Family $a 'IPv4'; IPv6 = Read-Family $a 'IPv6'
                }
            }
        }
        function Test-EthernetDnsOnlyPlan($plan) {
            return ($null -ne $plan -and $plan.Scope -eq 'Adapter' -and $plan.Category -eq 'Unchanged' -and
                $plan.IPv4.AddressMode -eq 'Unchanged' -and $plan.IPv6.AddressMode -eq 'Unchanged' -and
                $plan.IPv4.DnsMode -in @('Unchanged','Automatic','Manual') -and
                $plan.IPv6.DnsMode -in @('Unchanged','Automatic','Manual') -and
                ($plan.IPv4.DnsMode -ne 'Unchanged' -or $plan.IPv6.DnsMode -ne 'Unchanged'))
        }
        function Test-EthernetDnsPolicyValues($plan, $values, $hasSubkeys) {
            # Narrow exemption for DNS-only writes. ADMX_NetworkConnections /
            # NC_PersonalFirewallConfig governs legacy Internet Connection Firewall,
            # not DNS addressing. Never delete or change the policy itself.
            if (-not (Test-EthernetDnsOnlyPlan $plan) -or @($values).Count -ne 1) { return $false }
            return (Test-SsidPolicyValues $values $hasSubkeys)
        }
        function Check-LocalControl($plan) {
            if (Test-EthernetDnsOnlyPlan $plan) { Check-SsidLocalControl $plan; return }
            $computer = Get-CimInstance Win32_ComputerSystem
            if ($computer.PartOfDomain) { throw 'Domain-managed computer: use the network administrator.' }
            foreach ($hive in @('HKLM:', 'HKCU:')) {
                foreach ($suffix in @('\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient', '\SOFTWARE\Policies\Microsoft\Windows\Network Connections')) {
                    $path = $hive + $suffix
                    if (Test-Path -LiteralPath $path) { throw "Network policy is present: $path. No changes made; use the policy administrator." }
                }
            }
        }
        function Test-SsidPolicyValues($values, $hasSubkeys) {
            if ($hasSubkeys) { return $false }
            foreach ($entry in @($values)) {
                # NetworkConnections.admx: this legacy firewall-only policy is
                # unrelated to per-SSID addressing. Never modify its value.
                if ($entry.Name -ne 'NC_PersonalFirewallConfig' -or $entry.Kind -ne 'DWord' -or $entry.Value -notin @(0,1)) { return $false }
            }
            return $true
        }
        function Check-SsidLocalControl($ethernetPlan = $null) {
            if ((Get-CimInstance Win32_ComputerSystem).PartOfDomain) { throw 'Domain-managed computer: use the network administrator.' }
            foreach ($hive in @('HKLM:', 'HKCU:')) {
                $dnsPolicy = $hive + '\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient'
                if (Test-Path -LiteralPath $dnsPolicy) { throw 'DNS policy is present; use the policy administrator.' }
                $connectionPolicy = $hive + '\SOFTWARE\Policies\Microsoft\Windows\Network Connections'
                if (Test-Path -LiteralPath $connectionPolicy) {
                    if ($null -ne $ethernetPlan -and $hive -ne 'HKLM:') { throw "User network policy present: $connectionPolicy. No changes made." }
                    $key = Get-Item -LiteralPath $connectionPolicy -ErrorAction Stop
                    try {
                        $values = @($key.GetValueNames() | ForEach-Object { [pscustomobject]@{ Name = $_; Kind = [string]$key.GetValueKind($_); Value = $key.GetValue($_) } })
                        if (-not (Test-SsidPolicyValues $values ($key.SubKeyCount -ne 0))) { throw 'Network configuration policy is present or unknown; use the policy administrator.' }
                        if ($null -ne $ethernetPlan -and -not (Test-EthernetDnsPolicyValues $ethernetPlan $values ($key.SubKeyCount -ne 0))) { throw 'DNS-only policy exception does not apply. No changes made.' }
                    } finally { $key.Close() }
                }
            }
        }
        """;

    // Apply resolves the adapter GUID in its own process. It removes selected-
    // family persistent/live static settings explicitly, so deleted live values
    // cannot reappear after restart. DNS uses family-specific CIM instances to
    // preserve the other family. Keep this explanation outside EncodedCommand:
    // Windows bounds its total command-line length, including the JSON plan.
    internal const string Apply = """
        if ($p.Scope -ne 'Adapter') { throw 'SSID profiles cannot use adapter-wide mutation.' }
        Check-LocalControl $p
        $matches = @(Get-NetAdapter -Physical | Where-Object { ([guid]$_.InterfaceGuid) -eq ([guid]$p.AdapterId) -and $_.InterfaceType -in @(6,71) })
        if ($matches.Count -ne 1) { throw 'Adapter identity is missing or ambiguous.' }
        $a = $matches[0]
        if ($a.InterfaceType -ne 6) { throw 'Adapter-wide IP/DNS changes are Ethernet-only. For Wi-Fi, open Manage known networks and edit the intended SSID. No Wi-Fi adapter properties were changed.' }
        $index = [int]$a.ifIndex
        foreach ($family in @('IPv4','IPv6')) {
            $f = $p.$family
            if ($f.AddressMode -eq 'Unchanged') { continue }
            $existing = @(Get-NetIPAddress -InterfaceIndex $index -AddressFamily $family | Where-Object { $_.PrefixOrigin -eq 'Manual' -and $_.IPAddress -notlike 'fe80:*' })
            if ($existing.Count -gt 1 -or @($existing | Where-Object { $_.SkipAsSource }).Count -gt 0) { throw 'Complex static configuration requires Windows advanced properties.' }
        }
        foreach ($family in @('IPv4','IPv6')) {
            $f = $p.$family
            if ($f.AddressMode -ne 'Unchanged') {
                Set-NetIPInterface -InterfaceIndex $index -AddressFamily $family -Dhcp Disabled
                if ($family -eq 'IPv6') { Set-NetIPInterface -InterfaceIndex $index -AddressFamily IPv6 -RouterDiscovery Disabled }
                foreach ($store in @('PersistentStore','ActiveStore')) {
                    Get-NetIPAddress -PolicyStore $store | Where-Object { $_.InterfaceIndex -eq $index -and [string]$_.AddressFamily -eq $family -and $_.PrefixOrigin -eq 'Manual' -and $_.IPAddress -notlike 'fe80:*' } | Remove-NetIPAddress -Confirm:$false
                    Get-NetRoute -PolicyStore $store | Where-Object { $_.InterfaceIndex -eq $index -and [string]$_.AddressFamily -eq $family -and $_.DestinationPrefix -in @('0.0.0.0/0','::/0') } | Remove-NetRoute -Confirm:$false
                }
                if ($f.AddressMode -eq 'Automatic') {
                    Set-NetIPInterface -InterfaceIndex $index -AddressFamily $family -Dhcp Enabled
                    if ($family -eq 'IPv6') { Set-NetIPInterface -InterfaceIndex $index -AddressFamily IPv6 -RouterDiscovery Enabled }
                } else {
                    $prefix = 0
                    if ($family -eq 'IPv6') { $prefix = [int]$f.Subnet }
                    else { foreach ($byte in ([ipaddress]$f.Subnet).GetAddressBytes()) { for ($bit = 7; $bit -ge 0; $bit--) { if ($byte -band (1 -shl $bit)) { $prefix++ } } } }
                    $args = @{ InterfaceIndex = $index; AddressFamily = $family; IPAddress = [string]$f.Address; PrefixLength = $prefix }
                    if ($f.Gateway) { $args.DefaultGateway = [string]$f.Gateway }
                    New-NetIPAddress @args | Out-Null
                }
            }
            if ($f.DnsMode -ne 'Unchanged') {
                $dns = Get-DnsClientServerAddress -InterfaceIndex $index -AddressFamily $family
                if ($f.DnsMode -eq 'Automatic') { $dns | Set-DnsClientServerAddress -ResetServerAddresses }
                else { $dns | Set-DnsClientServerAddress -ServerAddresses @(@($f.PreferredDns, $f.AlternateDns) | Where-Object { $_ }) }
            }
        }
        if ($p.Category -ne 'Unchanged') {
            $connection = @(Get-NetConnectionProfile -InterfaceIndex $index)
            if ($connection.Count -ne 1 -or $connection[0].NetworkCategory -eq 'DomainAuthenticated') { throw 'Connection category is not locally editable.' }
            $connection[0] | Set-NetConnectionProfile -NetworkCategory $p.Category
        }
        """;
}
