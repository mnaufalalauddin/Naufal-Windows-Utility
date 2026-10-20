using Naufal_Windows_Tech_s_Powertoys;
using Microsoft.Win32;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed record WifiV6BootCheckpoint(string Machine, string Boot, string Phase, string SnapshotName,
    string Mac, NetworkAdapterState Original, string Global, string OtherProfiles, bool IsolationPassed, bool TestFailed);

// Test-only staged recovery; never registers a task or requests a reboot.
internal static class WifiSsidV6BootAudit
{
    internal static void Save(string directory, WifiV6BootCheckpoint checkpoint)
    {
        string manifest = Path.Combine(directory, "checkpoint.json"), temporary = manifest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { file.Write(JsonSerializer.SerializeToUtf8Bytes(checkpoint)); file.Flush(true); }
        File.Move(temporary, manifest, true);
    }

    internal static async Task<string> ReadBoot()
    {
        string script = "[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false); (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime.ToUniversalTime().ToString('O')";
        var result = await new NativeCommandRunner().RunAsync(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], TimeSpan.FromSeconds(20), outputEncoding: Encoding.UTF8);
        if (result.TimedOut || result.ExitCode != 0 || !DateTimeOffset.TryParse(result.StandardOutput.Trim(), out _)) throw new IOException("Cannot establish boot identity.");
        return result.StandardOutput.Trim();
    }

    internal static bool RuntimeMatches(NetworkAdapterState current, NetworkAdapterState original, NetworkFamilySettings manual, bool test) =>
        string.IsNullOrEmpty(current.IPv4.ReadError) && current.IPv4.Dhcp == original.IPv4.Dhcp &&
        current.IPv4.Addresses.Any(a => a.Origin == "Dhcp" && a.State == "Preferred") &&
        current.IPv4.Gateways.SequenceEqual(original.IPv4.Gateways) && current.IPv4.Dns.SequenceEqual(original.IPv4.Dns) &&
        WifiSsidManualV6Audit.ModeMatches(current.IPv6, test) &&
        WifiSsidManualV6Audit.AddressMatches(current.IPv6, manual.Address, NetworkSettingsValidation.Prefix(manual.Subnet, true), test ? "Manual" : "RouterAdvertisement") &&
        current.IPv6.Gateways.SequenceEqual(original.IPv6.Gateways) && current.IPv6.Dns.SequenceEqual(original.IPv6.Dns);

    internal static async Task Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Resume/Restore needs an existing evidence directory.");
        bool cancel = args[0] == "--ssid-manual-v6-boot-restore";
        string directory = Path.GetFullPath(args[1]), manifest = Path.Combine(directory, "checkpoint.json");
        if (!File.Exists(manifest) || new FileInfo(manifest).Length > 800000) throw new IOException("Missing/oversized checkpoint; no writes.");
        var checkpoint = JsonSerializer.Deserialize<WifiV6BootCheckpoint>(File.ReadAllText(manifest)) ?? throw new IOException("Invalid checkpoint.");
        if (checkpoint.Machine != Environment.MachineName || string.IsNullOrWhiteSpace(checkpoint.Mac) ||
            checkpoint.Phase is not ("AwaitTestBoot" or "AwaitRestoreBoot" or "Restoring") || Path.GetFileName(checkpoint.SnapshotName) != checkpoint.SnapshotName)
            throw new IOException("Wrong-machine, invalid or finalized checkpoint; no writes.");
        string boot = await ReadBoot();
        if (!cancel) WifiSsidBootAudit.RequireNextBoot(new(checkpoint.Machine, checkpoint.Boot, checkpoint.Phase, checkpoint.SnapshotName, [], "", ""), boot);
        using var log = new StreamWriter(new FileStream(Path.Combine(directory, "boot-audit.txt"), FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        void Note(string text) { log.WriteLine(DateTimeOffset.Now.ToString("O") + " " + text); Console.WriteLine(text); }
        string Hash(byte[]? value) => value is null ? "absent" : Convert.ToHexString(SHA256.HashData(value));
        string Mac(string value) => value.Replace(":", "").Replace("-", "").ToUpperInvariant();
        var network = new NetworkSettingsService();
        var service = new WifiSsidService(Path.Combine(directory, "snapshots"));
        var native = new WifiSsidNative();
        string snapshotPath = Path.Combine(directory, "snapshots", checkpoint.SnapshotName);
        var snapshot = service.LoadSnapshot(snapshotPath);
        var target = snapshot.Before.Target;
        var manual = WifiSsidCodec.Decode(snapshot.After.IPv6, true);
        if (!snapshot.ChangedV6 || snapshot.ChangedV4 || snapshot.Before.IPv6 is not null || manual.AddressMode != "Manual" ||
            !NetworkSettingsService.SameId(checkpoint.Original.Id, target.AdapterId) || checkpoint.Original.IPv4.Dhcp != "Enabled" ||
            Hash(snapshot.Before.IPv4) != Hash(snapshot.After.IPv4)) throw new IOException("Invalid IPv6-only snapshot/checkpoint.");
        await network.PreflightWifiAsync(target.AdapterId);
        var stored = service.Read(target.AdapterId);
        bool sameOriginal = Hash(stored.IPv6) == Hash(snapshot.Before.IPv6), sameManual = Hash(stored.IPv6) == Hash(snapshot.After.IPv6);
        if (!WifiSsidService.SameTarget(stored.Target, target) || Hash(stored.IPv4) != Hash(snapshot.Before.IPv4) ||
            (cancel ? !(sameOriginal || sameManual) : checkpoint.Phase == "AwaitTestBoot" ? !sameManual : !sameOriginal))
            throw new IOException("SSID/metadata conflict: no automatic overwrite; retain snapshot for review.");
        string Global() => JsonSerializer.Serialize(WifiSsidBootAudit.BaselineAdapterIds(checkpoint.Global).OrderBy(id => id).SelectMany(id => new[] { "Tcpip", "Tcpip6" }.SelectMany(protocol =>
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{protocol}\Parameters\Interfaces\{{{Guid.Parse(id):D}}}");
            return new[] { "NameServer", "EnableDHCP", "IPAddress", "SubnetMask", "DefaultGateway" }.Select(name => new { Id = id, protocol, name, Value = key?.GetValue(name), Kind = key?.GetValueNames().Contains(name) == true ? key.GetValueKind(name).ToString() : "Absent" }).ToArray();
        })));
        string Others() => string.Join("|", WifiProfileInventory.Read(target.AdapterId).Where(p => p.ProfileName != target.ProfileName).OrderBy(p => p.ProfileName).Select(p => p.ProfileName + ":" + Hash(native.Read(p, false)) + ":" + Hash(native.Read(p, true))));
        async Task<NetworkAdapterState> Current()
        {
            if (!WifiSsidService.SameTarget(native.Connected(target.AdapterId), target)) throw new IOException("Connect original SSID before continuing.");
            var adapters = await network.ReadAsync();
            var a = adapters.Single(a => NetworkSettingsService.SameId(a.Id, target.AdapterId));
            if (!WifiSsidService.SameTarget(native.Connected(target.AdapterId), target) || a.InterfaceType != 71 || Mac(a.Mac) != Mac(checkpoint.Mac) || !adapters.Any(a => a.InterfaceType == 6 && a.Status == "Up")) throw new IOException("Wi-Fi identity or Ethernet fallback mismatch.");
            return a;
        }
        async Task<NetworkAdapterState> Verify(bool test)
        {
            var a = await Current();
            File.WriteAllText(Path.Combine(directory, "runtime-" + (test ? "test-boot" : "restored") + ".json"), JsonSerializer.Serialize(new[] { a }, NetworkSettingsJson.Default.NetworkAdapterStateArray));
            if (!RuntimeMatches(a, checkpoint.Original, manual, test)) throw new IOException("Runtime addressing/gateway/DNS mismatch; evidence retained.");
            string global = Global();
            File.WriteAllText(Path.Combine(directory, "global-" + (test ? "test-boot" : "restored") + ".json"), global);
            if (!(test ? WifiSsidManualV6Audit.GlobalMatches(checkpoint.Global, global, target.AdapterId) : checkpoint.Global == global) || Others() != checkpoint.OtherProfiles) throw new IOException("Inspected global/other-profile isolation mismatch.");
            return a;
        }
        async Task Https(NetworkAdapterState a)
        {
            // System resolution; only the IPv6 HTTPS transport is interface-bound.
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            handler.ConnectCallback = async (context, token) =>
            {
                var destinations = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetworkV6, token);
                var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)31, a.Index);
                    if (Convert.ToInt32(socket.GetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)31)) != a.Index) throw new IOException("Interface mismatch.");
                    socket.Bind(new IPEndPoint(IPAddress.Parse(manual.Address), 0));
                    await socket.ConnectAsync(destinations[0], context.DnsEndPoint.Port, token);
                    Note("HTTPS bound to Wi-Fi interface " + a.Index + ", source " + manual.Address);
                    return new NetworkStream(socket, true);
                }
                catch { socket.Dispose(); throw; }
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://example.com/");
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new IOException("HTTPS status " + response.StatusCode);
            Note("HTTPS VERIFIED: valid TLS certificate, HTTP " + (int)response.StatusCode);
        }
        // Establish identity/fallback before entering the mutation/recovery region.
        _ = await Current();
        if (checkpoint.Phase == "AwaitRestoreBoot" && !cancel)
        {
            var restored = await Verify(false);
            await Https(restored);
            Save(directory, checkpoint with { Boot = boot, Phase = checkpoint.TestFailed ? "FailedTestRecoveryVerified" : "Complete" });
            if (checkpoint.TestFailed) throw new IOException("Recovery verified after reboot, but the manual test failed. Not a pass.");
            Note("PASS: manual IPv6 metadata/runtime and Wi-Fi-bound HTTPS after first reboot; exact automatic-mode recovery and HTTPS after second reboot. Isolation passed: " + checkpoint.IsolationPassed);
            return;
        }
        Exception? failure = null;
        try
        {
            if (!cancel)
            {
                await Https(await Verify(true));
                Note("TEST BOOT VERIFIED: exact manual IPv6/prefix/gateway/DNS and bound HTTPS after a distinct boot.");
            }
        }
        catch (Exception e) { failure = e; Note("TEST FAILED: " + e); }
        finally
        {
            checkpoint = checkpoint with { Phase = "Restoring", TestFailed = checkpoint.TestFailed || failure is not null };
            Save(directory, checkpoint);
            Note(await service.RestoreAsync(snapshotPath));
            foreach (var command in new[] { new[] { "wlan", "disconnect", "interface=" + checkpoint.Original.Name }, new[] { "wlan", "connect", "name=" + target.ProfileName, "interface=" + checkpoint.Original.Name } })
            {
                var result = await new NativeCommandRunner().RunAsync(Path.Combine(Environment.SystemDirectory, "netsh.exe"), command, TimeSpan.FromSeconds(15));
                if (result.TimedOut || result.ExitCode != 0) throw new IOException("Recovery reconnect failed; retained checkpoint/snapshot.");
            }
            NetworkAdapterState? recovered = null;
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                try { recovered = await Verify(false); break; }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { }
            }
            if (recovered is null) throw new IOException("Recovery runtime not verified; retained Restoring checkpoint.");
            var original = service.Read(target.AdapterId);
            if (Hash(original.IPv4) != Hash(snapshot.Before.IPv4) || Hash(original.IPv6) != Hash(snapshot.Before.IPv6)) throw new IOException("Original metadata recovery mismatch.");
            Save(directory, checkpoint with { Boot = boot, Phase = cancel ? "CancelledRestored" : "AwaitRestoreBoot" });
            Note(cancel ? "CANCELLED: original metadata/automatic runtime restored, not a reboot-test pass." : "AWAIT RESTORE BOOT: exact metadata and automatic runtime recovered. Restart manually again, then Resume. Not complete yet.");
        }
        if (failure is not null) throw new IOException("Manual post-boot test failed; recovery checked, failure retained in checkpoint.", failure);
    }
}
