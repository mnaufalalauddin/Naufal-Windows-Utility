using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed class NetworkSettingsService
{
    internal string DirectoryPath { get; }
    internal string ProfilesPath => Path.Combine(DirectoryPath, "profiles.json");
    internal string SnapshotDirectory => Path.Combine(DirectoryPath, "Snapshots");
    private readonly NativeCommandRunner _runner = new();
    private readonly Func<string, bool, Task<string>>? _execute;
    private readonly Func<bool> _isAdministrator;
    internal NetworkSettingsService(string? directory = null, Func<string, bool, Task<string>>? execute = null, Func<bool>? isAdministrator = null)
    {
        DirectoryPath = directory ?? Path.Combine(AppDataPaths.LocalRoot, "NetworkSettings");
        _execute = execute;
        _isAdministrator = isAdministrator ?? WindowsPrivilegeService.IsAdministrator;
    }

    internal NetworkSettingsProfile[] LoadProfiles()
    {
        if (!File.Exists(ProfilesPath)) return [];
        if (new FileInfo(ProfilesPath).Length > 2_000_000) throw new IOException("Profile store exceeds the size limit.");
        var profiles = JsonSerializer.Deserialize(File.ReadAllText(ProfilesPath), NetworkSettingsJson.Default.NetworkSettingsProfileArray) ?? throw new IOException("Invalid profile store.");
        foreach (var p in profiles) NetworkSettingsValidation.Validate(p);
        if (profiles.Length > 100 || profiles.Select(p => p.Id).Distinct().Count() != profiles.Length) throw new IOException("Profile store count/identity is invalid.");
        return profiles;
    }

    internal void SaveProfiles(NetworkSettingsProfile[] profiles)
    {
        if (File.Exists(ProfilesPath)) _ = LoadProfiles(); // Never overwrite an unreadable library with an empty UI state.
        if (profiles.Length > 100) throw new ArgumentException("At most 100 profiles are supported.");
        foreach (var p in profiles) NetworkSettingsValidation.Validate(p);
        Directory.CreateDirectory(DirectoryPath);
        string temp = ProfilesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profiles, NetworkSettingsJson.Default.NetworkSettingsProfileArray));
        File.Move(temp, ProfilesPath, true);
    }

    internal async Task<NetworkAdapterState[]> ReadAsync()
    {
        string json = await RunAsync(NetworkSettingsScript.Common + "\nRead-Adapters | ConvertTo-Json -Depth 10 -Compress -AsArray\n", readOnly: true);
        // Windows PowerShell 5.1 has no ConvertTo-Json -AsArray; RunAsync substitutes a forced array expression.
        return JsonSerializer.Deserialize(json, NetworkSettingsJson.Default.NetworkAdapterStateArray) ?? [];
    }

    internal async Task PreflightWifiAsync(string adapterId)
    {
        if (!_isAdministrator()) throw new InvalidOperationException("Restart as Administrator to save Windows SSID settings.");
        var adapter = (await ReadAsync()).SingleOrDefault(a => SameId(a.Id, adapterId));
        if (adapter is not { InterfaceType: 71, Status: "Up" }) throw new InvalidOperationException("Select an active physical Wi-Fi adapter.");
        await RunAsync(NetworkSettingsScript.Common + "\nCheck-SsidLocalControl\n", readOnly: true);
    }

    private async Task<string> RunAsync(string script, bool readOnly = false)
    {
        if (_execute is not null) return await _execute(script, readOnly);
        script = script.Replace("Read-Adapters | ConvertTo-Json -Depth 10 -Compress -AsArray", "ConvertTo-Json -InputObject @(Read-Adapters) -Depth 10 -Compress");
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await _runner.RunAsync(executable,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
            TimeSpan.FromSeconds(readOnly ? 35 : 90), outputEncoding: Encoding.UTF8);
        if (result.TimedOut || result.ExitCode != 0) throw new IOException(result.CombinedOutput);
        return result.StandardOutput.Trim();
    }

    internal async Task<string> ApplyAsync(NetworkSettingsProfile profile)
    {
        NetworkSettingsValidation.Validate(profile);
        RequireAdapterPlan(profile);
        if (!NetworkSettingsValidation.HasChanges(profile)) throw new ArgumentException("Select at least one setting to change.");
        if (!_isAdministrator()) throw new InvalidOperationException("Restart the utility as Administrator to apply network changes.");
        NetworkAdapterState current = (await ReadAsync()).SingleOrDefault(a => SameId(a.Id, profile.AdapterId)) ?? throw new InvalidOperationException("The selected physical adapter is no longer present.");
        RequireEthernet(current);
        if (current.Status != "Up") throw new InvalidOperationException("Apply requires a connected adapter. Reconnect and refresh.");
        NetworkSettingsSnapshot snapshot = new() { Before = current, Requested = profile };
        NetworkSettingsProfile restore = RestoreProfile(snapshot); // Refuse an original state that cannot be restored by this editor.
        Directory.CreateDirectory(SnapshotDirectory);
        string path = Path.Combine(SnapshotDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, snapshot, NetworkSettingsJson.Default.NetworkSettingsSnapshot);
            stream.Flush(true);
        }
        try
        {
            await SetAsync(profile);
            await VerifyAsync(profile);
            return "Configuration read-back verified. Connectivity and internet access are not guaranteed.\nRecovery snapshot: " + path;
        }
        catch (Exception original)
        {
            try { await SetAsync(restore); await VerifyAsync(restore); }
            catch (Exception recovery) { throw new IOException("Apply failed: " + original.Message + "\nRecovery NOT verified: " + recovery.Message + "\nRetained snapshot: " + path); }
            throw new IOException("Apply failed; original configuration was restored and read back. DHCP leases can change.\n" + original.Message + "\nSnapshot: " + path);
        }
    }

    internal string[] Snapshots() => Directory.Exists(SnapshotDirectory)
        ? Directory.GetFiles(SnapshotDirectory, "*.json").OrderByDescending(Path.GetFileName).ToArray() : [];

    internal NetworkSettingsSnapshot LoadSnapshot(string path)
    {
        string full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(SnapshotDirectory), StringComparison.OrdinalIgnoreCase) || new FileInfo(full).Length > 200_000) throw new IOException("Invalid recovery snapshot path.");
        var snapshot = JsonSerializer.Deserialize(File.ReadAllText(full), NetworkSettingsJson.Default.NetworkSettingsSnapshot) ?? throw new IOException("Empty snapshot.");
        if (snapshot.Schema != 1 || snapshot.Machine != Environment.MachineName || !SameId(snapshot.Before.Id, snapshot.Requested.AdapterId)) throw new IOException("Snapshot does not belong to this machine/adapter.");
        return snapshot;
    }

    internal async Task<string> RestoreAsync(string path)
    {
        if (!_isAdministrator()) throw new InvalidOperationException("Administrator rights are required.");
        NetworkSettingsProfile restore = RestoreProfile(LoadSnapshot(path));
        await SetAsync(restore); await VerifyAsync(restore);
        return "Snapshot configuration read-back verified. Automatic addresses/leases may differ; snapshot retained. Internet access was not tested.";
    }

    private async Task SetAsync(NetworkSettingsProfile profile)
    {
        NetworkSettingsValidation.Validate(profile);
        RequireAdapterPlan(profile);
        await RequireEthernetAsync(profile.AdapterId);
        string data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile, NetworkSettingsJson.Default.NetworkSettingsProfile)));
        await RunAsync(NetworkSettingsScript.Common + "\n$p = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "')) | ConvertFrom-Json\n" + NetworkSettingsScript.Apply);
    }

    private async Task VerifyAsync(NetworkSettingsProfile profile)
    {
        NetworkAdapterState after = (await ReadAsync()).SingleOrDefault(a => SameId(a.Id, profile.AdapterId)) ?? throw new IOException("Adapter not found during verification.");
        string problem = VerificationProblem(profile, after);
        if (problem.Length > 0) throw new IOException("Configuration verification failed: " + problem);
    }

    internal static bool SameId(string left, string right) => Guid.TryParse(left, out var a) && Guid.TryParse(right, out var b) && a == b;
    internal const string WifiScopeNotice = "Wi-Fi IP/DNS must be configured per SSID. Adapter-wide Save and restore are blocked. Use the connected-SSID editor on supported Windows builds or open Windows per-SSID settings. No global adapter fallback is provided.";
    internal static void RequireAdapterPlan(NetworkSettingsProfile profile)
    {
        if (profile.Scope != "Adapter") throw new InvalidOperationException("SSID draft saved locally only. The Windows per-SSID write provider is not implemented; it cannot use adapter-wide Apply or restore.");
    }
    internal static void RequireEthernet(NetworkAdapterState adapter)
    {
        if (adapter.InterfaceType != 6) throw new InvalidOperationException(adapter.InterfaceType == 71 ? WifiScopeNotice : "Adapter type is unknown or unsupported. No adapter-wide changes are allowed.");
    }
    internal async Task RequireEthernetAsync(string adapterId)
    {
        var adapter = (await ReadAsync()).SingleOrDefault(a => SameId(a.Id, adapterId)) ?? throw new InvalidOperationException("Adapter identity is missing or ambiguous.");
        RequireEthernet(adapter);
    }
    internal static string VerificationProblem(NetworkSettingsProfile p, NetworkAdapterState state)
    {
        if (!SameId(p.AdapterId, state.Id)) return "Adapter identity changed.";
        if (p.Category != "Unchanged" && p.Category != state.Category) return "Network category differs.";
        foreach (var item in new[] { (p.IPv4, state.IPv4, false), (p.IPv6, state.IPv6, true) })
        {
            var (f, s, v6) = item;
            string label = v6 ? "IPv6" : "IPv4";
            if ((f.AddressMode != "Unchanged" || f.DnsMode != "Unchanged") && !string.IsNullOrEmpty(s.ReadError)) return label + " unreadable: " + s.ReadError;
            if (f.AddressMode != "Unchanged" && !s.PersistenceConsistent) return label + " persistent/live configuration differs.";
            if (f.AddressMode == "Automatic" && (s.Dhcp != "Enabled" || (v6 && s.RouterDiscovery != "Enabled") || s.Addresses.Any(a => a.Origin == "Manual"))) return label + " automatic configuration differs.";
            if (f.AddressMode == "Manual" && (s.Dhcp != "Disabled" || (v6 && s.RouterDiscovery != "Disabled") ||
                !s.Addresses.Any(a => EqualAddress(a.Address, f.Address) && a.Prefix == NetworkSettingsValidation.Prefix(f.Subnet, v6) && a.State is not ("Invalid" or "Duplicate")) ||
                (f.Gateway.Length == 0 ? s.Gateways.Length != 0 : !s.Gateways.Any(g => EqualAddress(g, f.Gateway))))) return label + " static configuration differs.";
            if (f.DnsMode == "Automatic" && !s.DnsAutomatic) return label + " DNS is not automatic.";
            if (f.DnsMode == "Manual" && (s.DnsAutomatic || !s.Dns.SequenceEqual(new[] { f.PreferredDns, f.AlternateDns }.Where(v => v.Length > 0), new AddressComparer()))) return label + " DNS order differs.";
        }
        return "";
    }
    private static bool EqualAddress(string a, string b) => System.Net.IPAddress.TryParse(a, out var x) && System.Net.IPAddress.TryParse(b, out var y) && x.Equals(y);
    private sealed class AddressComparer : System.Collections.Generic.IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => x is not null && y is not null && EqualAddress(x, y);
        public int GetHashCode(string value) => System.Net.IPAddress.Parse(value).GetHashCode();
    }

    internal static NetworkSettingsProfile RestoreProfile(NetworkSettingsSnapshot snapshot)
    {
        var p = snapshot.Requested;
        NetworkSettingsProfile restored = p with
        {
            IPv4 = RestoreFamily(snapshot.Before.IPv4, p.IPv4, false),
            IPv6 = RestoreFamily(snapshot.Before.IPv6, p.IPv6, true),
            Category = p.Category == "Unchanged" ? "Unchanged" : snapshot.Before.Category
        };
        NetworkSettingsValidation.Validate(restored);
        return restored;
    }
    private static NetworkFamilySettings RestoreFamily(NetworkFamilyState state, NetworkFamilySettings change, bool v6)
    {
        if ((change.AddressMode != "Unchanged" || change.DnsMode != "Unchanged") && !string.IsNullOrEmpty(state.ReadError)) throw new InvalidOperationException("Cannot snapshot this address family: " + state.ReadError);
        var result = new NetworkFamilySettings();
        if (change.AddressMode != "Unchanged")
        {
            if (!state.PersistenceConsistent) throw new InvalidOperationException("Live and persistent IP settings differ; resolve the pending/temporary configuration in Windows first.");
            var manual = state.Addresses.Where(a => a.Origin == "Manual").ToArray();
            if (state.Dhcp == "Enabled" && manual.Length == 0 && (!v6 || state.RouterDiscovery == "Enabled")) result = result with { AddressMode = "Automatic" };
            else if (state.Dhcp == "Disabled" && manual.Length == 1 && state.Gateways.Length <= 1 && (!v6 || state.RouterDiscovery == "Disabled"))
                result = result with { AddressMode = "Manual", Address = manual[0].Address, Subnet = v6 ? manual[0].Prefix.ToString() : Mask(manual[0].Prefix), Gateway = state.Gateways.FirstOrDefault() ?? "" };
            else throw new InvalidOperationException("The current IP configuration is complex or unavailable (multiple static addresses/routes or mixed DHCP/router discovery). Use Windows advanced adapter properties; no changes were made.");
        }
        if (change.DnsMode != "Unchanged")
        {
            if (!state.DnsAutomatic && (state.Dns.Length < 1 || state.Dns.Length > 2)) throw new InvalidOperationException("More than two DNS servers, or unreadable DNS state: use Windows adapter properties.");
            result = result with { DnsMode = state.DnsAutomatic ? "Automatic" : "Manual", PreferredDns = state.Dns.FirstOrDefault() ?? "", AlternateDns = state.Dns.Skip(1).FirstOrDefault() ?? "" };
        }
        return result;
    }
    internal static string Mask(int prefix)
    {
        if (prefix < 1 || prefix > 32) throw new ArgumentException("Invalid stored IPv4 prefix.");
        uint mask = uint.MaxValue << (32 - prefix);
        return $"{mask >> 24}.{(mask >> 16) & 255}.{(mask >> 8) & 255}.{mask & 255}";
    }
}
