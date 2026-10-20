using System.Text;
using Naufal_Windows_Tech_s_Powertoys;

internal static class EthernetPolicyTests
{
    internal static async Task Run()
    {
        // Execute the actual production predicates and gate against in-memory
        // PowerShell functions. No live registry reads/writes or network changes.
        string script = NetworkSettingsScript.Common + "\n" + """
            $script:checks = 0
            function Assert($condition, $message) { if (-not $condition) { throw $message }; $script:checks++ }
            function New-Plan {
                [pscustomobject]@{ Scope='Adapter'; Category='Unchanged';
                    IPv4=[pscustomobject]@{AddressMode='Unchanged';DnsMode='Manual'};
                    IPv6=[pscustomobject]@{AddressMode='Unchanged';DnsMode='Unchanged'} }
            }
            $p = New-Plan
            $firewall = [pscustomobject]@{Name='NC_PersonalFirewallConfig';Kind='DWord';Value=0}
            foreach ($mode in @('Manual','Automatic')) {
                foreach ($family in @('IPv4','IPv6')) {
                    $p = New-Plan; $p.IPv4.DnsMode='Unchanged'; $p.$family.DnsMode=$mode
                    foreach ($value in @(0,1)) {
                        $firewall.Value = $value
                        Assert (Test-EthernetDnsPolicyValues $p @($firewall) $false) 'Reviewed DNS-only policy combination blocked'
                    }
                }
            }
            $p=New-Plan; $firewall.Value=0
            Assert (-not (Test-EthernetDnsPolicyValues $p @() $false)) 'Empty key exception broadened'
            Assert (-not (Test-EthernetDnsPolicyValues $p @($firewall) $true)) 'Subkey accepted'
            foreach ($entry in @(
                [pscustomobject]@{Name='NC_LanProperties';Kind='DWord';Value=0},
                [pscustomobject]@{Name='NC_AllowAdvancedTCPIPConfig';Kind='DWord';Value=0},
                [pscustomobject]@{Name='Unknown';Kind='DWord';Value=1},
                [pscustomobject]@{Name='NC_PersonalFirewallConfig';Kind='String';Value='0'},
                [pscustomobject]@{Name='NC_PersonalFirewallConfig';Kind='DWord';Value=2})) {
                Assert (-not (Test-EthernetDnsPolicyValues $p @($entry) $false)) 'Unknown or malformed policy accepted'
                Assert (-not (Test-EthernetDnsPolicyValues $p @($firewall,$entry) $false)) 'Mixed policy accepted'
            }
            foreach ($field in @('Scope','Category','IPv4Address','IPv6Address','NoDns','BadDns')) {
                $p=New-Plan
                switch ($field) {
                    Scope { $p.Scope='WifiSsid' }
                    Category { $p.Category='Private' }
                    IPv4Address { $p.IPv4.AddressMode='Automatic' }
                    IPv6Address { $p.IPv6.AddressMode='Manual' }
                    NoDns { $p.IPv4.DnsMode='Unchanged' }
                    BadDns { $p.IPv4.DnsMode='Invalid' }
                }
                Assert (-not (Test-EthernetDnsPolicyValues $p @($firewall) $false)) 'Exception leaked outside DNS-only scope'
            }
            Assert (-not (Test-EthernetDnsPolicyValues $null @($firewall) $false)) 'Missing plan accepted'

            # Shadow only read-side commands with deterministic in-memory evidence.
            $script:domain=$false; $script:paths=@(); $script:values=@($firewall); $script:subkeys=0; $script:denyRead=$false; $script:closed=0
            function Get-CimInstance { [pscustomobject]@{PartOfDomain=$script:domain} }
            function Test-Path { param($LiteralPath) return ($script:paths -contains $LiteralPath) }
            function Get-Item {
                param($LiteralPath,$ErrorAction)
                if ($script:denyRead) { throw 'Mock registry access denied' }
                $key=[pscustomobject]@{SubKeyCount=$script:subkeys}
                $key | Add-Member ScriptMethod GetValueNames { @($script:values | ForEach-Object { $_.Name }) }
                $key | Add-Member ScriptMethod GetValueKind { param($name) ($script:values | Where-Object Name -eq $name).Kind }
                $key | Add-Member ScriptMethod GetValue { param($name) ($script:values | Where-Object Name -eq $name).Value }
                $key | Add-Member ScriptMethod Close { $script:closed++ }
                return $key
            }
            function Gate-Passes($plan) { try { Check-LocalControl $plan; return $true } catch { return $false } }
            $p=New-Plan
            $machineConnection='HKLM:\SOFTWARE\Policies\Microsoft\Windows\Network Connections'
            Assert (Gate-Passes $p) 'Unmanaged no-policy gate rejected'
            $script:paths=@($machineConnection)
            Assert (Gate-Passes $p) 'Observed firewall-only guest policy rejected'
            Assert ($script:closed -eq 1) 'Read handle not closed'
            $p.IPv4.DnsMode='Automatic'
            Assert (Gate-Passes $p) 'DNS rollback blocked by irrelevant firewall policy'
            $p=New-Plan
            $script:domain=$true
            Assert (-not (Gate-Passes $p)) 'Domain gate bypassed'
            $script:domain=$false
            foreach ($path in @('HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient',
                                'HKCU:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient',
                                'HKCU:\SOFTWARE\Policies\Microsoft\Windows\Network Connections')) {
                $script:paths=@($machineConnection,$path)
                Assert (-not (Gate-Passes $p)) 'DNS/user policy bypassed'
            }
            $script:paths=@($machineConnection); $script:subkeys=1
            Assert (-not (Gate-Passes $p)) 'Unknown nested policy bypassed'
            $script:subkeys=0; $script:denyRead=$true
            Assert (-not (Gate-Passes $p)) 'Unreadable policy bypassed'
            $script:denyRead=$false; $script:values=@([pscustomobject]@{Name='Unknown';Kind='DWord';Value=1})
            Assert (-not (Gate-Passes $p)) 'Unknown policy bypassed'
            $script:values=@($firewall); $p.Category='Private'
            Assert (-not (Gate-Passes $p)) 'Category change received DNS exemption'
            Write-Output "PASS: $script:checks Ethernet DNS policy cases; mocked readers only, no registry/network changes."
            """;
        // Compress only this fixed test payload to fit Windows' command-line limit.
        using var compressed = new MemoryStream();
        using (var zip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Compress, true))
            zip.Write(Encoding.UTF8.GetBytes(script));
        string loader = "$m=[IO.MemoryStream]::new([Convert]::FromBase64String('" + Convert.ToBase64String(compressed.ToArray()) + "')); $g=[IO.Compression.GZipStream]::new($m,[IO.Compression.CompressionMode]::Decompress); $r=[IO.StreamReader]::new($g); try { & ([scriptblock]::Create($r.ReadToEnd())) } finally { $r.Dispose(); $g.Dispose(); $m.Dispose() }";
        var result = await new NativeCommandRunner().RunAsync(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(loader))], TimeSpan.FromSeconds(25));
        if (result.ExitCode != 0 || result.TimedOut || !result.StandardOutput.Contains("PASS: 39 Ethernet DNS policy cases"))
            throw new Exception("Ethernet policy regression failed: " + result.CombinedOutput);
        Console.WriteLine(result.StandardOutput.Trim());
        var plan = new NetworkSettingsProfile { Name = "VM DNS-only roundtrip", AdapterId = Guid.Empty.ToString(), IPv4 = new() { DnsMode = "Manual", PreferredDns = "192.168.150.2" } };
        string data = Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(plan, NetworkSettingsJson.Default.NetworkSettingsProfile)));
        string liveScript = NetworkSettingsScript.Common + "\n$p = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "')) | ConvertFrom-Json\n" + NetworkSettingsScript.Apply;
        int encodedLength = Convert.ToBase64String(Encoding.Unicode.GetBytes(liveScript)).Length;
        if (encodedLength + 512 >= 32767) throw new Exception("DNS-only command exceeds Windows process command-line budget: " + encodedLength);
        Console.WriteLine($"PASS: DNS-only command-line size guard ({encodedLength} encoded characters). No live command executed.");
    }
}
