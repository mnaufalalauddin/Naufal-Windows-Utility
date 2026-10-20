using Naufal_Windows_Tech_s_Powertoys;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

internal sealed record WifiBootCheckpoint(string Machine, string Boot, string Phase, string SnapshotName, string[] Dns, string Global, string OtherProfiles)
{
    public bool ManualIpv4 { get; init; }
    public string ExpectedMac { get; init; } = "";
    public string[] OriginalGateways { get; init; } = [];
}

// Explicit two-manual-reboot acceptance flow. No reboot/task/startup hook is installed.
internal static class WifiSsidBootAudit
{
    internal static bool ManualRuntimeMatches(NetworkFamilyState state, NetworkFamilySettings requested) =>
        requested.AddressMode == "Manual" && string.IsNullOrEmpty(state.ReadError) && state.Dhcp == "Disabled" &&
        state.Addresses.Length == 1 && state.Addresses[0] is { Origin: "Manual", State: "Preferred" } address &&
        address.Address == requested.Address && address.Prefix == NetworkSettingsValidation.Prefix(requested.Subnet, false) &&
        state.Gateways.SequenceEqual(new[] { requested.Gateway });

    internal static string[] BaselineAdapterIds(string baseline)
    {
        using var document = JsonDocument.Parse(baseline);
        var ids = document.RootElement.EnumerateArray().Select(row => Guid.Parse(row.GetProperty("Id").GetString()!).ToString("D")).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) throw new IOException("Empty global baseline.");
        return ids;
    }
    internal static void RequireNextBoot(WifiBootCheckpoint checkpoint, string boot)
    {
        if (checkpoint.Machine != Environment.MachineName) throw new IOException("Checkpoint belongs to a different PC.");
        if (checkpoint.Phase is not ("AwaitTestBoot" or "AwaitRestoreBoot")) throw new IOException("Checkpoint is not awaiting a boot; do not reapply.");
        // WMI can return the same boot with different sub-millisecond precision.
        // Compare timestamps, not strings; ambiguity must not finalize rollback.
        if (!DateTimeOffset.TryParseExact(checkpoint.Boot, "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var previous) ||
            !DateTimeOffset.TryParseExact(boot, "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var current))
            throw new IOException("Invalid Windows boot timestamp; review checkpoint without modifying it.");
        if ((current - previous).TotalSeconds < 1) throw new IOException("No clearly newer Windows boot detected. Restart manually before Resume; fractional precision changes do not count.");
    }
    internal static async Task Run(string[] args)
    {
        bool manualPrepare = args[0] == "--ssid-static-boot-prepare";
        bool prepare = args[0] == "--ssid-boot-prepare" || manualPrepare;
        if (prepare ? args.Length != (manualPrepare ? 8 : 4) : args.Length != 2) throw new ArgumentException("Prepare needs adapter GUID, exact SSID and directory; static Prepare additionally needs MAC, reserved IPv4, mask, gateway before directory; Resume needs directory.");
        string directory = Path.GetFullPath(args[^1]), manifest = Path.Combine(directory, "checkpoint.json");
        Directory.CreateDirectory(directory);
        if (prepare && (File.Exists(manifest) || Directory.Exists(Path.Combine(directory, "snapshots")))) throw new IOException("Refusing to reuse a boot audit directory.");
        using var log = new StreamWriter(new FileStream(Path.Combine(directory, "boot-audit.txt"), FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        void Note(string text) { log.WriteLine(DateTimeOffset.Now.ToString("O") + " " + text); Console.WriteLine(text); }
        void Save(WifiBootCheckpoint value)
        {
            string temporary = manifest + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(JsonSerializer.SerializeToUtf8Bytes(value)); file.Flush(true); }
            File.Move(temporary, manifest, true);
        }
        var network = new NetworkSettingsService();
        var service = new WifiSsidService(Path.Combine(directory, "snapshots"));
        var native = new WifiSsidNative();
        string Hash(byte[]? value) => value is null ? "absent" : Convert.ToHexString(SHA256.HashData(value));
        string Others(WifiProfileIdentity target) => string.Join("|", WifiProfileInventory.Read(target.AdapterId).Where(p => p.ProfileName != target.ProfileName).OrderBy(p => p.ProfileName, StringComparer.Ordinal).Select(p => p.ProfileName + ":" + Hash(native.Read(p, false)) + ":" + Hash(native.Read(p, true))));
        async Task<string> Global(string? baseline = null)
        {
            // Compare the original identities even when a previously disconnected
            // USB NIC is absent after boot. Missing registry values still fail.
            var all = baseline is null ? await network.ReadAsync() : BaselineAdapterIds(baseline).Select(id => new NetworkAdapterState { Id = id }).ToArray();
            return JsonSerializer.Serialize(all.OrderBy(a => a.Id).SelectMany(a => new[] { "Tcpip", "Tcpip6" }.SelectMany(protocol =>
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{protocol}\Parameters\Interfaces\{{{Guid.Parse(a.Id):D}}}");
                return new[] { "NameServer", "EnableDHCP", "IPAddress", "SubnetMask", "DefaultGateway" }.Select(name => new { a.Id, protocol, name, Value = key?.GetValue(name), Kind = key?.GetValueNames().Contains(name) == true ? key.GetValueKind(name).ToString() : "Absent" }).ToArray();
            })));
        }
        string bootScript = "[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false); (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime.ToUniversalTime().ToString('O')";
        var bootResult = await new NativeCommandRunner().RunAsync(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(bootScript))], TimeSpan.FromSeconds(20), outputEncoding: Encoding.UTF8);
        if (bootResult.ExitCode != 0 || bootResult.TimedOut || !DateTimeOffset.TryParse(bootResult.StandardOutput.Trim(), out _)) throw new IOException("Cannot establish Windows boot identity.");
        string boot = bootResult.StandardOutput.Trim();
        async Task<NetworkAdapterState> Effective(WifiProfileIdentity target, string[]? dns, NetworkFamilySettings? manual = null, string? mac = null)
        {
            if (!WifiSsidService.SameTarget(native.Connected(target.AdapterId), target)) throw new IOException("Connect to the checkpoint's original SSID before Resume.");
            var adapters = await network.ReadAsync();
            if (!adapters.Any(a => a.InterfaceType == 6 && a.Status == "Up")) throw new IOException("Keep Ethernet available during this host audit.");
            var a = adapters.Single(a => NetworkSettingsService.SameId(a.Id, target.AdapterId));
            bool addressMatches = manual is null ? a.IPv4.Dhcp == "Enabled" && a.IPv4.Addresses.Any(v => v.Origin == "Dhcp" && v.State == "Preferred") && a.IPv4.Gateways.Length > 0 : ManualRuntimeMatches(a.IPv4, manual);
            if (!string.IsNullOrEmpty(a.IPv4.ReadError) || !string.IsNullOrEmpty(a.IPv6.ReadError) || !addressMatches || a.IPv6.Dhcp != "Enabled" || a.IPv6.RouterDiscovery != "Enabled" || !a.IPv6.Addresses.Any(v => v.Origin == "RouterAdvertisement" && v.State == "Preferred")) throw new IOException("Expected IPv4 and IPv6 automatic/RA runtime state not established; wait for network initialization and retry read-only checks.");
            if (mac is not null && a.Mac.Replace("-", "").Replace(":", "").ToUpperInvariant() != mac.Replace("-", "").Replace(":", "").ToUpperInvariant()) throw new IOException("Wi-Fi MAC differs from reserved-address owner.");
            if (dns is not null && !a.IPv4.Dns.SequenceEqual(dns)) throw new IOException("Effective IPv4 DNS differs from checkpoint.");
            if (!WifiSsidService.SameTarget(native.Connected(target.AdapterId), target)) throw new IOException("SSID changed during scan.");
            return a;
        }
        async Task Reconnect(WifiProfileIdentity target, string alias)
        {
            if (!WifiSsidService.SameTarget(native.Connected(target.AdapterId), target)) throw new IOException("Refusing to disconnect a different SSID.");
            foreach (var command in new[] { new[] { "wlan", "disconnect", "interface=" + alias }, new[] { "wlan", "connect", "name=" + target.ProfileName, "interface=" + alias } })
            {
                var result = await new NativeCommandRunner().RunAsync(Path.Combine(Environment.SystemDirectory, "netsh.exe"), command, TimeSpan.FromSeconds(15));
                if (result.TimedOut || result.ExitCode != 0) throw new IOException("Reconnect failed: " + result.CombinedOutput);
            }
        }
        async Task<NetworkAdapterState> WaitEffective(WifiProfileIdentity target, string[] dns, NetworkFamilySettings? manual, string mac)
        {
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                try { return await Effective(target, dns, manual, mac); }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { }
            }
            throw new IOException("Expected runtime state did not settle after reconnect.");
        }
        if (prepare)
        {
            await network.PreflightWifiAsync(args[1]);
            var before = service.Read(args[1]);
            if (before.Target.DisplaySsid != args[2] || before.IPv4 is not null || before.IPv6 is not null) throw new IOException("Exact target with originally absent per-SSID metadata required.");
            var a = await Effective(before.Target, null, mac: manualPrepare ? args[3] : null);
            if (a.IPv4.Dns.Length is < 1 or > 2) throw new IOException("Simple current IPv4 DNS required.");
            string global = await Global(), others = Others(before.Target);
            var plan = new NetworkSettingsProfile { Scope = "WifiSsid", AdapterId = args[1], WifiProfileName = before.Target.ProfileName, SsidHex = before.Target.SsidHex,
                IPv4 = new() { AddressMode = manualPrepare ? "Manual" : "Unchanged", Address = manualPrepare ? args[4] : "", Subnet = manualPrepare ? args[5] : "", Gateway = manualPrepare ? args[6] : "", DnsMode = "Manual", PreferredDns = a.IPv4.Dns[0], AlternateDns = a.IPv4.Dns.ElementAtOrDefault(1) ?? "" },
                IPv6 = manualPrepare ? new() : new() { AddressMode = "Automatic", DnsMode = "Automatic" } };
            NetworkSettingsValidation.Validate(plan);
            WifiBootCheckpoint? prepared = null;
            try
            {
                Note(await service.ApplyAsync(plan, before));
                string snapshot = service.Snapshots().Single();
                prepared = new(Environment.MachineName, boot, "Preparing", Path.GetFileName(snapshot), a.IPv4.Dns, global, others) { ManualIpv4 = manualPrepare, ExpectedMac = manualPrepare ? args[3] : "", OriginalGateways = a.IPv4.Gateways };
                Save(prepared);
                if (manualPrepare)
                {
                    await Reconnect(before.Target, a.Name);
                    var active = await WaitEffective(before.Target, a.IPv4.Dns, plan.IPv4, args[3]);
                    File.WriteAllText(Path.Combine(directory, "runtime-prepared.json"), JsonSerializer.Serialize(new[] { active }, NetworkSettingsJson.Default.NetworkAdapterStateArray));
                    if (!WifiSsidManualV4Audit.ManualProjectionMatches(global, await Global(global), before.Target.AdapterId) || Others(before.Target) != others || Hash(service.Read(args[1]).IPv6) != Hash(before.IPv6)) throw new IOException("Unexpected isolation difference while preparing static reboot.");
                    Note("STATIC PREPARE VERIFIED: reserved IPv4/prefix/gateway and retained DNS active after reconnect; IPv6 unchanged; only expected active Wi-Fi DHCP projection differs.");
                }
                Save(prepared with { Phase = "AwaitTestBoot" });
                Note("AWAIT TEST BOOT: checkpoint and exact original metadata are durable. Restart this PC manually, reconnect original SSID, then Resume. This is NOT a pass. No automatic reboot requested.");
            }
            catch
            {
                var snapshots = service.Snapshots();
                if (snapshots.Length == 1)
                {
                    Note(await service.RestoreAsync(snapshots[0]));
                    if (manualPrepare)
                    {
                        await Reconnect(before.Target, a.Name);
                        await WaitEffective(before.Target, a.IPv4.Dns, null, args[3]);
                    }
                }
                if (prepared is not null) Save(prepared with { Phase = "PrepareFailedRestored" });
                throw;
            }
            return;
        }
        if (new FileInfo(manifest).Length > 800000) throw new IOException("Checkpoint too large.");
        var checkpoint = JsonSerializer.Deserialize<WifiBootCheckpoint>(File.ReadAllText(manifest)) ?? throw new IOException("Invalid checkpoint.");
        RequireNextBoot(checkpoint, boot);
        if (Path.GetFileName(checkpoint.SnapshotName) != checkpoint.SnapshotName) throw new IOException("Invalid snapshot identity.");
        string snapshotPath = Path.Combine(directory, "snapshots", checkpoint.SnapshotName);
        var snapshotData = service.LoadSnapshot(snapshotPath);
        await network.PreflightWifiAsync(snapshotData.Before.Target.AdapterId);
        var current = service.Read(snapshotData.Before.Target.AdapterId);
        if (!WifiSsidService.SameTarget(current.Target, snapshotData.Before.Target)) throw new IOException("Wrong SSID; no writes performed.");
        var expected = checkpoint.Phase == "AwaitTestBoot" ? snapshotData.After : snapshotData.Before;
        if (Hash(current.IPv4) != Hash(expected.IPv4) || Hash(current.IPv6) != Hash(expected.IPv6)) throw new IOException("Post-boot metadata mismatch; retain snapshot and review before recovery.");
        bool staticTest = checkpoint.ManualIpv4 && checkpoint.Phase == "AwaitTestBoot";
        var manualSettings = staticTest ? WifiSsidCodec.Decode(snapshotData.After.IPv4, false) : null;
        if (checkpoint.ManualIpv4 && (string.IsNullOrWhiteSpace(checkpoint.ExpectedMac) || checkpoint.OriginalGateways.Length == 0 || !snapshotData.ChangedV4 || snapshotData.ChangedV6 || WifiSsidCodec.Decode(snapshotData.After.IPv4, false).AddressMode != "Manual")) throw new IOException("Invalid static-address checkpoint.");
        var effective = await Effective(current.Target, checkpoint.Dns, manualSettings, checkpoint.ManualIpv4 ? checkpoint.ExpectedMac : null);
        string globalAfterBoot = await Global(checkpoint.Global);
        bool globalMatches = staticTest ? WifiSsidManualV4Audit.ManualProjectionMatches(checkpoint.Global, globalAfterBoot, current.Target.AdapterId) : globalAfterBoot == checkpoint.Global;
        if (!globalMatches || Others(current.Target) != checkpoint.OtherProfiles || (checkpoint.ManualIpv4 && !staticTest && !effective.IPv4.Gateways.SequenceEqual(checkpoint.OriginalGateways))) throw new IOException("Post-boot global/other-profile/gateway comparison failed; review retained evidence.");
        if (checkpoint.Phase == "AwaitTestBoot")
        {
            Note(staticTest ? "STATIC TEST BOOT VERIFIED: exact saved manual IPv4/prefix/gateway/DNS effective; IPv6 automatic/RA remains active; only expected active Wi-Fi DHCP projection differs." : "TEST BOOT VERIFIED: saved metadata persisted, effective IPv4 DNS/DHCP and IPv6 automatic/RA configuration match; inspected global/other-profile state unchanged.");
            Note(await service.RestoreAsync(snapshotPath));
            Save(checkpoint with { Boot = boot, Phase = "AwaitRestoreBoot" });
            Note("AWAIT RESTORE BOOT: original metadata restored. Restart manually again and Resume to verify post-boot rollback. Overall audit is NOT complete yet.");
        }
        else
        {
            Save(checkpoint with { Boot = boot, Phase = "Complete" });
            Note(checkpoint.ManualIpv4 ? "PASS: reserved manual IPv4/prefix/gateway/DNS persisted after reboot; exact rollback and DHCP recovery verified after the second reboot. Manual IPv6 and post-boot traffic remain untested." : "PASS: test and restore verified across two manual Windows reboots. IPv4 DNS and IPv6 automatic mode only; manual IP/gateway/IPv6 and traffic reachability remain untested.");
        }
    }
}
