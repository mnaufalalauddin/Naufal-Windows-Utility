using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed record WifiProfileIdentity(string AdapterId, string ProfileName, string SsidHex, string DisplaySsid, bool PolicyManaged)
{
    public override string ToString() => DisplaySsid + " · " + ProfileName + (PolicyManaged ? " · policy managed" : "");
}

// Read-only documented WLAN APIs. No profile setter, plaintext-key flag or raw XML persistence.
internal static partial class WifiProfileInventory
{
    internal static WifiProfileIdentity ReadConnected(string adapterId)
    {
        Guid id = Guid.Parse(adapterId);
        Check(WlanOpenHandle(2, 0, out _, out nint client));
        nint data = 0, xml = 0;
        try
        {
            // wlan_intf_opcode_current_connection = 7; WLAN_CONNECTION_ATTRIBUTES = 604 bytes.
            Check(WlanQueryInterface(client, in id, 7, 0, out uint size, out data, out _));
            if (size < 604 || data == 0 || Marshal.ReadInt32(data) != 1) throw new IOException("Wi-Fi is not connected. Refresh before saving.");
            string name = (Marshal.PtrToStringUni(data + 8, 256) ?? "").Split('\0')[0];
            int length = Marshal.ReadInt32(data + 520);
            if (length is < 1 or > 32) throw new IOException("Invalid connected SSID.");
            byte[] ssid = new byte[length]; Marshal.Copy(data + 524, ssid, 0, length);
            uint flags = 0;
            Check(WlanGetProfile(client, in id, name, 0, out xml, ref flags, out _));
            var identity = Parse(adapterId, name, (int)flags, Marshal.PtrToStringUni(xml) ?? "");
            if (identity.SsidHex != Convert.ToHexString(ssid)) throw new IOException("Connected SSID and saved Windows profile differ. Refresh.");
            return identity;
        }
        finally { if (xml != 0) WlanFreeMemory(xml); if (data != 0) WlanFreeMemory(data); WlanCloseHandle(client, 0); }
    }
    internal static WifiProfileIdentity[] Read(string adapterId)
    {
        Guid id = Guid.Parse(adapterId);
        Check(WlanOpenHandle(2, 0, out _, out nint client));
        nint list = 0;
        try
        {
            Check(WlanGetProfileList(client, in id, 0, out list));
            if (list == 0) throw new IOException("Windows returned an empty profile-list buffer.");
            int count = Marshal.ReadInt32(list);
            if (count < 0 || count > 1024) throw new IOException("Invalid WLAN profile count.");
            List<WifiProfileIdentity> result = [];
            // WLAN_PROFILE_INFO_LIST: two DWORDs, followed by WCHAR[256] + DWORD entries.
            for (int i = 0; i < count; i++)
            {
                nint entry = list + 8 + i * 516;
                string name = (Marshal.PtrToStringUni(entry, 256) ?? "").Split('\0')[0];
                uint flags = 0; // Never request WLAN_PROFILE_GET_PLAINTEXT_KEY.
                nint xml = 0;
                try
                {
                    Check(WlanGetProfile(client, in id, name, 0, out xml, ref flags, out _));
                    result.Add(Parse(id.ToString("D"), name, Marshal.ReadInt32(entry + 512), Marshal.PtrToStringUni(xml) ?? ""));
                }
                finally { if (xml != 0) WlanFreeMemory(xml); }
            }
            return result.ToArray();
        }
        finally { if (list != 0) WlanFreeMemory(list); WlanCloseHandle(client, 0); }
    }

    internal static WifiProfileIdentity Parse(string adapterId, string profileName, int flags, string xml)
    {
        if (!Guid.TryParse(adapterId, out _) || string.IsNullOrWhiteSpace(profileName) || profileName.Length > 255)
            throw new ArgumentException("Invalid WLAN profile identity.");
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 262144 });
        XElement root = XElement.Load(reader);
        XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";
        if (root.Name != ns + "WLANProfile" || (string?)root.Element(ns + "name") != profileName)
            throw new IOException("WLAN profile name or schema changed. Refresh before editing.");
        var ssids = root.Element(ns + "SSIDConfig")?.Elements(ns + "SSID").ToArray() ?? [];
        if (ssids.Length != 1) throw new IOException("Only single-SSID profiles can be bound to an IP/DNS draft.");
        string? hex = (string?)ssids[0].Element(ns + "hex");
        byte[] bytes;
        try { bytes = hex is not null ? Convert.FromHexString(hex) : Encoding.UTF8.GetBytes((string?)ssids[0].Element(ns + "name") ?? ""); }
        catch (FormatException) { throw new IOException("Invalid SSID byte identity."); }
        if (bytes.Length is < 1 or > 32) throw new IOException("SSID identity must contain 1–32 bytes.");
        string display;
        try { display = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { display = "SSID hex " + Convert.ToHexString(bytes); }
        if (display.Any(char.IsControl)) display = "SSID hex " + Convert.ToHexString(bytes);
        return new(adapterId, profileName, Convert.ToHexString(bytes), display, (flags & 1) != 0);
    }

    private static void Check(uint code) { if (code != 0) throw new Win32Exception((int)code, "Wi-Fi profile inventory failed (Windows error " + code + "). No settings changed."); }
    [LibraryImport("wlanapi.dll")] private static partial uint WlanOpenHandle(uint version, nint reserved, out uint negotiated, out nint handle);
    [LibraryImport("wlanapi.dll")] private static partial uint WlanCloseHandle(nint handle, nint reserved);
    [LibraryImport("wlanapi.dll")] private static partial uint WlanGetProfileList(nint handle, in Guid id, nint reserved, out nint list);
    [LibraryImport("wlanapi.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial uint WlanGetProfile(nint handle, in Guid id, string name, nint reserved, out nint xml, ref uint flags, out uint access);
    [LibraryImport("wlanapi.dll")] private static partial void WlanFreeMemory(nint memory);
    [LibraryImport("wlanapi.dll")] private static partial uint WlanQueryInterface(nint handle, in Guid id, uint opcode, nint reserved, out uint size, out nint data, out uint valueType);
}
