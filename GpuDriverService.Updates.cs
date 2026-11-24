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
                            var ordered = releases.OrderByDescending(r => Version.Parse(r.Version)).DistinctBy(r => r.Version).ToArray();
                            choices.AddRange(ordered.Skip(1).Select(r => new GpuDriverChoice(id, r)));
                            return ordered.FirstOrDefault();
                        });
                    break;
                case "AMD":
                    Uri amd = GpuDriverUpdates.AmdProductPage(entry.Name, entry.PortableSystem)
                        ?? throw new InvalidOperationException("No exact AMD desktop RX product mapping. Use Official source or your OEM; no alternative driver type is assumed compatible.");
                    string amdHtml = await Read(amd);
                    string amdArchive = "";
                    var previous = new Uri(amd.AbsoluteUri.Replace("/drivers.html/", "/previous-drivers.html/", StringComparison.Ordinal));
                    try { amdArchive = await Read(previous); }
                    catch (HttpRequestException error) { notes.Add("AMD previous releases: " + error.Message); }
                    foreach (var id in new[] { GpuDriverChannel.AmdRecommended, GpuDriverChannel.AmdOptional })
                        await TryChannel(id, async () =>
                        {
                            bool optional = id == GpuDriverChannel.AmdOptional;
                            var history = GpuDriverUpdates.ParseAmdHistory(amdHtml, amd, entry.Name, windows11, optional)
                                .Concat(GpuDriverUpdates.ParseAmdHistory(amdArchive, previous, entry.Name, windows11, optional))
                                .Where(r => r.DownloadUri is not null).DistinctBy(r => r.Version)
                                .OrderByDescending(r => Version.Parse(r.Version)).ToArray();
                            choices.AddRange(history.Skip(1).Select(r => new GpuDriverChoice(id, r)));
                            var release = history.FirstOrDefault();
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
                            choices.AddRange(IntelPreviousVersions(html, page.Page, page.Name, page.Id));
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
                case "Qualcomm":
                    if (!windows11 || !IsSnapdragonX()) throw new InvalidOperationException("Use your device manufacturer's driver catalog for this platform.");
                    notes.Add("Choose the Windows ARM64 package version in the Qualcomm Software Center below.");
                    break;
                default: throw new InvalidOperationException("No vendor catalog for this adapter.");
            }
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { notes.Add("Online check timed out. Retry Check for updates."); }
        catch (Exception error) { notes.Add("Online check failed: " + error.Message); }
        if (entry.Vendor == "NVIDIA" && entry.Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Uri index = new("https://nvidia.custhelp.com/app/answers/list/st/5/kw/Hotfix");
                using var supportTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                supportTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                var hotfixes = HotfixArticles(await fetch(index, entry.Vendor, supportTimeout.Token)).OrderByDescending(c => Version.Parse(c.Release.Version)).ToArray();
                choices.AddRange(hotfixes);
                notes.Add(hotfixes.Length == 0 ? "No GeForce Hotfix articles found in NVIDIA Support." :
                    "GeForce Hotfix " + hotfixes[0].Release.Version + " is listed by NVIDIA Support. Select a Hotfix to review applicability.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { notes.Add("NVIDIA Hotfix lookup: " + error.Message); }
        }
        return new(choices.DistinctBy(c => (c.Id, c.Release.Version)).OrderBy(c => c.Id)
            .ThenByDescending(c => Version.Parse(c.Release.Version)).ToArray(), DateTimeOffset.Now, string.Join("\n", notes));
    }

    private static IEnumerable<GpuDriverChoice> HotfixArticles(string html)
    {
        foreach (Match link in Regex.Matches(html, @"<a\b[^>]*\bhref=[""']/app/answers/detail/a_id/(\d+)[^""']*[""'][^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
        {
            string title = GpuDriverUpdates.PlainText(link.Groups[2].Value);
            var match = Regex.Match(title, @"\bGeForce\s+Hotfix\s+(?:Display\s+)?Driver\s+(?:Version\s+)?v?(\d{3}\.\d{2})\b", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            string version = match.Groups[1].Value;
            yield return new(GpuDriverChannel.GeForceHotfix, new(version, "",
                new Uri("https://nvidia.custhelp.com/app/answers/detail/a_id/" + link.Groups[1].Value),
                "GeForce Hotfix", [version], "NVIDIA Support announcement. Check affected products and operating systems in the article before installing."));
        }
    }

    private static IEnumerable<GpuDriverChoice> IntelPreviousVersions(string html, Uri page, string family, GpuDriverChannel id)
    {
        string path = page.AbsolutePath;
        int slash = path.LastIndexOf('/');
        foreach (Match row in Regex.Matches(html, @"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
        {
            string Find(string pattern) => Regex.Match(row.Value, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Groups[1].Value;
            string version = Find(@"data-label=[""']Version[""']>\s*(\d+\.\d+\.\d+\.\d+)");
            string href = Find(@"href=[""']([^""']+)[""']");
            if (version.Length == 0 || !Uri.TryCreate(page, WebUtility.HtmlDecode(href), out var uri) || uri.Host != page.Host ||
                !GpuDriverUpdates.IsOfficialSource(uri, "Intel") || !Regex.IsMatch(uri.AbsolutePath,
                    "^" + Regex.Escape(path[..(slash + 1)]) + @"\d+" + Regex.Escape(path[slash..]) + "$")) continue;
            yield return new(id, new(version, Find(@"data-label=[""']Release Date[""']>\s*([\d/]+)"), uri, family,
                [], "Archived release for this product family. Review the supported products on Intel's release page."));
        }
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
        var choice = catalog.Choices.FirstOrDefault(c => c.Id == channel && (expectedVersion is null || c.Release.Version == expectedVersion))
            ?? throw new InvalidOperationException("The selected driver type is no longer verified for this GPU/OS. Refresh the catalog. " + catalog.Detail);
        var release = choice.Release;
        if (entry.Vendor == "AMD" && release.DriverVersions.Count == 0)
        {
            var read = fetch ?? FetchCatalogTextAsync;
            try { release = GpuDriverUpdates.AddAmdDriverVersions(release, await read(release.Source, "AMD", cancellationToken)); }
            catch (HttpRequestException) { /* Release notes may be temporarily unavailable. */ }
        }
        if (release.DownloadUri is null) throw new InvalidOperationException("Review this release on the vendor's official page.");
        if (expectedVersion is not null && release.Version != expectedVersion)
            throw new InvalidOperationException("The selected release changed since confirmation. Refresh and confirm its new version before downloading.");
        var comparison = GpuDriverUpdates.Evaluate(entry.Vendor, entry.DriverVersion, entry.DriverInstalled, release, catalog.CheckedAt);
        bool historical = catalog.Choices.First(c => c.Id == channel).Release.Version != release.Version;
        if (historical && entry.DriverInstalled && comparison.State == GpuUpdateState.ComparisonUnavailable)
            throw new InvalidOperationException("The archived package cannot be compared with your installed driver. Review the official release details.");
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
