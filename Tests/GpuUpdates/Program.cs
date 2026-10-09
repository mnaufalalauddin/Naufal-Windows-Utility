using Naufal_Windows_Tech_s_Powertoys;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
GpuDriverEntry Entry(string vendor = "NVIDIA", string model = "NVIDIA GeForce RTX 4070 Ti SUPER", string version = "32.0.16.1714") =>
    new(model, model, vendor, version, "2026-09-01", vendor, "fixture.inf", true, "TEST-DEVICE", "2705", "https://www.nvidia.com/en-us/drivers/", true, 0, false);
var service = new GpuDriverService();
if (args.Contains("--live") || args.Contains("--live-catalogs") || args.Contains("--live-intel"))
{
    IEnumerable<GpuDriverEntry> devices = args.Contains("--live") ? await service.ReadInventoryAsync() :
        args.Contains("--live-intel") ?
        [Entry("Intel", "Intel Arc A770 Graphics", "31.0.101.5000"), Entry("Intel", "Intel Arc Pro A60 Graphics", "31.0.101.5000"),
         Entry("Intel", "Intel UHD Graphics 630", "31.0.101.2000"), Entry("Intel", "Intel Iris Xe Graphics", "31.0.101.5000")] :
        [Entry("Intel", "Intel Arc A770 Graphics", "31.0.101.5000"), Entry("AMD", "AMD Radeon RX 7900 XTX", "32.0.21001.1000"), Entry("NVIDIA", "NVIDIA RTX A4000", "31.0.15.0000")];
    Console.WriteLine("READ-ONLY ONLINE PROBE; catalogs are live, no downloads/installers/Windows changes. Synthetic catalog probes are not hardware certification.");
    foreach (var device in devices)
    {
        var catalog = await service.ReadDriverChannelsAsync(device);
        Console.WriteLine($"{device.Name} | installed {GpuDriverUpdates.DisplayInstalledVersion(device.Vendor, device.DriverVersion)}\n{catalog.Detail}");
        foreach (var choice in catalog.Choices)
        {
            var update = GpuDriverUpdates.Evaluate(device.Vendor, device.DriverVersion, device.DriverInstalled, choice.Release, catalog.CheckedAt);
            Console.WriteLine($"{choice.Name}\n{update.Headline}\n{update.Report}\nPackage metadata URL: {choice.Release.DownloadUri}\n");
        }
    }
    return;
}

DateTimeOffset now = new(2026, 10, 7, 13, 45, 12, TimeSpan.FromHours(7));
var source = new Uri("https://www.nvidia.com/en-us/drivers/details/280103/");
var release = new GpuDriverRelease("617.42", "Tue Oct 06, 2026", source, "Game Ready WHQL", ["617.42"]);
foreach (string culture in new[] { "en-US", "id-ID", "de-DE", "ar-SA" })
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
    foreach ((string raw, string expected) in new[] { ("Tue Oct 06, 2026", "2026-10-06"), ("2026.10.06", "2026-10-06"), ("10/6/2026", "2026-10-06"), ("October 6, 2026", "2026-10-06"), ("2026-10-06", "2026-10-06") })
    {
        var date = GpuDriverUpdates.FormatReleaseDate(raw);
        Check(date.Date == expected, "culture-independent vendor date " + raw);
        Check(date.Time == "Not published by vendor", "date-only metadata never invents midnight");
    }
}
Check(GpuDriverUpdates.FormatReleaseDate("2026-10-06T14:30:15-07:00").Time == "14:30:15 UTC-07:00 (vendor offset)", "preserve published offset");
Check(GpuDriverUpdates.FormatReleaseDate("2026-10-06T14:30:15Z").Time.Contains("UTC+00:00"), "UTC timestamp");
Check(GpuDriverUpdates.FormatReleaseDate("2026-10-06 14:30:15").Time.Contains("timezone not specified"), "do not assume timezone");
foreach (string raw in new[] { "", "Unknown", "TheReleaseDate", "2026-02-30", "tomorrow" })
    Check(GpuDriverUpdates.FormatReleaseDate(raw).Time == "Not published by vendor", "invalid date not made into a time");
