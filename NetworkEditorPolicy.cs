using System;
using System.Linq;

namespace Naufal_Windows_Tech_s_Powertoys;

// Projection only: loading current values never creates a pending Windows write.
internal static class NetworkEditorPolicy
{
    internal static NetworkFamilySettings FromState(NetworkFamilyState state, bool ipv6)
    {
        var address = state.Addresses.FirstOrDefault();
        bool readable = string.IsNullOrEmpty(state.ReadError) && state.PersistenceConsistent;
        bool automatic = readable && state.Dhcp == "Enabled" && state.Addresses.All(a => a.Origin != "Manual") && (!ipv6 || state.RouterDiscovery == "Enabled");
        bool manual = readable && state.Dhcp == "Disabled" && state.Addresses.Length == 1 && address?.Origin == "Manual" && state.Gateways.Length <= 1 && (!ipv6 || state.RouterDiscovery == "Disabled");
        return new()
        {
            AddressMode = automatic ? "Automatic" : manual ? "Manual" : "Unchanged",
            Address = address?.Address ?? "",
            Subnet = address is null ? "" : ipv6 ? address.Prefix.ToString(System.Globalization.CultureInfo.InvariantCulture) : address.Prefix is >= 1 and <= 32 ? NetworkSettingsService.Mask(address.Prefix) : "",
            Gateway = state.Gateways.FirstOrDefault() ?? "",
            DnsMode = !string.IsNullOrEmpty(state.ReadError) ? "Unchanged" : state.DnsAutomatic ? "Automatic" : state.Dns.Length is >= 1 and <= 2 ? "Manual" : "Unchanged",
            PreferredDns = state.Dns.FirstOrDefault() ?? "",
            AlternateDns = state.Dns.Skip(1).FirstOrDefault() ?? ""
        };
    }

    internal static NetworkFamilySettings Changes(NetworkFamilySettings before, NetworkFamilySettings edited) => edited with
    {
        AddressMode = edited.AddressMode == before.AddressMode && (edited.AddressMode != "Manual" ||
            edited.Address == before.Address && edited.Subnet == before.Subnet && edited.Gateway == before.Gateway) ? "Unchanged" : edited.AddressMode,
        DnsMode = edited.DnsMode == before.DnsMode && (edited.DnsMode != "Manual" ||
            edited.PreferredDns == before.PreferredDns && edited.AlternateDns == before.AlternateDns) ? "Unchanged" : edited.DnsMode
    };
}
