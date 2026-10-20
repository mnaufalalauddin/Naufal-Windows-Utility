using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed record NetworkFamilySettings
{
    public string AddressMode { get; init; } = "Unchanged";
    public string Address { get; init; } = "";
    public string Subnet { get; init; } = "";
    public string Gateway { get; init; } = "";
    public string DnsMode { get; init; } = "Unchanged";
    public string PreferredDns { get; init; } = "";
    public string AlternateDns { get; init; } = "";
}

internal sealed record NetworkSettingsProfile
{
    public int Schema { get; init; } = 1;
    public string Id { get; init; } = Guid.NewGuid().ToString("D");
    public string Name { get; init; } = "New profile";
    public string Group { get; init; } = "My networks";
    public string AdapterId { get; init; } = "";
    public string Scope { get; init; } = "Adapter";
    public string WifiProfileName { get; init; } = "";
    public string SsidHex { get; init; } = "";
    public NetworkFamilySettings IPv4 { get; init; } = new();
    public NetworkFamilySettings IPv6 { get; init; } = new();
    public string Category { get; init; } = "Unchanged";
    public string Notes { get; init; } = "";
    public NetworkAdditionalSettings Additional { get; init; } = new();
}

internal sealed record NetworkAddressState
{
    public string Address { get; init; } = "";
    public int Prefix { get; init; }
    public string Origin { get; init; } = "";
    public string State { get; init; } = "";
}

internal sealed record NetworkFamilyState
{
    public string ReadError { get; init; } = "";
    public bool PersistenceConsistent { get; init; } = true;
    public string Dhcp { get; init; } = "";
    public string RouterDiscovery { get; init; } = "";
    public NetworkAddressState[] Addresses { get; init; } = [];
    public string[] Gateways { get; init; } = [];
    public string[] Dns { get; init; } = [];
    public bool DnsAutomatic { get; init; }
}

internal sealed record NetworkAdapterState
{
    public string Id { get; init; } = "";
    public int Index { get; init; }
    public int InterfaceType { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Status { get; init; } = "";
    public string Mac { get; init; } = "";
    public string LinkSpeed { get; init; } = "";
    public string Category { get; init; } = "";
    public NetworkFamilyState IPv4 { get; init; } = new();
    public NetworkFamilyState IPv6 { get; init; } = new();
    public override string ToString() => Name + " · " + Status;
}

internal sealed record NetworkSettingsSnapshot
{
    public int Schema { get; init; } = 1;
    public string Machine { get; init; } = Environment.MachineName;
    public DateTimeOffset Captured { get; init; } = DateTimeOffset.UtcNow;
    public NetworkAdapterState Before { get; init; } = new();
    public NetworkSettingsProfile Requested { get; init; } = new();
}

internal static class NetworkSettingsValidation
{
    internal static int Prefix(string text, bool ipv6)
    {
        if (ipv6)
        {
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= 128) return n;
            throw new ArgumentException("IPv6 prefix length must be 1–128.");
        }
        if (!StrictAddress(text, false, out var mask)) throw new ArgumentException("Enter a dotted IPv4 subnet mask.");
        int bits = 0; bool zero = false;
        foreach (byte part in mask!.GetAddressBytes())
            for (int bit = 7; bit >= 0; bit--)
                if ((part & (1 << bit)) != 0) { if (zero) throw new ArgumentException("The subnet mask must be contiguous."); bits++; }
                else zero = true;
        if (bits == 0) throw new ArgumentException("The subnet mask cannot be 0.0.0.0.");
        return bits;
    }