foreach ((string version, GpuUpdateState state) in new[] { ("32.0.16.1714", GpuUpdateState.UpdateAvailable), ("32.0.16.1742", GpuUpdateState.UpToDate), ("618.00", GpuUpdateState.NewerInstalled), ("garbage", GpuUpdateState.ComparisonUnavailable) })
    Check(GpuDriverUpdates.Evaluate("NVIDIA", version, true, release, now).State == state, "numeric NVIDIA comparison " + version);
Check(GpuDriverUpdates.Evaluate("NVIDIA", "617.42", false, release, now).State == GpuUpdateState.ComparisonUnavailable, "basic display recovery is not up to date");
var result = GpuDriverUpdates.Evaluate("NVIDIA", "617.14", true, release, now);
Check(result.Report.Contains("Release time: Not published by vendor") && result.Report.Contains("13:45:12 +07:00 (local time; not release time)"), "check time separate from release time");
foreach (string url in new[] { "http://www.nvidia.com/test", "https://nvidia.com.evil.test/test", "https://www.nvidia.com:444/test", "https://user@www.nvidia.com/test", "file:///test" })
    Check(!GpuDriverUpdates.IsOfficialSource(new Uri(url), "NVIDIA"), "blocked source " + url);

string Json(string version, string type = "1", string beta = "0", string whql = "1", string active = "1") =>
    JsonSerializer.Serialize(new { IDS = new[] { new { downloadInfo = new { Version = version, DownloadTypeID = type, IsBeta = beta, IsWHQL = whql, IsActive = active,
        Name = "GeForce%20Game%20Ready%20Driver", IsCRD = "0", ReleaseDateTime = "Tue Oct 06, 2026", DetailsURL = source.AbsoluteUri,
        DownloadURL = $"https://us.download.nvidia.com/Windows/{version}/{version}-desktop-win10-win11-64bit-international-dch-whql.exe",
        OSList = new[] { new { OSName = "Windows%2011" }, new { OSName = "Windows%2010%2064-bit" } },
        series = new[] { new { products = new[] { new { productName = "NVIDIA%20GeForce%20RTX%204070%20Ti%20SUPER" } } } }
    } } } });
string nv = Json("617.42");
GpuDriverRelease? ParseNv(string json) => GpuDriverChannels.ParseNvidia(json, source, Entry().Name, GpuDriverChannel.GameReady, true).FirstOrDefault();
Check(ParseNv(nv)?.ReleaseDate == "Tue Oct 06, 2026", "real API ReleaseDateTime field");
Check(ParseNv(nv.Replace("ReleaseDateTime", "ReleaseDate"))?.Version == "617.42", "legacy date field");
foreach (string invalid in new[] { Json("bogus"), Json("617.42", type: "4"), Json("617.42", beta: "1"), Json("617.42", whql: "0"), Json("617.42", active: "0"), "{\"IDS\":[]}" })
    Check(ParseNv(invalid) is null, "reject beta/Studio/inactive/unversioned releases");
var intel = GpuDriverUpdates.ParseIntel("<b>Graphics Driver 32.0.101.9000</b><div>Date</div><p>10/06/2026</p>", new Uri("https://www.intel.com/download/785597"), "Arc")!;
Check(intel.ReleaseDate == "10/06/2026", "Intel visible catalog date");
Check(GpuDriverUpdates.Evaluate("Intel", "32.0.101.8999", true, intel, now).State == GpuUpdateState.UpdateAvailable, "Intel four-part comparison");
Check(GpuDriverUpdates.ParseIntel("<script>datePublished=2026-10-06</script>Graphics Driver 32.0.101.9000", intel.Source, "Arc")?.ReleaseDate == "", "page timestamp not release date");
Check(GpuDriverUpdates.ParseIntel("Access denied", intel.Source, "Arc") is null, "blocked Intel page not up-to-date");

string amdModel = "AMD Radeon RX 7900 XTX";
var amdUri = GpuDriverUpdates.AmdProductPage(amdModel, false)!;
Check(amdUri.AbsolutePath.EndsWith("amd-radeon-rx-7900-xtx.html"), "AMD exact desktop mapping");
foreach (string unsupported in new[] { "AMD Radeon Graphics", "AMD Radeon RX 7900 XT OEM", "AMD Radeon RX 7900M", "AMD Radeon RX 7900 XTX extra", "AMD Radeon RX 580" })
    Check(GpuDriverUpdates.AmdProductPage(unsupported, false) is null, "no guessed AMD family " + unsupported);
