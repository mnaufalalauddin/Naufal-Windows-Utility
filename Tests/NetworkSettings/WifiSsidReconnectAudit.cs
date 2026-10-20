using Naufal_Windows_Tech_s_Powertoys;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text.Json;

// Opt-in host test: saved profiles only, no credentials, static addresses or reboot.
internal static class WifiSsidReconnectAudit
{
    internal static async Task Run(string[] args)
    {
        if (args.Length != 5) throw new ArgumentException("Expected mode, adapter GUID, test SSID, second SSID, fresh evidence directory.");
        string id = Guid.Parse(args[1]).ToString("D"), directory = Path.GetFullPath(args[4]);
        Directory.CreateDirectory(directory);
        using var log = new StreamWriter(new FileStream(Path.Combine(directory, "audit.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        void Note(string text) { log.WriteLine(DateTimeOffset.Now.ToString("O") + " " + text); Console.WriteLine(text); }
        var network = new NetworkSettingsService();
        await network.PreflightWifiAsync(id);
        var store = new WifiSsidNative();
        var service = new WifiSsidService(Path.Combine(directory, "snapshots"));
        var profiles = WifiProfileInventory.Read(id);
        var first = profiles.Single(p => p.DisplaySsid == args[2] && !p.PolicyManaged);
        var second = profiles.Single(p => p.DisplaySsid == args[3] && !p.PolicyManaged);
        if (first.ProfileName == second.ProfileName) throw new ArgumentException("Two different saved profiles are required.");
        var initial = store.Connected(id);
        if (!WifiSsidService.SameTarget(initial, first) && !WifiSsidService.SameTarget(initial, second)) throw new IOException("Initial SSID is outside the authorized pair.");
        var physical = await network.ReadAsync();
        string alias = physical.Single(a => NetworkSettingsService.SameId(a.Id, id)).Name;
        if (!physical.Any(a => a.InterfaceType == 6 && a.Status == "Up")) throw new IOException("This host audit requires Ethernet to remain available.");
        string Hash(byte[]? b) => b is null ? "absent" : Convert.ToHexString(SHA256.HashData(b));
        string ProfileHash(WifiProfileIdentity p) => p.ProfileName + ":" + Hash(store.Read(p, false)) + ":" + Hash(store.Read(p, true));
        string Others() => string.Join("|", profiles.Where(p => p.ProfileName != first.ProfileName).Select(ProfileHash));
        string Global() => JsonSerializer.Serialize(physical.OrderBy(a => a.Id).SelectMany(a => new[] { "Tcpip", "Tcpip6" }.SelectMany(protocol =>
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{protocol}\Parameters\Interfaces\{{{Guid.Parse(a.Id):D}}}");
            return new[] { "NameServer", "EnableDHCP", "IPAddress", "SubnetMask", "DefaultGateway" }.Select(name => new { a.Id, protocol, name, Value = key?.GetValue(name), Kind = key?.GetValueNames().Contains(name) == true ? key.GetValueKind(name).ToString() : "Absent" }).ToArray();
        })));
        string originalGlobal = Global(), otherProfiles = Others();
        File.WriteAllText(Path.Combine(directory, "global-before.json"), originalGlobal);
        async Task<NetworkAdapterState> Current(WifiProfileIdentity target)
        {
            if (!WifiSsidService.SameTarget(store.Connected(id), target)) throw new IOException("SSID changed during verification.");
            var inventory = await network.ReadAsync();
            var a = inventory.Single(a => NetworkSettingsService.SameId(a.Id, id));
            if (!WifiSsidService.SameTarget(store.Connected(id), target)) throw new IOException("SSID changed during adapter scan.");
            if (!inventory.Any(a => a.InterfaceType == 6 && a.Status == "Up")) throw new IOException("Ethernet became unavailable.");
            return a;
        }
        async Task<NetworkAdapterState> Connect(WifiProfileIdentity target, bool reconnect = false)
        {
            var runner = new NativeCommandRunner();
            string netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
            if (reconnect)
            {
                var off = await runner.RunAsync(netsh, ["wlan", "disconnect", "interface=" + alias], TimeSpan.FromSeconds(15));
                if (off.TimedOut || off.ExitCode != 0) throw new IOException("Wi-Fi disconnect failed: " + off.CombinedOutput);
            }
            Note("CONNECT requested: " + target.DisplaySsid + (reconnect ? " (reconnect)" : ""));
            var result = await runner.RunAsync(netsh, ["wlan", "connect", "name=" + target.ProfileName, "interface=" + alias], TimeSpan.FromSeconds(15));
            if (result.TimedOut || result.ExitCode != 0) throw new IOException("Saved-profile connect failed: " + result.CombinedOutput);
            string previous = ""; int stable = 0;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                await Task.Delay(1000);
                NetworkAdapterState a;
                try { a = await Current(target); } catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { continue; }
                if (a.Status != "Up" || !string.IsNullOrEmpty(a.IPv4.ReadError) || a.IPv4.Addresses.Length == 0 || a.IPv4.Dns.Length == 0 || a.IPv4.Gateways.Length == 0 || a.IPv4.Addresses.Any(v => v.State is "Duplicate" or "Tentative")) continue;
                string signature = JsonSerializer.Serialize(new[] { a }, NetworkSettingsJson.Default.NetworkAdapterStateArray);
                stable = signature == previous ? stable + 1 : 0; previous = signature;
                if (stable >= 2) return a;
            }
            throw new IOException("Connected configuration did not settle; no successful effective-state claim.");
        }
        void Automatic(NetworkAdapterState a)
        {
            if (a.IPv4.Dhcp != "Enabled" || !a.IPv4.Addresses.Any(v => v.Origin == "Dhcp" && v.State == "Preferred") || a.IPv4.Gateways.Length == 0)
                throw new IOException("Effective automatic IPv4 addressing/default route not established.");
        }
        WifiSsidState? baseline = null;
        bool saved = false;
        Exception? failure = null;
        try
        {
            var secondBefore = await Connect(second);
            Automatic(secondBefore);
            var firstBefore = await Connect(first);
            Automatic(firstBefore);
            baseline = service.Read(id);
            if (baseline.IPv4 is not null || baseline.IPv6 is not null) throw new IOException("This bounded audit requires originally absent IP/DNS metadata on the test SSID.");
            if (firstBefore.IPv4.Dns.Length is < 1 or > 2 || !string.IsNullOrEmpty(firstBefore.IPv6.ReadError)) throw new IOException("Unsupported baseline DNS/IPv6 scan.");
            File.WriteAllText(Path.Combine(directory, "baselines.json"), JsonSerializer.Serialize(new[] { firstBefore, secondBefore }, NetworkSettingsJson.Default.NetworkAdapterStateArray));
            var plan = new NetworkSettingsProfile { Scope = "WifiSsid", AdapterId = id, WifiProfileName = first.ProfileName, SsidHex = first.SsidHex,
                IPv4 = new() { DnsMode = "Manual", PreferredDns = firstBefore.IPv4.Dns[0], AlternateDns = firstBefore.IPv4.Dns.ElementAtOrDefault(1) ?? "" },
                IPv6 = new() { AddressMode = "Automatic", DnsMode = "Automatic" } };
            Note(await service.ApplyAsync(plan, baseline)); saved = true;
            var expected = service.Read(id);
            var afterReconnect = await Connect(first, reconnect: true);
            Automatic(afterReconnect);
            if (!afterReconnect.IPv4.Dns.SequenceEqual(firstBefore.IPv4.Dns)) throw new IOException("DNS after reconnect differs from the saved test values.");
            if (afterReconnect.IPv6.Dhcp != "Enabled" || afterReconnect.IPv6.RouterDiscovery != "Enabled" || !afterReconnect.IPv6.Addresses.Any(a => a.Origin == "RouterAdvertisement" && a.State == "Preferred")) throw new IOException("IPv6 automatic/RA effective state not established on first SSID.");
            Note("RECONNECT VERIFIED: effective IPv4 DNS matches; DHCP address/default route and IPv6 automatic/RA address available. No manual address or gateway tested.");
            var onSecond = await Connect(second);
            Automatic(onSecond);
            if (!onSecond.IPv4.Dns.SequenceEqual(secondBefore.IPv4.Dns) || !onSecond.IPv4.Gateways.SequenceEqual(secondBefore.IPv4.Gateways) || Others() != otherProfiles) throw new IOException("Second SSID baseline/isolation mismatch.");
            Note("SECOND SSID VERIFIED: its own DNS/default gateway returned; saved metadata of all other profiles unchanged.");
            var onFirst = await Connect(first);
            var firstSaved = service.Read(id);
            if (Hash(firstSaved.IPv4) != Hash(expected.IPv4) || Hash(firstSaved.IPv6) != Hash(expected.IPv6) || !onFirst.IPv4.Dns.SequenceEqual(firstBefore.IPv4.Dns)) throw new IOException("First SSID did not retain saved settings across switching.");
            Note("RETURN VERIFIED: original test SSID retains its saved IPv4/IPv6 metadata and expected DNS.");
        }
        catch (Exception ex) { failure = ex; Note("AUDIT FAILED: " + ex.Message); }
        finally
        {
            try
            {
                var snapshots = service.Snapshots();
                if (snapshots.Length > 1) throw new IOException("Ambiguous recovery snapshots.");
                if (snapshots.Length == 1)
                {
                    await Connect(first);
                    Note(await service.RestoreAsync(snapshots[0]));
                    await Connect(first, reconnect: true);
                    var restored = service.Read(id);
                    if (baseline is null || Hash(restored.IPv4) != Hash(baseline.IPv4) || Hash(restored.IPv6) != Hash(baseline.IPv6)) throw new IOException("Original metadata mismatch after reconnect.");
                }
                await Connect(initial);
                string finalGlobal = Global();
                File.WriteAllText(Path.Combine(directory, "global-after.json"), finalGlobal);
                if (finalGlobal != originalGlobal || Others() != otherProfiles) throw new IOException("Post-restore global/other-profile comparison failed.");
                Note("RESTORE VERIFIED: original per-SSID metadata and inspected global/other-profile state retained; initial SSID reconnected.");
            }
            catch (Exception recovery) { Note("RECOVERY NOT VERIFIED: " + recovery); throw; }
        }
        if (failure is not null) throw new IOException("Audit did not pass; see retained recovery evidence.", failure);
        if (!saved) throw new IOException("No live save occurred.");
        Note("PASS: reconnect/two-SSID IPv4 DNS and IPv6 automatic-mode round trip. Manual IP/gateway, manual IPv6 and reboot remain UNTESTED.");
    }
}
