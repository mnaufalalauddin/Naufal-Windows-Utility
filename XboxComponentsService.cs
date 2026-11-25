using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal sealed class XboxComponentsService : IToolToggleService
    {
        private const string BackupPath =
            @"Software\Naufal Windows Tech\Powertoys\Backups\XboxComponents";
        private const string GameDvrConfigPath = @"System\GameConfigStore";
        private const string GameDvrCapturePath = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
        private const string GameDvrPolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";
        private static readonly string PreviousPackageSnapshotPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WindowsPowerToys",
            "XboxComponents_Original.json");

        private static readonly string[] XboxServices =
        {
            "GamingServices",
            "GamingServicesNet",
            "XblGameSave",
            "XblAuthManager",
            "XboxGipSvc",
            "XboxNetApiSvc"
        };

        private static readonly string[] XboxProcesses =
        {
            "XboxPcApp",
            "GameBar",
            "GameBarFTServer",
            "XboxGameBarWidgets",
            "XboxGameBar"
        };

        // This is deliberately an explicit allow-list. Microsoft.GamingServices is
        // not a package-removal target; only its services are disabled and restorable.
        private static readonly IReadOnlyDictionary<string, XboxTarget> Targets =
            new[]
            {
                new XboxTarget("Microsoft.GamingApp", "Xbox App", "9MV0B5HZVK9Z"),
                new XboxTarget("Microsoft.XboxGamingOverlay", "Xbox Game Bar", "9NZKPSTSNW4P"),
                new XboxTarget("Microsoft.XboxGameOverlay", "Xbox Game Overlay", null),
                new XboxTarget("Microsoft.Xbox.TCUI", "Xbox TCUI", null),
                new XboxTarget("Microsoft.XboxIdentityProvider", "Xbox Identity Provider", "9WZDNCRD1HKW"),
                new XboxTarget("Microsoft.XboxSpeechToTextOverlay", "Xbox Speech-to-Text Overlay", null),
                new XboxTarget("Microsoft.XboxApp", "Xbox Console Companion", null)
            }.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);

        private static readonly ToolToggleDefinition Definition = new(
            "XboxComponents",
            "Components / HIGH RISK",
            "Xbox Components",
            "Removes only the approved Xbox package set, disables Game DVR and Xbox or Gaming services, and preserves an exact first-change restore snapshot. The Microsoft.GamingServices package is never removed.",
            true,
            true);

        private readonly NativeCommandRunner _commandRunner = new();

        public string DescribeRecovery(ToolToggleDefinition definition) =>
            CatalogRecovery.Snapshot(BackupPath, "No current snapshot found. Restore uses the supported Xbox package, Game DVR and service defaults.");

        public IReadOnlyList<ToolToggleDefinition> GetDefinitions() => new[] { Definition };

        public async Task<ToolToggleState> ReadStateAsync(ToolToggleDefinition definition)
        {
            try
            {
                IReadOnlyList<PackageEntry> packages = FindTargetPackages();
                int installedServices = 0;
                int disabledServices = 0;
                List<string> serviceState = new();
                foreach (string serviceName in XboxServices)
                {
                    int? start = ReadDword(
                        RegistryHive.LocalMachine,
                        ServicePath(serviceName),
                        "Start");
                    if (!start.HasValue)
                    {
                        continue;
                    }

                    installedServices++;
                    if (start == 4)
                    {
                        disabledServices++;
                    }
                    serviceState.Add($"{serviceName}={start}");
                }

                bool gameDvrDisabled = IsGameDvrDisabled();
                bool applied = packages.Count == 0 &&
                               disabledServices == installedServices &&
                               gameDvrDisabled;
                string packageText = packages.Count == 0
                    ? "Xbox AppX removed"
                    : $"Xbox AppX installed={packages.Count}";
                string servicesText = installedServices == 0
                    ? "services=N/A"
                    : $"services disabled={disabledServices}/{installedServices}";

                return new ToolToggleState(
                    applied,
                    true,
                    $"{packageText}; {servicesText}; Game DVR={(gameDvrDisabled ? "disabled" : "enabled/default")}" +
                    (serviceState.Count == 0 ? string.Empty : $" [{string.Join(", ", serviceState)}]"));
            }
            catch (Exception exception)
            {
                return new ToolToggleState(false, false, "Unable to read", exception.Message);
            }
        }

        public async Task<ToolToggleOperationResult> SetStateAsync(
            ToolToggleDefinition definition,
            bool targetOn)
        {
            if (!WindowsPrivilegeService.IsAdministrator())
            {
                ToolToggleState denied = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(
                    false,
                    false,
                    "Administrator rights are required.",
                    denied);
            }

            try
            {
                string supplemental = targetOn
                    ? await ApplyAsync()
                    : await RestoreAsync();
                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable && (!targetOn || after.IsOn);
                if (!targetOn && verified)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        BackupPath,
                        throwOnMissingSubKey: false);
                }
                string message = verified
                    ? targetOn
                        ? "Xbox components, Game DVR, and installed Xbox services are disabled. Microsoft.GamingServices package was preserved."
                        : "The exact Game DVR/service snapshot was restored and expected Xbox packages are present."
                    : $"Verification did not match the requested state. Actual: {after.ActualValue}";
                if (!string.IsNullOrWhiteSpace(supplemental))
                {
                    message += Environment.NewLine + supplemental;
                }
                return new ToolToggleOperationResult(verified, verified, message, after);
            }
            catch (Exception exception)
            {
                ToolToggleState state = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(false, false, exception.Message, state);
            }
        }

        public Task<ToolToggleOperationResult> RestoreOriginalAsync(
            ToolToggleDefinition definition) =>
            RestoreUsingReferenceFallbackAsync(definition, windowsDefault: false);

        public Task<ToolToggleOperationResult> RestoreWindowsDefaultAsync(
            ToolToggleDefinition definition) =>
            RestoreUsingReferenceFallbackAsync(definition, windowsDefault: true);

        private async Task<ToolToggleOperationResult> RestoreUsingReferenceFallbackAsync(
            ToolToggleDefinition definition,
            bool windowsDefault)
        {
            if (!WindowsPrivilegeService.IsAdministrator())
            {
                ToolToggleState denied = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(
                    false,
                    false,
                    "Administrator rights are required.",
                    denied);
            }

            try
            {
                string diagnostics = await RestoreAsync(forceWindowsDefault: windowsDefault);
                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable;
                if (verified)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        BackupPath,
                        throwOnMissingSubKey: false);
                }
                string message = verified
                    ? windowsDefault
                        ? "Xbox packages, Game DVR, and installed Xbox services were restored to Windows defaults."
                        : "Xbox packages, Game DVR, and installed Xbox services were restored from their snapshots or documented fallback defaults."
                    : $"Restore completed, but read-back still reports an optimized state. Actual: {after.ActualValue}";
                if (!string.IsNullOrWhiteSpace(diagnostics))
                {
                    message += Environment.NewLine + diagnostics;
                }

                return new ToolToggleOperationResult(verified, verified, message, after);
            }
            catch (Exception exception)
            {
                ToolToggleState state = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(false, false, exception.Message, state);
            }
        }

        private async Task<string> ApplyAsync()
        {
            IReadOnlyList<PackageEntry> packages = FindTargetPackages();
            CapturePackageSnapshot(packages);
            CaptureGameDvrSnapshot();
            await CaptureAndDisableServicesAsync();
            DisableGameDvr();
            StopXboxProcesses();

            List<string> errors = new();
            PackageManager manager = new();
            foreach (PackageEntry package in packages)
            {
                if (!Targets.ContainsKey(package.Name) ||
                    package.Name.Equals("Microsoft.GamingServices", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    DeploymentResult result = await DeploymentOperationTimeout.AwaitAsync(
                        () => manager.RemovePackageAsync(
                            package.FullName,
                            RemovalOptions.RemoveForAllUsers),
                        $"Removing {package.Label}");
                    if (HasDeploymentError(result))
                    {
                        errors.Add($"{package.Label}: {DeploymentError(result)}");
                    }
                }
                catch (Exception exception)
                {
                    errors.Add($"{package.Label}: {exception.Message}");
                }
            }

            IReadOnlyList<PackageEntry> remaining = FindTargetPackages();
            foreach (PackageEntry package in remaining)
            {
                errors.Add($"Still installed: {package.Label} [{package.Name}]");
            }
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Distinct()));
            }

            return packages.Count == 0
                ? "No allow-listed Xbox AppX package was installed; service and Game DVR state were still processed."
                : $"Removed {packages.Count} allow-listed Xbox package registration(s).";
        }

        private async Task<string> RestoreAsync(bool forceWindowsDefault = false)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(BackupPath, writable: false);
            bool hasSnapshot = backup?.GetValue("Snapshot.Captured") is not null;
            if (backup is not null && !forceWindowsDefault)
            {
                if (backup.GetValue("Snapshot.Captured") is not int committed || committed != 1)
                    throw new InvalidOperationException("The Xbox snapshot is incomplete. Backup retained; defaults were not substituted.");
                _ = ReadPackageSnapshot();
                RegistryRestorePlan.RequireTags(key => backup.GetValue(key),
                    new[] { "GameDVR.Enabled", "GameDVR.Capture", "GameDVR.Historical", "GameDVR.Policy" }, Deserialize);
            }
            IReadOnlyList<PackageEntry> previousPackages =
                !hasSnapshot && !forceWindowsDefault
                    ? ReadPreviousPackageSnapshot()
                    : Array.Empty<PackageEntry>();

            await RestoreServicesAsync(useSnapshot: hasSnapshot && !forceWindowsDefault);
            if (hasSnapshot && !forceWindowsDefault)
            {
                RestoreGameDvrSnapshot();
            }
            else
            {
                RestoreGameDvrWindowsDefault();
            }

            IReadOnlyList<PackageEntry> expected = hasSnapshot && !forceWindowsDefault
                ? ReadPackageSnapshot()
                : previousPackages.Count > 0
                    ? previousPackages
                    : GetPublicStoreFallbackPackages();
            List<string> diagnostics = new();
            if (previousPackages.Count > 0)
            {
                diagnostics.Add(
                    $"Loaded {previousPackages.Count} package registration(s) from the existing original-state snapshot.");
            }
            foreach (PackageEntry package in expected)
            {
                if (IsPackageInstalled(package.Name))
                {
                    continue;
                }

                bool restored = await TryRegisterLocalPackageAsync(package, diagnostics);
                if (!restored && !string.IsNullOrWhiteSpace(package.StoreId))
                {
                    restored = await TryInstallStorePackageAsync(package, diagnostics);
                }
            }

            string[] missing = expected
                .Where(package => !IsPackageInstalled(package.Name))
                .Select(package => package.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (missing.Length > 0)
            {
                OpenStoreRecoveryPages(expected, missing, diagnostics);
                throw new InvalidOperationException(
                    "The exact service/Game DVR state was restored, but these Xbox packages still need Microsoft Store recovery: " +
                    string.Join(", ", missing) +
                    (diagnostics.Count == 0
                        ? string.Empty
                        : Environment.NewLine + string.Join(Environment.NewLine, diagnostics)));
            }

            return diagnostics.Count == 0
                ? string.Empty
                : string.Join(Environment.NewLine, diagnostics);
        }

        private async Task CaptureAndDisableServicesAsync()
        {
            foreach (string serviceName in XboxServices)
            {
                string path = ServicePath(serviceName);
                if (ReadDword(RegistryHive.LocalMachine, path, "Start") is null)
                {
                    continue;
                }

                CaptureValue($"Service.{serviceName}.Start", RegistryHive.LocalMachine, path, "Start");
                CaptureValue($"Service.{serviceName}.Delayed", RegistryHive.LocalMachine, path, "DelayedAutoStart");
                CaptureBoolean($"Service.{serviceName}.Running", await IsServiceRunningAsync(serviceName));
                await _commandRunner.RunAsync(
                    "sc.exe",
                    new[] { "stop", serviceName },
                    TimeSpan.FromSeconds(20));
                await RequireCommandSuccessAsync(
                    "sc.exe",
                    new[] { "config", serviceName, "start=", "disabled" });
            }
        }

        private async Task RestoreServicesAsync(bool useSnapshot)
        {
            using RegistryKey? backup = useSnapshot ? Registry.CurrentUser.OpenSubKey(BackupPath, writable: false) : null;
            var plans = new List<(string Name, int Start, int? Delayed, bool? Running)>();
            foreach (string service in XboxServices)
            {
                string path = ServicePath(service), tag = $"Service.{service}.Start";
                int? current = ReadDword(RegistryHive.LocalMachine, path, "Start");
                if (current is null) continue; // Do not create registry keys for absent services.
                int? start = useSnapshot ? ReadBackupDword(tag) : GetDocumentedServiceDefaultStart(service);
                if (useSnapshot)
                    RegistryRestorePlan.RequireTags(key => backup?.GetValue(key),
                        new[] { tag, $"Service.{service}.Delayed" }, Deserialize);
                if (start is null)
                {
                    if (useSnapshot || current == 4)
                        throw new InvalidOperationException($"No exact snapshot or verified vendor default exists for {service}. Backup retained.");
                    continue;
                }
                int? delayed = useSnapshot ? ReadBackupDword($"Service.{service}.Delayed") :
                    ReadDword(RegistryHive.LocalMachine, path, "DelayedAutoStart");
                _ = ServiceRestoreSnapshot.ToScStartMode(start.Value, delayed);
                bool? running = useSnapshot ? ServiceRestoreSnapshot.ReadRunningState(backup?.GetValue($"Service.{service}.Running"), service) :
                    start == 2 ? true : start == 4 ? false : null;
                if (running == true && start == 4) throw new InvalidOperationException("The saved service state is inconsistent. Backup retained.");
                plans.Add((service, start.Value, delayed, running));
            }
            foreach (var plan in plans)
            {
                string path = ServicePath(plan.Name);
                int? current = ReadDword(RegistryHive.LocalMachine, path, "Start");
                int? delayed = ReadDword(RegistryHive.LocalMachine, path, "DelayedAutoStart");
                if (current != plan.Start || (plan.Start == 2 && (plan.Delayed == 1) != (delayed == 1)))
                    await RequireCommandSuccessAsync("sc.exe", new[] { "config", plan.Name, "start=", ServiceRestoreSnapshot.ToScStartMode(plan.Start, plan.Delayed) });
                if (useSnapshot)
                {
                    RestoreValue($"Service.{plan.Name}.Start", RegistryHive.LocalMachine, path, "Start");
                    RestoreValue($"Service.{plan.Name}.Delayed", RegistryHive.LocalMachine, path, "DelayedAutoStart");
                }
                if (ReadDword(RegistryHive.LocalMachine, path, "Start") != plan.Start)
                    throw new InvalidOperationException($"{plan.Name} startup configuration did not verify. Backup retained.");
                if (plan.Running.HasValue)
                    await ServiceRestoreRuntime.EnsureAsync(plan.Name, plan.Running.Value, _commandRunner);
            }
        }

        private void CaptureGameDvrSnapshot()
        {
            CaptureValue("GameDVR.Enabled", RegistryHive.CurrentUser, GameDvrConfigPath, "GameDVR_Enabled");
            CaptureValue("GameDVR.Capture", RegistryHive.CurrentUser, GameDvrCapturePath, "AppCaptureEnabled");
            CaptureValue("GameDVR.Historical", RegistryHive.CurrentUser, GameDvrCapturePath, "HistoricalCaptureEnabled");
            CaptureValue("GameDVR.Policy", RegistryHive.LocalMachine, GameDvrPolicyPath, "AllowGameDVR");
        }

        private static void DisableGameDvr()
        {
            SetDword(RegistryHive.CurrentUser, GameDvrConfigPath, "GameDVR_Enabled", 0);
            SetDword(RegistryHive.CurrentUser, GameDvrCapturePath, "AppCaptureEnabled", 0);
            SetDword(RegistryHive.CurrentUser, GameDvrCapturePath, "HistoricalCaptureEnabled", 0);
            SetDword(RegistryHive.LocalMachine, GameDvrPolicyPath, "AllowGameDVR", 0);
        }

        private static bool IsGameDvrDisabled() =>
            ReadDword(RegistryHive.CurrentUser, GameDvrConfigPath, "GameDVR_Enabled") == 0 &&
            ReadDword(RegistryHive.CurrentUser, GameDvrCapturePath, "AppCaptureEnabled") == 0 &&
            ReadDword(RegistryHive.CurrentUser, GameDvrCapturePath, "HistoricalCaptureEnabled") == 0 &&
            ReadDword(RegistryHive.LocalMachine, GameDvrPolicyPath, "AllowGameDVR") == 0;

        private static void RestoreGameDvrSnapshot()
        {
            RestoreValue("GameDVR.Enabled", RegistryHive.CurrentUser, GameDvrConfigPath, "GameDVR_Enabled");
            RestoreValue("GameDVR.Capture", RegistryHive.CurrentUser, GameDvrCapturePath, "AppCaptureEnabled");
            RestoreValue("GameDVR.Historical", RegistryHive.CurrentUser, GameDvrCapturePath, "HistoricalCaptureEnabled");
            RestoreValue("GameDVR.Policy", RegistryHive.LocalMachine, GameDvrPolicyPath, "AllowGameDVR");
        }

        private static void RestoreGameDvrWindowsDefault() => RegistryRestorePlan.Execute(new[]
        {
            new RestoreRegistryValue(new(RegistryHive.CurrentUser, GameDvrConfigPath, "GameDVR_Enabled"), null, null),
            new RestoreRegistryValue(new(RegistryHive.CurrentUser, GameDvrCapturePath, "AppCaptureEnabled"), null, null),
            new RestoreRegistryValue(new(RegistryHive.CurrentUser, GameDvrCapturePath, "HistoricalCaptureEnabled"), null, null),
            new RestoreRegistryValue(new(RegistryHive.LocalMachine, GameDvrPolicyPath, "AllowGameDVR"), null, null)
        });

        private static IReadOnlyList<PackageEntry> GetPublicStoreFallbackPackages() =>
            Targets.Values
                .Where(target => !string.IsNullOrWhiteSpace(target.StoreId))
                .Select(target => new PackageEntry(
                    target.Name,
                    target.Label,
                    string.Empty,
                    string.Empty,
                    target.StoreId))
                .ToArray();

        private static int? GetDocumentedServiceDefaultStart(string serviceName) =>
            serviceName switch
            {
                "XblGameSave" => 3,
                "XblAuthManager" => 3,
                "XboxGipSvc" => 3,
                "XboxNetApiSvc" => 3,
                _ => null
            };

        private static IReadOnlyList<PackageEntry> FindTargetPackages()
        {
            PackageManager manager = new();
            IReadOnlyList<Package> packages;
            try
            {
                packages = manager.FindPackages().ToList();
            }
            catch
            {
                packages = manager.FindPackagesForUser(string.Empty).ToList();
            }

            return packages
                .Where(package => Targets.ContainsKey(package.Id.Name))
                .GroupBy(package => package.Id.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(group => ToEntry(group.First()))
                .ToList();
        }

        private static PackageEntry ToEntry(Package package)
        {
            XboxTarget target = Targets[package.Id.Name];
            string installLocation = string.Empty;
            try
            {
                installLocation = package.InstalledLocation?.Path ?? string.Empty;
            }
            catch
            {
                // A protected or staged package may not expose its install path.
            }
            return new PackageEntry(
                package.Id.Name,
                target.Label,
                package.Id.FullName,
                installLocation,
                target.StoreId);
        }

        private static bool IsPackageInstalled(string name)
        {
            try
            {
                PackageManager manager = new();
                return manager.FindPackagesForUser(string.Empty, name, string.Empty).Any();
            }
            catch
            {
                return FindTargetPackages().Any(item =>
                    item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static void CapturePackageSnapshot(IReadOnlyList<PackageEntry> packages)
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey(BackupPath, writable: true);
            if (backup.GetValue("Snapshot.Captured") is not null)
            {
                return;
            }

            backup.SetValue("Snapshot.Utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
            backup.SetValue("Packages.Names", packages.Select(item => item.Name).ToArray(), RegistryValueKind.MultiString);
            backup.SetValue("Packages.Labels", packages.Select(item => item.Label).ToArray(), RegistryValueKind.MultiString);
            backup.SetValue("Packages.FullNames", packages.Select(item => item.FullName).ToArray(), RegistryValueKind.MultiString);
            backup.SetValue("Packages.Locations", packages.Select(item => item.InstallLocation).ToArray(), RegistryValueKind.MultiString);
            backup.SetValue("Packages.StoreIds", packages.Select(item => item.StoreId ?? string.Empty).ToArray(), RegistryValueKind.MultiString);
            backup.Flush();
            backup.SetValue("Snapshot.Captured", 1, RegistryValueKind.DWord);
            backup.Flush();
        }

        private static IReadOnlyList<PackageEntry> ReadPackageSnapshot()
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(BackupPath, writable: false);
            if (backup?.GetValue("Snapshot.Captured") is not int captured || captured != 1 ||
                backup.GetValue("Packages.Names") is not string[] savedNames ||
                new[] { "Packages.Labels", "Packages.FullNames", "Packages.Locations", "Packages.StoreIds" }
                    .Any(key => backup.GetValue(key) is not string[] items || items.Length != savedNames.Length))
                throw new InvalidOperationException("The Xbox package snapshot is incomplete. Backup retained.");
            string[] names = backup?.GetValue("Packages.Names") as string[] ?? Array.Empty<string>();
            string[] labels = backup?.GetValue("Packages.Labels") as string[] ?? Array.Empty<string>();
            string[] fullNames = backup?.GetValue("Packages.FullNames") as string[] ?? Array.Empty<string>();
            string[] locations = backup?.GetValue("Packages.Locations") as string[] ?? Array.Empty<string>();
            string[] storeIds = backup?.GetValue("Packages.StoreIds") as string[] ?? Array.Empty<string>();
            List<PackageEntry> result = new();
            for (int index = 0; index < names.Length; index++)
            {
                if (!Targets.TryGetValue(names[index], out XboxTarget? target))
                {
                    throw new InvalidOperationException("The Xbox snapshot contains an unexpected package. Backup retained.");
                }
                result.Add(new PackageEntry(
                    names[index],
                    ValueAt(labels, index, target.Label),
                    ValueAt(fullNames, index, string.Empty),
                    ValueAt(locations, index, string.Empty),
                    ValueAt(storeIds, index, target.StoreId ?? string.Empty)));
            }
            return result;
        }

        private static IReadOnlyList<PackageEntry> ReadPreviousPackageSnapshot()
        {
            try
            {
                if (!File.Exists(PreviousPackageSnapshotPath))
                {
                    return Array.Empty<PackageEntry>();
                }

                using JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(PreviousPackageSnapshotPath));
                if (!document.RootElement.TryGetProperty("Packages", out JsonElement packages) ||
                    packages.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<PackageEntry>();
                }

                List<PackageEntry> result = new();
                HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonElement package in packages.EnumerateArray())
                {
                    string name = ReadJsonString(package, "Name");
                    if (string.IsNullOrWhiteSpace(name) ||
                        name.Equals("Microsoft.GamingServices", StringComparison.OrdinalIgnoreCase) ||
                        !Targets.TryGetValue(name, out XboxTarget? target))
                    {
                        continue;
                    }

                    string fullName = ReadJsonString(package, "PackageFullName");
                    string identity = name + "|" + fullName;
                    if (!seen.Add(identity))
                    {
                        continue;
                    }

                    string storeId = ReadJsonString(package, "StoreId");
                    result.Add(new PackageEntry(
                        name,
                        ValueOrFallback(ReadJsonString(package, "Label"), target.Label),
                        fullName,
                        ReadJsonString(package, "InstallLocation"),
                        ValueOrFallback(storeId, target.StoreId ?? string.Empty)));
                }

                return result;
            }
            catch (IOException)
            {
                return Array.Empty<PackageEntry>();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<PackageEntry>();
            }
            catch (JsonException)
            {
                return Array.Empty<PackageEntry>();
            }
        }

        private static string ReadJsonString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out JsonElement property) &&
                   property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }

        private static string ValueOrFallback(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value;

        private static async Task<bool> TryRegisterLocalPackageAsync(
            PackageEntry package,
            List<string> diagnostics)
        {
            if (string.IsNullOrWhiteSpace(package.InstallLocation))
            {
                return false;
            }
            string manifestPath = Path.Combine(package.InstallLocation, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            try
            {
                PackageManager manager = new();
                DeploymentResult result = await DeploymentOperationTimeout.AwaitAsync(
                    () => manager.RegisterPackageAsync(
                        new Uri(manifestPath),
                        null,
                        DeploymentOptions.None),
                    $"Registering {package.Label}");
                if (HasDeploymentError(result))
                {
                    diagnostics.Add($"Local re-register {package.Label}: {DeploymentError(result)}");
                    return false;
                }
                return IsPackageInstalled(package.Name);
            }
            catch (Exception exception)
            {
                diagnostics.Add($"Local re-register {package.Label}: {exception.Message}");
                return false;
            }
        }

        private async Task<bool> TryInstallStorePackageAsync(
            PackageEntry package,
            List<string> diagnostics)
        {
            NativeCommandResult result = await _commandRunner.RunAsync(
                "winget.exe",
                new[]
                {
                    "install",
                    "--id", package.StoreId!,
                    "--source", "msstore",
                    "--exact",
                    "--silent",
                    "--accept-source-agreements",
                    "--accept-package-agreements",
                    "--disable-interactivity"
                },
                TimeSpan.FromMinutes(3));
            if (result.ExitCode != 0)
            {
                diagnostics.Add($"Microsoft Store install {package.Label}: {result.CombinedOutput}");
                return false;
            }
            return IsPackageInstalled(package.Name);
        }

        private static void OpenStoreRecoveryPages(
            IReadOnlyList<PackageEntry> expected,
            IReadOnlyCollection<string> missing,
            List<string> diagnostics)
        {
            bool opened = false;
            foreach (PackageEntry package in expected.Where(item => missing.Contains(item.Name)))
            {
                if (string.IsNullOrWhiteSpace(package.StoreId))
                {
                    continue;
                }
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = $"ms-windows-store://pdp/?ProductId={package.StoreId}",
                        UseShellExecute = true
                    });
                    opened = true;
                }
                catch (Exception exception)
                {
                    diagnostics.Add($"Microsoft Store {package.Label}: {exception.Message}");
                }
            }
            if (!opened)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "ms-windows-store://search/?query=Xbox",
                        UseShellExecute = true
                    });
                }
                catch (Exception exception)
                {
                    diagnostics.Add($"Microsoft Store search: {exception.Message}");
                }
            }
        }

        private static void StopXboxProcesses()
        {
            foreach (string processName in XboxProcesses)
            {
                foreach (Process process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Package removal will report an authoritative error if still locked.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
        }

        private async Task<bool> IsServiceRunningAsync(string serviceName)
        {
            NativeCommandResult result = await _commandRunner.RunAsync(
                "sc.exe",
                new[] { "query", serviceName },
                TimeSpan.FromSeconds(10));
            return result.ExitCode == 0 &&
                   result.StandardOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }

        private async Task RequireCommandSuccessAsync(
            string executable,
            IReadOnlyList<string> arguments)
        {
            NativeCommandResult result = await _commandRunner.RunAsync(
                executable,
                arguments,
                TimeSpan.FromSeconds(25));
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.CombinedOutput)
                        ? $"{executable} exited with code {result.ExitCode}."
                        : result.CombinedOutput);
            }
        }

        private static void CaptureValue(
            string tag,
            RegistryHive hive,
            string path,
            string name)
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey(BackupPath, writable: true);
            if (RegistrySnapshotCommit.IsCaptured(key => backup.GetValue(key), tag))
            {
                return;
            }

            using RegistryKey? source = OpenKey(hive, path, writable: false);
            RegistrySnapshotCommit.Capture(backup, tag, source, name, Serialize);
        }

        private static void RestoreValue(string tag, RegistryHive hive, string path, string name)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(BackupPath, writable: false);
            if (backup is null) throw new InvalidOperationException("The original snapshot is missing. Backup retained.");
            RegistryRestorePlan.RestoreTagged(key => backup.GetValue(key), tag, new(hive, path, name), Deserialize);
        }

        private static void CaptureBoolean(string name, bool value)
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey(BackupPath, writable: true);
            if (backup.GetValue(name) is null)
            {
                backup.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        private static bool ReadBackupBoolean(string name)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(BackupPath, writable: false);
            return Convert.ToInt32(backup?.GetValue(name, 0) ?? 0, CultureInfo.InvariantCulture) == 1;
        }

        private static int? ReadBackupDword(string tag)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(BackupPath, writable: false);
            if (Convert.ToInt32(
                    backup?.GetValue($"{tag}.Exists", 0) ?? 0,
                    CultureInfo.InvariantCulture) != 1)
            {
                return null;
            }
            string serialized = Convert.ToString(
                backup?.GetValue($"{tag}.Value"),
                CultureInfo.InvariantCulture) ?? string.Empty;
            return int.TryParse(
                serialized,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value)
                ? value
                : null;
        }

        private static int? ReadDword(RegistryHive hive, string path, string name)
        {
            using RegistryKey? key = OpenKey(hive, path, writable: false);
            object? value = key?.GetValue(name);
            return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static void SetDword(RegistryHive hive, string path, string name, int value)
        {
            using RegistryKey key = CreateKey(hive, path);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }

        private static RegistryKey? OpenKey(RegistryHive hive, string path, bool writable)
        {
            RegistryView view = Environment.Is64BitOperatingSystem
                ? RegistryView.Registry64
                : RegistryView.Default;
            RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            RegistryKey? key = baseKey.OpenSubKey(path, writable);
            baseKey.Dispose();
            return key;
        }

        private static RegistryKey CreateKey(RegistryHive hive, string path)
        {
            RegistryView view = Environment.Is64BitOperatingSystem
                ? RegistryView.Registry64
                : RegistryView.Default;
            RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            RegistryKey key = baseKey.CreateSubKey(path, writable: true);
            baseKey.Dispose();
            return key;
        }

        private static string Serialize(object value, RegistryValueKind kind) => kind switch
        {
            RegistryValueKind.Binary => Convert.ToBase64String((byte[])value),
            RegistryValueKind.MultiString => string.Join("\u001f", (string[])value),
            RegistryValueKind.DWord => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };

        private static object Deserialize(string value, RegistryValueKind kind) => kind switch
        {
            RegistryValueKind.Binary => Convert.FromBase64String(value),
            RegistryValueKind.MultiString => value.Split('\u001f'),
            RegistryValueKind.DWord => int.Parse(value, CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => long.Parse(value, CultureInfo.InvariantCulture),
            _ => value
        };

        private static bool HasDeploymentError(DeploymentResult result) =>
            result.ExtendedErrorCode is not null && result.ExtendedErrorCode.HResult < 0;

        private static string DeploymentError(DeploymentResult result) =>
            $"{result.ErrorText} (0x{result.ExtendedErrorCode.HResult:X8})";

        private static string ServicePath(string serviceName) =>
            $@"SYSTEM\CurrentControlSet\Services\{serviceName}";

        private static string ValueAt(string[] values, int index, string fallback) =>
            index >= 0 && index < values.Length ? values[index] : fallback;

        private sealed record XboxTarget(string Name, string Label, string? StoreId);

        private sealed record PackageEntry(
            string Name,
            string Label,
            string FullName,
            string InstallLocation,
            string? StoreId);
    }
}
