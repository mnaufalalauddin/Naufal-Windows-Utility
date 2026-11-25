using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys
{
    /// <summary>
    /// Native implementation of the registry-backed Performance Lab catalog.
    /// The legacy PowerShell source is used only as a static behavioral specification;
    /// no script is loaded or executed at runtime.
    /// </summary>
    internal sealed class PerformanceLabService : IToolToggleService, ICatalogEffectSource
    {
        private const string BackupRoot =
            @"Software\Naufal Windows Tech\Powertoys\Backups\PerformanceLab";
        private const string MigrationRoot =
            @"Software\Naufal Windows Tech\Powertoys\Migrations\PreviousPerformanceLab";
        private static string PreviousSnapshotPath => AppDataPaths.ResolveLegacyBackup("performance-lab-original.json");
        private const string DisplayClassPath =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static readonly IReadOnlyList<PerformanceLab> AllLabs = CreateCatalog();
        private readonly IReadOnlyDictionary<string, PerformanceLab> _catalog;
        private readonly object _displayPathLock = new();
        private readonly Dictionary<string, string> _displayPaths =
            new(StringComparer.OrdinalIgnoreCase);

        public PerformanceLabService(string module)
        {
            _catalog = AllLabs
                .Where(lab => string.Equals(lab.Module, module, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(lab => lab.Definition.Id, StringComparer.OrdinalIgnoreCase);
        }

        public string DescribeRecovery(ToolToggleDefinition definition) =>
            CatalogRecovery.Snapshot($@"{BackupRoot}\{definition.Id}", definition.Id is "UacSecureDesktopDimOff" or "NvTdr10" or "LongPathsOn"
                    ? "No current snapshot found. A documented Windows default exists for this setting."
                    : "No current snapshot found. This setting requires a valid original backup.");

        public IReadOnlyList<ToolToggleDefinition> GetDefinitions() =>
            _catalog.Values.Select(lab => lab.Definition.Id == "FastStartupEnable"
                ? lab.Definition with { IsFeatureSwitch = true, Name = "Fast Startup", Description = "ON enables Fast Startup; OFF disables it by setting HiberbootEnabled=0. Restore is separate and replays the saved setting. Fast Startup also requires hibernation; disabling it can increase cold-start time." }
                : LegacyMitigationPolicy.IsBlocked(lab.Definition.Id)
                    ? lab.Definition with { Description = LegacyMitigationPolicy.ApplyBlocked, Warning = "Restore-only legacy bundle. Registry values below describe its old configuration, not current effective protection. Resource savings are unmeasured." }
                    : lab.Definition).ToArray();

        public IReadOnlyList<CatalogEffect> GetEffects(ToolToggleDefinition definition) => Resolve(definition).Settings.Select(s =>
        {
            int separator = s.Path.IndexOf(':');
            return separator > 0
                ? CatalogEffect.Registry(s.Path[..separator], s.Path[(separator + 1)..], s.Name, s.Kind.ToString(), s.Value)
                : new CatalogEffect("DynamicRegistry/" + s.Path.ToUpperInvariant() + "/" + s.Name.ToUpperInvariant(), s.Kind + ":" + Convert.ToString(s.Value, CultureInfo.InvariantCulture));
        }).ToArray();

        public Task<ToolToggleState> ReadStateAsync(ToolToggleDefinition definition)
        {
            try
            {
                PerformanceLab lab = Resolve(definition);
                if (!TryResolveSettings(lab, out IReadOnlyList<ResolvedSetting> settings, out string unavailable))
                {
                    return Task.FromResult(ToolToggleState.Unavailable(unavailable));
                }

                bool applied = true;
                bool anyApplied = false;
                List<string> actual = new(settings.Count);
                foreach (ResolvedSetting setting in settings)
                {
                    using RegistryKey? key = OpenKey(setting.Hive, setting.Path, writable: false);
                    object? value = key?.GetValue(
                        setting.Name,
                        null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames);
                    bool matches = ValueMatches(value, setting.Kind, setting.Value);
                    applied &= matches;
                    anyApplied |= matches;
                    actual.Add($"{setting.Name}={FormatValue(value)}");
                }

                return Task.FromResult(new ToolToggleState(
                    applied,
                    true,
                    string.Join(", ", actual), HasAppliedParts: anyApplied));
            }
            catch (Exception exception)
            {
                return Task.FromResult(new ToolToggleState(
                    false,
                    false,
                    "Unable to read",
                    exception.Message));
            }
        }

        public Task<ToolToggleOperationResult> SetStateAsync(ToolToggleDefinition definition, bool targetOn) =>
            ChangeStateAsync(definition, targetOn, restoreOriginal: false);

        private async Task<ToolToggleOperationResult> ChangeStateAsync(
            ToolToggleDefinition definition,
            bool targetOn, bool restoreOriginal)
        {
            if (targetOn && LegacyMitigationPolicy.IsBlocked(definition.Id))
                return new ToolToggleOperationResult(false, false, LegacyMitigationPolicy.ApplyBlocked, await ReadStateAsync(definition));

            if (definition.RequiresAdministrator && !WindowsPrivilegeService.IsAdministrator())
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
                PerformanceLab lab = Resolve(definition);
                if (!TryResolveSettings(lab, out IReadOnlyList<ResolvedSetting> settings, out string unavailable))
                {
                    ToolToggleState missing = ToolToggleState.Unavailable(unavailable);
                    return new ToolToggleOperationResult(false, false, unavailable, missing, SkippedUnavailable: true);
                }

                bool explicitOff = definition.Id == "FastStartupEnable" && !targetOn && !restoreOriginal;
                if (explicitOff)
                {
                    Apply(lab, settings.Select(setting => setting with { Value = 0 }).ToArray());
                }
                else if (targetOn)
                {
                    Apply(lab, settings);
                }
                else
                {
                    TryImportPreviousSnapshot(lab, settings);
                    using RegistryKey? snapshot = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{definition.Id}", writable: false);
                    if (snapshot is null)
                    {
                        ToolToggleOperationResult fallback = await RestoreWindowsDefaultAsync(definition);
                        return fallback with { Message = "No original backup was found. " + fallback.Message, DefaultFallbackHandled = true };
                    }
                    Restore(lab, settings);
                }

                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable && (explicitOff
                    ? settings.All(setting =>
                    {
                        using RegistryKey? key = OpenKey(setting.Hive, setting.Path, writable: false);
                        return key?.GetValue(setting.Name) is int value && value == 0 &&
                            key.GetValueKind(setting.Name) == RegistryValueKind.DWord;
                    })
                    : !targetOn || after.IsOn);
                if (verified && !targetOn && !explicitOff) DeleteVerifiedBackup(definition.Id);
                string restart = verified && definition.RestartRecommended
                    ? " Restart Windows before evaluating the result."
                    : string.Empty;
                return new ToolToggleOperationResult(
                    verified,
                    verified,
                    verified
                        ? $"{definition.Name} is now {(explicitOff ? "OFF" : targetOn ? "APPLIED" : "RESTORED")}.{restart}"
                        : $"Verification did not match the requested state. Actual: {after.ActualValue}",
                    after);
            }
            catch (Exception exception)
            {
                ToolToggleState state = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(false, false, exception.Message, state);
            }
        }

        public Task<ToolToggleOperationResult> RestoreOriginalAsync(
            ToolToggleDefinition definition) =>
            ChangeStateAsync(definition, targetOn: false, restoreOriginal: true);

        public async Task<ToolToggleOperationResult> RestoreWindowsDefaultAsync(
            ToolToggleDefinition definition)
        {
            if (definition.RequiresAdministrator &&
                !WindowsPrivilegeService.IsAdministrator())
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
                PerformanceLab lab = Resolve(definition);
                if (!TryResolveSettings(
                        lab,
                        out IReadOnlyList<ResolvedSetting> settings,
                        out string unavailable))
                {
                    ToolToggleState missing = ToolToggleState.Unavailable(unavailable);
                    return new ToolToggleOperationResult(false, false, unavailable, missing, SkippedUnavailable: true);
                }

                var plan = DocumentedRestoreDefaults.PerformanceLab(definition.Id,
                    settings.Select(s => new RestoreRegistryTarget(s.Hive, s.Path, s.Name)).ToArray());
                RegistryRestorePlan.Execute(plan);

                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable;
                if (verified) DeleteVerifiedBackup(definition.Id);
                return new ToolToggleOperationResult(
                    verified,
                    verified,
                    verified
                        ? $"{definition.Name} was returned to the documented Microsoft default (not a saved original state)."
                        : $"Windows-default read-back failed. Actual: {after.ActualValue}",
                    after);
            }
            catch (Exception exception)
            {
                ToolToggleState state = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(false, false, exception.Message, state);
            }
        }

        private PerformanceLab Resolve(ToolToggleDefinition definition) =>
            _catalog.TryGetValue(definition.Id, out PerformanceLab? lab)
                ? lab
                : throw new KeyNotFoundException(definition.Id);

        private bool TryResolveSettings(
            PerformanceLab lab,
            out IReadOnlyList<ResolvedSetting> settings,
            out string unavailable)
        {
            if (!VendorPresent(lab.Vendor))
            {
                settings = Array.Empty<ResolvedSetting>();
                unavailable = $"{lab.Vendor} display hardware was not detected.";
                return false;
            }

            List<ResolvedSetting> resolved = new(lab.Settings.Count);
            foreach (RegistrySetting setting in lab.Settings)
            {
                if (!TryResolveSetting(setting, out ResolvedSetting target))
                {
                    settings = Array.Empty<ResolvedSetting>();
                    unavailable = $"The registry target for {setting.Name} is not available on this hardware.";
                    return false;
                }
                resolved.Add(target);
            }

            settings = resolved;
            unavailable = string.Empty;
            return true;
        }

        private bool TryResolveSetting(RegistrySetting setting, out ResolvedSetting resolved)
        {
            string raw = setting.Path;
            if (raw.StartsWith("@", StringComparison.Ordinal))
            {
                string vendor = raw switch
                {
                    "@NVIDIA_GPU@" => "NVIDIA",
                    "@AMD_GPU@" => "AMD",
                    "@INTEL_GPU@" => "Intel",
                    _ => throw new InvalidOperationException($"Unknown display target: {raw}")
                };
                string path = ResolveDisplayClassPath(vendor);
                if (string.IsNullOrWhiteSpace(path))
                {
                    resolved = default;
                    return false;
                }

                resolved = new ResolvedSetting(
                    RegistryHive.LocalMachine,
                    path,
                    setting.Name,
                    setting.Kind,
                    setting.Value);
                return true;
            }

            const string localMachine = @"HKLM:\";
            const string currentUser = @"HKCU:\";
            if (raw.StartsWith(localMachine, StringComparison.OrdinalIgnoreCase))
            {
                resolved = new ResolvedSetting(
                    RegistryHive.LocalMachine,
                    raw[localMachine.Length..],
                    setting.Name,
                    setting.Kind,
                    setting.Value);
                return true;
            }
            if (raw.StartsWith(currentUser, StringComparison.OrdinalIgnoreCase))
            {
                resolved = new ResolvedSetting(
                    RegistryHive.CurrentUser,
                    raw[currentUser.Length..],
                    setting.Name,
                    setting.Kind,
                    setting.Value);
                return true;
            }

            throw new InvalidOperationException($"Unknown registry target: {raw}");
        }

        private bool VendorPresent(string vendor)
        {
            if (string.IsNullOrWhiteSpace(vendor) ||
                vendor.Equals("Any", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return !string.IsNullOrWhiteSpace(ResolveDisplayClassPath(vendor));
        }

        private string ResolveDisplayClassPath(string vendor)
        {
            if (string.IsNullOrWhiteSpace(vendor))
            {
                return string.Empty;
            }

            lock (_displayPathLock)
            {
                if (_displayPaths.TryGetValue(vendor, out string? cached))
                {
                    return cached;
                }

                string result = string.Empty;
                using RegistryKey? root = OpenKey(
                    RegistryHive.LocalMachine,
                    DisplayClassPath,
                    writable: false);
                if (root is not null)
                {
                    foreach (string childName in root.GetSubKeyNames()
                                 .Where(name => Regex.IsMatch(name, @"^\d{4}$")))
                    {
                        using RegistryKey? child = root.OpenSubKey(childName, writable: false);
                        if (child is null)
                        {
                            continue;
                        }

                        string haystack =
                            $"{child.GetValue("DriverDesc")} " +
                            $"{child.GetValue("ProviderName")} " +
                            $"{child.GetValue("MatchingDeviceId")}";
                        if (VendorMatches(vendor, haystack))
                        {
                            result = $"{DisplayClassPath}\\{childName}";
                            break;
                        }
                    }
                }

                // A missing vendor may become available after a driver install.
                // Do not retain negative detection for the application's lifetime.
                if (!string.IsNullOrWhiteSpace(result)) _displayPaths[vendor] = result;
                return result;
            }
        }

        private static bool VendorMatches(string vendor, string text) =>
            vendor.ToUpperInvariant() switch
            {
                "NVIDIA" => Regex.IsMatch(text, "NVIDIA|GeForce|Quadro|RTX|GTX", RegexOptions.IgnoreCase),
                "AMD" => Regex.IsMatch(text, "AMD|Radeon", RegexOptions.IgnoreCase),
                "INTEL" => Regex.IsMatch(text, "Intel|Arc|Iris|UHD Graphics|HD Graphics", RegexOptions.IgnoreCase),
                _ => true
            };

        private static void Apply(PerformanceLab lab, IReadOnlyList<ResolvedSetting> settings)
        {
            CaptureSnapshot(lab.Definition.Id, lab.Definition.Name, settings);
            foreach (ResolvedSetting setting in settings)
            {
                using RegistryKey key = CreateKey(setting.Hive, setting.Path);
                key.SetValue(setting.Name, setting.Value, setting.Kind);
            }
        }

        private static void CaptureSnapshot(
            string id,
            string name,
            IReadOnlyList<ResolvedSetting> settings)
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey(
                $@"{BackupRoot}\{id}",
                writable: true);
            if (backup.GetValue("Snapshot.Captured") is not null)
            {
                ReadRestorePlan(backup, settings);
                return;
            }

            backup.SetValue("Snapshot.Name", name, RegistryValueKind.String);
            backup.SetValue("Snapshot.Count", settings.Count, RegistryValueKind.DWord);
            for (int index = 0; index < settings.Count; index++)
            {
                ResolvedSetting setting = settings[index];
                string prefix = $"Item.{index}.";
                using RegistryKey? source = OpenKey(setting.Hive, setting.Path, writable: false);
                object? value = source?.GetValue(
                    setting.Name,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                backup.SetValue(prefix + "Hive", setting.Hive.ToString(), RegistryValueKind.String);
                backup.SetValue(prefix + "Path", setting.Path, RegistryValueKind.String);
                backup.SetValue(prefix + "Name", setting.Name, RegistryValueKind.String);
                backup.SetValue(prefix + "Exists", value is null ? 0 : 1, RegistryValueKind.DWord);
                if (value is not null && source is not null)
                {
                    RegistryValueKind kind = source.GetValueKind(setting.Name);
                    backup.SetValue(prefix + "Kind", kind.ToString(), RegistryValueKind.String);
                    backup.SetValue(prefix + "Value", Serialize(value, kind), RegistryValueKind.String);
                }
            }
            backup.Flush();
            backup.SetValue("Snapshot.Captured", 1, RegistryValueKind.DWord);
            backup.Flush();
        }

        private static void Restore(
            PerformanceLab lab,
            IReadOnlyList<ResolvedSetting> currentSettings)
        {
            TryImportPreviousSnapshot(lab, currentSettings);
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                $@"{BackupRoot}\{lab.Definition.Id}",
                writable: false);
            if (backup is null)
            {
                throw new InvalidOperationException(
                    "No original-state snapshot exists for this tweak. Apply it once through this control before using Restore.");
            }

            RegistryRestorePlan.Execute(ReadRestorePlan(backup, currentSettings));
        }

        private static IReadOnlyList<RestoreRegistryValue> ReadRestorePlan(RegistryKey backup,
            IReadOnlyList<ResolvedSetting> settings) => RegistryRestorePlan.ReadIndexed(
                key => backup.GetValue(key), settings.Select(s => new RestoreRegistryTarget(s.Hive, s.Path, s.Name)).ToArray(), Deserialize);

        private static void DeleteVerifiedBackup(string id)
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(BackupRoot, writable: true);
            root?.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
        }

        private static void TryImportPreviousSnapshot(
            PerformanceLab lab,
            IReadOnlyList<ResolvedSetting> currentSettings)
        {
            string id = lab.Definition.Id;
            try
            {
                using (RegistryKey? existing = Registry.CurrentUser.OpenSubKey(
                           $@"{BackupRoot}\{id}",
                           writable: false))
                {
                    if (existing is not null)
                    {
                        return;
                    }
                }

                if (!CatalogAvailability.FileIsPresent(PreviousSnapshotPath))
                {
                    return;
                }

                FileInfo source = new(PreviousSnapshotPath);
                if (source.Length > 8 * 1024 * 1024) throw new InvalidOperationException("The previous-app snapshot is too large to validate.");
                string fingerprint = $"{source.Length}:{source.LastWriteTimeUtc.Ticks}";
                using (RegistryKey? migration = Registry.CurrentUser.OpenSubKey(
                           MigrationRoot,
                           writable: false))
                {
                    if (string.Equals(
                            Convert.ToString(migration?.GetValue(id), CultureInfo.InvariantCulture),
                            fingerprint,
                            StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                using JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(PreviousSnapshotPath));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("The previous-app snapshot root is invalid.");
                if (!document.RootElement.TryGetProperty(id, out JsonElement snapshot)) return;
                if (snapshot.ValueKind != JsonValueKind.Object ||
                    !snapshot.TryGetProperty("Items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("The previous-app snapshot entry is incomplete. Defaults were not substituted.");

                List<ImportedSnapshotValue> imports = new();
                foreach (ResolvedSetting current in currentSettings)
                {
                    JsonElement? match = null;
                    foreach (JsonElement item in items.EnumerateArray())
                    {
                        if (!JsonStringEquals(item, "Name", current.Name) ||
                            !item.TryGetProperty("Path", out JsonElement pathElement) ||
                            pathElement.ValueKind != JsonValueKind.String ||
                            !TryNormalizeRegistryPath(
                                pathElement.GetString(),
                                out RegistryHive hive,
                                out string path) ||
                            hive != current.Hive ||
                            !path.Equals(current.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        match = item;
                        break;
                    }

                    if (!match.HasValue)
                    {
                        throw new InvalidOperationException("The previous-app snapshot lacks a required registry target. Defaults were not substituted.");
                    }

                    JsonElement entry = match.Value;
                    if (!entry.TryGetProperty("Exists", out JsonElement existsElement) ||
                        existsElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidOperationException("The previous-app snapshot existence flag is invalid.");
                    bool existed = existsElement.ValueKind == JsonValueKind.True;
                    RegistryValueKind kind = current.Kind;
                    string value = string.Empty;
                    if (existed)
                    {
                        string kindText = ReadJsonString(entry, "Type");
                        if (!Enum.TryParse(kindText, ignoreCase: true, out kind) ||
                            !entry.TryGetProperty("Value", out JsonElement valueElement) ||
                            !TrySerializeJsonValue(valueElement, kind, out value))
                        {
                            throw new InvalidOperationException("The previous-app snapshot value is invalid. Defaults were not substituted.");
                        }
                    }

                    imports.Add(new ImportedSnapshotValue(current, existed, kind, value));
                }

                using RegistryKey backup = Registry.CurrentUser.CreateSubKey(
                    $@"{BackupRoot}\{id}",
                    writable: true);
                backup.SetValue("Snapshot.Imported", 1, RegistryValueKind.DWord);
                backup.SetValue("Snapshot.Name", lab.Definition.Name, RegistryValueKind.String);
                backup.SetValue("Snapshot.Count", imports.Count, RegistryValueKind.DWord);
                for (int index = 0; index < imports.Count; index++)
                {
                    ImportedSnapshotValue import = imports[index];
                    string prefix = $"Item.{index}.";
                    backup.SetValue(prefix + "Hive", import.Setting.Hive.ToString(), RegistryValueKind.String);
                    backup.SetValue(prefix + "Path", import.Setting.Path, RegistryValueKind.String);
                    backup.SetValue(prefix + "Name", import.Setting.Name, RegistryValueKind.String);
                    backup.SetValue(prefix + "Exists", import.Existed ? 1 : 0, RegistryValueKind.DWord);
                    if (import.Existed)
                    {
                        backup.SetValue(prefix + "Kind", import.Kind.ToString(), RegistryValueKind.String);
                        backup.SetValue(prefix + "Value", import.Value, RegistryValueKind.String);
                    }
                }
                backup.SetValue("Snapshot.Captured", 1, RegistryValueKind.DWord);

                using RegistryKey marker = Registry.CurrentUser.CreateSubKey(
                    MigrationRoot,
                    writable: true);
                marker.SetValue(id, fingerprint, RegistryValueKind.String);
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException("The previous-app backup could not be read or validated. Defaults were not substituted.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new InvalidOperationException("The previous-app backup could not be read or validated. Defaults were not substituted.", exception);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("The previous-app backup could not be read or validated. Defaults were not substituted.", exception);
            }
            catch (System.Security.SecurityException exception)
            {
                throw new InvalidOperationException("The previous-app backup could not be read or validated. Defaults were not substituted.", exception);
            }
        }

        private static void DeleteImportedBackup(string id)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    $@"{BackupRoot}\{id}",
                    writable: false);
                if (Convert.ToInt32(
                        key?.GetValue("Snapshot.Imported", 0) ?? 0,
                        CultureInfo.InvariantCulture) != 1)
                {
                    return;
                }

                using RegistryKey? root = Registry.CurrentUser.OpenSubKey(
                    BackupRoot,
                    writable: true);
                root?.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
            }
            catch
            {
                // An incomplete imported snapshot is never used without Captured=1.
            }
        }

        private static void DeleteIncompleteImportedBackup(string id)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    $@"{BackupRoot}\{id}",
                    writable: false);
                bool imported = Convert.ToInt32(
                    key?.GetValue("Snapshot.Imported", 0) ?? 0,
                    CultureInfo.InvariantCulture) == 1;
                bool complete = Convert.ToInt32(
                    key?.GetValue("Snapshot.Captured", 0) ?? 0,
                    CultureInfo.InvariantCulture) == 1;
                if (imported && !complete)
                {
                    using RegistryKey? root = Registry.CurrentUser.OpenSubKey(
                        BackupRoot,
                        writable: true);
                    root?.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
                }
            }
            catch
            {
                // Restore will reject an incomplete snapshot if cleanup is denied.
            }
        }

        private static bool JsonStringEquals(
            JsonElement element,
            string propertyName,
            string expected) =>
            element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String &&
            string.Equals(property.GetString(), expected, StringComparison.OrdinalIgnoreCase);

        private static string ReadJsonString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;

        private static bool TryNormalizeRegistryPath(
            string? source,
            out RegistryHive hive,
            out string path)
        {
            string value = source ?? string.Empty;
            string[] localMachinePrefixes =
            {
                @"HKLM:\",
                @"Microsoft.PowerShell.Core\Registry::HKEY_LOCAL_MACHINE\",
                @"Registry::HKEY_LOCAL_MACHINE\"
            };
            foreach (string prefix in localMachinePrefixes)
            {
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    hive = RegistryHive.LocalMachine;
                    path = value[prefix.Length..];
                    return path.Length > 0;
                }
            }

            string[] currentUserPrefixes =
            {
                @"HKCU:\",
                @"Microsoft.PowerShell.Core\Registry::HKEY_CURRENT_USER\",
                @"Registry::HKEY_CURRENT_USER\"
            };
            foreach (string prefix in currentUserPrefixes)
            {
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    hive = RegistryHive.CurrentUser;
                    path = value[prefix.Length..];
                    return path.Length > 0;
                }
            }

            hive = default;
            path = string.Empty;
            return false;
        }

        private static bool TrySerializeJsonValue(
            JsonElement value,
            RegistryValueKind kind,
            out string serialized)
        {
            try
            {
                serialized = kind switch
                {
                    RegistryValueKind.Binary when value.ValueKind == JsonValueKind.Array =>
                        Convert.ToBase64String(value.EnumerateArray()
                            .Select(item => item.GetByte())
                            .ToArray()),
                    RegistryValueKind.MultiString when value.ValueKind == JsonValueKind.Array =>
                        string.Join("\u001f", value.EnumerateArray()
                            .Select(item => item.GetString() ?? string.Empty)),
                    RegistryValueKind.DWord => value.GetInt32().ToString(CultureInfo.InvariantCulture),
                    RegistryValueKind.QWord => value.GetInt64().ToString(CultureInfo.InvariantCulture),
                    _ when value.ValueKind == JsonValueKind.String => value.GetString() ?? string.Empty,
                    _ => value.ToString()
                };
                return true;
            }
            catch (Exception exception) when (
                exception is FormatException or InvalidOperationException or OverflowException)
            {
                serialized = string.Empty;
                return false;
            }
        }

        private static bool ValueMatches(
            object? actual,
            RegistryValueKind expectedKind,
            object expected)
        {
            if (actual is null)
            {
                return false;
            }
            try
            {
                return expectedKind switch
                {
                    RegistryValueKind.DWord =>
                        Convert.ToInt32(actual, CultureInfo.InvariantCulture) ==
                        Convert.ToInt32(expected, CultureInfo.InvariantCulture),
                    RegistryValueKind.QWord =>
                        Convert.ToInt64(actual, CultureInfo.InvariantCulture) ==
                        Convert.ToInt64(expected, CultureInfo.InvariantCulture),
                    _ => string.Equals(
                        Convert.ToString(actual, CultureInfo.InvariantCulture),
                        Convert.ToString(expected, CultureInfo.InvariantCulture),
                        StringComparison.Ordinal)
                };
            }
            catch
            {
                return false;
            }
        }

        private static string FormatValue(object? value) =>
            value is null
                ? "<absent>"
                : value is string[] strings
                    ? string.Join(", ", strings)
                    : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<absent>";

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
            RegistryValueKind.DWord => Convert.ToInt32(value, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => Convert.ToInt64(value, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture),
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

        private static IReadOnlyList<PerformanceLab> CreateCatalog() => new[]
        {
            Lab(@"NoNetCrawling", @"De-Bloat Windows", @"Desktop / UX / Extended", @"Explorer Network Folder Auto-Crawl Disable",
                @"NoNetCrawling=1 to reduce background network folder discovery.", @"Safe", false, @"Any",
                Setting(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", @"NoNetCrawling", RegistryValueKind.DWord, 1)),
            Lab(@"TaskViewButtonOff", @"De-Bloat Windows", @"Desktop / UX / Extended", @"Task View Button Hide",
                @"ShowTaskViewButton=0.", @"Safe", false, @"Any",
                Setting(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"ShowTaskViewButton", RegistryValueKind.DWord, 0)),
            Lab(@"TaskbarChatOff", @"De-Bloat Windows", @"Desktop / UX / Extended", @"Taskbar Chat / Teams Icon Hide",
                @"TaskbarMn=0.", @"Safe", false, @"Any",
                Setting(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"TaskbarMn", RegistryValueKind.DWord, 0)),
            Lab(@"NvOverlayOff", @"De-Bloat Windows", @"GPU - NVIDIA", @"NVIDIA ShadowPlay / Overlay Disable",
                @"Disables the NVIDIA ShadowPlay/overlay registry preference when present.", @"Safe", false, @"NVIDIA",
                Setting(@"HKLM:\SOFTWARE\NVIDIA Corporation\Global\ShadowPlay\NVSPCAPS", @"Enable", RegistryValueKind.DWord, 0)),
            Lab(@"NvTelemetryOptOut", @"De-Bloat Windows", @"GPU - NVIDIA / Extended", @"NVIDIA Control Panel Telemetry Opt-Out",
                @"NvControlPanel2 client OptInOrOutPreference=0. Service locking is available in Advanced Windows Tweaks & De-Bloat.", @"Safe", false, @"NVIDIA",
                Setting(@"HKLM:\SOFTWARE\NVIDIA Corporation\NvControlPanel2\Client", @"OptInOrOutPreference", RegistryValueKind.DWord, 0)),
            Lab(@"PrefetchSuperfetchOff", @"De-Bloat Windows", @"Memory / Prefetch", @"Prefetcher + Superfetch Registry Disable",
                @"EnablePrefetcher=0 + EnableSuperfetch=0. SysMain service control is grouped in Advanced Windows Tweaks & De-Bloat.", @"High", true, @"Any",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", @"EnablePrefetcher", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", @"EnableSuperfetch", RegistryValueKind.DWord, 0)),
            Lab(@"StickyKeysHotkeysOff", @"De-Bloat Windows", @"Windows / Accessibility", @"Sticky / Toggle / Filter Keys Shortcut Popups Disable",
                @"Suppresses the keyboard shortcuts that trigger StickyKeys, ToggleKeys and FilterKeys prompts. Accessibility features remain available through Windows Settings.", @"High", false, @"Any",
                Setting(@"HKCU:\Control Panel\Accessibility\StickyKeys", @"Flags", RegistryValueKind.String, @"506"),
                Setting(@"HKCU:\Control Panel\Accessibility\ToggleKeys", @"Flags", RegistryValueKind.String, @"58"),
                Setting(@"HKCU:\Control Panel\Accessibility\Keyboard Response", @"Flags", RegistryValueKind.String, @"122")),
            Lab(@"ModernStandbyOverride", @"De-Bloat Windows", @"Windows / Compatibility", @"Modern Standby Platform Override",
                @"PlatformAoAcOverride=0. Firmware-dependent; may make sleep/lid-close unstable.", @"High", true, @"Any",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Power", @"PlatformAoAcOverride", RegistryValueKind.DWord, 0)),
            Lab(@"TpmCpuBypass", @"De-Bloat Windows", @"Windows / Compatibility", @"Windows 11 Unsupported TPM/CPU Upgrade Bypass",
                @"AllowUpgradesWithUnsupportedTPMOrCPU=1 plus LabConfig TPM/SecureBoot bypass flags.", @"Legacy", false, @"Any",
                Setting(@"HKLM:\SYSTEM\Setup\MoSetup", @"AllowUpgradesWithUnsupportedTPMOrCPU", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SYSTEM\Setup\LabConfig", @"BypassTPMCheck", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SYSTEM\Setup\LabConfig", @"BypassSecureBootCheck", RegistryValueKind.DWord, 1)),
            Lab(@"WindowsUiResponsiveness", @"Essential Windows Tweaks", @"Desktop / UX", @"Windows UI Responsiveness Optimization",
                @"Combines MenuShowDelay, mouse-hover delay and Explorer startup-delay controls that target shell/UI responsiveness.", @"Safe", false, @"Any",
                Setting(@"HKCU:\Control Panel\Desktop", @"MenuShowDelay", RegistryValueKind.String, @"200"),
                Setting(@"HKCU:\Control Panel\Mouse", @"MouseHoverTime", RegistryValueKind.String, @"10"),
                Setting(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Serialize", @"StartupDelayInMSec", RegistryValueKind.DWord, 0)),
            Lab(@"WindowsVisualEffects", @"Essential Windows Tweaks", @"Desktop / UX", @"Windows Visual Effects / Animations Disable",
                @"Combines transparency, full-window dragging, Explorer list-view visual effects, DWM shadows, blur/Aero Peek and DWM animations into one visual-overhead option.", @"Safe", false, @"Any",
                Setting(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", @"EnableTransparency", RegistryValueKind.DWord, 0),
                Setting(@"HKCU:\Control Panel\Desktop", @"DragFullWindows", RegistryValueKind.String, @"0"),
                Setting(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"ListviewAlphaSelect", RegistryValueKind.DWord, 0),
                Setting(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"ListviewShadow", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SOFTWARE\Microsoft\Windows\Dwm", @"EnableShadow", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SOFTWARE\Microsoft\Windows\Dwm", @"Blur", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SOFTWARE\Microsoft\Windows\Dwm", @"EnableAeroPeek", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SOFTWARE\Microsoft\Windows\Dwm", @"DisallowAnimations", RegistryValueKind.DWord, 1)),
            Lab(@"DwmUseDpiScalingOff", @"Essential Windows Tweaks", @"Display / DWM", @"DWM UseDpiScaling Disable",
                @"UseDpiScaling=0. This can be effectively a no-op at 100% scaling and has mixed/unverified benefit.", @"Experimental", true, @"Any",
                Setting(@"HKLM:\SOFTWARE\Microsoft\Windows\Dwm", @"UseDpiScaling", RegistryValueKind.DWord, 0)),
            Lab(@"LowDiskNotificationOff", @"Essential Windows Tweaks", @"Explorer / Notifications", @"Low Disk Space Notification Disable",
                @"Sets NoLowDiskSpaceChecks=1 for the current user so Explorer stops displaying low-disk-space notifications. This does not create free space or stop applications and Windows Update from failing when a drive is actually full.", @"Experimental", false, @"Any",
                Setting(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", @"NoLowDiskSpaceChecks", RegistryValueKind.DWord, 1)),
            Lab(@"FileExtensionsShow", @"Essential Windows Tweaks", @"Explorer / Visual", @"Show File Extensions",
                @"Sets HideFileExt=0 so File Explorer displays known filename extensions. This helps distinguish executable, script, document, and image file types without changing file associations or file contents.", @"Safe", false, @"Any",
                Setting(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"HideFileExt", RegistryValueKind.DWord, 0)),
            Lab(@"InputResponsiveness", @"Essential Windows Tweaks", @"Input / Mouse", @"Mouse / Keyboard Responsiveness Optimization",
                @"Combines Windows mouse acceleration disable, keyboard repeat delay and background raw-mouse throttling controls.", @"Experimental", true, @"Any",
                Setting(@"HKCU:\Control Panel\Mouse", @"MouseSpeed", RegistryValueKind.String, @"0"),
                Setting(@"HKCU:\Control Panel\Mouse", @"MouseThreshold1", RegistryValueKind.String, @"0"),
                Setting(@"HKCU:\Control Panel\Mouse", @"MouseThreshold2", RegistryValueKind.String, @"0"),
                Setting(@"HKCU:\Control Panel\Keyboard", @"KeyboardDelay", RegistryValueKind.String, @"0"),
                Setting(@"HKCU:\Control Panel\Mouse", @"RawMouseThrottleEnabled", RegistryValueKind.DWord, 1)),
            Lab(@"UacSecureDesktopDimOff", @"Essential Windows Tweaks", @"Startup / Responsiveness", @"UAC Secure Desktop Dimming Disable",
                @"PromptOnSecureDesktop=0. UAC prompts remain enabled but no longer switch to the secure desktop.", @"High", false, @"Any",
                Setting(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", @"PromptOnSecureDesktop", RegistryValueKind.DWord, 0)),
            Lab(@"StartupExperience", @"Essential Windows Tweaks", @"Startup / Responsiveness", @"Windows Startup Experience Disable",
                @"Combines first-logon animation and Windows startup-sound suppression into one startup-experience option.", @"Safe", true, @"Any",
                Setting(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", @"EnableFirstLogonAnimation", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", @"DisableStartupSound", RegistryValueKind.DWord, 1)),
            Lab(@"FastStartupEnable", @"Essential Windows Tweaks", @"Windows / Compatibility", @"Fast Startup / Hiberboot Enable",
                @"Sets HiberbootEnabled=1. Fast Startup also requires Windows hibernation to be enabled; the setting can be ineffective while the separate Disable Hibernation tweak is active.", @"High", true, @"Any",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power", @"HiberbootEnabled", RegistryValueKind.DWord, 1)),
            Lab(@"RealTimeUtc", @"Essential Windows Tweaks", @"Windows / Compatibility", @"Hardware Clock Uses UTC",
                @"RealTimeIsUniversal=1. Useful for dual-boot environments; can surprise Windows-only users.", @"Experimental", true, @"Any",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\TimeZoneInformation", @"RealTimeIsUniversal", RegistryValueKind.DWord, 1)),
            Lab(@"LongPathsOn", @"Essential Windows Tweaks", @"Windows / Compatibility", @"Win32 Long Path Support Enable",
                @"Sets LongPathsEnabled=1 so compatible manifested Win32 applications can use paths beyond the traditional MAX_PATH limit. Older applications remain subject to their own path handling and a restart is recommended.", @"Safe", true, @"Any",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem", @"LongPathsEnabled", RegistryValueKind.DWord, 1)),
            Lab(@"CpuPowerLatencyOptimization", @"Gaming Tweaks", @"CPU / Kernel", @"CPU Power / Parking Latency Optimization",
                @"Combines Windows power throttling, the recovered core-parking disable flag and energy-estimation disable controls without changing CPU minimum/maximum power-plan percentages.", @"Experimental", true, @"Any",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", @"PowerThrottlingOff", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Power", @"CoreParkingDisabled", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Power", @"EnergyEstimationEnabled", RegistryValueKind.DWord, 0)),
            Lab(@"KernelMemoryResidency", @"Gaming Tweaks", @"CPU / Kernel", @"Kernel / Memory Residency Optimization",
                @"Combines LargeSystemCache gaming behavior and keeping the kernel executive resident in RAM.", @"Experimental", true, @"Any",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", @"LargeSystemCache", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", @"DisablePagingExecutive", RegistryValueKind.DWord, 1)),
            Lab(@"KernelInterruptScheduling", @"Gaming Tweaks", @"CPU / Kernel", @"Kernel Interrupt / Scheduling Optimization",
                @"Combines interrupt steering, timer coalescing, cache-aware scheduling and IRQ0 priority controls that target kernel scheduling latency.", @"High", true, @"Any",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", @"InterruptSteeringDisabled", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", @"CoalescingTimerInterval", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", @"CacheAwareScheduling", RegistryValueKind.DWord, 7),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl", @"IRQ0Priority", RegistryValueKind.DWord, 2),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Kernel", @"ThreadDpcEnable", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Kernel", @"MaximumDpcQueueDepth", RegistryValueKind.DWord, 1)),
            Lab(@"SecurityMitigationsPerformance", @"Gaming Tweaks", @"CPU / Kernel", @"Security Mitigations / HVCI Performance Override",
                @"Combines the high-risk speculative-execution mitigation override and Memory Integrity/HVCI disable controls into one clearly high-risk performance option.", @"High", true, @"Any",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", @"FeatureSettingsOverride", RegistryValueKind.DWord, 3),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", @"FeatureSettingsOverrideMask", RegistryValueKind.DWord, 3),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", @"Enabled", RegistryValueKind.DWord, 0)),
            Lab(@"DpcWatchdogExperimentalOverride", @"Gaming Tweaks", @"CPU / Kernel / DPC", @"DPC Watchdog Experimental Override",
                @"Applies the recovered zero-value DPC watchdog timeout/profile override family. These registry controls are not a supported Microsoft performance interface and can make DPC failures harder to diagnose; use only for controlled testing.", @"High", true, @"Any",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Kernel", @"DpcWatchdogProfileOffset", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Kernel", @"DpcTimeout", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Kernel", @"DpcWatchdogPeriod", RegistryValueKind.DWord, 0)),
            Lab(@"LegacyIrq89PriorityHints", @"Gaming Tweaks", @"CPU / Kernel / DPC", @"Legacy IRQ8 / IRQ9 Priority Hints",
                @"Creates IRQ8Priority=1 and IRQ9Priority=1 under PriorityControl. These values are undocumented on modern Windows and may be ignored on APIC/MSI systems; they are exposed only as a reversible legacy experiment.", @"Legacy", true, @"Any",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl", @"IRQ8Priority", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl", @"IRQ9Priority", RegistryValueKind.DWord, 1)),
            Lab(@"IntelPpmDisabled", @"Gaming Tweaks", @"CPU / Kernel / Extended", @"Intel PPM Driver Disable",
                @"intelppm Start=4 behavior. Legacy/aggressive: can materially change CPU power-management behavior.", @"Legacy", true, @"Intel",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Services\intelppm", @"Start", RegistryValueKind.DWord, 4)),
            Lab(@"NumLockStartup", @"Gaming Tweaks", @"Desktop / UX", @"NumLock at Sign-In Enable",
                @"Sets InitialKeyboardIndicators=2 for the current user so Num Lock is requested at sign-in. Firmware, keyboard hardware, and Fast Startup can still influence the final LED/keypad state.", @"Safe", false, @"Any",
                Setting(@"HKCU:\Control Panel\Keyboard", @"InitialKeyboardIndicators", RegistryValueKind.String, @"2")),
            Lab(@"DisplayFlipLatency", @"Gaming Tweaks", @"Display / DWM", @"DWM / DirectX Flip & Frame-Latency Optimization",
                @"Combines Independent Flip, Advanced Direct Flip, low-latency DWM buffering and DirectX maximum-frame-latency controls.", @"Experimental", true, @"Any",
                Setting(@"HKLM:\SOFTWARE\Microsoft\Windows\Dwm", @"DisableIndependentFlip", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SOFTWARE\Microsoft\Windows\Dwm", @"DisableAdvancedDirectFlip", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SOFTWARE\Microsoft\Windows\Dwm", @"EnableDirectFlip", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SOFTWARE\Microsoft\Windows\Dwm", @"BufferCount", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SOFTWARE\Microsoft\Windows\Dwm", @"FrameLatency", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SOFTWARE\Microsoft\DirectX", @"MaximumFrameLatency", RegistryValueKind.DWord, 1)),
            Lab(@"AmdPerformanceLatency", @"Gaming Tweaks", @"GPU - AMD", @"AMD GPU Performance / Latency Optimization",
                @"Combines AMD light-sleep, ULPS, clock/power gating, ASPM, 3D-performance and Radeon Boost controls that target the same maximum-performance/latency objective.", @"High", true, @"AMD",
                Setting(@"@AMD_GPU@", @"DisableGfxCoarseGrainLightSleep", RegistryValueKind.DWord, 1),
                Setting(@"@AMD_GPU@", @"EnableUlps", RegistryValueKind.DWord, 0),
                Setting(@"@AMD_GPU@", @"DisableAllClockGating", RegistryValueKind.DWord, 1),
                Setting(@"@AMD_GPU@", @"DisablePowerGating", RegistryValueKind.DWord, 1),
                Setting(@"@AMD_GPU@", @"DisableAspmL1", RegistryValueKind.DWord, 1),
                Setting(@"@AMD_GPU@", @"PP_Force3DPerformanceMode", RegistryValueKind.DWord, 1),
                Setting(@"@AMD_GPU@", @"KMD_RadeonBoostEnabled", RegistryValueKind.DWord, 0)),
            Lab(@"IntelGpuPerformanceLatency", @"Gaming Tweaks", @"GPU - Intel", @"Intel GPU Performance / Latency Optimization",
                @"Combines Intel GPU power-saving disable, low-latency, render P-state, clock-gating, render-boost, FPS-boost and VT-d throttle preferences into one option.", @"High", true, @"Intel",
                Setting(@"@INTEL_GPU@", @"PowerSavingProfile", RegistryValueKind.DWord, 0),
                Setting(@"@INTEL_GPU@", @"AdaptiveVsyncPower", RegistryValueKind.DWord, 0),
                Setting(@"@INTEL_GPU@", @"LowLatencyMode", RegistryValueKind.DWord, 1),
                Setting(@"@INTEL_GPU@", @"ForceLowestInputLatency", RegistryValueKind.DWord, 1),
                Setting(@"@INTEL_GPU@", @"MaxGpuLatency", RegistryValueKind.DWord, 1),
                Setting(@"@INTEL_GPU@", @"ForceRenderP0", RegistryValueKind.DWord, 1),
                Setting(@"@INTEL_GPU@", @"DisableClockGating", RegistryValueKind.DWord, 1),
                Setting(@"@INTEL_GPU@", @"ForceAllEnginesOn", RegistryValueKind.DWord, 1),
                Setting(@"@INTEL_GPU@", @"P3RenderBoost", RegistryValueKind.DWord, 1),
                Setting(@"@INTEL_GPU@", @"KmtExclusive", RegistryValueKind.DWord, 1),
                Setting(@"@INTEL_GPU@", @"GfxFpsBoost", RegistryValueKind.DWord, 1),
                Setting(@"@INTEL_GPU@", @"DisableVtdThrottle", RegistryValueKind.DWord, 1),
                Setting(@"@INTEL_GPU@", @"OverrideVtdSettings", RegistryValueKind.DWord, 1),
                Setting(@"@INTEL_GPU@", @"ForceP0State", RegistryValueKind.DWord, 1),
                Setting(@"@INTEL_GPU@", @"PreventP0Exit", RegistryValueKind.DWord, 1)),
            Lab(@"NvCudaCache4G", @"Gaming Tweaks", @"GPU - NVIDIA", @"NVIDIA CUDA Cache Maximum - 4 GB",
                @"Sets the machine CUDA_CACHE_MAXSIZE environment value to 4 GB for newly started CUDA applications. Existing processes keep their current environment, and actual cache use remains controlled by the NVIDIA driver/application.", @"Safe", false, @"NVIDIA",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", @"CUDA_CACHE_MAXSIZE", RegistryValueKind.String, @"4294967296")),
            Lab(@"NvidiaPerformanceLatency", @"Gaming Tweaks", @"GPU - NVIDIA", @"NVIDIA Performance / Latency Optimization",
                @"Combines NVIDIA maximum-performance, low-latency, P-state, ASPM, OpenGL threading, pre-render and driver frame-limiter preferences into one coordinated GPU performance option.", @"High", true, @"NVIDIA",
                Setting(@"HKLM:\SOFTWARE\NVIDIA Corporation\Global\NvTweak", @"Powermizer_enable", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SOFTWARE\NVIDIA Corporation\Global\NvTweak", @"PowerMizerEnable", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SOFTWARE\NVIDIA Corporation\Global\NvTweak", @"PerfLevelSrc", RegistryValueKind.DWord, 8738),
                Setting(@"HKLM:\SOFTWARE\NVIDIA Corporation\Global\NvTweak", @"PowerMizerLevel", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SOFTWARE\NVIDIA Corporation\Global\NvTweak", @"PowerMizerLevelAC", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm", @"LOWLATENCY", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm", @"EnablePerformanceMode", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm", @"RMDisableGpuASPMFlags", RegistryValueKind.DWord, 1),
                Setting(@"@NVIDIA_GPU@", @"DisableDynamicPstate", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SOFTWARE\NVIDIA Corporation\Global\NVTweak", @"OGLThreadedOptimizations", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SOFTWARE\NVIDIA Corporation\Global\NVTweak", @"MaxFramesAllowed", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SOFTWARE\NVIDIA Corporation\Global\NVTweak", @"FRL_Enable", RegistryValueKind.DWord, 0)),
            Lab(@"NvTdr10", @"Gaming Tweaks", @"GPU - NVIDIA", @"NVIDIA TDR Watchdog - 10 Seconds",
                @"Keeps Windows Timeout Detection and Recovery enabled while raising the NVIDIA GPU response timeout to 10 seconds. This can reduce premature driver resets during long workloads but also delays recovery from a genuinely hung GPU; restart required.", @"Safe", true, @"NVIDIA",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", @"TdrLevel", RegistryValueKind.DWord, 3),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", @"TdrDelay", RegistryValueKind.DWord, 10)),
            Lab(@"NvEccOff", @"Gaming Tweaks", @"GPU - NVIDIA / Extended", @"NVIDIA L1 ECC Disable Flag",
                @"Writes the NVIDIA RMEnableL1ECC=0 driver flag. Support and effect are entirely GPU/driver dependent; consumer GPUs may ignore it, while supported hardware can lose an error-correction feature. The original value is captured and a restart is required.", @"Experimental", true, @"NVIDIA",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm", @"RMEnableL1ECC", RegistryValueKind.DWord, 0)),
            Lab(@"InputLatencyOptimization", @"Gaming Tweaks", @"Input / USB", @"Keyboard / Mouse Input Latency Optimization",
                @"Combines raw-input exclusive mode, keyboard/mouse class queue sizing, layered-latency and HID USB low-latency controls.", @"Experimental", true, @"Any",
                Setting(@"HKLM:\SOFTWARE\Microsoft\Input", @"AllowRawInputExclusive", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", @"KeyboardDataQueueSize", RegistryValueKind.DWord, 22),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Services\mouclass\Parameters", @"MouseDataQueueSize", RegistryValueKind.DWord, 22),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", @"LayeredLatency", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Services\mouclass\Parameters", @"LayeredLatency", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Services\kbdhid\Parameters", @"LayeredLatency", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Services\mouhid\Parameters", @"LayeredLatency", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Services\HidUsb\Parameters", @"DeviceIdleEnabled", RegistryValueKind.DWord, 0),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Services\HidUsb\Parameters", @"ForceLowestInputLatency", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Services\HidUsb\Parameters", @"LowLatencyMode", RegistryValueKind.DWord, 1)),
            Lab(@"LanmanIrpStack20", @"Gaming Tweaks", @"Network / QoS / Extended", @"LanmanServer IRP Stack Size - 20",
                @"Sets the LanmanServer IRPStackSize value to 20, changing the request-stack allocation used by Windows file/server networking. It is a compatibility-oriented legacy control rather than a universal latency improvement and requires restart.", @"Experimental", true, @"Any",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", @"IRPStackSize", RegistryValueKind.DWord, 20)),
            Lab(@"KernelTimerLatency", @"Gaming Tweaks", @"Timer / Scheduler", @"Kernel / Packet Timer Latency Optimization",
                @"Combines global timer-resolution requests, serialized timer expiration and packet-scheduler timer-resolution settings.", @"Experimental", true, @"Any",
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", @"GlobalTimerResolutionRequests", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", @"SerializeTimerExpiration", RegistryValueKind.DWord, 1),
                Setting(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched", @"TimerResolution", RegistryValueKind.DWord, 1))
        };

        private static PerformanceLab Lab(
            string id,
            string module,
            string category,
            string name,
            string description,
            string risk,
            bool reboot,
            string vendor,
            params RegistrySetting[] settings)
        {
            string normalizedRisk = risk.Equals("High", StringComparison.OrdinalIgnoreCase)
                ? "HIGH RISK"
                : risk;
            return new PerformanceLab(
                new ToolToggleDefinition(
                    id,
                    $"{category} / {normalizedRisk}",
                    name,
                    description,
                    true,
                    reboot),
                module,
                vendor,
                settings);
        }

        private static RegistrySetting Setting(
            string path,
            string name,
            RegistryValueKind kind,
            object value) =>
            new(path, name, kind, value);

        private sealed record PerformanceLab(
            ToolToggleDefinition Definition,
            string Module,
            string Vendor,
            IReadOnlyList<RegistrySetting> Settings);

        private readonly record struct RegistrySetting(
            string Path,
            string Name,
            RegistryValueKind Kind,
            object Value);

        private readonly record struct ResolvedSetting(
            RegistryHive Hive,
            string Path,
            string Name,
            RegistryValueKind Kind,
            object Value);

        private readonly record struct ImportedSnapshotValue(
            ResolvedSetting Setting,
            bool Existed,
            RegistryValueKind Kind,
            string Value);
    }
}
