using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Naufal_Windows_Tech_s_Powertoys;

internal enum GpuUpdateState { UpdateAvailable, UpToDate, NewerInstalled, ComparisonUnavailable, Unavailable }

internal sealed record GpuDriverRelease(string Version, string ReleaseDate, Uri Source, string Channel,
    IReadOnlyList<string> DriverVersions, string Note = "", Uri? DownloadUri = null, string PublishedSha256 = "");

internal sealed record GpuDriverUpdate(GpuUpdateState State, GpuDriverRelease? Release, DateTimeOffset CheckedAt, string Detail)
{
    internal string Headline => State switch
    {
        GpuUpdateState.UpdateAvailable => "New GPU driver available",
        GpuUpdateState.UpToDate => "Driver is up to date for this catalog",
        GpuUpdateState.NewerInstalled => "Installed driver is newer than this catalog",
        GpuUpdateState.ComparisonUnavailable => "Release found — version comparison unavailable",
        _ => "Unable to verify the latest driver"
    };

    internal string Report
    {
        get
        {
            var published = Release is null ? (Date: "Not verified", Time: "Not verified")
                : GpuDriverUpdates.FormatReleaseDate(Release.ReleaseDate);
            return $"Latest catalog version: {Release?.Version ?? "Not verified"}\n" +
                $"Release date: {published.Date}\nRelease time: {published.Time}\n" +
                (Release is null ? "" : $"Channel: {Release.Channel}\nSource: {Release.Source}\n") +
                $"Checked at: {CheckedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)} (local time; not release time)\n" +
                Detail + (string.IsNullOrWhiteSpace(Release?.Note) ? "" : "\n" + Release.Note);
        }
    }
}

// Pure metadata parsing/comparison. Never download or execute driver packages.
internal static class GpuDriverUpdates
{
    internal static string DisplayInstalledVersion(string vendor, string raw)
    {
        if (vendor == "NVIDIA")
        {
            string marketing = GpuDriverVersionVerification.NvidiaMarketingVersion(raw);
            return marketing.Length > 0 ? marketing : "Unknown";
        }
        // Intel uses four-part public versions; AMD INF cannot be converted to
        // Adrenalin by truncation. Preserve these real versions rather than guess.
        return string.IsNullOrWhiteSpace(raw) ? "Unknown" : raw;
    }
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static Match Match(string text, string pattern) => Regex.Match(text, pattern, Options, RegexTimeout);
    private static MatchCollection Matches(string text, string pattern) => Regex.Matches(text, pattern, Options, RegexTimeout);
    internal static string PlainText(string html) => WebUtility.HtmlDecode(Regex.Replace(
        Regex.Replace(html, @"<script\b[^>]*>.*?</script>|<style\b[^>]*>.*?</style>", " ", Options, RegexTimeout),
        @"<[^>]+>", " ", Options, RegexTimeout)).Replace('\u00a0', ' ');
    private static string Compact(string text) => Regex.Replace(PlainText(text), @"\s+", " ").Trim();

