using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed record WifiSsidState(WifiProfileIdentity Target, byte[]? IPv4, byte[]? IPv6);
internal sealed record WifiSsidSnapshot(int Schema, string Machine, DateTimeOffset Captured, WifiSsidState Before, WifiSsidState After, bool ChangedV4, bool ChangedV6);
[JsonSerializable(typeof(WifiSsidSnapshot))]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
internal partial class WifiSsidJson : JsonSerializerContext { }

internal sealed class WifiSsidService
{
    private readonly IWifiSsidStore _store;
    private readonly Func<string, Task> _preflight;
    private readonly string _directory;
    internal WifiSsidService(string? directory = null, IWifiSsidStore? store = null, Func<string, Task>? preflight = null)
    {
        _directory = directory ?? Path.Combine(AppDataPaths.LocalRoot, "NetworkSettings", "WifiSnapshots");
        _store = store ?? new WifiSsidNative();
        _preflight = preflight ?? (id => new NetworkSettingsService().PreflightWifiAsync(id));
    }
    internal static bool SameTarget(WifiProfileIdentity a, WifiProfileIdentity b) =>
        NetworkSettingsService.SameId(a.AdapterId, b.AdapterId) && a.ProfileName == b.ProfileName && a.SsidHex == b.SsidHex && !a.PolicyManaged && !b.PolicyManaged;
    private static bool Equal(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.SequenceEqual(b);
    private void CheckTarget(WifiProfileIdentity target)
    {
        if (!SameTarget(target, _store.Connected(target.AdapterId))) throw new InvalidOperationException("The connected SSID or Windows Wi-Fi profile changed. Refresh; no further writes are allowed.");
    }
    internal WifiSsidState Read(string adapterId)
    {
        var target = _store.Connected(adapterId);
        if (target.PolicyManaged) throw new InvalidOperationException("Policy-managed SSID.");
        var state = new WifiSsidState(target, _store.Read(target, false), _store.Read(target, true));
        _ = WifiSsidCodec.Decode(state.IPv4, false); _ = WifiSsidCodec.Decode(state.IPv6, true);
        CheckTarget(target);
        return state;
    }
    internal string[] Snapshots() => Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.json").OrderByDescending(p => p).ToArray() : [];
    internal WifiSsidSnapshot LoadSnapshot(string path)
    {
        string full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) != Path.GetFullPath(_directory) || new FileInfo(full).Length > 800000) throw new IOException("Invalid Wi-Fi recovery file.");
        var s = JsonSerializer.Deserialize(File.ReadAllText(full), WifiSsidJson.Default.WifiSsidSnapshot) ?? throw new IOException("Empty Wi-Fi recovery file.");
        if (s.Schema != 1 || s.Machine != Environment.MachineName || !SameTarget(s.Before.Target, s.After.Target) || !(s.ChangedV4 || s.ChangedV6))
            throw new IOException("Wi-Fi snapshot identity does not match this PC.");
        _ = WifiSsidCodec.Decode(s.Before.IPv4, false); _ = WifiSsidCodec.Decode(s.Before.IPv6, true);
        _ = WifiSsidCodec.Decode(s.After.IPv4, false); _ = WifiSsidCodec.Decode(s.After.IPv6, true);
        return s;
    }
    internal async Task<string> ApplyAsync(NetworkSettingsProfile edit, WifiSsidState baseline)
    {
        NetworkSettingsValidation.Validate(edit);
        if (edit.Scope != "WifiSsid" || edit.Category != "Unchanged" ||
            !NetworkSettingsService.SameId(edit.AdapterId, baseline.Target.AdapterId) || edit.WifiProfileName != baseline.Target.ProfileName || edit.SsidHex != baseline.Target.SsidHex)
            throw new InvalidOperationException("Wi-Fi request is not bound to the displayed SSID.");
        if (!NetworkSettingsValidation.HasChanges(edit)) throw new InvalidOperationException("No pending SSID changes.");
        await _preflight(edit.AdapterId);
        CheckTarget(baseline.Target);
        var fresh = Read(edit.AdapterId);
        if (!SameTarget(fresh.Target, baseline.Target) || !Equal(fresh.IPv4, baseline.IPv4) || !Equal(fresh.IPv6, baseline.IPv6)) throw new InvalidOperationException("Windows SSID or its configuration changed since the scan. Refresh before saving.");
        bool v4 = edit.IPv4.AddressMode != "Unchanged" || edit.IPv4.DnsMode != "Unchanged";
        bool v6 = edit.IPv6.AddressMode != "Unchanged" || edit.IPv6.DnsMode != "Unchanged";
        var after = fresh with { IPv4 = v4 ? WifiSsidCodec.Merge(fresh.IPv4, edit.IPv4, false) : fresh.IPv4, IPv6 = v6 ? WifiSsidCodec.Merge(fresh.IPv6, edit.IPv6, true) : fresh.IPv6 };
        if (v4) _store.Validate(after.IPv4!);
        if (v6) _store.Validate(after.IPv6!);
        var snapshot = new WifiSsidSnapshot(1, Environment.MachineName, DateTimeOffset.UtcNow, fresh, after, v4, v6);
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            file.Write(JsonSerializer.SerializeToUtf8Bytes(snapshot, WifiSsidJson.Default.WifiSsidSnapshot)); file.Flush(true);
        }
        bool attempted4 = false, attempted6 = false;
        try
        {
            foreach (bool ipv6 in new[] { false, true })
            {
                if (!(ipv6 ? v6 : v4)) continue;
                CheckTarget(fresh.Target);
                if (!Equal(_store.Read(fresh.Target, ipv6), ipv6 ? fresh.IPv6 : fresh.IPv4)) throw new IOException("SSID configuration changed before writing.");
                if (ipv6) attempted6 = true; else attempted4 = true;
                _store.Write(fresh.Target, ipv6, ipv6 ? after.IPv6 : after.IPv4);
                if (!Equal(_store.Read(fresh.Target, ipv6), ipv6 ? after.IPv6 : after.IPv4)) throw new IOException("Windows SSID metadata read-back did not match.");
            }
            CheckTarget(fresh.Target);
            return "Saved directly to Windows SSID: " + fresh.Target.DisplaySsid + ". Stored IPv4/IPv6 metadata read-back verified. Reconnect this SSID manually if needed; effective connectivity and cross-SSID isolation still require hardware testing. Snapshot: " + path;
        }
        catch (Exception failure)
        {
            try
            {
                await RestoreCore(snapshot with { ChangedV4 = attempted4, ChangedV6 = attempted6 });
                throw new IOException("Save failed: " + failure.Message + " Original attempted sections restored and read back. Snapshot: " + path, failure);
            }
            catch (IOException restored) when (restored.InnerException == failure) { throw; }
            catch (Exception recovery) { throw new IOException("Save failed: " + failure.Message + " Recovery NOT verified: " + recovery.Message + ". Review the retained snapshot: " + path, failure); }
        }
    }
    internal async Task<string> RestoreAsync(string path)
    {
        var snapshot = LoadSnapshot(path); await _preflight(snapshot.Before.Target.AdapterId);
        await RestoreCore(snapshot);
        return "Original Windows SSID metadata restored and read back. Reconnect manually if needed; this does not prove connectivity.";
    }
    private Task RestoreCore(WifiSsidSnapshot s)
    {
        // Reject a known conflict in either family before restoring the other.
        CheckTarget(s.Before.Target);
        foreach (bool ipv6 in new[] { false, true })
        {
            if (!(ipv6 ? s.ChangedV6 : s.ChangedV4)) continue;
            var current = _store.Read(s.Before.Target, ipv6);
            if (!Equal(current, ipv6 ? s.Before.IPv6 : s.Before.IPv4) && !Equal(current, ipv6 ? s.After.IPv6 : s.After.IPv4))
                throw new IOException("SSID was modified outside this save; refusing to overwrite newer data.");
        }
        foreach (bool ipv6 in new[] { true, false })
        {
            if (!(ipv6 ? s.ChangedV6 : s.ChangedV4)) continue;
            CheckTarget(s.Before.Target);
            byte[]? before = ipv6 ? s.Before.IPv6 : s.Before.IPv4, after = ipv6 ? s.After.IPv6 : s.After.IPv4;
            var current = _store.Read(s.Before.Target, ipv6);
            if (Equal(current, before)) continue;
            if (!Equal(current, after)) throw new IOException("SSID was modified outside this save; refusing to overwrite newer data.");
            _store.Write(s.Before.Target, ipv6, before);
            if (!Equal(_store.Read(s.Before.Target, ipv6), before)) throw new IOException("Original SSID metadata recovery failed read-back.");
        }
        return Task.CompletedTask;
    }
}
