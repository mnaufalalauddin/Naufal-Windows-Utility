using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Naufal_Windows_Tech_s_Powertoys;

internal interface IWifiSsidStore
{
    WifiProfileIdentity Connected(string adapterId);
    byte[]? Read(WifiProfileIdentity target, bool ipv6);
    void Write(WifiProfileIdentity target, bool ipv6, byte[]? value);
    void Validate(byte[] value);
}

// Undocumented ABI: strictly allowlisted to independently analyzed Windows binaries.
// Never fall back to Set-NetIPAddress, interface DNS setters, registry edits, or SSID watchers.
internal sealed partial class WifiSsidNative : IWifiSsidStore
{
    private static readonly Lazy<bool> Supported = new(CheckPlatform);
    private static bool CheckPlatform()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException("Per-SSID saving currently requires x64 Windows.");
        foreach (var (file, hash) in new[] {
            ("wlanapi.dll", "23AE26CF389988BB08D34851FAF38E221E5556CDFCD120F09241B78055246D74"),
            ("WlanMediaManager.dll", "CC0E99E666CBDA5984E03356E3EC0B073DE9078A665325BC9E3B2A44E9592ED4") })
        {
            using var stream = File.OpenRead(Path.Combine(Environment.SystemDirectory, file));
            if (Convert.ToHexString(SHA256.HashData(stream)) != hash)
                throw new PlatformNotSupportedException("This Windows WLAN build has not been reviewed for per-SSID writes. Use Windows per-SSID settings. Global adapter settings will not be changed.");
        }
        return true;
    }
    public WifiProfileIdentity Connected(string adapterId)
    {
        _ = Supported.Value;
        var target = WifiProfileInventory.ReadConnected(adapterId);
        if (target.PolicyManaged) throw new InvalidOperationException("This SSID is policy-managed. Use your network administrator.");
        return target;
    }
    private static string Key(bool ipv6) => ipv6 ? "ProfileIpConfigurationV6" : "ProfileIpConfigurationV4";
    public byte[]? Read(WifiProfileIdentity target, bool ipv6)
    {
        _ = Supported.Value;
        Guid id = Guid.Parse(target.AdapterId);
        nint data = 0;
        try
        {
            uint error = WlanGetProfileMetadata(in id, target.ProfileName, 0, Key(ipv6), out uint size, out data);
            if (error is 2 or 1168) return null;
            Check(error);
            if (size == 0) return null;
            if (data == 0 || size > WifiSsidCodec.MaximumBytes) throw new IOException("Windows returned invalid SSID IP metadata.");
            byte[] result = new byte[size]; Marshal.Copy(data, result, 0, result.Length);
            Validate(result); return result;
        }
        finally { if (data != 0) WlanFreeMemory(data); }
    }
    public unsafe void Validate(byte[] value)
    {
        _ = Supported.Value;
        if (value.Length is < 28 or > WifiSsidCodec.MaximumBytes) throw new IOException("Invalid SSID IP buffer size.");
        fixed (byte* data = value) Check(WlanVerifyProfileIpConfiguration((uint)value.Length, (nint)data));
    }
    public unsafe void Write(WifiProfileIdentity target, bool ipv6, byte[]? value)
    {
        _ = Supported.Value;
        if (value is not null) Validate(value);
        Guid id = Guid.Parse(target.AdapterId);
        fixed (byte* data = value)
            Check(WlanSetProfileMetadata(in id, target.ProfileName, 0, Key(ipv6), (uint)(value?.Length ?? 0), (nint)data));
    }
    private static void Check(uint error) { if (error != 0) throw new Win32Exception((int)error, "Windows SSID metadata operation failed (" + error + ")."); }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wlanapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint WlanGetProfileMetadata(in Guid adapterId, string profileName, uint flags, string key, out uint size, out nint data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wlanapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint WlanSetProfileMetadata(in Guid adapterId, string profileName, uint flags, string key, uint size, nint data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanVerifyProfileIpConfiguration(uint size, nint data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wlanapi.dll")] private static partial void WlanFreeMemory(nint data);
}
