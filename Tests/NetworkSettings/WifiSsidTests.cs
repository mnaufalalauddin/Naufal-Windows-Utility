using Naufal_Windows_Tech_s_Powertoys;
using System.Buffers.Binary;

internal static class WifiSsidTests
{
    internal static async Task Run(bool nativeBuffersOnly = false)
    {
        int checks = 0;
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        async Task Reject(Func<Task> action, string message)
        {
            try { await action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { checks++; return; }
            throw new Exception(message);
        }
        var v4 = new NetworkFamilySettings { AddressMode = "Manual", Address = "192.0.2.20", Subnet = "255.255.255.0", Gateway = "192.0.2.1", DnsMode = "Manual", PreferredDns = "192.0.2.53", AlternateDns = "192.0.2.54" };
        var v6 = new NetworkFamilySettings { AddressMode = "Manual", Address = "2001:db8::20", Subnet = "64", Gateway = "fe80::1", DnsMode = "Manual", PreferredDns = "2001:db8::53", AlternateDns = "2001:db8::54" };
        foreach (bool ipv6 in new[] { false, true })
        foreach (string addressMode in new[] { "Automatic", "Manual" })
        foreach (string dnsMode in new[] { "Automatic", "Manual" })
        {
            var edit = (ipv6 ? v6 : v4) with { AddressMode = addressMode, DnsMode = dnsMode };
            var bytes = WifiSsidCodec.Merge(null, edit, ipv6);
            var decoded = WifiSsidCodec.Decode(bytes, ipv6);
            Assert(decoded.AddressMode == addressMode && decoded.DnsMode == dnsMode, "SSID mode roundtrip");
            if (addressMode == "Manual") Assert(decoded.Address == edit.Address && decoded.Subnet == edit.Subnet && decoded.Gateway == edit.Gateway, "SSID address/subnet/gateway roundtrip");
            if (dnsMode == "Manual") Assert(decoded.PreferredDns == edit.PreferredDns && decoded.AlternateDns == edit.AlternateDns, "SSID DNS order roundtrip");
            Assert(bytes.SequenceEqual(WifiSsidCodec.Merge(bytes, new(), ipv6)), "Unchanged sections preserved byte-for-byte");
            if (nativeBuffersOnly) { new WifiSsidNative().Validate(bytes); checks++; }
        }
        if (nativeBuffersOnly) { Console.WriteLine($"PASS: {checks} SSID native-buffer checks; Windows validates in-memory buffers only. No profile reads/writes or network changes."); return; }
        var raw4 = WifiSsidCodec.Merge(null, v4, false);
        var dnsOnly = WifiSsidCodec.Merge(raw4, new() { DnsMode = "Automatic" }, false);
        Assert(WifiSsidCodec.Decode(dnsOnly, false).Address == v4.Address && WifiSsidCodec.Decode(dnsOnly, false).DnsMode == "Automatic", "DNS-only edit preserves static IP");
        var ipOnly = WifiSsidCodec.Merge(raw4, new() { AddressMode = "Automatic" }, false);
        Assert(WifiSsidCodec.Decode(ipOnly, false).PreferredDns == v4.PreferredDns && WifiSsidCodec.Decode(ipOnly, false).AddressMode == "Automatic", "IP-only edit preserves static DNS");
        var doh = raw4.ToArray();
        int optionsOffset = BinaryPrimitives.ReadInt32LittleEndian(doh.AsSpan(24)) + 256;
        BinaryPrimitives.WriteInt32LittleEndian(doh.AsSpan(optionsOffset + 4), 1);
        await Reject(() => Task.Run(() => WifiSsidCodec.Merge(doh, new() { DnsMode = "Automatic" }, false)), "Encrypted DNS overwritten");
        Assert(WifiSsidCodec.Merge(doh, new(), false).SequenceEqual(doh), "Encrypted DNS retained unchanged");
        foreach (int offset in new[] { 0, 4, 8, 12, 16, 20, 24 })
        {
            var bad = raw4.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(offset), int.MaxValue);
            await Reject(() => Task.Run(() => WifiSsidCodec.Decode(bad, false)), "Malformed offset/count accepted");
        }
        await Reject(() => Task.Run(() => WifiSsidCodec.Decode(raw4[..^1], false)), "Truncated buffer accepted");
        await Reject(() => Task.Run(() => WifiSsidCodec.Decode(raw4, true)), "Mixed-family payload accepted");

        string root = Path.Combine(Path.GetTempPath(), "NWU-SSID-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (int scenario = 0; scenario < 10; scenario++)
            {
                var store = new MemoryStore();
                var service = new WifiSsidService(Path.Combine(root, scenario.ToString()), store, _ => Task.CompletedTask);
                var baseline = service.Read(store.Current.AdapterId);
                var plan = new NetworkSettingsProfile { AdapterId = baseline.Target.AdapterId, Scope = "WifiSsid", WifiProfileName = baseline.Target.ProfileName, SsidHex = baseline.Target.SsidHex, IPv4 = v4, IPv6 = v6 };
                if (scenario == 0)
                {
                    string result = await service.ApplyAsync(plan, baseline);
                    Assert(result.Contains("Saved directly"), "SSID save result");
                    Assert(store.Writes == 2 && store.OtherWrites == 0, "Both families saved only to the target SSID");
                    Assert(WifiSsidCodec.Decode(store.V4, false).Address == v4.Address, "Saved IPv4 readback");
                    Assert(WifiSsidCodec.Decode(store.V6, true).Address == v6.Address, "Saved IPv6 readback");
                    Assert(service.Snapshots().Length == 1, "Durable recovery snapshot exists");
                    var recovered = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(service.Snapshots()[0]), WifiSsidJson.Default.WifiSsidSnapshot)!;
                    Assert(recovered.Before.IPv4 is null && recovered.Before.IPv6 is null, "Snapshot JSON preserves absent metadata, not empty arrays");
                    await service.RestoreAsync(service.Snapshots()[0]);
                    Assert(store.V4 is null && store.V6 is null, "Originally absent metadata restored as absent");
                }
                else if (scenario == 1)
                {
                    store.Current = store.Other;
                    await Reject(() => service.ApplyAsync(plan, baseline), "Changed SSID accepted");
                    Assert(store.Writes == 0 && service.Snapshots().Length == 0, "SSID mismatch blocked before write/snapshot");
                }
                else if (scenario == 2)
                {
                    store.V4 = raw4;
                    await Reject(() => service.ApplyAsync(plan, baseline), "Stale scan accepted");
                    Assert(store.Writes == 0, "Stale Windows metadata never overwritten");
                }
                else if (scenario == 3)
                {
                    store.FailWrite = 2;
                    await Reject(() => service.ApplyAsync(plan, baseline), "Failed second-family write claimed success");
                    Assert(store.V4 is null && store.V6 is null && store.Writes == 3, "First family rolled back after second family failure");
                }
                else if (scenario == 4)
                {
                    store.SwitchAfterWrite = true;
                    await Reject(() => service.ApplyAsync(plan, baseline), "SSID switch during Save claimed success");
                    Assert(store.Writes == 1 && store.OtherWrites == 0, "SSID race cannot write second family or other SSID; snapshot retained");
                }
                else if (scenario == 5)
                {
                    store.Current = store.Current with { PolicyManaged = true };
                    await Reject(() => service.ApplyAsync(plan, baseline), "Policy-managed target accepted");
                    Assert(store.Writes == 0, "Managed SSID has zero writes");
                }
                else if (scenario == 6)
                {
                    var blocked = new WifiSsidService(Path.Combine(root, "blocked"), store, _ => throw new InvalidOperationException("policy"));
                    await Reject(() => blocked.ApplyAsync(plan, baseline), "Preflight bypass");
                    Assert(store.Writes == 0 && blocked.Snapshots().Length == 0, "Preflight failure prevents snapshots and writes");
                }
                else if (scenario == 7)
                {
                    await service.ApplyAsync(plan, baseline);
                    int writes = store.Writes;
                    store.V4 = WifiSsidCodec.Merge(raw4, v4 with { Address = "192.0.2.21" }, false);
                    await Reject(() => service.RestoreAsync(service.Snapshots()[0]), "Recovery overwrote external edit");
                    Assert(WifiSsidCodec.Decode(store.V4, false).Address == "192.0.2.21" && store.OtherWrites == 0, "External changes retained");
                    Assert(store.Writes == writes, "Known recovery conflict prevents every family write");
                }
                else if (scenario == 8)
                {
                    await Reject(() => service.ApplyAsync(plan with { Scope = "Adapter" }, baseline), "Adapter plan accepted by SSID provider");
                    await Reject(() => service.ApplyAsync(plan with { SsidHex = "41" }, baseline), "Wrong SSID bytes accepted");
                    Assert(store.Writes == 0, "Plan binding guards precede writes");
                }
                else
                {
                    // Same SSID display text is insufficient: Windows profile identity also matters.
                    store.Current = store.Current with { ProfileName = "Different Windows profile" };
                    await Reject(() => service.ApplyAsync(plan, baseline), "Different profile with same SSID accepted");
                    Assert(store.Writes == 0, "Exact Windows profile binding enforced");
                }
                Assert(store.OtherWrites == 0, "Unrelated SSID untouched");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        string policyChecks = NetworkSettingsScript.Common + "\n" + """
            if (-not (Test-SsidPolicyValues @() $false)) { throw 'Empty key rejected' }
            foreach ($n in @(0,1)) {
                if (-not (Test-SsidPolicyValues @([pscustomobject]@{Name='NC_PersonalFirewallConfig';Kind='DWord';Value=$n}) $false)) { throw 'Firewall-only value rejected' }
            }
            foreach ($entry in @(
                [pscustomobject]@{Name='NC_LanProperties';Kind='DWord';Value=0},
                [pscustomobject]@{Name='NC_LanChangeProperties';Kind='DWord';Value=0},
                [pscustomobject]@{Name='Unknown';Kind='DWord';Value=1},
                [pscustomobject]@{Name='NC_PersonalFirewallConfig';Kind='String';Value='0'},
                [pscustomobject]@{Name='NC_PersonalFirewallConfig';Kind='DWord';Value=2})) {
                if (Test-SsidPolicyValues @($entry) $false) { throw 'Unknown/configuration policy accepted' }
            }
            if (Test-SsidPolicyValues @() $true) { throw 'Unknown subkey accepted' }
            Write-Output 'PASS: 9 pure SSID policy predicate cases; no registry changes.'
            """;
        var policyResult = await new NativeCommandRunner().RunAsync(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(policyChecks))], TimeSpan.FromSeconds(20));
        Assert(policyResult.ExitCode == 0 && !policyResult.TimedOut && policyResult.StandardOutput.Contains("PASS: 9"), "Policy predicate tests failed: " + policyResult.CombinedOutput);
        Console.WriteLine(policyResult.StandardOutput.Trim());
        Console.WriteLine($"PASS: {checks} SSID codec/identity/persistence/recovery assertions, simulated stores only.");
    }
    private sealed class MemoryStore : IWifiSsidStore
    {
        public WifiProfileIdentity Current = new(Guid.NewGuid().ToString(), "Office Windows profile", "4F6666696365", "Office", false);
        public WifiProfileIdentity Other => Current with { ProfileName = "Guest Windows profile", SsidHex = "4775657374", DisplaySsid = "Guest" };
        public byte[]? V4, V6;
        public int Writes, OtherWrites, FailWrite;
        public bool SwitchAfterWrite;
        private string? _originalName;
        public WifiProfileIdentity Connected(string id) { _originalName ??= Current.ProfileName; return Current; }
        public byte[]? Read(WifiProfileIdentity target, bool ipv6) => (ipv6 ? V6 : V4)?.ToArray();
        public void Validate(byte[] value) { if (value.Length < 28) throw new IOException("bad buffer"); }
        public void Write(WifiProfileIdentity target, bool ipv6, byte[]? value)
        {
            Writes++; if (target.ProfileName != _originalName) OtherWrites++;
            if (Writes == FailWrite) throw new IOException("injected failure");
            if (ipv6) V6 = value?.ToArray(); else V4 = value?.ToArray();
            if (SwitchAfterWrite) Current = Other;
        }
    }
}