Check(GpuDriverUpdates.AmdProductPage(amdModel, true) is null, "portable OEM not generic desktop");
string AmdArticle(string version, string title = "AMD Software: Adrenalin Edition") => $"<article><h4>{title}</h4>Revision Number Adrenalin {version} WHQL (Optional) Release Date 2026-09-29 <a href='/en/resources/support-articles/release-notes/RN-RAD-WIN-{version.Replace('.', '-')}.html'>Release notes</a></article>";
string amdHtml = $"<h1>AMD Radeon™ RX 7900 XTX Drivers and Downloads</h1>Windows 11 - 64-Bit Edition {AmdArticle("26.8.1")} {AmdArticle("26.9.2")} {AmdArticle("99.9.9", "Auto-Detect and Install")} Windows 10 - 64-Bit Edition {AmdArticle("99.0.0")} </main>";
var amd = GpuDriverUpdates.ParseAmd(amdHtml, amdUri, amdModel)!;
Check(amd.Version == "26.9.2" && amd.ReleaseDate == "2026-09-29", "newest AMD Windows 11 package, never Auto-Detect or wrong OS");
Check(GpuDriverUpdates.ParseAmd(amdHtml, amdUri, "AMD Radeon RX 7900 XT") is null, "XT and XTX not interchangeable");
Check(GpuDriverUpdates.ParseAmd(amdHtml, amdUri, amdModel, windows11: false)?.Version == "99.0.0", "Windows 10 gets its own AMD package section");
string notes = "AMD Software: Adrenalin Edition 26.9.2 Driver Version 26.20.15.02 (Windows Driver Store Version 32.0.32015.2008).";
amd = GpuDriverUpdates.AddAmdDriverVersions(amd, notes);
Check(GpuDriverUpdates.Evaluate("AMD", "32.0.32015.1000", true, amd, now).State == GpuUpdateState.UpdateAvailable, "AMD compares official INF not Adrenalin version");
amd = GpuDriverUpdates.AddAmdDriverVersions(amd, notes + " AMD Software: Adrenalin Edition 26.9.2 Driver Version 25.10.45.11 (Windows Driver Store Version 32.0.21045.11001).");
Check(GpuDriverUpdates.Evaluate("AMD", "32.0.21045.11001", true, amd, now).State == GpuUpdateState.UpToDate, "AMD exact matching branch");
Check(GpuDriverUpdates.Evaluate("AMD", "32.0.21045.10000", true, amd, now).State == GpuUpdateState.ComparisonUnavailable, "multiple AMD branches not compared blindly");
Check(GpuDriverUpdates.AddAmdDriverVersions(amd, notes.Replace("26.9.2", "26.8.1")).DriverVersions.Count == 0, "wrong release notes not used for INF comparison");
Check(GpuDriverUpdates.FormatReleaseDate(amd.ReleaseDate).Time == "Not published by vendor", "AMD page publication time is not a driver release hour");

