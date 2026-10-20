using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net;

namespace Naufal_Windows_Tech_s_Powertoys;

// Layout independently inspected in the Windows binaries recorded in docs/wifi-ssid-provider.md.
// This is profile metadata, never the adapter TCP/IP configuration or WLAN credential XML.
internal static class WifiSsidCodec
{
    internal const int MaximumBytes = 131072;
    private sealed record Parts(byte[] Addresses, byte[] Gateways, byte[] Dns, byte[] DnsOptions);
    private static int Int(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at, 4));
    private static void Put(byte[] b, int at, int n) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at, 4), n);
    private static Parts Split(byte[]? b)
    {
        if (b is null) return new([], [], [], []);
        if (b.Length < 28 || b.Length > MaximumBytes || Int(b, 0) is not (1 or 2)) throw new IOException("Unsupported Windows SSID IP metadata.");
        int a = Int(b, 4), g = Int(b, 12), d = Int(b, 20);
        if (a is < 0 or > 16 || g is < 0 or > 16 || d is < 0 or > 16) throw new IOException("SSID metadata counts exceed the supported range.");
        int ao = 28, go = ao + a * 136, dns = go + g * 136, opt = dns + d * 128;
        int length = opt + (Int(b, 0) == 2 ? d * 4104 : 0);
        if (b.Length != length || Int(b, 8) != (a == 0 ? 0 : ao) || Int(b, 16) != (g == 0 ? 0 : go) || Int(b, 24) != (d == 0 ? 0 : dns))
            throw new IOException("Noncanonical SSID metadata is not rewritten by this editor.");
        byte[] options = Int(b, 0) == 2 ? b[opt..] : PlainOptions(d);
        return new(b[ao..go], b[go..dns], b[dns..opt], options);
    }
    private static byte[] PlainOptions(int count)
    {
        byte[] b = new byte[count * 4104];
        for (int i = 0; i < count; i++) Put(b, i * 4104, 1);
        return b;
    }
    private static string Address(byte[] b, int offset, bool ipv6)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset, 2)) != (ipv6 ? 23 : 2))
            throw new IOException("SSID metadata address family does not match its Windows property.");
        return new IPAddress(b.AsSpan(offset + (ipv6 ? 8 : 4), ipv6 ? 16 : 4)).ToString();
    }
    private static byte[] Socket(string ip, bool ipv6, int size = 128)
    {
        byte[] b = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)(ipv6 ? 23 : 2));
        IPAddress.Parse(ip).GetAddressBytes().CopyTo(b, ipv6 ? 8 : 4);
        return b;
    }
    internal static NetworkFamilySettings Decode(byte[]? b, bool ipv6)
    {
        var p = Split(b);
        if (p.Addresses.Length > 136 || p.Gateways.Length > 136 || p.Dns.Length > 256 || (p.Addresses.Length == 0 && p.Gateways.Length > 0))
            throw new IOException("This SSID uses multiple addresses/routes/DNS entries. Use Windows advanced settings.");
        int prefix = p.Addresses.Length == 0 ? 0 : Int(p.Addresses, 128);
        if (p.Addresses.Length != 0 && (prefix < 1 || prefix > (ipv6 ? 128 : 32))) throw new IOException("Invalid SSID prefix.");
        return new()
        {
            AddressMode = p.Addresses.Length == 0 ? "Automatic" : "Manual",
            Address = p.Addresses.Length == 0 ? "" : Address(p.Addresses, 0, ipv6),
            Subnet = prefix == 0 ? "" : ipv6 ? prefix.ToString(System.Globalization.CultureInfo.InvariantCulture) : NetworkSettingsService.Mask(prefix),
            Gateway = p.Gateways.Length == 0 ? "" : Address(p.Gateways, 0, ipv6),
            DnsMode = p.Dns.Length == 0 ? "Automatic" : "Manual",
            PreferredDns = p.Dns.Length == 0 ? "" : Address(p.Dns, 0, ipv6),
            AlternateDns = p.Dns.Length <= 128 ? "" : Address(p.Dns, 128, ipv6)
        };
    }
    internal static byte[] Merge(byte[]? original, NetworkFamilySettings edit, bool ipv6)
    {
        // Reuse the UI/backend validators even when the caller is not the UI.
        var plan = new NetworkSettingsProfile { AdapterId = Guid.Empty.ToString(), IPv4 = ipv6 ? new() : edit, IPv6 = ipv6 ? edit : new() };
        NetworkSettingsValidation.Validate(plan);
        _ = Decode(original, ipv6);
        var p = Split(original);
        if (edit.AddressMode != "Unchanged")
        {
            byte[] address = [], gateway = [];
            if (edit.AddressMode == "Manual")
            {
                address = Socket(edit.Address, ipv6, 136); Put(address, 128, NetworkSettingsValidation.Prefix(edit.Subnet, ipv6));
                if (edit.Gateway.Length > 0) gateway = Socket(edit.Gateway, ipv6, 136);
            }
            p = p with { Addresses = address, Gateways = gateway };
        }
        if (edit.DnsMode != "Unchanged")
        {
            // Preserve encrypted/custom DNS rather than silently downgrading it.
            for (int i = 0; i < p.DnsOptions.Length; i += 4104)
                if (Int(p.DnsOptions, i) != 1 || p.DnsOptions.AsSpan(i + 4, 4100).IndexOfAnyExcept((byte)0) >= 0)
                    throw new InvalidOperationException("This SSID has encrypted/custom DNS settings. Change DNS in Windows Settings; IP-only edits preserve those DNS options.");
            byte[] dns = edit.DnsMode == "Automatic" ? [] : Socket(edit.PreferredDns, ipv6);
            if (edit.DnsMode == "Manual" && edit.AlternateDns.Length > 0) dns = [.. dns, .. Socket(edit.AlternateDns, ipv6)];
            p = p with { Dns = dns, DnsOptions = PlainOptions(dns.Length / 128) };
        }
        byte[] result = new byte[28 + p.Addresses.Length + p.Gateways.Length + p.Dns.Length + p.DnsOptions.Length];
        Put(result, 0, 2);
        int offset = 28;
        foreach (var (bytes, stride, countAt) in new[] { (p.Addresses, 136, 4), (p.Gateways, 136, 12), (p.Dns, 128, 20) })
        {
            Put(result, countAt, bytes.Length / stride); Put(result, countAt + 4, bytes.Length == 0 ? 0 : offset);
            bytes.CopyTo(result, offset); offset += bytes.Length;
        }
        p.DnsOptions.CopyTo(result, offset);
        return result;
    }
}