    internal static bool StrictAddress(string text, bool ipv6, out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(text) || text != text.Trim() || text.Contains('%')) return false;
        if (!IPAddress.TryParse(text, out address) || address.AddressFamily != (ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork)) return false;
        return ipv6 || text.Split('.').Length == 4 && text.Split('.').All(p => p.Length > 0 && p.All(char.IsAsciiDigit) && (p.Length == 1 || p[0] != '0'));
    }

    internal static void Address(string value, bool ipv6, string label, bool allowEmpty = false)
    {
        if (allowEmpty && value.Length == 0) return;
        if (!StrictAddress(value, ipv6, out var ip)) throw new ArgumentException(label + " must be a valid " + (ipv6 ? "IPv6" : "IPv4") + " address (no zone suffix).");
        byte[] b = ip!.GetAddressBytes();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
            (ipv6 ? ip.IsIPv6Multicast : b[0] >= 224 || b[0] == 0))
            throw new ArgumentException(label + " must be a unicast address, not loopback, unspecified or multicast.");
    }

    internal static void Validate(NetworkSettingsProfile profile)
    {
        if (profile is null || profile.Name is null || profile.Group is null || profile.Notes is null || profile.Additional is null) throw new ArgumentException("Profile data is incomplete.");
        if (profile.Schema != 1 || !Guid.TryParse(profile.Id, out _) || !Guid.TryParse(profile.AdapterId, out _)) throw new ArgumentException("Select a current adapter; profile identity is invalid.");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80 || profile.Group.Length > 80 || profile.Notes.Length > 2000) throw new ArgumentException("Profile name/group must be 1–80 characters; notes at most 2000.");
        if (profile.Category is not ("Unchanged" or "Public" or "Private")) throw new ArgumentException("Invalid network category.");
        if (profile.Scope is not ("Adapter" or "WifiSsidDraft" or "WifiSsid")) throw new ArgumentException("Invalid profile scope.");
        if (profile.Scope is "WifiSsidDraft" or "WifiSsid")
        {
            if (string.IsNullOrWhiteSpace(profile.WifiProfileName) || profile.WifiProfileName.Length > 255 || profile.SsidHex is null || profile.SsidHex.Length is < 2 or > 64 || profile.SsidHex.Length % 2 != 0 || !profile.SsidHex.All(Uri.IsHexDigit))
                throw new ArgumentException("Select a Windows Wi-Fi profile with a valid SSID byte identity.");
            if (profile.Category != "Unchanged") throw new ArgumentException("Wi-Fi drafts do not change network category.");
        }
        else if (profile.WifiProfileName != "" || profile.SsidHex != "") throw new ArgumentException("SSID-bound settings cannot be applied as adapter-wide settings.");
        Family(profile.IPv4, false); Family(profile.IPv6, true);
    }

    private static void Family(NetworkFamilySettings value, bool ipv6)
    {
        if (value is null || value.AddressMode is not ("Unchanged" or "Automatic" or "Manual") || value.DnsMode is not ("Unchanged" or "Automatic" or "Manual")) throw new ArgumentException("Invalid address/DNS mode.");
        if (value.Address is null || value.Subnet is null || value.Gateway is null || value.PreferredDns is null || value.AlternateDns is null) throw new ArgumentException("Address fields cannot be null.");
        if (value.AddressMode == "Manual")
        {
            Address(value.Address, ipv6, "IP address"); int prefix = Prefix(value.Subnet, ipv6);
            Address(value.Gateway, ipv6, "Gateway", true);
            if (!ipv6 && prefix < 31)
            {
                byte[] bytes = IPAddress.Parse(value.Address).GetAddressBytes();
                uint address = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                uint hostMask = ~(uint.MaxValue << (32 - prefix));
                if ((address & hostMask) == 0 || (address & hostMask) == hostMask) throw new ArgumentException("Do not use a subnet network/broadcast address.");
            }
            if (!ipv6 && value.Gateway.Length > 0)
            {
                byte[] a = IPAddress.Parse(value.Address).GetAddressBytes(), g = IPAddress.Parse(value.Gateway).GetAddressBytes();
                uint av = ((uint)a[0] << 24) | ((uint)a[1] << 16) | ((uint)a[2] << 8) | a[3];
                uint gv = ((uint)g[0] << 24) | ((uint)g[1] << 16) | ((uint)g[2] << 8) | g[3];
                uint mask = uint.MaxValue << (32 - prefix);
                if ((av & mask) != (gv & mask) || av == gv) throw new ArgumentException("IPv4 gateway must be a different address in the same subnet.");
                if (prefix < 31 && ((av & ~mask) == 0 || (av & ~mask) == ~mask || (gv & ~mask) == 0 || (gv & ~mask) == ~mask)) throw new ArgumentException("Do not use a subnet network/broadcast address.");
            }
        }
        if (value.DnsMode == "Manual")
        {
            Address(value.PreferredDns, ipv6, "Preferred DNS"); Address(value.AlternateDns, ipv6, "Alternate DNS", true);
            if (value.AlternateDns.Length > 0 && IPAddress.Parse(value.PreferredDns).Equals(IPAddress.Parse(value.AlternateDns))) throw new ArgumentException("Preferred and alternate DNS must differ.");
        }
    }

    internal static bool HasChanges(NetworkSettingsProfile p) => p.Category != "Unchanged" ||
        p.IPv4.AddressMode != "Unchanged" || p.IPv6.AddressMode != "Unchanged" || p.IPv4.DnsMode != "Unchanged" || p.IPv6.DnsMode != "Unchanged";

    internal static string Summary(NetworkSettingsProfile p) =>
        (p.Group.Length == 0 ? p.Name : p.Group + " / " + p.Name) + $"\nAdapter: {p.AdapterId}\n" + (p.Scope is "WifiSsidDraft" or "WifiSsid" ? $"Windows Wi-Fi profile: {p.WifiProfileName}\nSSID bytes: {p.SsidHex}\n" + (p.Scope == "WifiSsidDraft" ? "DRAFT ONLY — not applied to Windows.\n" : "Save directly to this Windows SSID; global adapter settings are not written.\n") : "") + FamilySummary("IPv4", p.IPv4) + FamilySummary("IPv6", p.IPv6) + "Network category: " + p.Category;
    private static string FamilySummary(string name, NetworkFamilySettings f) =>
        $"{name}: {f.AddressMode}" + (f.AddressMode == "Manual" ? $" · {f.Address} / {f.Subnet} · gateway {(f.Gateway.Length == 0 ? "none" : f.Gateway)}" : "") +
        $"\n{name} DNS: {f.DnsMode}" + (f.DnsMode == "Manual" ? $" · {f.PreferredDns} {f.AlternateDns}" : "") + "\n";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NetworkSettingsProfile))]
[JsonSerializable(typeof(NetworkAdditionalSettings))]
[JsonSerializable(typeof(NetworkSettingsProfile[]))]
[JsonSerializable(typeof(NetworkAdapterState[]))]
[JsonSerializable(typeof(NetworkSettingsSnapshot))]
internal partial class NetworkSettingsJson : JsonSerializerContext { }
