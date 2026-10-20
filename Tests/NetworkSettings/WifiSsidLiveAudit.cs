using Naufal_Windows_Tech_s_Powertoys;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text.Json;

// Explicit opt-in acceptance test. Never run from the default regression suite.
internal static class WifiSsidLiveAudit
{
    internal static async Task Run(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("Expected mode, adapter GUID, exact SSID, evidence directory.");
        string directory = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(directory);
        using var log = new StreamWriter(new FileStream(Path.Combine(directory, "audit.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        void Note(string value) { log.WriteLine(DateTimeOffset.Now.ToString("O") + " " + value); Console.WriteLine(value); }
        var network = new NetworkSettingsService();
        var service = new WifiSsidService(Path.Combine(directory, "snapshots"));
        await network.PreflightWifiAsync(args[1]);
        var before = service.Read(args[1]);
        if (before.Target.DisplaySsid != args[2]) throw new InvalidOperationException("Connected SSID differs from explicit test target.");
        var adapters = await network.ReadAsync();
        var adapter = adapters.Single(a => NetworkSettingsService.SameId(a.Id, args[1]));
        if (!string.IsNullOrEmpty(adapter.IPv4.ReadError) || adapter.IPv4.Dns.Length is < 1 or > 2) throw new InvalidOperationException($"A simple, readable current IPv4 DNS configuration is required. DNS count={adapter.IPv4.Dns.Length}; read error={adapter.IPv4.ReadError}");
        if (WifiSsidCodec.Decode(before.IPv4, false).DnsMode != "Automatic") throw new InvalidOperationException("This bounded audit requires original automatic per-SSID DNS.");
        var store = new WifiSsidNative();
        var profiles = WifiProfileInventory.Read(args[1]);
        string Others() => string.Join("|", profiles.Where(p => p.ProfileName != before.Target.ProfileName).SelectMany(p => new[] { false, true }.Select(v6 => Hash(store.Read(p, v6)))));
        string Global() => JsonSerializer.Serialize(adapters.OrderBy(a => a.Id).SelectMany(a => new[] { "Tcpip", "Tcpip6" }.SelectMany(protocol =>
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{protocol}\Parameters\Interfaces\{{{Guid.Parse(a.Id):D}}}");
            return new[] { "NameServer", "EnableDHCP", "IPAddress", "SubnetMask", "DefaultGateway" }.Select(name => new { a.Id, protocol, name, Value = key?.GetValue(name), Kind = key?.GetValueNames().Contains(name) == true ? key.GetValueKind(name).ToString() : "Absent" }).ToArray();
        })));
        string originalGlobal = Global(), originalOthers = Others();
        File.WriteAllText(Path.Combine(directory, "global-before.json"), originalGlobal);
        Note("PREPARED: exact SSID identity verified. Current IPv4 DNS will be stored per-SSID and immediately restored. No reconnect or adapter-global setter.");
        var edit = new NetworkSettingsProfile
        {
            Scope = "WifiSsid", AdapterId = args[1], WifiProfileName = before.Target.ProfileName, SsidHex = before.Target.SsidHex,
            IPv4 = new() { DnsMode = "Manual", PreferredDns = adapter.IPv4.Dns[0], AlternateDns = adapter.IPv4.Dns.ElementAtOrDefault(1) ?? "" }
        };
        Exception? applyFailure = null;
        try
        {
            Note(await service.ApplyAsync(edit, before));
            var saved = service.Read(args[1]);
            var settings = WifiSsidCodec.Decode(saved.IPv4, false);
            if (settings.DnsMode != "Manual" || settings.PreferredDns != edit.IPv4.PreferredDns || settings.AlternateDns != edit.IPv4.AlternateDns || Hash(saved.IPv6) != Hash(before.IPv6)) throw new IOException("Saved metadata did not match expected DNS-only edit.");
            if (Global() != originalGlobal || Others() != originalOthers) throw new IOException("Global settings or other-profile metadata changed during audit; review evidence.");
            Note("APPLY VERIFIED: per-SSID DNS metadata matches; IPv6, inspected global settings and other saved SSID metadata unchanged.");
        }
        catch (Exception ex) { applyFailure = ex; Note("APPLY FAILED: " + ex.Message); }
        finally
        {
            var snapshots = service.Snapshots();
            if (snapshots.Length > 1) throw new IOException("Unexpected multiple snapshots. Review evidence; no arbitrary recovery selected.");
            if (snapshots.Length == 1) Note(await service.RestoreAsync(snapshots[0]));
        }
        var restored = service.Read(args[1]);
        if (!WifiSsidService.SameTarget(before.Target, restored.Target) || Hash(restored.IPv4) != Hash(before.IPv4) || Hash(restored.IPv6) != Hash(before.IPv6)) throw new IOException("Original SSID metadata recovery mismatch.");
        string finalGlobal = Global();
        File.WriteAllText(Path.Combine(directory, "global-after.json"), finalGlobal);
        if (finalGlobal != originalGlobal || Others() != originalOthers) throw new IOException("Post-restore isolation mismatch; retained evidence requires review.");
        Note("RESTORE VERIFIED: exact original metadata, inspected adapter-global values, and other SSID metadata unchanged.");
        if (applyFailure is not null) throw new IOException("Round-trip did not pass; recovery completed.", applyFailure);
        Note("PASS: IPv4 DNS metadata save/read-back/restore only. Effective reconnect/reboot behavior, IPv6 mutation and switching SSIDs NOT tested.");
    }
    private static string Hash(byte[]? bytes) => bytes is null ? "absent" : Convert.ToHexString(SHA256.HashData(bytes));
}
