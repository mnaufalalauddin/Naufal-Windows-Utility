using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal sealed record GpuDriverEntry(
        string Name,
        string RawName,
        string Vendor,
        string DriverVersion,
        string DriverDate,
        string DriverProvider,
        string InfName,
        bool DriverInstalled,
        string PnpDeviceId,
        string PciDeviceId,
        string OfficialUrl,
        bool DeviceHealthy,
        uint ProblemCode,
        bool PortableSystem);

    internal enum GpuDriverOperationMode
    {
        Repair,
        InstallOrUpdate
    }

    internal sealed record GpuDriverOperationResult(
        bool Success,
        int WarningCount,
        bool RestartRequired,
        string Report);

    internal sealed record GpuDriverPackage(
        string Vendor,
        Uri DownloadUri,
        string FileName,
        string OnlineVersion,
        string ReleaseDate,
        string PublishedSha256,
        IReadOnlyList<string> ExpectedPublishers,
        IReadOnlyList<string> Arguments,
        IReadOnlySet<int> AcceptedExitCodes,
        string Resolver,
        Uri CatalogUri,
        IReadOnlyList<string>? DriverVersions = null);

    internal sealed partial class GpuDriverService
    {
        private sealed record IntelFamily(
            string PageId,
            Uri PageUri,
            string Name);

        private sealed record AuthenticodeVerification(
            bool Trusted,
            bool PublisherMatches,
            string Subject,
            string Thumbprint,
            int TrustResult);

        private const uint CrSuccess = 0;
        private const uint CrBufferSmall = 0x0000001A;
        private const uint DnHasProblem = 0x00000400;
        private const long MaximumPackageBytes = 4L * 1024L * 1024L * 1024L;
        private const string PciRoot = @"SYSTEM\CurrentControlSet\Enum\PCI";
        private const string DriverClassRoot = @"SYSTEM\CurrentControlSet\Control\Class";
        private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(45);
        private static readonly Regex DeviceIdPattern = new(
            @"(?i)\bDEV_([0-9A-F]{4})\b",
            RegexOptions.CultureInvariant);
        private static readonly HttpClient HttpClient = CreateHttpClient();
        private static readonly IReadOnlyDictionary<string, string> NvidiaPciNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["2684"] = "NVIDIA GeForce RTX 4090",
                ["2685"] = "NVIDIA GeForce RTX 4090 D",
                ["2689"] = "NVIDIA GeForce RTX 4070 Ti SUPER",
                ["2702"] = "NVIDIA GeForce RTX 4080 SUPER",
                ["2704"] = "NVIDIA GeForce RTX 4080",
                ["2705"] = "NVIDIA GeForce RTX 4070 Ti SUPER",
                ["2709"] = "NVIDIA GeForce RTX 4070",
                ["2782"] = "NVIDIA GeForce RTX 4070 Ti",
                ["2783"] = "NVIDIA GeForce RTX 4070 SUPER",
                ["2786"] = "NVIDIA GeForce RTX 4070",
                ["2788"] = "NVIDIA GeForce RTX 4060 Ti",
                ["2803"] = "NVIDIA GeForce RTX 4060 Ti",
                ["2805"] = "NVIDIA GeForce RTX 4060 Ti",
                ["2808"] = "NVIDIA GeForce RTX 4060",
                ["2882"] = "NVIDIA GeForce RTX 4060",
                ["2B85"] = "NVIDIA GeForce RTX 5090",
                ["2B87"] = "NVIDIA GeForce RTX 5090 D",
                ["2C02"] = "NVIDIA GeForce RTX 5080",
                ["2C05"] = "NVIDIA GeForce RTX 5070 Ti",
                ["2D04"] = "NVIDIA GeForce RTX 5060 Ti",
                ["2D05"] = "NVIDIA GeForce RTX 5060",
                ["2D83"] = "NVIDIA GeForce RTX 5050",
                ["2F04"] = "NVIDIA GeForce RTX 5070",
                ["2F06"] = "NVIDIA GeForce RTX 5060"
            };
        private static readonly IReadOnlyDictionary<string, string> IntelPciNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["3E91"] = "Intel UHD Graphics 630",
                ["3E92"] = "Intel UHD Graphics 630",
                ["3E98"] = "Intel UHD Graphics 630",
                ["3E9B"] = "Intel UHD Graphics 630",
                ["9BC5"] = "Intel UHD Graphics 630",
                ["9BC8"] = "Intel UHD Graphics 630",
                ["3E96"] = "Intel UHD Graphics P630",
                ["3E9A"] = "Intel UHD Graphics P630",
                ["3E94"] = "Intel UHD Graphics P630",
                ["9BC6"] = "Intel UHD Graphics P630",
                ["9BE6"] = "Intel UHD Graphics P630",
                ["9BF6"] = "Intel UHD Graphics P630",
                ["3EA9"] = "Intel UHD Graphics 620",
                ["3EA0"] = "Intel UHD Graphics 620",
                ["5917"] = "Intel UHD Graphics 620",
                ["5912"] = "Intel HD Graphics 630",
                ["591B"] = "Intel HD Graphics 630",
                ["5916"] = "Intel HD Graphics 620",
                ["5921"] = "Intel HD Graphics 620",
                ["5926"] = "Intel Iris Plus Graphics 640",
                ["5927"] = "Intel Iris Plus Graphics 650",
                ["9A49"] = "Intel Iris Xe Graphics",
                ["56A0"] = "Intel Arc A770 Graphics",
                ["56A1"] = "Intel Arc A750 Graphics",
                ["56A2"] = "Intel Arc A580 Graphics",
                ["56A5"] = "Intel Arc A380 Graphics",
                ["56A6"] = "Intel Arc A310 Graphics",
                ["5694"] = "Intel Arc A350M Graphics",
                ["56B3"] = "Intel Arc Pro A60 Graphics",
                ["56B2"] = "Intel Arc Pro A60M Graphics",
                ["56B1"] = "Intel Arc Pro A40/A50 Graphics",
                ["56B0"] = "Intel Arc Pro A30M Graphics",
                ["56BA"] = "Intel Arc A380E Graphics"
            };

        private readonly NativeCommandRunner _commandRunner = new();

        public Task<IReadOnlyList<GpuDriverEntry>> ReadInventoryAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<GpuDriverEntry>>(
                ReadInventory,
                cancellationToken);
        }

        public SecuritySettingsLaunchResult OpenOfficialDownload(GpuDriverEntry entry)
        {
            try
            {
                Uri uri = new(entry.OfficialUrl, UriKind.Absolute);
                if (!IsApprovedCatalogUri(uri, entry.Vendor))
                {
                    return new SecuritySettingsLaunchResult(
                        false,
                        "The vendor source URL did not pass the HTTPS allow-list.");
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = uri.AbsoluteUri,
                    UseShellExecute = true
                });
                return new SecuritySettingsLaunchResult(
                    true,
                    $"Opened the official {entry.Vendor} driver page.");
            }
            catch (Exception exception)
            {
                return new SecuritySettingsLaunchResult(
                    false,
                    $"Unable to open the official driver page. {exception.Message}");
            }
        }

        public async Task<GpuDriverOperationResult> RunDriverOperationAsync(
            GpuDriverEntry entry,
            GpuDriverOperationMode mode,
            IProgress<MaintenanceProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default,
            GpuDriverChannel? channel = null,
            string? expectedVersion = null)
        {
            const int stageCount = 6;
            StringBuilder report = new();
            int warningCount = 0;
            MaintenanceStageTracker stages = new(progress, () => warningCount);
            progress = stages;
            bool restartRequired = false;
            string operation = mode == GpuDriverOperationMode.Repair
                ? "GPU DRIVER REPAIR"
                : "GPU DRIVER DOWNLOAD AND INSTALL";

            report.AppendLine(operation);
            report.AppendLine($"Started: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            report.AppendLine($"GPU: {entry.Name}");
            report.AppendLine($"Vendor: {entry.Vendor}");
            report.AppendLine($"PNP Device ID: {entry.PnpDeviceId}");
            report.AppendLine($"Driver before: {ValueOrMissing(entry.DriverVersion)}");
            report.AppendLine(new string('=', 76));

            if (!WindowsPrivilegeService.IsAdministrator())
            {
                report.AppendLine("FAILED: Administrator rights are required.");
                return new GpuDriverOperationResult(false, 0, false, report.ToString().TrimEnd());
            }
            if (mode == GpuDriverOperationMode.Repair && !entry.DriverInstalled)
            {
                report.AppendLine("FAILED: Repair requires an installed vendor driver. Use Download & install for recovery.");
                return new GpuDriverOperationResult(false, 0, false, report.ToString().TrimEnd());
            }

            try
            {
                ReportStage(progress, 1, stageCount, "Resolve official GPU driver", "Resolving the current official vendor package...");
                if (channel is null) throw new InvalidOperationException("Select a verified driver type before installing or repairing.");
                GpuDriverPackage package = await ResolveSelectedPackageAsync(entry, channel.Value, expectedVersion, cancellationToken);
                report.AppendLine("[1/6] Resolve official GPU driver");
                report.AppendLine($"Resolver: {package.Resolver}");
                report.AppendLine($"Catalog: {package.CatalogUri}");
                report.AppendLine($"Package: {package.DownloadUri}");
                report.AppendLine($"Online version: {ValueOrMissing(package.OnlineVersion)}");
                if (entry.PortableSystem)
                {
                    warningCount++;
                    report.AppendLine("WARNING: Portable/hybrid graphics detected. The computer manufacturer may provide a customized graphics driver.");
                }

                ReportStage(progress, 2, stageCount, "Download official driver package", $"Downloading {package.FileName}...");
                string packagePath = GetPackagePath(package);
                long packageBytes = await DownloadPackageAsync(
                    package,
                    packagePath,
                    progress,
                    stageCount,
                    cancellationToken);
                report.AppendLine("[2/6] Download official driver package");
                report.AppendLine($"Cached package: {packagePath}");
                report.AppendLine($"Package size: {packageBytes / 1024d / 1024d:N1} MB");

                ReportStage(progress, 3, stageCount, "Verify package integrity and vendor signature", "Computing SHA-256 and validating Authenticode...");
                string actualHash = await ComputeSha256Async(packagePath, cancellationToken);
                report.AppendLine("[3/6] Verify package integrity and vendor signature");
                report.AppendLine($"Downloaded SHA-256: {actualHash}");
                if (!string.IsNullOrWhiteSpace(package.PublishedSha256))
                {
                    report.AppendLine($"Published SHA-256 : {package.PublishedSha256}");
                    if (!string.Equals(actualHash, package.PublishedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The downloaded package SHA-256 does not match the vendor-published hash.");
                    }
                }

                AuthenticodeVerification signature = VerifyAuthenticode(
                    packagePath,
                    package.ExpectedPublishers);
                report.AppendLine($"WinVerifyTrust result: 0x{signature.TrustResult:X8}");
                report.AppendLine($"Signer: {ValueOrMissing(signature.Subject)}");
                report.AppendLine($"Certificate thumbprint: {ValueOrMissing(signature.Thumbprint)}");
                if (!signature.Trusted)
                {
                    throw new InvalidDataException("Windows did not trust the downloaded Authenticode signature.");
                }
                if (!signature.PublisherMatches)
                {
                    throw new InvalidDataException($"The trusted signer is not an approved {entry.Vendor} publisher.");
                }

                string installStage = mode == GpuDriverOperationMode.Repair
                    ? "Repair or re-install GPU driver"
                    : "Install or update GPU driver";
                ReportStage(progress, 4, stageCount, installStage, "Running the verified vendor installer silently...");
                NativeCommandResult install = await _commandRunner.RunAsync(
                    packagePath,
                    package.Arguments,
                    TimeSpan.FromHours(1),
                    cancellationToken);
                report.AppendLine($"[4/6] {installStage}");
                report.AppendLine($"Installer exit code: {install.ExitCode}");
                report.AppendLine($"Installer duration: {install.Duration:hh\\:mm\\:ss}");
                if (!string.IsNullOrWhiteSpace(install.CombinedOutput))
                {
                    report.AppendLine(install.CombinedOutput);
                }
                if (install.TimedOut || !package.AcceptedExitCodes.Contains(install.ExitCode))
                {
                    throw new InvalidOperationException(
                        install.TimedOut
                            ? "The GPU driver installer timed out."
                            : $"The GPU driver installer returned exit code {install.ExitCode}.");
                }
                restartRequired = install.ExitCode is 3010 or 1641 ||
                                  (entry.Vendor == "NVIDIA" && install.ExitCode == 1);
                if (restartRequired)
                {
                    warningCount++;
                    report.AppendLine("WARNING: Windows restart is required or recommended by the vendor installer.");
                }

                ReportStage(progress, 5, stageCount, "Re-enumerate selected GPU", "Waiting for the selected PCI device and vendor driver to become active...");
                GpuDriverEntry? after = null;
                for (int attempt = 1; attempt <= 40; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(500, cancellationToken);
                    after = ReadInventory().FirstOrDefault(item =>
                        string.Equals(item.PnpDeviceId, entry.PnpDeviceId, StringComparison.OrdinalIgnoreCase));
                    progress?.Report(new MaintenanceProgressUpdate(
                        5,
                        stageCount,
                        "Re-enumerate selected GPU",
                        $"Re-enumerating GPU ({attempt}/40)...",
                        attempt * 2.5d));
                    if (after is { DriverInstalled: true, DeviceHealthy: true } &&
                        GpuDriverVersionVerification.Compare(entry.Vendor, after.DriverVersion, package.OnlineVersion) != DriverVersionMatch.Mismatch)
                    {
                        break;
                    }
                }
                report.AppendLine("[5/6] Re-enumerate selected GPU");
                if (after is null)
                {
                    throw new InvalidOperationException("The selected GPU was not re-enumerated after installation.");
                }
                if (!after.DriverInstalled)
                {
                    throw new InvalidOperationException("The GPU is still using Microsoft Basic Display Adapter or another non-vendor driver.");
                }

                ReportStage(progress, 6, stageCount, "Verify final driver state", "Reading back provider, version, INF and device health...");
                report.AppendLine("[6/6] Verify final driver state");
                report.AppendLine($"Windows display name after: {after.Name}");
                report.AppendLine($"Driver provider after: {ValueOrMissing(after.DriverProvider)}");
                report.AppendLine($"Driver version after: {ValueOrMissing(after.DriverVersion)}");
                report.AppendLine($"Driver INF after: {ValueOrMissing(after.InfName)}");
                report.AppendLine($"Config Manager problem code: {after.ProblemCode}");
                if (!after.DeviceHealthy || after.ProblemCode != 0)
                {
                    throw new InvalidOperationException($"GPU device health verification failed with problem code {after.ProblemCode}.");
                }
                if (string.IsNullOrWhiteSpace(after.DriverVersion))
                {
                    throw new InvalidOperationException("The active vendor driver version could not be read after installation.");
                }

                DriverVersionMatch targetMatch = GpuDriverVersionVerification.Compare(
                    entry.Vendor, after.DriverVersion, package.OnlineVersion);
                if (entry.Vendor == "AMD" && package.DriverVersions is { Count: > 0 })
                    targetMatch = package.DriverVersions.Contains(after.DriverVersion) ? DriverVersionMatch.Matched : DriverVersionMatch.Mismatch;
                if (targetMatch == DriverVersionMatch.Mismatch && !restartRequired)
                    throw new InvalidOperationException(
                        $"The active driver does not match the selected package. Target={package.OnlineVersion}; active={after.DriverVersion}. Installation was not verified.");
                if (targetMatch != DriverVersionMatch.Matched)
                {
                    warningCount++;
                    report.AppendLine(targetMatch == DriverVersionMatch.Mismatch
                        ? $"WARNING: Target {package.OnlineVersion} is not active yet. Restart was requested; recheck the driver after restarting Windows."
                        : "WARNING: The package does not expose a comparable target display-driver version. Device health was checked, but target-version installation is not verified.");
                }

                bool changed = !string.Equals(
                    entry.DriverVersion,
                    after.DriverVersion,
                    StringComparison.OrdinalIgnoreCase);
                report.AppendLine(targetMatch != DriverVersionMatch.Matched
                    ? $"Device read-back: healthy vendor driver {after.DriverVersion}; target-version verification remains pending."
                    : mode == GpuDriverOperationMode.Repair && !changed
                    ? $"PASS: Same-version repair/re-install verified. Active driver={after.DriverVersion}."
                    : string.IsNullOrWhiteSpace(entry.DriverVersion)
                        ? $"PASS: Vendor driver installed from a driver-missing state. Active driver={after.DriverVersion}."
                        : changed
                            ? $"PASS: Driver version changed: {entry.DriverVersion} -> {after.DriverVersion}."
                            : $"PASS: Selected package version is active. Active driver={after.DriverVersion}.");
                if (restartRequired)
                {
                    report.AppendLine("Restart Windows to complete the vendor installation.");
                }
                stages.Complete(true);
                return new GpuDriverOperationResult(
                    true,
                    warningCount,
                    restartRequired,
                    report.ToString().TrimEnd());
            }
            catch (Exception exception)
            {
                report.AppendLine($"FAILED: {exception.Message}");
                stages.Complete(false);
                return new GpuDriverOperationResult(
                    false,
                    warningCount,
                    restartRequired,
                    report.ToString().TrimEnd());
            }
        }

        private static IReadOnlyList<GpuDriverEntry> ReadInventory()
        {
            List<GpuDriverEntry> entries = new();
            bool portable = IsPortableSystem();
            using RegistryKey? pci = OpenLocalMachineKey(PciRoot);
            if (pci is null)
            {
                return entries;
            }

            foreach (string hardwareId in pci.GetSubKeyNames())
            {
                string vendor = GetVendor(hardwareId);
                if (string.IsNullOrWhiteSpace(vendor))
                {
                    continue;
                }
                using RegistryKey? hardware = pci.OpenSubKey(hardwareId);
                if (hardware is null)
                {
                    continue;
                }

                foreach (string instance in hardware.GetSubKeyNames())
                {
                    string pnpDeviceId = $@"PCI\{hardwareId}\{instance}";
                    if (!TryReadDeviceStatus(pnpDeviceId, out uint deviceInstance, out uint status, out uint problemCode))
                    {
                        continue;
                    }

                    using RegistryKey? device = hardware.OpenSubKey(instance);
                    if (device is null || !IsDisplayDevice(device))
                    {
                        continue;
                    }

                    string rawName = ReadDisplayName(device, $"{vendor} Display Adapter");
                    string driverKeyName = ReadString(device, "Driver");
                    string driverVersion = string.Empty;
                    string driverDate = string.Empty;
                    string provider = string.Empty;
                    string infName = string.Empty;
                    if (!string.IsNullOrWhiteSpace(driverKeyName))
                    {
                        using RegistryKey? driver = OpenLocalMachineKey($@"{DriverClassRoot}\{driverKeyName}");
                        driverVersion = ReadString(driver, "DriverVersion");
                        driverDate = ReadString(driver, "DriverDate");
                        provider = ReadString(driver, "ProviderName");
                        infName = ReadString(driver, "InfPath");
                    }

                    bool installed = IsVendorDriverInstalled(
                        vendor,
                        rawName,
                        provider,
                        driverVersion);
                    string pciDeviceId = GetPciDeviceId(hardwareId);
                    string displayName = rawName;
                    if (!installed)
                    {
                        string busName = ReadBusReportedName(device, deviceInstance);
                        displayName = ResolveMissingDriverDisplayName(
                            vendor,
                            pciDeviceId,
                            rawName,
                            busName);
                        driverVersion = string.Empty;
                        driverDate = string.Empty;
                    }

                    bool healthy = (status & DnHasProblem) == 0 && problemCode == 0;
                    entries.Add(new GpuDriverEntry(
                        displayName,
                        rawName,
                        vendor,
                        driverVersion,
                        driverDate,
                        provider,
                        infName,
                        installed,
                        pnpDeviceId,
                        pciDeviceId,
                        GetOfficialUrl(vendor),
                        healthy,
                        problemCode,
                        portable));
                }
            }

            return entries
                .GroupBy(item => item.PnpDeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.Vendor, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool IsDisplayDevice(RegistryKey device)
        {
            // Newer Windows builds do not always persist the legacy Enum\PCI
            // "Class" string. ClassGUID remains the authoritative setup class.
            return string.Equals(
                       ReadString(device, "Class"),
                       "Display",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       ReadString(device, "ClassGUID"),
                       DisplayClassGuid,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<long> DownloadPackageAsync(
            GpuDriverPackage package, string destination,
            IProgress<MaintenanceProgressUpdate>? progress, int stageCount,
            CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendFollowingRedirectsAsync(
                package.DownloadUri, package.Vendor, downloadOnly: true,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            long? expected = response.Content.Headers.ContentLength;
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
            int lastReported = -5;
            return await AtomicDownloadFile.SaveAsync(input, destination, expected, 1, MaximumPackageBytes,
                received =>
                {
                    if (expected > 0)
                    {
                        int percent = (int)Math.Clamp(received * 100L / expected.Value, 0, 100);
                        if (percent >= lastReported + 5 || percent == 100)
                        {
                            lastReported = percent;
                            progress?.Report(new MaintenanceProgressUpdate(
                                2, stageCount, "Download official driver package",
                                $"Downloaded {received / 1024d / 1024d:N1} of {expected.Value / 1024d / 1024d:N1} MB ({percent}%).",
                                percent));
                        }
                    }
                }, cancellationToken);
        }

        private static async Task<string> FetchCatalogTextAsync(
            Uri uri,
            string vendor,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeout = new(CatalogTimeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            using HttpResponseMessage response = await SendFollowingRedirectsAsync(
                uri,
                vendor,
                downloadOnly: false,
                HttpCompletionOption.ResponseContentRead,
                linked.Token);
            return await response.Content.ReadAsStringAsync(linked.Token);
        }

        private static async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
            Uri initialUri,
            string vendor,
            bool downloadOnly,
            HttpCompletionOption completionOption,
            CancellationToken cancellationToken)
        {
            Uri current = initialUri;
            for (int redirect = 0; redirect <= 8; redirect++)
            {
                bool approved = downloadOnly
                    ? IsApprovedDownloadUri(current, vendor)
                    : IsApprovedCatalogUri(current, vendor) || IsApprovedDownloadUri(current, vendor);
                if (!approved)
                {
                    throw new InvalidOperationException($"Blocked non-approved {vendor} HTTPS host: {current.DnsSafeHost}");
                }
                using HttpRequestMessage request = new(HttpMethod.Get, current);
                HttpResponseMessage response = await HttpClient.SendAsync(
                    request,
                    completionOption,
                    cancellationToken);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
                {
                    Uri next = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                    response.Dispose();
                    current = next;
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return response;
            }
            throw new HttpRequestException("The vendor request exceeded the redirect limit.");
        }

        private static HttpClient CreateHttpClient()
        {
            HttpClientHandler handler = new()
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All
            };
            HttpClient client = new(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Naufal-Windows-Powertoys/1.0");
            return client;
        }

        private static bool IsApprovedCatalogUri(Uri uri, string vendor)
        {
            if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string host = uri.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
            return vendor switch
            {
                "NVIDIA" => HostIsOrSubdomain(host, "nvidia.com") ||
                            HostIsOrSubdomain(host, "geforce.com"),
                "AMD" => HostIsOrSubdomain(host, "amd.com"),
                "Intel" => HostIsOrSubdomain(host, "intel.com"),
                _ => false
            };
        }

        private static bool IsApprovedDownloadUri(Uri uri, string vendor)
        {
            if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string host = uri.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
            return vendor switch
            {
                "NVIDIA" => HostIsOrSubdomain(host, "nvidia.com"),
                "AMD" => HostIsOrSubdomain(host, "drivers.amd.com"),
                "Intel" => host.Equals("downloadmirror.intel.com", StringComparison.Ordinal),
                _ => false
            };
        }

        private static bool HostIsOrSubdomain(string host, string root)
        {
            return host.Equals(root, StringComparison.Ordinal) ||
                   host.EndsWith('.' + root, StringComparison.Ordinal);
        }

        private static AuthenticodeVerification VerifyAuthenticode(
            string path,
            IReadOnlyList<string> expectedPublishers)
        {
            int trustResult = WinVerifyTrustFile(path);
            string subject = string.Empty;
            string thumbprint = string.Empty;
            try
            {
#pragma warning disable SYSLIB0057 // .NET has no loader API that extracts an Authenticode signer from a signed PE file.
                using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                using X509Certificate2 certificate2 = X509CertificateLoader.LoadCertificate(
                    certificate.GetRawCertData());
                subject = certificate2.Subject;
                thumbprint = certificate2.Thumbprint;
            }
            catch
            {
            }
            bool publisherMatches = expectedPublishers.Any(expected =>
                subject.Contains(expected, StringComparison.OrdinalIgnoreCase));
            return new AuthenticodeVerification(
                trustResult == 0,
                publisherMatches,
                subject,
                thumbprint,
                trustResult);
        }

        private static int WinVerifyTrustFile(string path)
        {
            IntPtr filePath = IntPtr.Zero;
            IntPtr fileInfoPointer = IntPtr.Zero;
            try
            {
                filePath = Marshal.StringToCoTaskMemUni(path);
                WinTrustFileInfo fileInfo = new()
                {
                    StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    FilePath = filePath
                };
                fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
                WinTrustData trustData = new()
                {
                    StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                    UiChoice = 2,
                    RevocationChecks = 1,
                    UnionChoice = 1,
                    FileInfo = fileInfoPointer,
                    StateAction = 0,
                    ProviderFlags = 0x00000040,
                    UiContext = 0
                };
                Guid action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
                return WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
            }
            finally
            {
                if (fileInfoPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(fileInfoPointer);
                }
                if (filePath != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(filePath);
                }
            }
        }

        private static async Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken)
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 128,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using SHA256 algorithm = SHA256.Create();
            byte[] hash = await algorithm.ComputeHashAsync(stream, cancellationToken);
            return Convert.ToHexString(hash);
        }

        private static string GetPackagePath(GpuDriverPackage package)
        {
            string root = Path.Combine(
                AppDataPaths.RuntimeCacheDirectory,
                "GPUDrivers",
                SanitizePathPart(package.Vendor));
            return Path.Combine(root, SanitizePathPart(package.FileName));
        }

        private static string SanitizePathPart(string value)
        {
            string sanitized = Regex.Replace(value, @"[^A-Za-z0-9._-]", "_");
            return string.IsNullOrWhiteSpace(sanitized) ? "package.exe" : sanitized;
        }

        private static string SafeFileName(Uri uri, string fallback)
        {
            string fileName = Path.GetFileName(uri.AbsolutePath);
            return string.IsNullOrWhiteSpace(fileName)
                ? fallback
                : SanitizePathPart(fileName);
        }

        private static string ReadXmlValue(XElement element, string name)
        {
            return element.Elements().FirstOrDefault(child =>
                       child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value
                   ?? element.Attributes().FirstOrDefault(attribute =>
                       attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value
                   ?? string.Empty;
        }

        private static IntelFamily? GetIntelFamily(GpuDriverEntry entry)
        {
            string name = entry.Name;
            if (Regex.IsMatch(name, @"(?i)\bArc\b|Core.*Ultra|Arc.*Graphics") ||
                Regex.IsMatch(entry.PciDeviceId, @"^(56A0|56A1|56A2|56A5|56A6|5694|56B0|56B1|56B2|56B3|56BA)$"))
            {
                return new IntelFamily(
                    "785597",
                    new Uri("https://www.intel.com/content/www/us/en/download/785597/intel-arc-graphics-windows.html"),
                    "Intel Arc / Core Ultra graphics");
            }
            if (Regex.IsMatch(name, @"(?i)Iris\s*Xe|UHD\s*(Graphics\s*)?(710|730|750|770)|11th|12th|13th|14th"))
            {
                return new IntelFamily(
                    "864990",
                    new Uri("https://www.intel.com/content/www/us/en/download/864990/intel-11th-14th-gen-processor-graphics-windows.html"),
                    "Intel 11th-14th Gen processor graphics");
            }
            if (Regex.IsMatch(name, @"(?i)UHD\s*(Graphics\s*)?(600|605|610|615|617|620|630)|HD\s*(Graphics\s*)?(610|615|620|630)|Iris\s*Plus|6th|7th|8th|9th|10th"))
            {
                return new IntelFamily(
                    "776137",
                    new Uri("https://www.intel.com/content/www/us/en/download/776137/intel-7th-10th-gen-processor-graphics-windows.html"),
                    "Intel 6th-10th Gen legacy processor graphics");
            }

            int generation = GetIntelProcessorGeneration();
            if (generation is >= 11 and <= 14)
            {
                return new IntelFamily(
                    "864990",
                    new Uri("https://www.intel.com/content/www/us/en/download/864990/intel-11th-14th-gen-processor-graphics-windows.html"),
                    $"Intel processor graphics inferred from CPU generation {generation}");
            }
            if (generation is >= 6 and <= 10)
            {
                return new IntelFamily(
                    "776137",
                    new Uri("https://www.intel.com/content/www/us/en/download/776137/intel-7th-10th-gen-processor-graphics-windows.html"),
                    $"Intel legacy processor graphics inferred from CPU generation {generation}");
            }
            return null;
        }

        private static int GetIntelProcessorGeneration()
        {
            string cpu = string.Empty;
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    writable: false);
                cpu = ReadString(key, "ProcessorNameString");
            }
            catch
            {
            }
            if (cpu.Contains("Core Ultra", StringComparison.OrdinalIgnoreCase))
            {
                return 14;
            }
            Match generation = Regex.Match(cpu, @"(?i)\b(6|7|8|9|10|11|12|13|14)(?:st|nd|rd|th)\s+Gen\b");
            if (generation.Success && int.TryParse(generation.Groups[1].Value, out int parsed))
            {
                return parsed;
            }
            Match model = Regex.Match(cpu, @"(?i)\bi[3579]-([0-9]{4,5})[A-Z0-9]*\b");
            if (!model.Success)
            {
                return 0;
            }
            string digits = model.Groups[1].Value;
            if (digits.StartsWith("10", StringComparison.Ordinal)) return 10;
            if (digits.StartsWith("11", StringComparison.Ordinal)) return 11;
            if (digits.StartsWith("12", StringComparison.Ordinal)) return 12;
            if (digits.StartsWith("13", StringComparison.Ordinal)) return 13;
            if (digits.StartsWith("14", StringComparison.Ordinal)) return 14;
            return int.TryParse(digits[..1], out int first) ? first : 0;
        }

        private static string GetVendor(string hardwareId)
        {
            if (hardwareId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
            if (hardwareId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) return "Intel";
            if (hardwareId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase)) return "AMD";
            return string.Empty;
        }

        private static string GetOfficialUrl(string vendor) => vendor switch
        {
            "NVIDIA" => "https://www.nvidia.com/en-us/drivers/",
            "Intel" => "https://www.intel.com/content/www/us/en/download-center/home.html",
            "AMD" => "https://www.amd.com/en/support/download/drivers.html",
            _ => "https://support.microsoft.com/windows"
        };

        private static string GetPciDeviceId(string value)
        {
            Match match = DeviceIdPattern.Match(value);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
        }

        private static bool IsVendorDriverInstalled(
            string vendor,
            string windowsName,
            string provider,
            string version)
        {
            if (windowsName.Contains("Microsoft Basic Display Adapter", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(version))
            {
                return false;
            }
            return vendor switch
            {
                "NVIDIA" => provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                            windowsName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                            windowsName.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                            windowsName.Contains("Quadro", StringComparison.OrdinalIgnoreCase),
                "AMD" => provider.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) ||
                         provider.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                         provider.Contains("ATI", StringComparison.OrdinalIgnoreCase) ||
                         windowsName.Contains("Radeon", StringComparison.OrdinalIgnoreCase),
                "Intel" => provider.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                           windowsName.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                           windowsName.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
                           windowsName.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
                           windowsName.Contains("UHD", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        private static string ResolveMissingDriverDisplayName(
            string vendor,
            string pciDeviceId,
            string rawName,
            string busName)
        {
            if (!string.IsNullOrWhiteSpace(busName) &&
                !busName.Contains("Microsoft Basic Display Adapter", StringComparison.OrdinalIgnoreCase))
            {
                return busName;
            }
            if (vendor == "NVIDIA" && NvidiaPciNames.TryGetValue(pciDeviceId, out string? nvidia))
            {
                return nvidia;
            }
            if (vendor == "Intel" && IntelPciNames.TryGetValue(pciDeviceId, out string? intel))
            {
                return intel;
            }
            return vendor switch
            {
                "NVIDIA" => string.IsNullOrWhiteSpace(pciDeviceId)
                    ? "NVIDIA Display Adapter"
                    : $"NVIDIA Display Adapter (PCI DEV_{pciDeviceId})",
                "AMD" => string.IsNullOrWhiteSpace(pciDeviceId)
                    ? "AMD Radeon Graphics"
                    : $"AMD Radeon Graphics (PCI DEV_{pciDeviceId})",
                "Intel" => string.IsNullOrWhiteSpace(pciDeviceId)
                    ? "Intel Graphics"
                    : $"Intel Graphics (PCI DEV_{pciDeviceId})",
                _ => rawName
            };
        }

        private static string ReadBusReportedName(RegistryKey device, uint deviceInstance)
        {
            try
            {
                using RegistryKey? property = device.OpenSubKey(
                    @"Properties\{540b947e-8b40-45bc-a8a2-6a0b894cbda2}\0004",
                    writable: false);
                object? value = property?.GetValue(string.Empty);
                if (value is string text && !string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
                if (value is byte[] bytes)
                {
                    string decoded = Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
                    if (!string.IsNullOrWhiteSpace(decoded))
                    {
                        return decoded;
                    }
                }
            }
            catch
            {
            }

            DevPropKey key = new()
            {
                FormatId = new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"),
                PropertyId = 4
            };
            uint propertyType;
            uint size = 0;
            uint first = CM_Get_DevNode_PropertyW(
                deviceInstance,
                ref key,
                out propertyType,
                null,
                ref size,
                0);
            if (first != CrBufferSmall || size < 2 || size > 4096)
            {
                return string.Empty;
            }
            byte[] buffer = new byte[size];
            uint second = CM_Get_DevNode_PropertyW(
                deviceInstance,
                ref key,
                out propertyType,
                buffer,
                ref size,
                0);
            return second == CrSuccess
                ? Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0').Trim()
                : string.Empty;
        }

        private static bool TryReadDeviceStatus(
            string deviceId,
            out uint deviceInstance,
            out uint status,
            out uint problemCode)
        {
            status = 0;
            problemCode = 0;
            if (CM_Locate_DevNodeW(out deviceInstance, deviceId, 0) != CrSuccess)
            {
                return false;
            }
            return CM_Get_DevNode_Status(
                out status,
                out problemCode,
                deviceInstance,
                0) == CrSuccess;
        }

        private static bool IsPortableSystem()
        {
            return GetSystemPowerStatus(out SystemPowerStatus status) &&
                   status.BatteryFlag != 128;
        }

        private static string ReadDisplayName(RegistryKey device, string fallback)
        {
            string value = Convert.ToString(
                device.GetValue("FriendlyName") ?? device.GetValue("DeviceDesc"),
                CultureInfo.InvariantCulture) ?? fallback;
            int separator = value.LastIndexOf(';');
            if (separator >= 0 && separator + 1 < value.Length)
            {
                value = value[(separator + 1)..];
            }
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string ReadString(RegistryKey? key, string name)
        {
            return Convert.ToString(key?.GetValue(name), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static RegistryKey? OpenLocalMachineKey(string path)
        {
            RegistryView view = Environment.Is64BitOperatingSystem
                ? RegistryView.Registry64
                : RegistryView.Default;
            RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            RegistryKey? key = baseKey.OpenSubKey(path, writable: false);
            baseKey.Dispose();
            return key;
        }

        private static string ValueOrMissing(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<not detected>" : value;
        }

        private static void ReportStage(
            IProgress<MaintenanceProgressUpdate>? progress,
            int index,
            int count,
            string name,
            string detail)
        {
            progress?.Report(new MaintenanceProgressUpdate(index, count, name, detail));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DevPropKey
        {
            public Guid FormatId;
            public uint PropertyId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte AcLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint StructSize;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint StructSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Locate_DevNodeW(
            out uint deviceInstance,
            string deviceId,
            uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_DevNode_Status(
            out uint status,
            out uint problemNumber,
            uint deviceInstance,
            uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_DevNode_PropertyW(
            uint deviceInstance,
            ref DevPropKey propertyKey,
            out uint propertyType,
            [Out] byte[]? buffer,
            ref uint bufferSize,
            uint flags);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WinVerifyTrust(
            IntPtr windowHandle,
            ref Guid actionId,
            ref WinTrustData trustData);
    }
}
