using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed partial class GpuDriverService
{
    // All three UI actions use this catalog. Never fall back to another channel,
    // Auto-Detect, a generated filename, or the installed version as a release.
    internal async Task<GpuDriverCatalog> ReadDriverChannelsAsync(GpuDriverEntry entry,
        CancellationToken cancellationToken = default,
        Func<Uri, string, CancellationToken, Task<string>>? fetch = null, int? windowsBuild = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        bool windows11 = (windowsBuild ?? Environment.OSVersion.Version.Build) >= 22000;
        fetch ??= FetchCatalogTextAsync;
        List<GpuDriverChoice> choices = [];
        List<string> notes = [];
        async Task<string> Read(Uri uri)
        {
            if (!GpuDriverUpdates.IsOfficialSource(uri, entry.Vendor)) throw new InvalidOperationException("Release source is not approved vendor HTTPS.");
            return await fetch(uri, entry.Vendor, token);
        }
        async Task TryChannel(GpuDriverChannel id, Func<Task<GpuDriverRelease?>> read)
        {
            try
            {
                var release = await read();
                if (release is not null && release.DownloadUri is not null && GpuDriverChannels.ApprovedPackage(release.DownloadUri, entry.Vendor))
                    choices.Add(new(id, release));
                else notes.Add(GpuDriverChannels.Name(id) + ": no matching package verified in the current catalog.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { notes.Add(GpuDriverChannels.Name(id) + ": " + error.Message); }
        }
        try
        {
            token.ThrowIfCancellationRequested();
            switch (entry.Vendor)
            {
                case "NVIDIA":
                    string model = entry.Name;
                    if (!GpuDriverChannels.NvidiaCandidates(model).Any() && NvidiaPciNames.TryGetValue(entry.PciDeviceId, out var known)) model = known;
                    var lookup = XDocument.Parse(await Read(new Uri("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3&ParentID=0")));
                    var mappings = lookup.Descendants().Where(n => n.Name.LocalName == "LookupValue" &&
                        GpuDriverChannels.NormalizeModel(ReadXmlValue(n, "Name")) == GpuDriverChannels.NormalizeModel(model))
                        .Select(n => (Family: ReadXmlValue(n, "ParentID"), Product: ReadXmlValue(n, "Value")))
                        .Distinct().ToArray();
                    if (mappings.Length is 0 or > 4) throw new InvalidOperationException("An exact NVIDIA product mapping could not be established.");
                    foreach (var id in GpuDriverChannels.NvidiaCandidates(model))
                        await TryChannel(id, async () =>
                        {
                            List<GpuDriverRelease> releases = [];
                            foreach (var mapping in mappings)
                            {
                                if (!Regex.IsMatch(mapping.Family, @"^\d+$") || !Regex.IsMatch(mapping.Product, @"^\d+$")) continue;
                                Uri query = new($"https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid={mapping.Family}&pfid={mapping.Product}&osID={(windows11 ? "135" : "57")}&languageCode=1033&beta=0&dch=1&sort1=0&numberOfResults=10&{GpuDriverChannels.NvidiaParameters(id)}");
                                releases.AddRange(GpuDriverChannels.ParseNvidia(await Read(query), query, model, id, windows11));
                            }
                            return releases.OrderByDescending(r => Version.Parse(r.Version)).FirstOrDefault();
                        });
                    break;
                case "AMD":
                    Uri amd = GpuDriverUpdates.AmdProductPage(entry.Name, entry.PortableSystem)
                        ?? throw new InvalidOperationException("No exact AMD desktop RX product mapping. Use Official source or your OEM; no alternative driver type is assumed compatible.");
                    string amdHtml = await Read(amd);
                    foreach (var id in new[] { GpuDriverChannel.AmdRecommended, GpuDriverChannel.AmdOptional })
                        await TryChannel(id, async () =>
                        {
                            var release = GpuDriverUpdates.ParseAmd(amdHtml, amd, entry.Name, windows11, optional: id == GpuDriverChannel.AmdOptional);
                            if (release is not null)
                            {
                                try { release = GpuDriverUpdates.AddAmdDriverVersions(release, await Read(release.Source)); }
                                catch (HttpRequestException) { notes.Add("AMD release notes unavailable; installed package comparison is not verified."); }
                            }
                            return release;
                        });
                    break;
                case "Intel":
                    var intel = GetIntelFamily(entry);
                    if (intel is null || intel.Name.Contains("inferred", StringComparison.OrdinalIgnoreCase) || entry.Name.Contains("6th", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("No confirmed Intel graphics family. Review the PC manufacturer's driver page.");
                    bool arc = entry.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase);
                    var intelPages = new List<(GpuDriverChannel Id, Uri Page, string Name)> { (GpuDriverChannel.IntelGraphics, intel.PageUri, intel.Name) };
                    if (arc) intelPages.Add((GpuDriverChannel.IntelPro, new Uri("https://www.intel.com/content/www/us/en/download/741626/intel-arc-pro-graphics-windows.html"), "Intel Arc Pro Graphics"));
                    foreach (var page in intelPages)
                        await TryChannel(page.Id, async () =>
                        {
                            string html = await Read(page.Page);
                            if (!GpuDriverChannels.IntelOsListed(html, windows11)) return null;
                            if (arc && !GpuDriverChannels.IntelProductListed(html, entry.Name)) return null;
                            var release = GpuDriverUpdates.ParseIntel(html, page.Page, page.Name);
                            if (release is null) return null;
                            string content = WebUtility.HtmlDecode(html).Replace("\\/", "/", StringComparison.Ordinal);
                            var urls = Regex.Matches(content, @"https://downloadmirror\.intel\.com/\d+/[^""'<>\s]+\.exe", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))
                                .Select(m => Uri.TryCreate(m.Value, UriKind.Absolute, out var u) ? u : null)
                                .Where(u => u is not null && GpuDriverChannels.ApprovedPackage(u, "Intel") &&
                                    u.AbsolutePath.Contains(string.Join(".", release.Version.Split('.').Skip(2)), StringComparison.Ordinal)).Distinct().ToArray();
                            Match hash = Regex.Match(GpuDriverUpdates.PlainText(html), @"(?i)SHA256\s*[:=]?\s*([A-F0-9]{64})\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
                            return urls.Length == 1 ? release with { DownloadUri = urls[0], Channel = page.Name,
                                PublishedSha256 = hash.Success ? hash.Groups[1].Value.ToUpperInvariant() : "" } : null;
                        });
                    break;
                default: throw new InvalidOperationException("Only official NVIDIA, AMD and Intel catalogs are supported.");
            }
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { notes.Add("Online check timed out. Retry Check for updates."); }
        catch (Exception error) { notes.Add("Online check failed: " + error.Message); }
        return new(choices, DateTimeOffset.Now, string.Join("\n", notes));
    }

    internal async Task<GpuDriverUpdate> CheckForUpdatesAsync(GpuDriverEntry entry,
        CancellationToken cancellationToken = default,
        Func<Uri, string, CancellationToken, Task<string>>? fetch = null, int? windowsBuild = null,
        GpuDriverChannel? channel = null)
    {
        var catalog = await ReadDriverChannelsAsync(entry, cancellationToken, fetch, windowsBuild);
        var choice = channel is null ? catalog.Choices.FirstOrDefault() : catalog.Choices.FirstOrDefault(c => c.Id == channel);
        return choice is null ? new(GpuUpdateState.Unavailable, null, catalog.CheckedAt,
            "No compatible release was verified for the selected driver type. " + catalog.Detail)
            : GpuDriverUpdates.Evaluate(entry.Vendor, entry.DriverVersion, entry.DriverInstalled, choice.Release, catalog.CheckedAt);
    }

    internal async Task<GpuDriverPackage> ResolveSelectedPackageAsync(GpuDriverEntry entry, GpuDriverChannel channel,
        string? expectedVersion, CancellationToken cancellationToken = default,
        Func<Uri, string, CancellationToken, Task<string>>? fetch = null, int? windowsBuild = null)
    {
        var catalog = await ReadDriverChannelsAsync(entry, cancellationToken, fetch, windowsBuild);
        var choice = catalog.Choices.FirstOrDefault(c => c.Id == channel)
            ?? throw new InvalidOperationException("The selected driver type is no longer verified for this GPU/OS. Refresh the catalog. " + catalog.Detail);
        var release = choice.Release;
        if (expectedVersion is not null && release.Version != expectedVersion)
            throw new InvalidOperationException("The selected release changed since confirmation. Refresh and confirm its new version before downloading.");
        var comparison = GpuDriverUpdates.Evaluate(entry.Vendor, entry.DriverVersion, entry.DriverInstalled, release, catalog.CheckedAt);
        if (comparison.State == GpuUpdateState.NewerInstalled)
            throw new InvalidOperationException("The selected channel is older than the installed driver. Automatic downgrade is not offered; review the vendor's manual instructions.");
        Uri download = release.DownloadUri ?? throw new InvalidOperationException("No verified direct package.");
        if (!GpuDriverChannels.ApprovedPackage(download, entry.Vendor) || !IsApprovedDownloadUri(download, entry.Vendor))
            throw new InvalidOperationException("The selected package host is not approved.");
        return new(entry.Vendor, download, SafeFileName(download, "gpu-driver.exe"), release.Version, release.ReleaseDate, release.PublishedSha256,
            entry.Vendor switch { "NVIDIA" => ["NVIDIA Corporation", "NVIDIA"], "AMD" => ["Advanced Micro Devices", "AMD"], "Intel" => ["Intel Corporation", "Intel"], _ => [] },
            entry.Vendor switch { "NVIDIA" => ["/s"], "AMD" => ["-install"], "Intel" => ["--overwrite", "-s"], _ => [] },
            new HashSet<int>(entry.Vendor == "NVIDIA" ? [0, 1, 3010, 1641] : entry.Vendor == "AMD" ? [0, 3, 3010, 1641] : [0, 3010, 1641]),
            "Exact GPU/OS catalog — " + choice.Name, release.Source, release.DriverVersions);
    }
}