const string mapping = "<LookupValueSearch><LookupValue ParentID='127'><Name>NVIDIA GeForce RTX 4070 Ti SUPER</Name><Value>1040</Value></LookupValue><LookupValue ParentID='127'><Name>NVIDIA GeForce RTX 4070</Name><Value>1015</Value></LookupValue></LookupValueSearch>";
var requests = new List<Uri>();
Task<string> Fetch(Uri uri, string vendor, CancellationToken token)
{
    requests.Add(uri);
    Check(!uri.AbsolutePath.EndsWith(".exe"), "metadata check must not request driver binaries");
    return Task.FromResult(uri.AbsolutePath.EndsWith("lookupValueSearch.aspx") ? mapping : nv);
}
var checkedUpdate = await service.CheckForUpdatesAsync(Entry(), fetch: Fetch, windowsBuild: 26300);
Check(checkedUpdate.State == GpuUpdateState.UpdateAvailable, "service notification for older installed GPU");
Check(requests.Count == 3 && requests.Skip(1).All(u => u.Query.Contains("pfid=1040")), "service uses exact model for both driver types instead of substring match");
Check(requests[1].Query.Contains("osID=135") && checkedUpdate.Report.Contains("Windows 11 x64"), "Windows 11 uses NVIDIA catalog OS 135");
requests.Clear();
var win10 = await service.CheckForUpdatesAsync(Entry(), fetch: Fetch, windowsBuild: 19045);
Check(requests[1].Query.Contains("osID=57") && win10.Report.Contains("Windows 10 x64"), "Windows 10 uses NVIDIA catalog OS 57");
var unsupportedUpdate = await service.CheckForUpdatesAsync(Entry(model: "NVIDIA GeForce RTX 4070 Ti"), fetch: Fetch);
Check(unsupportedUpdate.State == GpuUpdateState.Unavailable && unsupportedUpdate.Release is null, "no installed-version fallback when lookup fails");
var offline = await service.CheckForUpdatesAsync(Entry(), fetch: (_, _, _) => throw new HttpRequestException("offline"));
Check(offline.State == GpuUpdateState.Unavailable && offline.Release is null, "offline cannot certify up-to-date");
Check(offline.Report.Contains("Release time: Not verified") && !offline.Report.Contains("Not published"), "network failure cannot determine what the vendor publishes");
var malformed = await service.CheckForUpdatesAsync(Entry(), fetch: (_, _, _) => Task.FromResult("garbage"));
Check(malformed.State == GpuUpdateState.Unavailable, "malformed response fails safely");
using var cancel = new CancellationTokenSource();
cancel.Cancel();
try { await service.CheckForUpdatesAsync(Entry(), cancel.Token, Fetch); throw new Exception("Cancellation ignored"); }
catch (OperationCanceledException) { Check(true, "cancelled view does not publish late response"); }
Check(!(await service.RunDriverOperationAsync(Entry(), GpuDriverOperationMode.InstallOrUpdate)).Success, "harness cannot install drivers");

// Driver-type selection must govern both metadata and the eventual package resolver.
Check(GpuDriverUpdates.DisplayInstalledVersion("NVIDIA", "32.0.16.1714") == "617.14", "NVIDIA public display version");
Check(GpuDriverUpdates.DisplayInstalledVersion("NVIDIA", "unknown") == "Unknown", "unreadable version not fabricated");
Check(GpuDriverUpdates.DisplayInstalledVersion("Intel", "32.0.101.9034") == "32.0.101.9034", "Intel public version not truncated");
Check(GpuDriverUpdates.DisplayInstalledVersion("AMD", "32.0.32015.2008") == "32.0.32015.2008", "AMD Adrenalin not guessed from INF");
string studio = nv.Replace("GeForce%20Game%20Ready%20Driver", "NVIDIA%20Studio%20Driver")
    .Replace("\"IsCRD\":\"0\"", "\"IsCRD\":\"1\"").Replace("\"IsWHQL\":\"1\"", "\"IsWHQL\":\"0\"")
    .Replace("international-dch", "international-nsd-dch");
