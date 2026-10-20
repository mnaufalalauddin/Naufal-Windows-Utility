using Naufal_Windows_Tech_s_Powertoys;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text.Json;

// Explicitly authorized DNS-only per-SSID operation. Default roundtrip restores;
// the separate apply-retain mode retains only after successful verification.
internal static class WifiSsidDnsV6Audit
{
    internal static async Task Run(string[] args)
    {
        if (args.Length != 7) throw new ArgumentException("Expected mode, GUID, exact SSID, MAC, fresh evidence directory, Python executable and reviewed probe script.");
        bool retainRequested = args[0] == "--ssid-dns-v6-apply-retain", retained = false;
        string id = Guid.Parse(args[1]).ToString("D"), directory = Path.GetFullPath(args[4]);
        if (Directory.Exists(directory)) throw new IOException("Use fresh evidence directory.");
        if (!File.Exists(args[5]) || !File.Exists(args[6])) throw new IOException("Missing reviewed traffic probe.");
        Directory.CreateDirectory(directory);
        using var log = new StreamWriter(new FileStream(Path.Combine(directory, "audit.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        void Note(string s) { log.WriteLine(DateTimeOffset.Now.ToString("O") + " " + s); Console.WriteLine(s); }
        var network = new NetworkSettingsService();
        await network.PreflightWifiAsync(id);
        var service = new WifiSsidService(Path.Combine(directory, "snapshots"));
        var native = new WifiSsidNative();
        var before = service.Read(id);
        var all = await network.ReadAsync();
        var original = all.Single(a => NetworkSettingsService.SameId(a.Id, id));
        static string Mac(string s) => s.Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (before.Target.DisplaySsid != args[2] || before.IPv6 is not null || original.InterfaceType != 71 || Mac(original.Mac) != Mac(args[3]) || !all.Any(a => a.InterfaceType == 6 && a.Status == "Up")) throw new IOException("Target, baseline metadata or fallback mismatch.");
        if (original.IPv4.Dhcp != "Enabled" || original.IPv6.Dhcp != "Enabled" || original.IPv6.RouterDiscovery != "Enabled" || !string.IsNullOrEmpty(original.IPv6.ReadError) || !string.IsNullOrEmpty(original.IPv4.ReadError)) throw new IOException("Automatic readable baseline required.");
        string Hash(byte[]? b) => b is null ? "absent" : Convert.ToHexString(SHA256.HashData(b));
        var profiles = WifiProfileInventory.Read(id);
        string Others() => string.Join("|", profiles.Where(p => p.ProfileName != before.Target.ProfileName).OrderBy(p => p.ProfileName).Select(p => p.ProfileName + ":" + Hash(native.Read(p, false)) + ":" + Hash(native.Read(p, true))));
        string Global() => JsonSerializer.Serialize(all.OrderBy(a => a.Id).SelectMany(a => new[] { "Tcpip", "Tcpip6" }.SelectMany(protocol =>
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{protocol}\Parameters\Interfaces\{{{Guid.Parse(a.Id):D}}}");
            return new[] { "NameServer", "EnableDHCP", "IPAddress", "SubnetMask", "DefaultGateway" }.Select(name => new { a.Id, protocol, name, Value = key?.GetValue(name), Kind = key?.GetValueNames().Contains(name) == true ? key.GetValueKind(name).ToString() : "Absent" }).ToArray();
        })));
        string global = Global(), others = Others();
        File.WriteAllText(Path.Combine(directory, "global-before.json"), global);
        void Save(string name, NetworkAdapterState a) => File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new[] { a }, NetworkSettingsJson.Default.NetworkAdapterStateArray));
        Save("runtime-before", original);
        async Task<NetworkAdapterState> Current()
        {
            if (!WifiSsidService.SameTarget(native.Connected(id), before.Target)) throw new IOException("SSID changed.");
            var scan = await network.ReadAsync();
            var a = scan.Single(a => NetworkSettingsService.SameId(a.Id, id));
            if (!WifiSsidService.SameTarget(native.Connected(id), before.Target) || Mac(a.Mac) != Mac(args[3]) || !scan.Any(a => a.InterfaceType == 6 && a.Status == "Up")) throw new IOException("Identity or Ethernet fallback changed.");
            return a;
        }
        async Task Reconnect()
        {
            var runner = new NativeCommandRunner();
            foreach (var command in new[] { new[] { "wlan", "disconnect", "interface=" + original.Name }, new[] { "wlan", "connect", "name=" + before.Target.ProfileName, "interface=" + original.Name } })
            {
                var r = await runner.RunAsync(Path.Combine(Environment.SystemDirectory, "netsh.exe"), command, TimeSpan.FromSeconds(15));
                if (r.TimedOut || r.ExitCode != 0) throw new IOException("Reconnect failed: " + r.CombinedOutput);
            }
        }
        async Task<NetworkAdapterState> Wait(string[] dns)
        {
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                try
                {
                    var a = await Current();
                    Save("runtime-last", a);
                    if (a.Status == "Up" && string.IsNullOrEmpty(a.IPv6.ReadError) && string.IsNullOrEmpty(a.IPv4.ReadError) && a.IPv6.Dns.SequenceEqual(dns) && a.IPv6.Dhcp == original.IPv6.Dhcp && a.IPv6.RouterDiscovery == original.IPv6.RouterDiscovery && a.IPv6.Gateways.SequenceEqual(original.IPv6.Gateways) && a.IPv6.Addresses.Any(v => v.Origin == "RouterAdvertisement" && v.State == "Preferred") && a.IPv4.Dhcp == original.IPv4.Dhcp && a.IPv4.Dns.SequenceEqual(original.IPv4.Dns) && a.IPv4.Gateways.SequenceEqual(original.IPv4.Gateways)) return a;
                }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { }
            }
            throw new IOException("Expected effective DNS/automatic addressing did not settle.");
        }
        string[] testDns = ["2606:4700:4700::1111", "2606:4700:4700::1001"];
        try
        {
            var plan = new NetworkSettingsProfile { Scope = "WifiSsid", AdapterId = id, WifiProfileName = before.Target.ProfileName, SsidHex = before.Target.SsidHex,
                IPv6 = new() { DnsMode = "Manual", PreferredDns = testDns[0], AlternateDns = testDns[1] } };
            Note("PREPARED: authorized Cloudflare DNS-only " + (retainRequested ? "apply and retain after verification" : "temporary test") + "; IPv4/addressing untouched; exact snapshot before mutation.");
            Note(await service.ApplyAsync(plan, before));
            var saved = service.Read(id);
            var decoded = WifiSsidCodec.Decode(saved.IPv6, true);
            if (Hash(saved.IPv4) != Hash(before.IPv4) || decoded.DnsMode != "Manual" || decoded.PreferredDns != testDns[0] || decoded.AlternateDns != testDns[1] || decoded.AddressMode != "Automatic") throw new IOException("DNS-only stored metadata mismatch.");
            await Reconnect();
            var active = await Wait(testDns);
            Save("runtime-test", active);
            if (Global() != global || Others() != others) throw new IOException("Global/other-profile settings changed.");
            Note("EFFECTIVE VERIFIED: selected SSID uses both test DNS servers; automatic addressing, IPv4 and inspected global/other-profile settings unchanged.");
            string source = active.IPv6.Addresses.First(a => a.Origin == "RouterAdvertisement" && a.State == "Preferred").Address;
            var probe = await new NativeCommandRunner().RunAsync(Path.GetFullPath(args[5]), [Path.GetFullPath(args[6]), "--source", source, "--index", active.Index.ToString()], TimeSpan.FromSeconds(100));
            File.WriteAllText(Path.Combine(directory, "traffic.jsonl"), probe.CombinedOutput);
            if (probe.TimedOut || probe.ExitCode != 0) throw new IOException("DNS traffic incomplete; inspect traffic.jsonl. Rollback will run.");
            var rows = probe.CombinedOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            var queries = rows.Where(row => row.TryGetProperty("test", out var test) && test.GetString() == "DNS").ToArray();
            if (queries.Length != 8 || queries.Any(row => row.GetProperty("result").GetString() != "PASS" || row.GetProperty("interface").GetInt32() != active.Index || row.GetProperty("source").GetString() != source || !testDns.Contains(row.GetProperty("resolver").GetString())) || !rows.Any(row => row.TryGetProperty("summary", out var summary) && summary.GetBoolean() && row.GetProperty("failures").GetInt32() == 0)) throw new IOException("Traffic evidence does not establish eight successful bound DNS queries.");
            Note("TRAFFIC VERIFIED: eight successful DNS query records match the selected interface/source/resolvers, with a zero-failure summary.");
            if (retainRequested)
            {
                var final = await Wait(testDns);
                var finalMetadata = service.Read(id);
                if (Global() != global || Others() != others || Hash(finalMetadata.IPv4) != Hash(before.IPv4) || Hash(finalMetadata.IPv6) != Hash(saved.IPv6)) throw new IOException("Settings drifted during traffic verification; rollback required.");
                Save("runtime-retained", final);
                File.WriteAllText(Path.Combine(directory, "global-after.json"), Global());
                var snapshot = service.Snapshots().Single();
                File.WriteAllText(Path.Combine(directory, "retained.json"), JsonSerializer.Serialize(new { Status = "VerifiedRetained", Captured = DateTimeOffset.Now, Snapshot = snapshot, AdapterId = id, Ssid = before.Target.DisplaySsid, Dns = testDns }));
                Note("RETAINED: explicitly requested DNS-only per-SSID configuration verified. Recovery snapshot: " + snapshot);
                retained = true;
            }
        }
        finally
        {
            if (!retained)
            {
            var snapshots = service.Snapshots();
            if (snapshots.Length > 1) throw new IOException("Ambiguous recovery snapshots; manual review required.");
            if (snapshots.Length == 1)
            {
                _ = await Current();
                Note(await service.RestoreAsync(snapshots[0]));
                await Reconnect();
            }
            var restored = await Wait(original.IPv6.Dns);
            var metadata = service.Read(id);
            if (Hash(metadata.IPv4) != Hash(before.IPv4) || Hash(metadata.IPv6) != Hash(before.IPv6) || Global() != global || Others() != others) throw new IOException("Recovery not verified; retain snapshot and investigate.");
            Save("runtime-restored", restored);
            File.WriteAllText(Path.Combine(directory, "global-after.json"), Global());
            Note("RESTORE VERIFIED: exact original SSID metadata, router DNS, automatic addressing and inspected global/other-profile baseline restored.");
            }
        }
        Note(retained ? "PASS: authorized per-SSID IPv6 DNS retained after Save/reconnect/traffic verification; snapshot preserved. Router DNS service itself is not repaired." : "PASS: temporary per-SSID IPv6 DNS save/reconnect/traffic/rollback. This does not repair the router DNS service or retain Cloudflare settings.");
    }
}