    internal static bool IsOfficialSource(Uri uri, string vendor)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0) return false;
        string[] roots = vendor switch { "NVIDIA" => ["nvidia.com", "geforce.com"], "Intel" => ["intel.com"], "AMD" => ["amd.com"], _ => [] };
        return roots.Any(root => uri.IdnHost.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.EndsWith("." + root, StringComparison.OrdinalIgnoreCase));
    }

    internal static GpuDriverUpdate Evaluate(string vendor, string installed, bool driverInstalled, GpuDriverRelease release, DateTimeOffset checkedAt)
    {
        string active = vendor == "NVIDIA" ? GpuDriverVersionVerification.NvidiaMarketingVersion(installed) : installed;
        bool validActive = driverInstalled && (vendor == "NVIDIA"
            ? Match(active, @"^\d{3}\.\d{2}$").Success : Match(active, @"^\d+\.\d+\.\d+\.\d+$").Success);
        if (!IsOfficialSource(release.Source, vendor) || !Version.TryParse(release.Version, out _))
            return new(GpuUpdateState.Unavailable, null, checkedAt, "The vendor response did not contain a verified release.");
        string? target = release.DriverVersions.Count == 1 ? release.DriverVersions[0] :
            release.DriverVersions.FirstOrDefault(value => value == active);
        if (!validActive || target is null || !Version.TryParse(active, out Version? installedVersion) || !Version.TryParse(target, out Version? targetVersion))
            return new(GpuUpdateState.ComparisonUnavailable, release, checkedAt,
                "The installed driver could not be compared unambiguously. Package and Windows driver versions may differ; review the official source.");
        int comparison = targetVersion.CompareTo(installedVersion);
        return new(comparison > 0 ? GpuUpdateState.UpdateAvailable : comparison == 0 ? GpuUpdateState.UpToDate : GpuUpdateState.NewerInstalled,
            release, checkedAt, $"Installed comparable version: {active}. Vendor driver version: {target}. No automatic installation or downgrade is performed.");
    }

    internal static (string Date, string Time) FormatReleaseDate(string raw)
    {
        const string missing = "Not published by vendor";
        raw = WebUtility.HtmlDecode(raw).Trim();
        // Only an explicit clock in release metadata is a release time. Never use
        // HTTP Last-Modified, a page's datePublished, INF date or the check time.
        if (Match(raw, @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})$").Success &&
            DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
            return (stamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), stamp.ToString("HH:mm:ss 'UTC'zzz", CultureInfo.InvariantCulture) + " (vendor offset)");
        if (Match(raw, @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2})?$").Success &&
            DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localStamp))
            return (localStamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), localStamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " (vendor timezone not specified)");
        string[] formats = ["yyyy-MM-dd", "yyyy-M-d", "yyyy.MM.dd", "M/d/yyyy", "MM/dd/yyyy", "ddd MMM dd, yyyy", "ddd MMM d, yyyy", "MMMM d, yyyy", "MMM d, yyyy", "d MMM yyyy"];
        return DateOnly.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
            ? (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), missing)
            : (string.IsNullOrEmpty(raw) ? missing : "Not available (unrecognized vendor date)", missing);
    }

    internal static GpuDriverRelease? ParseIntel(string html, Uri source, string family)
    {
        string text = Compact(html);
        Match version = Match(text, @"(?:Graphics\s+Driver|Driver|Version)\s*:?\s*(\d+\.\d+\.\d+\.\d+)\b");
        if (!version.Success) return null;
        Match date = Match(text, @"\b(?:Release\s+Date|Date)\s*:?\s*(\d{1,2}/\d{1,2}/\d{4}|\d{4}-\d{2}-\d{2}|[A-Za-z]+ \d{1,2}, \d{4})\b");
        return new(version.Groups[1].Value, date.Success ? date.Groups[1].Value : "", source,
            family + " / vendor generic Windows graphics", [version.Groups[1].Value],
            "The PC manufacturer may supply a customized OEM driver. Verify the supported products and operating system before installation.");
    }

    internal static Uri? AmdProductPage(string gpuName, bool portable)
    {
        if (portable) return null; // Notebook/APU/OEM families require their own exact product mapping.
        Match model = Match(gpuName.Trim(), @"^(?:AMD\s+)?Radeon(?:\(TM\)|™)?\s+RX\s+([5679]\d{3})(?:\s+(GRE|XTX|XT))?$");
        if (!model.Success) return null;
        string number = model.Groups[1].Value;
        string suffix = model.Groups[2].Success ? "-" + model.Groups[2].Value.ToLowerInvariant() : "";
        return new Uri($"https://www.amd.com/en/support/downloads/drivers.html/graphics/radeon-rx/radeon-rx-{number[0]}000-series/amd-radeon-rx-{number}{suffix}.html");
    }

    internal static GpuDriverRelease? ParseAmd(string html, Uri source, string gpuName, bool windows11 = true, bool? optional = null)
    {
        string title = Compact(Match(html, @"<h1\b[^>]*>(.*?)</h1>").Groups[1].Value);
        string Normalize(string value) => Regex.Replace(value.ToUpperInvariant().Replace("AMD", "").Replace("(TM)", ""), @"[^A-Z0-9]", "");
        string titleModel = Regex.Split(title, @"\s+Drivers\b", RegexOptions.IgnoreCase)[0];
        if (Normalize(titleModel) != Normalize(gpuName)) return null;
        // Only the matching OS section, not Linux, another Windows section, or Auto-Detect.
        Match section = Match(html, @"Windows\s+" + (windows11 ? "11" : "10") + @"\s*-\s*64-Bit\s+Edition(.*?)(?:Windows\s+\d+\s*-\s*64-Bit\s+Edition|Linux\s*x86|Ubuntu|</main>)");
        if (!section.Success) return null;
        var releases = new List<GpuDriverRelease>();
        foreach (Match article in Matches(section.Groups[1].Value, @"<article\b[^>]*>.*?</article>"))
        {
            string text = Compact(article.Value);
            if (!text.StartsWith("AMD Software: Adrenalin Edition", StringComparison.OrdinalIgnoreCase)) continue;
            bool isOptional = text.Contains("Optional", StringComparison.OrdinalIgnoreCase);
            if (optional is not null && (isOptional != optional.Value || (!isOptional && !text.Contains("Recommended", StringComparison.OrdinalIgnoreCase)))) continue;
            Match version = Match(text, @"Revision Number\s+Adrenalin\s+(\d+\.\d+\.\d+)\b");
            Match date = Match(text, @"Release Date\s+(\d{4}-\d{2}-\d{2})\b");
            Match notes = Match(article.Value, @"href=[""']([^""']*?/release-notes/RN-RAD-WIN-[^""']+)[""']");
            if (!version.Success || !notes.Success || !text.Contains("WHQL", StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(source, WebUtility.HtmlDecode(notes.Groups[1].Value), out var uri) || !IsOfficialSource(uri, "AMD")) continue;
            // Require notes for this exact package, not a nearby article's release.
            if (!uri.AbsolutePath.EndsWith("RN-RAD-WIN-" + version.Groups[1].Value.Replace('.', '-') + ".html", StringComparison.OrdinalIgnoreCase)) continue;
            Uri? download = Matches(article.Value, @"href=[""'](https://drivers\.amd\.com/[^""']+\.exe)[""']")
                .Select(m => Uri.TryCreate(WebUtility.HtmlDecode(m.Groups[1].Value), UriKind.Absolute, out var u) ? u : null)
                .FirstOrDefault(u => u is not null && GpuDriverChannels.ApprovedPackage(u, "AMD") &&
                    u.AbsolutePath.Contains("adrenalin-edition-" + version.Groups[1].Value + "-", StringComparison.OrdinalIgnoreCase) &&
                    !u.AbsolutePath.Contains("minimalsetup", StringComparison.OrdinalIgnoreCase));
            releases.Add(new(version.Groups[1].Value, date.Groups[1].Value, uri,
                "AMD Adrenalin / WHQL " + (text.Contains("Optional", StringComparison.OrdinalIgnoreCase) ? "Optional" : "Recommended") + $" (Windows {(windows11 ? "11" : "10")} x64)", [],
                "AMD Adrenalin package versions are not Windows Driver Store versions.", download));
        }
        return releases.OrderByDescending(release => Version.Parse(release.Version)).FirstOrDefault();
    }

    internal static GpuDriverRelease AddAmdDriverVersions(GpuDriverRelease release, string notes)
    {
        string text = Compact(notes);
        // A release may bundle multiple hardware branches; never select the largest INF blindly.
        string expected = Regex.Escape(release.Version);
        var versions = Matches(text, @"Adrenalin Edition\s+" + expected + @"\s+Driver Version.{0,180}?Windows Driver Store Version\s+(\d+\.\d+\.\d+\.\d+)")
            .Select(match => match.Groups[1].Value).Distinct().ToArray();
        return release with { DriverVersions = versions };
    }
}