Task<string> ChannelsFetch(Uri uri, string vendor, CancellationToken token)
{
    Check(!uri.AbsolutePath.EndsWith(".exe"), "resolver is metadata-only until installation is explicitly requested");
    return Task.FromResult(uri.AbsolutePath.EndsWith("lookupValueSearch.aspx") ? mapping : uri.Query.Contains("upCRD=1") ? studio : nv);
}
var channels = await service.ReadDriverChannelsAsync(Entry(), fetch: ChannelsFetch, windowsBuild: 26300);
Check(channels.Choices.Select(c => c.Id).SequenceEqual(new[] { GpuDriverChannel.GameReady, GpuDriverChannel.Studio }), "GeForce offers verified Game Ready and Studio, not Enterprise");
Check(GpuDriverChannels.NvidiaCandidates("NVIDIA RTX A4000").SequenceEqual(new[] { GpuDriverChannel.RtxEnterprise }), "RTX workstation uses Enterprise candidate, not GeForce inference");
Check(GpuDriverChannels.NvidiaCandidates("NVIDIA GeForce RTX 4070 Ti SUPER").Length == 2, "RTX in a GeForce name does not mean Enterprise");
var studioPackage = await service.ResolveSelectedPackageAsync(Entry(), GpuDriverChannel.Studio, "617.42", fetch: ChannelsFetch);
var gamePackage = await service.ResolveSelectedPackageAsync(Entry(), GpuDriverChannel.GameReady, "617.42", fetch: ChannelsFetch);
Check(studioPackage.DownloadUri.AbsolutePath.Contains("-nsd-") && !gamePackage.DownloadUri.AbsolutePath.Contains("-nsd-"), "selected channel resolves the exact different package at the same numeric version");
Check(studioPackage.Resolver.Contains("Studio") && studioPackage.OnlineVersion == "617.42", "operation log preserves selected type and version");
Check(studioPackage.ExpectedPublishers.Contains("NVIDIA Corporation") && studioPackage.Arguments.SequenceEqual(new[] { "/s" }), "vendor signature and normal installer flags preserved");
foreach (var request in new[] { (GpuDriverChannel.Studio, "616.92", "release changed"), (GpuDriverChannel.RtxEnterprise, "617.42", "wrong type") })
{
    try { await service.ResolveSelectedPackageAsync(Entry(), request.Item1, request.Item2, fetch: ChannelsFetch); throw new Exception("Unsafe selection accepted"); }
    catch (InvalidOperationException) { Check(true, "reject " + request.Item3 + " before binary download"); }
}
try { await service.ResolveSelectedPackageAsync(Entry(version: "618.00"), GpuDriverChannel.Studio, "617.42", fetch: ChannelsFetch); throw new Exception("Downgrade accepted"); }
catch (InvalidOperationException) { Check(true, "no automatic downgrade when switching channels"); }
foreach (string bad in new[] { studio.Replace("RTX%204070%20Ti%20SUPER", "RTX%204070"), studio.Replace("Windows%2011", "Windows%207"), studio.Replace("us.download.nvidia.com", "untrusted.example"), studio.Replace("\"IsActive\":\"1\"", "\"IsActive\":\"0\"") })
    Check(GpuDriverChannels.ParseNvidia(bad, source, Entry().Name, GpuDriverChannel.Studio, true).Count == 0, "reject wrong model, OS, host or withdrawn channel");
Check(GpuDriverChannels.ParseNvidia(nv, source, Entry().Name, GpuDriverChannel.Studio, true).Count == 0, "Game Ready response cannot masquerade as Studio");
string enterprise = nv.Replace("GeForce%20Game%20Ready%20Driver", "NVIDIA%20RTX%20Driver%20Release%20595")
    .Replace("NVIDIA%20GeForce%20RTX%204070%20Ti%20SUPER", "NVIDIA%20RTX%20A4000");
Check(GpuDriverChannels.ParseNvidia(enterprise, source, "NVIDIA RTX A4000", GpuDriverChannel.RtxEnterprise, true).Count == 1, "verified Enterprise model/type is available");
Check(GpuDriverChannels.ParseNvidia(enterprise, source, Entry().Name, GpuDriverChannel.RtxEnterprise, true).Count == 0, "Enterprise never offered to consumer RTX solely by name prefix");

string FullAmd(string version, bool optional) => AmdArticle(version).Replace("(Optional)", optional ? "(Optional)" : "Recommended")
    .Replace("</article>", $"<a href='https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-{version}-win11-b.exe'>Download</a></article>");
string amdCatalog = $"<h1>AMD Radeon RX 7900 XTX Drivers and Downloads</h1>Windows 11 - 64-Bit Edition {FullAmd("26.8.1", false)} {FullAmd("26.9.2", true)} </main>";
Task<string> AmdFetch(Uri uri, string vendor, CancellationToken token) => Task.FromResult(uri.AbsolutePath.Contains("release-notes") ? notes : amdCatalog);
var amdChannels = await service.ReadDriverChannelsAsync(Entry("AMD", amdModel, "32.0.32015.1000"), fetch: AmdFetch, windowsBuild: 26300);
Check(amdChannels.Choices.Select(c => c.Id).SequenceEqual(new[] { GpuDriverChannel.AmdRecommended, GpuDriverChannel.AmdOptional }), "AMD offers Recommended and Optional only from exact product section");
var amdPackage = await service.ResolveSelectedPackageAsync(Entry("AMD", amdModel, "32.0.32015.1000"), GpuDriverChannel.AmdOptional, "26.9.2", fetch: AmdFetch, windowsBuild: 26300);
Check(amdPackage.DownloadUri.AbsolutePath.Contains("26.9.2") && !amdPackage.DownloadUri.AbsolutePath.Contains("minimalsetup"), "AMD Optional resolves full selected package, never Auto-Detect");
Check(amdPackage.DriverVersions!.Contains("32.0.32015.2008"), "AMD target INF metadata is carried to post-install verification");

