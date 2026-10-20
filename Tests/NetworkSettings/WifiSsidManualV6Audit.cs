using Naufal_Windows_Tech_s_Powertoys;
using Microsoft.Win32;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

// Opt-in temporary reuse of the exact currently assigned IPv6, never an invented address.
internal static class WifiSsidManualV6Audit
{
    // Windows may retain DHCPv6 after an SSID switch despite a Manual address.
    // Pair this predicate with AddressMatches; a DHCP flag never proves manual IP.
    // Never disable DHCP/RA globally to manufacture an expected test result.
    internal static bool ModeMatches(NetworkFamilyState state, bool manual) =>
        (manual ? state.Dhcp is "Disabled" or "Enabled" : state.Dhcp == "Enabled") && state.RouterDiscovery == "Enabled";

    internal static bool AddressMatches(NetworkFamilyState state, string address, int prefix, string origin) =>
        string.IsNullOrEmpty(state.ReadError) && state.Addresses.Any(a => a.Address == address && a.Prefix == prefix && a.Origin == origin && a.State == "Preferred");

    internal static bool SecondMatches(NetworkAdapterState baseline, NetworkAdapterState current, string testAddress) =>
        string.IsNullOrEmpty(current.IPv6.ReadError) && string.IsNullOrEmpty(current.IPv4.ReadError) &&
        !current.IPv6.Addresses.Any(a => a.Address == testAddress || a.Origin == "Manual") &&
        current.IPv6.Dhcp == baseline.IPv6.Dhcp && current.IPv6.RouterDiscovery == baseline.IPv6.RouterDiscovery &&
        current.IPv6.Gateways.SequenceEqual(baseline.IPv6.Gateways) && current.IPv6.Dns.SequenceEqual(baseline.IPv6.Dns) &&
        current.IPv4.Dhcp == "Enabled" && current.IPv4.Addresses.Any(a => a.Origin == "Dhcp" && a.State == "Preferred") &&
        current.IPv4.Gateways.SequenceEqual(baseline.IPv4.Gateways) && current.IPv4.Dns.SequenceEqual(baseline.IPv4.Dns);

    internal static bool GlobalMatches(string original, string active, string id)
    {
        if (original == active) return true;
        var expected = JsonNode.Parse(original)!.AsArray();
        int changed = 0;
        foreach (var row in expected)
            if (row?["Id"]?.GetValue<string>() == id && row["protocol"]?.GetValue<string>() == "Tcpip6" && row["name"]?.GetValue<string>() == "EnableDHCP")
            {
                if (row["Kind"]?.GetValue<string>() != "DWord" || row["Value"]?.GetValue<int>() != 1) return false;
                row["Value"] = 0; changed++;
            }
        return changed == 1 && JsonNode.DeepEquals(expected, JsonNode.Parse(active));
    }

