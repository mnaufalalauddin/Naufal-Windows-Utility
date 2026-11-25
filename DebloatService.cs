using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal sealed class DebloatService : IToolToggleService, ICatalogEffectSource
    {
        private const string BackupRoot =
            @"Software\Naufal Windows Tech\Powertoys\Backups\Debloat";
        private const string MigrationRoot =
            @"Software\Naufal Windows Tech\Powertoys\Migrations\PreviousDebloat";
        private static string PreviousSnapshotPath => AppDataPaths.ResolveLegacyBackup("Debloat_LowRisk_Original.json");

        private static readonly IReadOnlyDictionary<string, DebloatDefinition> Catalog =
            CreateCatalog();

        public string DescribeRecovery(ToolToggleDefinition definition) =>
            CatalogRecovery.Snapshot($@"{BackupRoot}\{definition.Id}", "No current snapshot found. Restore checks for a previous backup before using the Windows default for this option.");

        public IReadOnlyList<ToolToggleDefinition> GetDefinitions()
        {
            return Catalog.Values
                .Select(item => item.Definition)
                .ToArray();
        }

        public IReadOnlyList<CatalogEffect> GetEffects(ToolToggleDefinition definition) => Catalog[definition.Id].Settings
            .Select(s => SharedPrivacySnapshot.Describe(definition.Id,
                CatalogEffect.Registry("HKCU", s.Path, s.Name, s.Kind.ToString(), s.Value, s.Remove))).ToArray();

        public Task<ToolToggleState> ReadStateAsync(ToolToggleDefinition definition)
        {
            try
            {
                DebloatDefinition item = Catalog[definition.Id];
                bool available = IsAvailable(item);
                if (!available)
                {
                    return Task.FromResult(new ToolToggleState(
                        false,
                        false,
                        "Not installed / not available",
                        "The related application or startup entry is not present.", UnavailableOnThisPc: true));
                }

                bool applied = true;
                bool anyApplied = false;
                List<string> actualValues = new();
                foreach (DebloatSetting setting in item.Settings)
                {
                    object? value = ReadCurrentUserValue(setting.Path, setting.Name);
                    bool exists = value is not null;
                    bool matches = setting.Remove
                        ? !exists
                        : exists && string.Equals(
                            Convert.ToString(value, CultureInfo.InvariantCulture),
                            Convert.ToString(setting.Value, CultureInfo.InvariantCulture),
                            StringComparison.Ordinal);
                    applied &= matches;
                    anyApplied |= matches;
                    actualValues.Add(
                        $"{setting.Name}={(exists ? Convert.ToString(value, CultureInfo.InvariantCulture) : "<absent>")}");
                }

                return Task.FromResult(new ToolToggleState(
                    applied,
                    true,
                    string.Join(", ", actualValues), HasAppliedParts: anyApplied));
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

        public async Task<ToolToggleOperationResult> SetStateAsync(
            ToolToggleDefinition definition,
            bool targetOn)
        {
            try
            {
                DebloatDefinition item = Catalog[definition.Id];
                bool usedSnapshot = false;
                if (targetOn)
                {
                    Apply(item);
                }
                else
                {
                    usedSnapshot = Restore(item);
                }

                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable && (targetOn
                    ? after.IsOn
                    : usedSnapshot
                        ? VerifySavedState(item)
                        : !after.IsOn);
                if (!targetOn && verified)
                {
                    DeleteBackup(item.Definition.Id);
                }
                return new ToolToggleOperationResult(
                    verified,
                    verified,
                    verified
                        ? $"{definition.Name} is now {(targetOn ? "APPLIED" : "RESTORED")}."
                        : $"Verification did not match the requested state. Actual: {after.ActualValue}",
                    after);
            }
            catch (Exception exception)
            {
                ToolToggleState after = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(
                    false,
                    false,
                    exception.Message,
                    after);
            }
        }

        public Task<ToolToggleOperationResult> RestoreOriginalAsync(
            ToolToggleDefinition definition) =>
            SetStateAsync(definition, targetOn: false);

        public async Task<ToolToggleOperationResult> RestoreWindowsDefaultAsync(
            ToolToggleDefinition definition)
        {
            try
            {
                DebloatDefinition item = Catalog[definition.Id];
                RestoreWindowsDefault(item);
                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable && !after.IsOn;
                if (verified)
                {
                    DeleteBackup(item.Definition.Id);
                }
                return new ToolToggleOperationResult(
                    verified,
                    verified,
                    verified
                        ? $"{definition.Name} was returned to the Windows default."
                        : $"Windows-default read-back failed. Actual: {after.ActualValue}",
                    after);
            }
            catch (Exception exception)
            {
                ToolToggleState after = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(false, false, exception.Message, after);
            }
        }

        private static void Apply(DebloatDefinition definition)
        {
            PrepareSharedSnapshots(definition.Definition.Id, SharedSnapshotPreparation.Capture);
            foreach (DebloatSetting setting in definition.Settings)
            {
                Capture(definition.Definition.Id, setting);
                if (setting.Remove)
                {
                    DeleteCurrentUserValue(setting.Path, setting.Name);
                }
                else
                {
                    using RegistryKey key = Registry.CurrentUser.CreateSubKey(
                        setting.Path,
                        writable: true);
                    key.SetValue(setting.Name, setting.Value!, setting.Kind);
                }
            }
        }

        internal static void PrepareSharedSnapshots(string owner, SharedSnapshotPreparation mode)
        {
            foreach (var target in SharedPrivacySnapshot.Targets.Where(t => owner == "Telemetry" || t.Id == owner))
                TryImportPreviousSnapshot(Catalog[target.Id]);
            SharedPrivacySnapshot.Prepare(owner, mode);
        }

        private static bool Restore(DebloatDefinition definition)
        {
            TryImportPreviousSnapshot(definition);
            PrepareSharedSnapshots(definition.Definition.Id, SharedSnapshotPreparation.Restore);
            bool ignoreIncompleteImport;
            using (RegistryKey? imported = Registry.CurrentUser.OpenSubKey(
                       $@"{BackupRoot}\{definition.Definition.Id}",
                       writable: false))
            {
                ignoreIncompleteImport = Convert.ToInt32(
                                             imported?.GetValue("Snapshot.Imported", 0) ?? 0,
                                             CultureInfo.InvariantCulture) == 1 &&
                                         Convert.ToInt32(
                                             imported?.GetValue("Snapshot.Complete", 0) ?? 0,
                                             CultureInfo.InvariantCulture) != 1;
            }
            bool hadCompleteBackup = false;
            using (RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                       $@"{BackupRoot}\{definition.Definition.Id}",
                       writable: false))
            {
                if (!ignoreIncompleteImport && backup is not null)
                {
                    RegistryRestorePlan.RequireTags(key => backup.GetValue(key), definition.Settings.Select(MakeTag), Deserialize);
                    hadCompleteBackup = RegistrySnapshotCommit.HasCompleteSet(
                        key => backup.GetValue(key),
                        definition.Settings.Select(MakeTag),
                        definition.Definition.Name);
                }
                else if (backup is not null)
                    throw new InvalidOperationException("The imported snapshot is incomplete. Defaults were not substituted; backup retained.");
            }
            if (!hadCompleteBackup)
            {
                RestoreWindowsDefault(definition);
                return false;
            }

            foreach (DebloatSetting setting in definition.Settings)
            {
                string tag = MakeTag(setting);
                using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                    $@"{BackupRoot}\{definition.Definition.Id}",
                    writable: false);
                bool existed = Convert.ToInt32(
                    backup!.GetValue($"{tag}.Exists", 0),
                    CultureInfo.InvariantCulture) == 1;
                if (!existed)
                {
                    DeleteCurrentUserValue(setting.Path, setting.Name);
                    continue;
                }

                string kindText = Convert.ToString(
                    backup!.GetValue($"{tag}.Kind", RegistryValueKind.String.ToString()),
                    CultureInfo.InvariantCulture) ?? RegistryValueKind.String.ToString();
                RegistryValueKind kind = Enum.TryParse(kindText, out RegistryValueKind parsed)
                    ? parsed
                    : RegistryValueKind.String;
                string serialized = Convert.ToString(
                    backup!.GetValue($"{tag}.Value", string.Empty),
                    CultureInfo.InvariantCulture) ?? string.Empty;
                using RegistryKey destination = Registry.CurrentUser.CreateSubKey(
                    setting.Path,
                    writable: true);
                destination.SetValue(
                    setting.Name,
                    Deserialize(serialized, kind),
                    kind);
            }

            return true;
        }

        private static bool VerifySavedState(DebloatDefinition definition)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                $@"{BackupRoot}\{definition.Definition.Id}",
                writable: false);
            if (backup is null)
            {
                return false;
            }

            foreach (DebloatSetting setting in definition.Settings)
            {
                object? actual = ReadCurrentUserValue(setting.Path, setting.Name);
                RegistryValueKind? kind = null;
                if (actual is not null)
                {
                    using RegistryKey? source = Registry.CurrentUser.OpenSubKey(
                        setting.Path,
                        writable: false);
                    kind = source?.GetValueKind(setting.Name);
                }
                try
                {
                    RegistrySnapshotCommit.RequireRestored(
                        key => backup.GetValue(key),
                        MakeTag(setting),
                        actual,
                        kind,
                        Serialize);
                }
                catch
                {
                    return false;
                }
            }
            return true;
        }

        private static void TryImportPreviousSnapshot(DebloatDefinition definition)
        {
            string id = definition.Definition.Id;
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
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("The previous-app snapshot is malformed. Defaults were not substituted.");

                if (!document.RootElement.EnumerateArray().Any(e => JsonStringEquals(e, "ItemId", id))) return;
                List<ImportedRegistryValue> imports = new();
                foreach (DebloatSetting setting in definition.Settings)
                {
                    JsonElement? match = null;
                    foreach (JsonElement entry in document.RootElement.EnumerateArray())
                    {
                        if (!JsonStringEquals(entry, "ItemId", id) ||
                            !JsonStringEquals(entry, "Name", setting.Name) ||
                            !entry.TryGetProperty("Path", out JsonElement pathElement) ||
                            pathElement.ValueKind != JsonValueKind.String ||
                            !TryNormalizeCurrentUserPath(pathElement.GetString(), out string path) ||
                            !path.Equals(setting.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        match = entry;
                        break;
                    }

                    if (!match.HasValue)
                        throw new InvalidOperationException("The previous-app snapshot is incomplete. Defaults were not substituted.");

                    JsonElement snapshot = match.Value;
                    if (!snapshot.TryGetProperty("Existed", out JsonElement existedElement) || existedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidOperationException("The previous-app snapshot existence flag is invalid.");
                    bool existed = existedElement.ValueKind == JsonValueKind.True;
                    RegistryValueKind kind = RegistryValueKind.String;
                    string value = string.Empty;
                    if (existed)
                    {
                        string kindText = ReadJsonString(snapshot, "Kind");
                        if (!Enum.TryParse(kindText, ignoreCase: true, out kind) ||
                            !snapshot.TryGetProperty("Value", out JsonElement valueElement) ||
                            !TrySerializeJsonValue(valueElement, kind, out value))
                            throw new InvalidOperationException("The previous-app snapshot value is invalid.");
                    }

                    imports.Add(new ImportedRegistryValue(setting, existed, kind, value));
                }

                using RegistryKey backup = Registry.CurrentUser.CreateSubKey(
                    $@"{BackupRoot}\{id}",
                    writable: true);
                backup.SetValue("Snapshot.Imported", 1, RegistryValueKind.DWord);
                foreach (ImportedRegistryValue import in imports)
                {
                    string tag = MakeTag(import.Setting);
                    backup.SetValue($"{tag}.Exists", import.Existed ? 1 : 0, RegistryValueKind.DWord);
                    if (import.Existed)
                    {
                        backup.SetValue($"{tag}.Kind", import.Kind.ToString(), RegistryValueKind.String);
                        backup.SetValue($"{tag}.Value", import.Value, RegistryValueKind.String);
                    }
                    backup.Flush();
                    backup.SetValue($"{tag}.Captured", 1, RegistryValueKind.DWord);
                }
                backup.SetValue("Snapshot.Complete", 1, RegistryValueKind.DWord);

                using RegistryKey marker = Registry.CurrentUser.CreateSubKey(
                    MigrationRoot,
                    writable: true);
                marker.SetValue(id, fingerprint, RegistryValueKind.String);
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException("The previous-app backup could not be read or validated. Defaults were not substituted; backup retained.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new InvalidOperationException("The previous-app backup could not be read or validated. Defaults were not substituted; backup retained.", exception);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("The previous-app backup could not be read or validated. Defaults were not substituted; backup retained.", exception);
            }
            catch (System.Security.SecurityException exception)
            {
                throw new InvalidOperationException("The previous-app backup could not be read or validated. Defaults were not substituted; backup retained.", exception);
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
                    key?.GetValue("Snapshot.Complete", 0) ?? 0,
                    CultureInfo.InvariantCulture) == 1;
                if (imported && !complete)
                {
                    DeleteBackup(id);
                }
            }
            catch
            {
                // Restore ignores the imported key unless every item was written.
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

        private static bool TryNormalizeCurrentUserPath(string? path, out string normalized)
        {
            const string prefix = @"HKCU:\";
            if (!string.IsNullOrWhiteSpace(path) &&
                path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = path[prefix.Length..];
                return normalized.Length > 0;
            }

            normalized = string.Empty;
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

        private static void RestoreWindowsDefault(DebloatDefinition definition)
        {
            PrepareSharedSnapshots(definition.Definition.Id, SharedSnapshotPreparation.Validate);
            if (definition.Definition.Id == "OneDriveAutoStartup")
            {
                string? executable = FindOneDriveExecutable();
                if (!string.IsNullOrWhiteSpace(executable))
                {
                    using RegistryKey run = Registry.CurrentUser.CreateSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run",
                        writable: true);
                    run.SetValue(
                        "OneDrive",
                        $"\"{executable}\" /background",
                        RegistryValueKind.String);
                }
                return;
            }

            foreach (DebloatSetting setting in definition.Settings)
            {
                DeleteCurrentUserValue(setting.Path, setting.Name);
            }
        }

        private static void Capture(string id, DebloatSetting setting)
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey(
                $@"{BackupRoot}\{id}",
                writable: true);
            string tag = MakeTag(setting);
            if (RegistrySnapshotCommit.IsCaptured(key => backup.GetValue(key), tag))
            {
                return;
            }

            using RegistryKey? source = Registry.CurrentUser.OpenSubKey(
                setting.Path,
                writable: false);
            RegistrySnapshotCommit.Capture(backup, tag, source, setting.Name, Serialize);
        }

        private static bool IsAvailable(DebloatDefinition definition)
        {
            if (definition.Definition.Id == "EdgeBackground")
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                using RegistryKey? edgePolicy = Registry.CurrentUser.OpenSubKey(@"Software\Policies\Microsoft\Edge");
                return edgePolicy is not null ||
                       CatalogAvailability.FileIsPresent(Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe")) ||
                       CatalogAvailability.FileIsPresent(Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"));
            }
            if (definition.Definition.Id == "OneDriveAutoStartup")
            {
                return ReadCurrentUserValue(
                           @"Software\Microsoft\Windows\CurrentVersion\Run",
                           "OneDrive") is not null ||
                       FindOneDriveExecutable() is not null;
            }
            return true;
        }

        private static string? FindOneDriveExecutable()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] candidates =
            {
                Path.Combine(local, "Microsoft", "OneDrive", "OneDrive.exe"),
                Path.Combine(programFiles, "Microsoft OneDrive", "OneDrive.exe"),
                Path.Combine(programFilesX86, "Microsoft OneDrive", "OneDrive.exe")
            };
            return candidates.FirstOrDefault(CatalogAvailability.FileIsPresent);
        }

        private static object? ReadCurrentUserValue(string path, string name)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        private static void DeleteCurrentUserValue(string path, string name)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }

        private static void DeleteBackup(string id)
        {
            try
            {
                using RegistryKey? root = Registry.CurrentUser.OpenSubKey(
                    BackupRoot,
                    writable: true);
                root?.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
                root?.Flush();
                SharedPrivacySnapshot.RetireUnused(id);
            }
            catch
            {
                // A stale backup is safer than deleting unrelated state.
            }
        }

        private static string MakeTag(DebloatSetting setting)
        {
            uint hash = 2166136261;
            foreach (char character in $"{setting.Path}|{setting.Name}")
            {
                hash ^= character;
                hash *= 16777619;
            }
            return hash.ToString("X8", CultureInfo.InvariantCulture);
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

        private static IReadOnlyDictionary<string, DebloatDefinition> CreateCatalog()
        {
            DebloatDefinition[] definitions =
            {
                Create(
                    "WindowsTips",
                    "Components",
                    "Windows Tips & Suggestions",
                    "Disables Windows tips, welcome suggestions, and setup recommendations without removing a component.",
                    Set(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SoftLandingEnabled", 0),
                    Set(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0),
                    Set(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-310093Enabled", 0),
                    Set(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0)),
                Create(
                    "StartRecommendations",
                    "Components",
                    "Start Menu Recommendations",
                    "Disables Start recommendations and account-notification suggestions.",
                    Set(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations", 0),
                    Set(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_AccountNotifications", 0)),
                Create(
                    "SearchHighlights",
                    "Components",
                    "Windows Search Highlights",
                    "Disables dynamic Search Highlights while keeping Windows Search and indexing enabled.",
                    Set(@"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0)),
                Create(
                    "AdvertisingId",
                    "Privacy",
                    "Advertising ID",
                    "Disables the current user's Windows advertising identifier.",
                    Set(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0)),
                Create(
                    "TailoredExperiences",
                    "Privacy",
                    "Tailored Experiences",
                    "Disables tailored experiences based on diagnostic data.",
                    Set(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0)),
                Create(
                    "SettingsSuggestedContent",
                    "Privacy",
                    "Settings Suggested Content",
                    "Disables promotional and suggested content in Windows Settings.",
                    Set(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338393Enabled", 0),
                    Set(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-353694Enabled", 0),
                    Set(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-353696Enabled", 0)),
                Create(
                    "EdgeBackground",
                    "Startup",
                    "Edge Startup Boost / Background Mode",
                    "Disables Edge Startup Boost and background mode; Edge and WebView2 remain installed.",
                    Set(@"Software\Policies\Microsoft\Edge", "StartupBoostEnabled", 0),
                    Set(@"Software\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0)),
                Create(
                    "OneDriveAutoStartup",
                    "Startup",
                    "OneDrive Auto-Startup",
                    "Disables OneDrive auto-start without uninstalling OneDrive or deleting synchronized files.",
                    Remove(@"Software\Microsoft\Windows\CurrentVersion\Run", "OneDrive"))
            };
            return definitions.ToDictionary(
                item => item.Definition.Id,
                StringComparer.OrdinalIgnoreCase);
        }

        private static DebloatDefinition Create(
            string id,
            string category,
            string name,
            string description,
            params DebloatSetting[] settings)
        {
            return new DebloatDefinition(
                new ToolToggleDefinition(
                    id,
                    category,
                    name,
                    description,
                    false,
                    false),
                settings);
        }

        private static DebloatSetting Set(string path, string name, int value) =>
            new(path, name, RegistryValueKind.DWord, false, value);

        private static DebloatSetting Remove(string path, string name) =>
            new(path, name, RegistryValueKind.String, true, null);

        private sealed record DebloatDefinition(
            ToolToggleDefinition Definition,
            IReadOnlyList<DebloatSetting> Settings);

        private sealed record DebloatSetting(
            string Path,
            string Name,
            RegistryValueKind Kind,
            bool Remove,
            object? Value);

        private sealed record ImportedRegistryValue(
            DebloatSetting Setting,
            bool Existed,
            RegistryValueKind Kind,
            string Value);
    }
}