string IntelPage(string product) => $"<h1>Graphics Driver 32.0.101.9034</h1><p>Date 10/06/2026</p><h2>Available Downloads</h2>Windows 11 Family Windows 10 (22H2)<a href='https://downloadmirror.intel.com/929557/gfx_win_101.9034.exe'>Download</a>SHA256: {new string('A', 64)}<h2>Detailed Description</h2><h2>This download is valid for the product(s) listed below.</h2><a>{product}</a></main>";
Task<string> IntelFetch(Uri uri, string vendor, CancellationToken token) => Task.FromResult(IntelPage(uri.AbsolutePath.Contains("741626") ? "Intel® Arc™ Pro A60 Graphics" : "Intel® Arc™ A770 Graphics"));
var intelChannels = await service.ReadDriverChannelsAsync(Entry("Intel", "Intel Arc A770 Graphics", "32.0.101.8000"), fetch: IntelFetch);
Check(intelChannels.Choices.Count == 1 && intelChannels.Choices[0].Id == GpuDriverChannel.IntelGraphics, "Intel Pro hidden when selected GPU is not in Pro products list");
var intelProChannels = await service.ReadDriverChannelsAsync(Entry("Intel", "Intel Arc Pro A60 Graphics", "32.0.101.8000"), fetch: IntelFetch);
Check(intelProChannels.Choices.Count == 1 && intelProChannels.Choices[0].Id == GpuDriverChannel.IntelPro, "Intel Pro shown only for explicit supported product");
var intelPackage = await service.ResolveSelectedPackageAsync(Entry("Intel", "Intel Arc Pro A60 Graphics", "32.0.101.8000"), GpuDriverChannel.IntelPro, "32.0.101.9034", fetch: IntelFetch);
Check(intelPackage.CatalogUri.AbsolutePath.Contains("741626") && intelPackage.OnlineVersion == "32.0.101.9034", "Intel Pro package uses its own verified catalog");
Check(intelPackage.PublishedSha256 == new string('A', 64), "vendor-published SHA256 preserved for integrity checks");
Check(GpuDriverChannels.IntelOsListed(IntelPage("Intel Arc A770 Graphics"), true), "Intel OS supported in actual download section");
Check(!GpuDriverChannels.IntelOsListed(IntelPage("Intel Arc A770 Graphics").Replace("Windows 11", "Windows 10"), true), "Intel Windows 10-only package hidden on Windows 11");
Check(GpuDriverChannels.IntelProductListed(IntelPage("Intel® Arc™ A770 Graphics (16GB)"), "Intel Arc A770 Graphics"), "Intel explicit memory-size variant accepted without broad substring model matching");
foreach (var requestCase in new[] { ("Intel", false), ("Intel", true), ("NVIDIA", false), ("AMD", false) })
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.intel.com/content/www/us/en/download/785597/intel-arc-graphics-windows.html");
    GpuDriverService.ConfigureCatalogRequest(request, requestCase.Item1, requestCase.Item2);
    bool intelMetadata = requestCase == ("Intel", false);
    Check((request.Headers.Accept.ToString() == "text/html, application/xhtml+xml") == intelMetadata, "HTML negotiation applies only to Intel metadata, not installer binaries or other vendors");
    Check((request.Headers.AcceptLanguage.ToString() == "en-US, en; q=0.9") == intelMetadata, "English catalog language is explicit only for Intel metadata");
    Check(!request.Headers.Contains("Cookie") && request.Headers.Authorization is null && request.Headers.UserAgent.Count == 0, "request negotiation does not borrow browser sessions or impersonate a browser");
}
Console.WriteLine($"PASS: {checks} GPU release metadata/service assertions. No Windows changes or driver packages downloaded.");
