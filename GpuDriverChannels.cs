using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Naufal_Windows_Tech_s_Powertoys;

internal enum GpuDriverChannel { GameReady, Studio, RtxEnterprise, AmdRecommended, AmdOptional, IntelGraphics, IntelPro }
internal sealed record GpuDriverChoice(GpuDriverChannel Id, GpuDriverRelease Release)
{
    public string Name => GpuDriverChannels.Name(Id);
    public override string ToString() => Name;
}
internal sealed record GpuDriverCatalog(IReadOnlyList<GpuDriverChoice> Choices, DateTimeOffset CheckedAt, string Detail);

internal static class GpuDriverChannels
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    internal static string Name(GpuDriverChannel id) => id switch
    {
        GpuDriverChannel.GameReady => "GeForce Game Ready Driver",
        GpuDriverChannel.Studio => "NVIDIA Studio Driver",
        GpuDriverChannel.RtxEnterprise => "NVIDIA RTX Driver / Enterprise Production Branch",
        GpuDriverChannel.AmdRecommended => "AMD Software: Adrenalin Edition — Recommended",
        GpuDriverChannel.AmdOptional => "AMD Software: Adrenalin Edition — Optional",
        GpuDriverChannel.IntelPro => "Intel Arc Pro Graphics",
        _ => "Intel Graphics — vendor recommended"
    };

    internal static string NormalizeModel(string name) => Regex.Replace(WebUtility.HtmlDecode(name).ToUpperInvariant()
        .Replace("NVIDIA", "").Replace("(TM)", "").Replace("(R)", ""), @"[^A-Z0-9]", "", RegexOptions.None, Timeout);

    internal static GpuDriverChannel[] NvidiaCandidates(string model) =>
        model.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || model.Contains("TITAN", StringComparison.OrdinalIgnoreCase)
            ? [GpuDriverChannel.GameReady, GpuDriverChannel.Studio]
            : Regex.IsMatch(model, @"(?i)\b(?:Quadro|RTX)\b", RegexOptions.CultureInvariant, Timeout)
                ? [GpuDriverChannel.RtxEnterprise] : [];

    internal static string NvidiaParameters(GpuDriverChannel channel) => channel switch
    {
        // Studio entries use IsCRD=1; NVIDIA's matrix may mark IsWHQL=0 even
        // when the filename says WHQL. Use the documented Studio flag, not dltype=4.
        GpuDriverChannel.Studio => "isWHQL=0&dltype=-1&upCRD=1&qnf=null",
        GpuDriverChannel.GameReady => "isWHQL=1&dltype=-1&upCRD=0&qnf=null",
        GpuDriverChannel.RtxEnterprise => "isWHQL=1&dltype=-1&upCRD=0&qnf=0",
        _ => throw new InvalidOperationException("Not a NVIDIA channel.")
    };

    internal static IReadOnlyList<GpuDriverRelease> ParseNvidia(string json, Uri query, string model,
        GpuDriverChannel channel, bool windows11)
    {
        if (!NvidiaCandidates(model).Contains(channel)) return [];
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array) return [];
        List<GpuDriverRelease> releases = [];
        foreach (var row in ids.EnumerateArray())
        {
            if (!row.TryGetProperty("downloadInfo", out var info) || info.ValueKind != JsonValueKind.Object) continue;
            string Read(string key) => info.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
            string version = Read("Version"), name = Uri.UnescapeDataString(Read("Name"));
            if (!Regex.IsMatch(version, @"^\d{3}\.\d{2}$", RegexOptions.None, Timeout) || Read("IsBeta") != "0" || Read("IsActive") != "1" || Read("DownloadTypeID") != "1") continue;
            bool typeMatches = channel switch
            {
                GpuDriverChannel.GameReady => Read("IsCRD") == "0" && Read("IsWHQL") == "1" && name.Contains("Game Ready", StringComparison.OrdinalIgnoreCase),
                GpuDriverChannel.Studio => Read("IsCRD") == "1" && name.Contains("Studio", StringComparison.OrdinalIgnoreCase),
                GpuDriverChannel.RtxEnterprise => Read("IsCRD") == "0" && Read("IsWHQL") == "1" && Read("IsFeaturePreview") != "1" &&
                    ((name.Contains("RTX", StringComparison.OrdinalIgnoreCase) && name.Contains("Driver", StringComparison.OrdinalIgnoreCase)) || name.Contains("Quadro", StringComparison.OrdinalIgnoreCase)) && !name.Contains("GeForce", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (!typeMatches) continue;
            // A matching query is not enough: verify returned supported products and OS.
            bool productMatches = info.TryGetProperty("series", out var series) && series.ValueKind == JsonValueKind.Array &&
                series.EnumerateArray().Any(s => s.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array &&
                    products.EnumerateArray().Any(p => p.TryGetProperty("productName", out var n) && n.ValueKind == JsonValueKind.String &&
                        NormalizeModel(Uri.UnescapeDataString(n.GetString()!)) == NormalizeModel(model)));
            bool osMatches = info.TryGetProperty("OSList", out var systems) && systems.ValueKind == JsonValueKind.Array &&
                systems.EnumerateArray().Any(os => os.TryGetProperty("OSName", out var n) && n.ValueKind == JsonValueKind.String &&
                    Uri.UnescapeDataString(n.GetString()!).Equals(windows11 ? "Windows 11" : "Windows 10 64-bit", StringComparison.OrdinalIgnoreCase));
            if (!productMatches || !osMatches) continue;
            if (!Uri.TryCreate(Read("DownloadURL"), UriKind.Absolute, out var download) || !ApprovedPackage(download, "NVIDIA") ||
                !download.AbsolutePath.Contains("/" + version + "/", StringComparison.Ordinal)) continue;
            Uri source = Uri.TryCreate(Read("DetailsURL"), UriKind.Absolute, out var details) && GpuDriverUpdates.IsOfficialSource(details, "NVIDIA") ? details : query;
            string date = Read("ReleaseDateTime");
            if (date.Length == 0) date = Read("ReleaseDate");
            releases.Add(new(version, date, source, Name(channel) + $" (Windows {(windows11 ? "11" : "10")} x64)", [version],
                "Release channel is the selected catalog, not proof of the installed driver's channel.", download));
        }
        return releases.OrderByDescending(r => Version.Parse(r.Version)).ToArray();
    }

    internal static bool ApprovedPackage(Uri uri, string vendor)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        return vendor switch
        {
            "NVIDIA" => uri.IdnHost is "us.download.nvidia.com" or "international.download.nvidia.com" or "download.nvidia.com",
            "AMD" => uri.IdnHost == "drivers.amd.com",
            "Intel" => uri.IdnHost == "downloadmirror.intel.com",
            _ => false
        };
    }

    internal static bool IntelProductListed(string html, string model)
    {
        var section = Regex.Match(html, @"(?is)This download is valid for the product.*?(?:Automatically update|</main>)", RegexOptions.CultureInvariant, Timeout);
        if (!section.Success) return false;
        string target = NormalizeModel(model);
        return Regex.Matches(section.Value, @"(?is)<a\b[^>]*>(.*?)</a>", RegexOptions.CultureInvariant, Timeout)
            .Any(a =>
            {
                string product = NormalizeModel(GpuDriverUpdates.PlainText(a.Groups[1].Value).Trim());
                return product == target || product == target + "8GB" || product == target + "16GB";
            });
    }

    internal static bool IntelOsListed(string html, bool windows11)
    {
        string text = GpuDriverUpdates.PlainText(html);
        var downloads = Regex.Match(text, @"(?is)Available Downloads(.*?)Detailed Description", RegexOptions.CultureInvariant, Timeout);
        return downloads.Success && Regex.IsMatch(downloads.Groups[1].Value,
            @"\bWindows\s*" + (windows11 ? "11" : "10") + @"\b", RegexOptions.IgnoreCase, Timeout);
    }
}