    internal static async Task Run(string[] args)
    {
        if (args.Length is not (8 or 9)) throw new ArgumentException("Expected mode, GUID, SSID, MAC, currently assigned IPv6, prefix, gateway, fresh directory and optional authorized second SSID.");
        bool prepareBoot = args[0] == "--ssid-manual-v6-boot-prepare", leaveForBoot = false;
        string? boot = prepareBoot ? await WifiSsidV6BootAudit.ReadBoot() : null;
        string id = Guid.Parse(args[1]).ToString("D"), address = IPAddress.Parse(args[4]).ToString(), gateway = IPAddress.Parse(args[6]).ToString(), directory = Path.GetFullPath(args[7]);
        int prefix = NetworkSettingsValidation.Prefix(args[5], true);
        if (Directory.Exists(directory)) throw new IOException("Use fresh evidence directory.");
        Directory.CreateDirectory(directory);
        using var log = new StreamWriter(new FileStream(Path.Combine(directory, "audit.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        void Note(string text) { log.WriteLine(DateTimeOffset.Now.ToString("O") + " " + text); Console.WriteLine(text); }
        static string Mac(string text) => text.Replace(":", "").Replace("-", "").ToUpperInvariant();
        string Hash(byte[]? bytes) => bytes is null ? "absent" : Convert.ToHexString(SHA256.HashData(bytes));
        var network = new NetworkSettingsService();
        var service = new WifiSsidService(Path.Combine(directory, "snapshots"));
        var native = new WifiSsidNative();
        await network.PreflightWifiAsync(id);
        var before = service.Read(id);
        var all = await network.ReadAsync();
        var original = all.Single(a => NetworkSettingsService.SameId(a.Id, id));
        if (before.Target.DisplaySsid != args[2] || before.IPv6 is not null || original.InterfaceType != 71 || Mac(original.Mac) != Mac(args[3]) || !all.Any(a => a.InterfaceType == 6 && a.Status == "Up")) throw new IOException("Wrong target, existing IPv6 metadata or unavailable Ethernet.");
        if (prepareBoot && (original.IPv4.Dhcp != "Enabled" || !string.IsNullOrEmpty(original.IPv4.ReadError))) throw new IOException("Boot harness requires unchanged automatic IPv4 baseline.");
        if (!AddressMatches(original.IPv6, address, prefix, "RouterAdvertisement") || original.IPv6.Dhcp != "Enabled" || original.IPv6.RouterDiscovery != "Enabled" || !original.IPv6.Gateways.Contains(gateway) || original.IPv6.Dns.Length is < 1 or > 2) throw new IOException("Approved IPv6/prefix/gateway is not currently assigned through automatic configuration. No address guessing.");
        var profiles = WifiProfileInventory.Read(id);
        var second = args.Length == 9 ? profiles.Single(p => p.DisplaySsid == args[8] && !p.PolicyManaged) : null;
        if (second is not null && (second.ProfileName == before.Target.ProfileName || native.Read(second, true) is not null)) throw new IOException("Isolation requires a different SSID with originally automatic IPv6 metadata.");
        string Others() => string.Join("|", profiles.Where(p => p.ProfileName != before.Target.ProfileName).OrderBy(p => p.ProfileName).Select(p => p.ProfileName + ":" + Hash(native.Read(p, false)) + ":" + Hash(native.Read(p, true))));
        string Global() => JsonSerializer.Serialize(all.OrderBy(a => a.Id).SelectMany(a => new[] { "Tcpip", "Tcpip6" }.SelectMany(protocol =>
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{protocol}\Parameters\Interfaces\{{{Guid.Parse(a.Id):D}}}");
            return new[] { "NameServer", "EnableDHCP", "IPAddress", "SubnetMask", "DefaultGateway" }.Select(name => new { a.Id, protocol, name, Value = key?.GetValue(name), Kind = key?.GetValueNames().Contains(name) == true ? key.GetValueKind(name).ToString() : "Absent" }).ToArray();
        })));
        string global = Global(), others = Others();
        File.WriteAllText(Path.Combine(directory, "global-before.json"), global);
        void SaveRuntime(string name, NetworkAdapterState a) => File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new[] { a }, NetworkSettingsJson.Default.NetworkAdapterStateArray));
        SaveRuntime("runtime-before", original);
        async Task<NetworkAdapterState> Current(WifiProfileIdentity? target = null)
        {
            target ??= before.Target;
            if (!WifiSsidService.SameTarget(native.Connected(id), target)) throw new IOException("SSID changed.");
            var inventory = await network.ReadAsync();
            var a = inventory.Single(a => NetworkSettingsService.SameId(a.Id, id));
            if (!WifiSsidService.SameTarget(native.Connected(id), target) || Mac(a.Mac) != Mac(args[3]) || !inventory.Any(a => a.InterfaceType == 6 && a.Status == "Up")) throw new IOException("Identity or Ethernet check failed.");
            return a;
        }
        async Task<NetworkAdapterState> Switch(WifiProfileIdentity target)
        {
            var result = await new NativeCommandRunner().RunAsync(Path.Combine(Environment.SystemDirectory, "netsh.exe"), ["wlan", "connect", "name=" + target.ProfileName, "interface=" + original.Name], TimeSpan.FromSeconds(15));
            if (result.TimedOut || result.ExitCode != 0) throw new IOException("Switch failed: " + result.CombinedOutput);
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                try
                {
                    var a = await Current(target);
                    if (a.Status == "Up" && string.IsNullOrEmpty(a.IPv4.ReadError) && string.IsNullOrEmpty(a.IPv6.ReadError) && a.IPv4.Addresses.Any(v => v.State == "Preferred") && a.IPv4.Gateways.Length > 0) return a;
                }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { }
            }
            throw new IOException("Switch did not settle: " + target.DisplaySsid);
        }
        async Task Connect(bool disconnect)
        {
            var runner = new NativeCommandRunner();
            var commands = new List<string[]>();
            if (disconnect) commands.Add(["wlan", "disconnect", "interface=" + original.Name]);
            commands.Add(["wlan", "connect", "name=" + before.Target.ProfileName, "interface=" + original.Name]);
            foreach (var command in commands)
            {
                var result = await runner.RunAsync(Path.Combine(Environment.SystemDirectory, "netsh.exe"), command, TimeSpan.FromSeconds(15));
                if (result.TimedOut || result.ExitCode != 0) throw new IOException("Reconnect failed: " + result.CombinedOutput);
            }
        }
        async Task<NetworkAdapterState> Wait(bool manual)
        {
            for (int attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(1000);
                try
                {
                    var a = await Current();
                    SaveRuntime(manual ? "runtime-manual-last" : "runtime-recovery-last", a);
                    bool mode = ModeMatches(a.IPv6, manual);
                    if (mode && AddressMatches(a.IPv6, address, prefix, manual ? "Manual" : "RouterAdvertisement") && a.IPv6.Gateways.Contains(gateway) && a.IPv6.Dns.SequenceEqual(original.IPv6.Dns) && a.IPv4.Dhcp == original.IPv4.Dhcp && a.IPv4.Gateways.SequenceEqual(original.IPv4.Gateways) && a.IPv4.Dns.SequenceEqual(original.IPv4.Dns)) return a;
                }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { }
            }
            throw new IOException("Expected IPv6 runtime not established; see last runtime evidence.");
        }
        async Task Https(NetworkAdapterState a)
        {
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            handler.ConnectCallback = async (context, cancel) =>
            {
                // System resolution only; this check certifies IPv6 HTTPS socket routing, not DNS routing.
                var destinations = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetworkV6, cancel);
                var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)31, a.Index);
                    if (Convert.ToInt32(socket.GetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)31)) != a.Index) throw new IOException("Outgoing interface mismatch.");
                    socket.Bind(new IPEndPoint(IPAddress.Parse(address), 0));
                    await socket.ConnectAsync(destinations[0], context.DnsEndPoint.Port, cancel);
                    Note("IPv6 HTTPS socket bound to Wi-Fi " + a.Index + ", source " + address);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://example.com/");
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new IOException("HTTPS status " + response.StatusCode);
            Note("IPv6 HTTPS VERIFIED: certificate valid; HTTP " + (int)response.StatusCode);
        }
        Exception? failure = null;
        try
        {
            NetworkAdapterState? secondBefore = null;
            if (second is not null)
            {
                secondBefore = await Switch(second);
                SaveRuntime("second-before", secondBefore);
                if (!SecondMatches(secondBefore, secondBefore, address)) throw new IOException("Second SSID baseline unsuitable for isolation.");
                await Switch(before.Target);
                await Wait(manual: false);
            }
            var plan = new NetworkSettingsProfile { Scope = "WifiSsid", AdapterId = id, WifiProfileName = before.Target.ProfileName, SsidHex = before.Target.SsidHex,
                IPv6 = new() { AddressMode = "Manual", Address = address, Subnet = args[5], Gateway = gateway, DnsMode = "Manual", PreferredDns = original.IPv6.Dns[0], AlternateDns = original.IPv6.Dns.ElementAtOrDefault(1) ?? "" } };
            Note("PREPARED: temporary manual reuse of the exact currently assigned and user-approved IPv6. Original DNS values retained explicitly. IPv4 metadata untouched; no reboot.");
            Note(await service.ApplyAsync(plan, before));
            var saved = service.Read(id);
            var settings = WifiSsidCodec.Decode(saved.IPv6, true);
            if (settings.AddressMode != "Manual" || settings.Address != address || settings.Subnet != args[5] || settings.Gateway != gateway || Hash(saved.IPv4) != Hash(before.IPv4)) throw new IOException("Stored manual IPv6 mismatch.");
            await Connect(disconnect: true);
            var active = await Wait(manual: true);
            SaveRuntime("runtime-manual", active);
            File.WriteAllText(Path.Combine(directory, "global-manual.json"), Global());
            if (!GlobalMatches(global, Global(), id) || Others() != others || Hash(service.Read(id).IPv4) != Hash(before.IPv4)) throw new IOException("Unexpected global/other-profile or IPv4 metadata change.");
            Note("MANUAL VERIFIED: IPv6 manual address/prefix/gateway/DNS effective; DHCPv6=" + active.IPv6.Dhcp + ", RA retained (additional privacy addresses may remain). IPv4 and other profile metadata unchanged.");
            await Https(active);
            if (second is not null)
            {
                var diagnosticDestination = (await Dns.GetHostAddressesAsync("example.com", AddressFamily.InterNetworkV6))[0];
                var onSecond = await Switch(second);
                SaveRuntime("second-during-manual", onSecond);
                if (!SecondMatches(secondBefore!, onSecond, address) || Global() != global || Others() != others || Hash(native.Read(before.Target, true)) != Hash(saved.IPv6)) throw new IOException("Second SSID isolation failed; see runtime evidence.");
                Note("SECOND SSID VERIFIED: original automatic modes/gateway/DNS; no Biznet manual IPv6 address, unchanged metadata and inspected global baseline. IPv6 Internet service on the hotspot is not asserted.");
                var retained = onSecond.IPv6.Addresses.Where(a => active.IPv6.Addresses.Any(b => b.Address == a.Address)).ToArray();
                if (retained.Length > 0) Note("TRANSITION: " + retained.Length + " RA-learned address(es) remain listed immediately after switching; checking again after settling. Requested manual address is absent.");
                foreach (int delay in new[] { 10, 20 })
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay));
                    onSecond = await Current(second);
                    SaveRuntime(delay == 10 ? "second-after10s" : "second-after30s", onSecond);
                    if (!SecondMatches(secondBefore!, onSecond, address) || Global() != global || Others() != others) throw new IOException("SSID isolation drift during settling observation.");
                    retained = onSecond.IPv6.Addresses.Where(a => active.IPv6.Addresses.Any(b => b.Address == a.Address)).ToArray();
                    Note("RA SETTLING " + (delay == 10 ? 10 : 30) + "s: retained old RA=" + retained.Length + "; IPv6 gateways=" + onSecond.IPv6.Gateways.Length);
                }
                foreach (var old in retained)
                {
                    using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        socket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)31, onSecond.Index);
                        if (Convert.ToInt32(socket.GetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)31)) != onSecond.Index) throw new IOException("Diagnostic interface mismatch.");
                        socket.Bind(new IPEndPoint(IPAddress.Parse(old.Address), 0));
                        await socket.ConnectAsync(diagnosticDestination, 443, deadline.Token);
                        throw new IOException("Retained old RA reached external TCP endpoint on the second SSID; review isolation.");
                    }
                    catch (SocketException e) { Note("OLD RA TRANSPORT: " + old.Address + " state=" + old.State + "; connection failed: " + e.SocketErrorCode + " (" + e.NativeErrorCode + ")"); }
                    catch (OperationCanceledException) { Note("OLD RA TRANSPORT: timeout; not proof of full isolation."); }
                }
                if (retained.Length > 0) throw new IOException("Old RA addresses still listed after settling; isolation remains incomplete even if external transport failed. Original configuration will be restored.");
                Note("SETTLED RA VERIFIED: no original-network address remains on the second SSID after 30 seconds. Immediate transition isolation and hotspot IPv6 Internet service are not certified.");
                await Switch(before.Target);
                var returned = await Wait(manual: true);
                SaveRuntime("runtime-returned", returned);
                if (Hash(service.Read(id).IPv6) != Hash(saved.IPv6) || !GlobalMatches(global, Global(), id) || Others() != others) throw new IOException("Manual IPv6 not retained on return.");
                await Https(returned);
                Note("RETURN VERIFIED: manual IPv6 effective again only on original SSID; bound HTTPS passed; DHCPv6=" + returned.IPv6.Dhcp);
            }
            if (prepareBoot)
            {
                var checkpoint = new WifiV6BootCheckpoint(Environment.MachineName, boot!, "AwaitTestBoot", Path.GetFileName(service.Snapshots().Single()), args[3], original, global, others, second is not null, false);
                WifiSsidV6BootAudit.Save(directory, checkpoint);
                leaveForBoot = true;
                Note("AWAIT TEST BOOT: manual IPv6 intentionally retained with durable original snapshot. Restart manually, reconnect original SSID, then Resume. Not a reboot pass; no automatic reboot/task.");
            }
        }
        catch (Exception ex) { failure = ex; Note("TEST FAILED: " + ex); }
        finally
        {
            var snapshots = service.Snapshots();
            if (snapshots.Length > 1) throw new IOException("Ambiguous recovery snapshots.");
            if (!leaveForBoot)
            {
                await Connect(disconnect: false);
                for (int attempt = 0; ; attempt++)
                {
                    try { _ = await Current(); break; }
                    catch (Exception e) when (attempt < 14 && e is IOException or System.ComponentModel.Win32Exception) { await Task.Delay(1000); }
                }
                if (snapshots.Length == 1) Note(await service.RestoreAsync(snapshots[0]));
                await Connect(disconnect: true);
                var restored = await Wait(manual: false);
                var state = service.Read(id);
                if (Hash(state.IPv4) != Hash(before.IPv4) || Hash(state.IPv6) != Hash(before.IPv6) || Global() != global || Others() != others) throw new IOException("Recovery/isolation mismatch; retain snapshot and review.");
                SaveRuntime("runtime-restored", restored);
                File.WriteAllText(Path.Combine(directory, "global-after.json"), Global());
                Note("RESTORE VERIFIED: exact original metadata and inspected global/other-profile state; IPv6 automatic DHCP/RA and IPv4 baseline recovered.");
                await Https(restored);
            }
        }
        if (failure is not null) throw new IOException("Manual IPv6 test failed; review retained evidence.", failure);
        if (!leaveForBoot) Note("PASS: temporary manual IPv6, reconnect, Wi-Fi-bound HTTPS and exact rollback. Two-SSID isolation tested: " + (second is not null) + ". Reboot not tested.");
    }
}
