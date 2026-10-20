using Naufal_Windows_Tech_s_Powertoys;
using Microsoft.Win32;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

// Explicit host opt-in only. Requires a user-reserved IPv4 address; never guesses one.
internal static class WifiSsidManualV4Audit
{
    internal static bool ManualProjectionMatches(string baseline, string active, string adapterId)
    {
        var expected = JsonNode.Parse(baseline)!.AsArray();
        int changed = 0;
        foreach (var row in expected)
            if (row?["Id"]?.GetValue<string>() == adapterId && row["protocol"]?.GetValue<string>() == "Tcpip" && row["name"]?.GetValue<string>() == "EnableDHCP")
            {
                if (row["Kind"]?.GetValue<string>() != "DWord" || row["Value"]?.GetValue<int>() != 1) return false;
                row["Value"] = 0; changed++;
            }
        return changed == 1 && JsonNode.DeepEquals(expected, JsonNode.Parse(active));
    }

    internal static async Task Run(string[] args)
    {
        if (args.Length != 9) throw new ArgumentException("Expected mode, adapter GUID, SSID, MAC, reserved IPv4, mask, gateway, fresh evidence directory, authorized second SSID.");
        string id = Guid.Parse(args[1]).ToString("D"), directory = Path.GetFullPath(args[7]);
        if (Directory.Exists(directory)) throw new IOException("Evidence directory must be fresh.");
        Directory.CreateDirectory(directory);
        using var log = new StreamWriter(new FileStream(Path.Combine(directory, "audit.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        void Note(string text) { log.WriteLine(DateTimeOffset.Now.ToString("O") + " " + text); Console.WriteLine(text); }
        var network = new NetworkSettingsService();
        await network.PreflightWifiAsync(id);
        var store = new WifiSsidNative();
        var service = new WifiSsidService(Path.Combine(directory, "snapshots"));
        var before = service.Read(id);
        if (before.Target.DisplaySsid != args[2] || before.Target.PolicyManaged) throw new IOException("Wrong or managed SSID.");
        if (before.IPv4 is not null) throw new IOException("This bounded test requires originally absent IPv4 metadata.");
        var physical = await network.ReadAsync();
        var original = physical.Single(a => NetworkSettingsService.SameId(a.Id, id));
        static string Mac(string value) => value.Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (original.InterfaceType != 71 || Mac(original.Mac) != Mac(args[3]) || original.IPv4.Dhcp != "Enabled" ||
            !string.IsNullOrEmpty(original.IPv4.ReadError) || original.IPv4.Dns.Length is < 1 or > 2 || !physical.Any(a => a.InterfaceType == 6 && a.Status == "Up"))
            throw new IOException("Requires matching Wi-Fi MAC, readable automatic IPv4/DNS and available Ethernet.");
        var edit = new NetworkSettingsProfile { Scope = "WifiSsid", AdapterId = id, WifiProfileName = before.Target.ProfileName, SsidHex = before.Target.SsidHex,
            IPv4 = new() { AddressMode = "Manual", Address = args[4], Subnet = args[5], Gateway = args[6], DnsMode = "Manual", PreferredDns = original.IPv4.Dns[0], AlternateDns = original.IPv4.Dns.ElementAtOrDefault(1) ?? "" } };
        NetworkSettingsValidation.Validate(edit);
        int prefix = NetworkSettingsValidation.Prefix(args[5], false);
        string Hash(byte[]? bytes) => bytes is null ? "absent" : Convert.ToHexString(SHA256.HashData(bytes));
        var profiles = WifiProfileInventory.Read(id);
        var second = profiles.Single(p => p.DisplaySsid == args[8] && !p.PolicyManaged);
        if (second.ProfileName == before.Target.ProfileName) throw new IOException("A different saved SSID is required.");
        string Others() => string.Join("|", profiles.Where(p => p.ProfileName != before.Target.ProfileName).OrderBy(p => p.ProfileName).Select(p => p.ProfileName + ":" + Hash(store.Read(p, false)) + ":" + Hash(store.Read(p, true))));
        string Global() => JsonSerializer.Serialize(physical.OrderBy(a => a.Id).SelectMany(a => new[] { "Tcpip", "Tcpip6" }.SelectMany(protocol =>
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{protocol}\Parameters\Interfaces\{{{Guid.Parse(a.Id):D}}}");
            return new[] { "NameServer", "EnableDHCP", "IPAddress", "SubnetMask", "DefaultGateway" }.Select(name => new { a.Id, protocol, name, Value = key?.GetValue(name), Kind = key?.GetValueNames().Contains(name) == true ? key.GetValueKind(name).ToString() : "Absent" }).ToArray();
        })));
        string global = Global(), others = Others();
        File.WriteAllText(Path.Combine(directory, "global-before.json"), global);
        File.WriteAllText(Path.Combine(directory, "runtime-before.json"), JsonSerializer.Serialize(new[] { original }, NetworkSettingsJson.Default.NetworkAdapterStateArray));
        async Task<NetworkAdapterState> Current(WifiProfileIdentity? target = null)
        {
            target ??= before.Target;
            if (!WifiSsidService.SameTarget(store.Connected(id), target)) throw new IOException("Connected SSID changed.");
            var all = await network.ReadAsync();
            var current = all.Single(a => NetworkSettingsService.SameId(a.Id, id));
            if (!WifiSsidService.SameTarget(store.Connected(id), target) || Mac(current.Mac) != Mac(args[3])) throw new IOException("Wi-Fi identity changed.");
            if (!all.Any(a => a.InterfaceType == 6 && a.Status == "Up")) throw new IOException("Ethernet unavailable.");
            return current;
        }
        async Task<NetworkAdapterState> Switch(WifiProfileIdentity target)
        {
            var runner = new NativeCommandRunner();
            var result = await runner.RunAsync(Path.Combine(Environment.SystemDirectory, "netsh.exe"), ["wlan", "connect", "name=" + target.ProfileName, "interface=" + original.Name], TimeSpan.FromSeconds(15));
            if (result.TimedOut || result.ExitCode != 0) throw new IOException("SSID switch failed: " + result.CombinedOutput);
            Note("CONNECT: " + target.DisplaySsid);
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                try
                {
                    var a = await Current(target);
                    if (a.Status == "Up" && string.IsNullOrEmpty(a.IPv4.ReadError) && a.IPv4.Addresses.Any(v => v.State == "Preferred") && a.IPv4.Gateways.Length > 0 && a.IPv4.Dns.Length > 0) return a;
                }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { }
            }
            throw new IOException("SSID switch did not settle.");
        }
        async Task<NetworkAdapterState> Reconnect(bool manual)
        {
            if (!WifiSsidService.SameTarget(store.Connected(id), before.Target)) throw new IOException("Refusing to disconnect another SSID.");
            var runner = new NativeCommandRunner();
            string netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
            foreach (var command in new[] { new[] { "wlan", "disconnect", "interface=" + original.Name }, new[] { "wlan", "connect", "name=" + before.Target.ProfileName, "interface=" + original.Name } })
            {
                var result = await runner.RunAsync(netsh, command, TimeSpan.FromSeconds(15));
                if (result.TimedOut || result.ExitCode != 0) throw new IOException("Reconnect failed: " + result.CombinedOutput);
            }
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                NetworkAdapterState a;
                try { a = await Current(); } catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { continue; }
                bool address = manual
                    ? a.IPv4.Dhcp == "Disabled" && a.IPv4.Addresses.Any(v => v.Address == args[4] && v.Prefix == prefix && v.Origin == "Manual" && v.State == "Preferred") && a.IPv4.Gateways.Contains(args[6])
                    : a.IPv4.Dhcp == "Enabled" && a.IPv4.Addresses.Any(v => v.Origin == "Dhcp" && v.State == "Preferred") && a.IPv4.Gateways.SequenceEqual(original.IPv4.Gateways);
                if (a.Status == "Up" && string.IsNullOrEmpty(a.IPv4.ReadError) && address && a.IPv4.Dns.SequenceEqual(original.IPv4.Dns)) return a;
            }
            throw new IOException("Requested runtime address/DNS/gateway did not become effective.");
        }
        async Task Https(NetworkAdapterState a)
        {
            // Hostname resolution is system-wide; only the HTTPS socket is certified Wi-Fi-bound here.
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            handler.ConnectCallback = async (context, cancellation) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellation);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    const SocketOptionName ipUnicastInterface = (SocketOptionName)31; // Windows IP_UNICAST_IF.
                    socket.SetSocketOption(SocketOptionLevel.IP, ipUnicastInterface, IPAddress.HostToNetworkOrder(a.Index));
                    if (Convert.ToInt32(socket.GetSocketOption(SocketOptionLevel.IP, ipUnicastInterface)) != a.Index) throw new IOException("Interface readback mismatch.");
                    string source = a.IPv4.Addresses.Single(v => v.State == "Preferred" && v.Origin == (a.IPv4.Dhcp == "Disabled" ? "Manual" : "Dhcp")).Address;
                    socket.Bind(new IPEndPoint(IPAddress.Parse(source), 0));
                    await socket.ConnectAsync(addresses[0], context.DnsEndPoint.Port, cancellation);
                    Note("HTTPS socket bound to Wi-Fi interface " + a.Index + "; source " + source);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://example.com/");
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new IOException("HTTPS status " + response.StatusCode);
            Note("HTTPS VERIFIED: TLS certificate validated; HTTP " + (int)response.StatusCode + ". DNS routing not asserted by this test.");
        }
        Exception? failure = null;
        try
        {
            var secondBefore = await Switch(second);
            if (secondBefore.IPv4.Dhcp != "Enabled" || !secondBefore.IPv4.Addresses.Any(v => v.Origin == "Dhcp" && v.State == "Preferred") || secondBefore.IPv4.Addresses.Any(v => v.Address == args[4])) throw new IOException("Second SSID baseline not suitable for isolation test.");
            File.WriteAllText(Path.Combine(directory, "second-before.json"), JsonSerializer.Serialize(new[] { secondBefore }, NetworkSettingsJson.Default.NetworkAdapterStateArray));
            await Switch(before.Target);
            Note("PREPARED: user-reserved IPv4 " + args[4] + "/" + prefix + ", gateway " + args[6] + "; existing DNS retained as explicit DNS for the test. IPv6 metadata unchanged. No reboot.");
            Note(await service.ApplyAsync(edit, before));
            var saved = service.Read(id);
            var decoded = WifiSsidCodec.Decode(saved.IPv4, false);
            if (decoded.AddressMode != "Manual" || decoded.Address != args[4] || decoded.Gateway != args[6] || NetworkSettingsValidation.Prefix(decoded.Subnet, false) != prefix || Hash(saved.IPv6) != Hash(before.IPv6)) throw new IOException("Saved metadata differs from plan.");
            var active = await Reconnect(manual: true);
            File.WriteAllText(Path.Combine(directory, "runtime-manual.json"), JsonSerializer.Serialize(new[] { active }, NetworkSettingsJson.Default.NetworkAdapterStateArray));
            File.WriteAllText(Path.Combine(directory, "global-manual.json"), Global());
            File.WriteAllText(Path.Combine(directory, "other-profile-comparison.txt"), Others() == others ? "Unchanged" : "Changed");
            await Https(active);
            if (!ManualProjectionMatches(global, Global(), id) || Others() != others) throw new IOException("Unexpected global/other-profile settings change beyond active Wi-Fi DHCP projection.");
            Note("MANUAL VERIFIED: exact IPv4/prefix/gateway/DNS effective after reconnect; only active Wi-Fi EnableDHCP projects 1 to 0. IPv6 and other profiles unchanged.");
            var onSecond = await Switch(second);
            if (onSecond.IPv4.Dhcp != "Enabled" || !onSecond.IPv4.Addresses.Any(v => v.Origin == "Dhcp" && v.State == "Preferred") || onSecond.IPv4.Addresses.Any(v => v.Address == args[4]) || !onSecond.IPv4.Gateways.SequenceEqual(secondBefore.IPv4.Gateways) || !onSecond.IPv4.Dns.SequenceEqual(secondBefore.IPv4.Dns) || Others() != others || Global() != global) throw new IOException("Second SSID did not regain its own automatic configuration/global baseline.");
            File.WriteAllText(Path.Combine(directory, "second-during-manual.json"), JsonSerializer.Serialize(new[] { onSecond }, NetworkSettingsJson.Default.NetworkAdapterStateArray));
            Note("SECOND SSID VERIFIED: its own DHCP address/gateway/DNS, no test IP, unchanged profile metadata; inspected global values returned to baseline while Biznet manual metadata remains saved.");
            await Switch(before.Target);
            var returned = await Reconnect(manual: true);
            if (!ManualProjectionMatches(global, Global(), id) || Others() != others || Hash(service.Read(id).IPv4) != Hash(saved.IPv4)) throw new IOException("Manual configuration not retained on return.");
            await Https(returned);
            Note("RETURN VERIFIED: saved manual IPv4/gateway effective again only on original SSID.");
        }
        catch (Exception e) { failure = e; Note("TEST FAILED: " + e); }
        finally
        {
            var snapshots = service.Snapshots();
            if (snapshots.Length > 1) throw new IOException("Multiple recovery snapshots: manual review required.");
            // Also reconnect when the failed operation left Wi-Fi disconnected.
            await Switch(before.Target);
            if (snapshots.Length == 1)
            {
                Note(await service.RestoreAsync(snapshots[0]));
                var restored = await Reconnect(manual: false);
                var state = service.Read(id);
                if (Hash(state.IPv4) != Hash(before.IPv4) || Hash(state.IPv6) != Hash(before.IPv6) || Global() != global || Others() != others) throw new IOException("Rollback/isolation mismatch: review retained evidence.");
                File.WriteAllText(Path.Combine(directory, "runtime-restored.json"), JsonSerializer.Serialize(new[] { restored }, NetworkSettingsJson.Default.NetworkAdapterStateArray));
                File.WriteAllText(Path.Combine(directory, "global-after.json"), Global());
                Note("RESTORE VERIFIED: exact metadata restored, DHCP active, original gateway/DNS and inspected global/other-profile settings restored. DHCP lease may differ due to the new router reservation.");
                await Https(restored);
            }
        }
        if (failure is not null) throw new IOException("Manual IPv4 audit did not pass; see recovery evidence.", failure);
        Note("PASS: reserved manual IPv4/prefix/gateway, reconnect, two-SSID isolation, Wi-Fi-bound HTTPS and exact metadata rollback with DHCP recovery. Static-address reboot and manual IPv6 NOT tested.");
    }
}
