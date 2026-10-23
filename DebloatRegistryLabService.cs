using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal sealed partial class DebloatRegistryLabService : IToolToggleService, ICatalogEffectSource
    {
        private const string BackupRoot =
            @"Software\Naufal Windows Tech\Powertoys\Backups\DebloatRegistryLab";

        private static readonly IReadOnlyDictionary<string, RegistryLab> Catalog =
            CreateCatalog().Concat(CreatePrivacyCatalog()).ToDictionary(item => item.Definition.Id, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<ToolToggleDefinition> GetDefinitions() =>
            Catalog.Values.Select(item => item.Definition).ToArray();

        public IReadOnlyList<CatalogEffect> GetEffects(ToolToggleDefinition definition) => Catalog[definition.Id].Settings
            .Select(s => CatalogEffect.Registry(s.Hive.ToString(), s.Path, s.Name, s.Kind.ToString(), s.Value)).ToArray();

        public Task<ToolToggleState> ReadStateAsync(ToolToggleDefinition definition)
        {
            try
            {
                RegistryLab lab = Catalog[definition.Id];
                if (PrivacyUnavailable(definition.Id) is string unavailable)
                    return Task.FromResult(ToolToggleState.Unavailable(unavailable));
                if (definition.Id == "NvidiaOverlay")
                {
                    using RegistryKey? nvidiaKey = OpenKey(
                        RegistryHive.LocalMachine,
                        lab.Settings[0].Path,
                        writable: false);
                    if (nvidiaKey is null)
                    {
                        return Task.FromResult(new ToolToggleState(
                            false,
                            false,
                            "NVIDIA ShadowPlay key is absent",
                            "This NVIDIA overlay registry target is not present on this PC.", UnavailableOnThisPc: true));
                    }
                }

                bool applied = true;
                bool anyApplied = false;
                List<string> actual = new();
                foreach (RegistryLabSetting setting in lab.Settings)
                {
                    object? value = ReadValue(setting.Hive, setting.Path, setting.Name);
                    bool matches = value is not null && string.Equals(
                        Convert.ToString(value, CultureInfo.InvariantCulture),
                        Convert.ToString(setting.Value, CultureInfo.InvariantCulture),
                        StringComparison.Ordinal);
                    applied &= matches;
                    anyApplied |= matches;
                    actual.Add($"{setting.Name}={Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<absent>"}");
                }
                return Task.FromResult(new ToolToggleState(applied, true, string.Join(", ", actual), HasAppliedParts: anyApplied));
            }
            catch (Exception exception)
            {
                return Task.FromResult(new ToolToggleState(false, false, "Unable to read", exception.Message));
            }
        }

        public async Task<ToolToggleOperationResult> SetStateAsync(
            ToolToggleDefinition definition,
            bool targetOn)
        {
            if (definition.RequiresAdministrator && !WindowsPrivilegeService.IsAdministrator())
            {
                ToolToggleState denied = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(false, false, "Administrator rights are required.", denied);
            }

            try
            {
                RegistryLab lab = Catalog[definition.Id];
                bool usedSnapshot = false;
                if (PrivacyUnavailable(definition.Id) is string unavailable)
                    return new(false, false, unavailable, ToolToggleState.Unavailable(unavailable), SkippedUnavailable: true);
                if (targetOn)
                {
                    Apply(lab);
                }
                else
                {
                    usedSnapshot = Restore(lab);
                }

                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable && (targetOn
                    ? after.IsOn
                    : usedSnapshot
                        ? VerifySavedState(lab)
                        : VerifyWindowsDefaults(lab));
                if (!targetOn && verified)
                {
                    DeleteBackup(lab.Definition.Id);
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
                ToolToggleState state = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(false, false, exception.Message, state);
            }
        }

        public Task<ToolToggleOperationResult> RestoreOriginalAsync(
            ToolToggleDefinition definition) =>
            SetStateAsync(definition, targetOn: false);

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
                RegistryLab lab = Catalog[definition.Id];
                if (PrivacyUnavailable(definition.Id) is string unavailable)
                    return new(false, false, unavailable, ToolToggleState.Unavailable(unavailable), SkippedUnavailable: true);
                RestoreWindowsDefaults(lab);
                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable && VerifyWindowsDefaults(lab);
                if (verified)
                {
                    DeleteBackup(lab.Definition.Id);
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
                ToolToggleState state = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(false, false, exception.Message, state);
            }
        }

        private static void Apply(RegistryLab lab)
        {
            if (lab.Definition.Id == "PaintAI") DebloatJournal.RequireNoGranularAi();
            // Capture the entire plan before the first write. A mid-operation
            // error must not turn an already-mutated value into a new baseline.
            foreach (RegistryLabSetting setting in lab.Settings) Capture(lab.Definition.Id, setting);
            foreach (RegistryLabSetting setting in lab.Settings)
            {
                using RegistryKey key = CreateKey(setting.Hive, setting.Path);
                key.SetValue(setting.Name, setting.Value, setting.Kind);
            }
        }

        private static bool Restore(RegistryLab lab)
        {
            if (lab.Definition.Id == "PaintAI") DebloatJournal.RequireNoGranularAi();
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                $@"{BackupRoot}\{lab.Definition.Id}",
                writable: false);
            bool restoredSnapshot = backup is not null &&
                RegistrySnapshotCommit.HasCompleteSet(
                    key => backup.GetValue(key),
                    lab.Settings.Select(MakeTag),
                    lab.Definition.Name);
            if (backup is not null)
            {
                RegistryRestorePlan.RequireTags(key => backup.GetValue(key), lab.Settings.Select(MakeTag), Deserialize);
                restoredSnapshot = true;
            }
            if (!restoredSnapshot)
            {
                RestoreWindowsDefaults(lab);
                return false;
            }

            foreach (RegistryLabSetting setting in lab.Settings)
            {
                string tag = MakeTag(setting);
                bool existed = Convert.ToInt32(
                    backup!.GetValue($"{tag}.Exists", 0),
                    CultureInfo.InvariantCulture) == 1;
                if (!existed)
                {
                    using RegistryKey? destination = OpenKey(setting.Hive, setting.Path, writable: true);
                    destination?.DeleteValue(setting.Name, throwOnMissingValue: false);
                    continue;
                }

                string kindText = Convert.ToString(
                    backup!.GetValue($"{tag}.Kind"),
                    CultureInfo.InvariantCulture) ?? RegistryValueKind.String.ToString();
                RegistryValueKind kind = Enum.TryParse(kindText, out RegistryValueKind parsed)
                    ? parsed
                    : RegistryValueKind.String;
                string value = Convert.ToString(
                    backup!.GetValue($"{tag}.Value"),
                    CultureInfo.InvariantCulture) ?? string.Empty;
                using RegistryKey key = CreateKey(setting.Hive, setting.Path);
                key.SetValue(setting.Name, Deserialize(value, kind), kind);
            }

            return true;
        }

        private static bool VerifySavedState(RegistryLab lab)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                $@"{BackupRoot}\{lab.Definition.Id}",
                writable: false);
            if (backup is null)
            {
                return false;
            }

            foreach (RegistryLabSetting setting in lab.Settings)
            {
                string tag = MakeTag(setting);
                using RegistryKey? source = OpenKey(
                    setting.Hive,
                    setting.Path,
                    writable: false);
                object? actual = source?.GetValue(
                    setting.Name,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                RegistryValueKind? kind = actual is null
                    ? null
                    : source!.GetValueKind(setting.Name);
                try
                {
                    RegistrySnapshotCommit.RequireRestored(
                        key => backup.GetValue(key),
                        tag,
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

        private static bool VerifyWindowsDefaults(RegistryLab lab)
        {
            string[] accessibilityDefaults = { "510", "62", "126" };
            for (int index = 0; index < lab.Settings.Count; index++)
            {
                RegistryLabSetting setting = lab.Settings[index];
                using RegistryKey? key = OpenKey(setting.Hive, setting.Path, writable: false);
                object? actual = key?.GetValue(setting.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (lab.Definition.Id == "StickyKeysHotkeysOff")
                {
                    if (actual is not string text || text != accessibilityDefaults[index] ||
                        key!.GetValueKind(setting.Name) != RegistryValueKind.String) return false;
                }
                else if (actual is not null) return false;
            }
            return true;
        }

        private static void RestoreWindowsDefaults(RegistryLab lab)
        {
            if (lab.Definition.Id == "PaintAI") DebloatJournal.RequireNoGranularAi();
            if (lab.Definition.Id == "StickyKeysHotkeysOff")
            {
                string[] defaults = { "510", "62", "126" };
                for (int index = 0; index < lab.Settings.Count; index++)
                {
                    RegistryLabSetting setting = lab.Settings[index];
                    using RegistryKey key = CreateKey(setting.Hive, setting.Path);
                    key.SetValue(setting.Name, defaults[index], RegistryValueKind.String);
                }
                return;
            }

            foreach (RegistryLabSetting setting in lab.Settings)
            {
                using RegistryKey? key = OpenKey(setting.Hive, setting.Path, writable: true);
                key?.DeleteValue(setting.Name, throwOnMissingValue: false);
            }
        }

        private static void Capture(string id, RegistryLabSetting setting)
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey($@"{BackupRoot}\{id}", writable: true);
            string tag = MakeTag(setting);
            if (RegistrySnapshotCommit.IsCaptured(key => backup.GetValue(key), tag))
            {
                return;
            }

            using RegistryKey? source = OpenKey(setting.Hive, setting.Path, writable: false);
            RegistrySnapshotCommit.Capture(backup, tag, source, setting.Name, Serialize);
        }

        private static object? ReadValue(RegistryHive hive, string path, string name)
        {
            using RegistryKey? key = OpenKey(hive, path, writable: false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        private static RegistryKey? OpenKey(RegistryHive hive, string path, bool writable)
        {
            RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            RegistryKey? key = baseKey.OpenSubKey(path, writable);
            baseKey.Dispose();
            return key;
        }

        private static RegistryKey CreateKey(RegistryHive hive, string path)
        {
            RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            RegistryKey key = baseKey.CreateSubKey(path, writable: true);
            baseKey.Dispose();
            return key;
        }

        private static void DeleteBackup(string id)
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(BackupRoot, writable: true);
            root?.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
        }

        private static string MakeTag(RegistryLabSetting setting)
        {
            uint hash = 2166136261;
            foreach (char character in $"{setting.Hive}|{setting.Path}|{setting.Name}")
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

        private static IReadOnlyList<RegistryLab> CreateCatalog() => new[]
        {
            Lab("NvidiaOverlay", "Startup / Safe", "NVIDIA ShadowPlay / Overlay",
                "Disables the NVIDIA ShadowPlay/overlay registry preference when present.", true, false,
                Dword(RegistryHive.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\ShadowPlay\NVSPCAPS", "Enable", 0)),
            Lab("TaskbarChatOff", "Components / Safe", "Taskbar Chat / Teams Icon",
                "Hides the legacy Taskbar Chat/Teams button.", false, false,
                Dword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarMn", 0)),
            Lab("TaskViewButtonOff", "Components / Safe", "Task View Button",
                "Hides the Task View button without removing virtual desktop support.", false, false,
                Dword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowTaskViewButton", 0)),
            Lab("NoNetCrawling", "Startup / Safe", "Explorer Network Folder Auto-Crawl",
                "Sets NoNetCrawling=1 to reduce Explorer background network discovery.", false, false,
                Dword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoNetCrawling", 1)),
            Lab("StickyKeysHotkeysOff", "System / HIGH RISK", "Sticky / Toggle / Filter Keys Shortcut Popups",
                "Suppresses keyboard shortcut prompts while leaving accessibility features available in Settings.", false, false,
                Text(RegistryHive.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", "506"),
                Text(RegistryHive.CurrentUser, @"Control Panel\Accessibility\ToggleKeys", "Flags", "58"),
                Text(RegistryHive.CurrentUser, @"Control Panel\Accessibility\Keyboard Response", "Flags", "122")),
            Lab("ModernStandbyOverride", "System / HIGH RISK", "Modern Standby Platform Override",
                "Sets PlatformAoAcOverride=0. Firmware-dependent and may affect sleep/lid-close behavior.", true, true,
                Dword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "PlatformAoAcOverride", 0)),
            Lab("TpmCpuBypass", "System / Legacy", "Windows 11 Unsupported TPM/CPU Upgrade Bypass",
                "Stores the three setup compatibility bypass values used by Windows Setup.", true, false,
                Dword(RegistryHive.LocalMachine, @"SYSTEM\Setup\MoSetup", "AllowUpgradesWithUnsupportedTPMOrCPU", 1),
                Dword(RegistryHive.LocalMachine, @"SYSTEM\Setup\LabConfig", "BypassTPMCheck", 1),
                Dword(RegistryHive.LocalMachine, @"SYSTEM\Setup\LabConfig", "BypassSecureBootCheck", 1))
        };

        private static RegistryLab Lab(
            string id,
            string category,
            string name,
            string description,
            bool requiresAdministrator,
            bool restartRecommended,
            params RegistryLabSetting[] settings) =>
            new(
                new ToolToggleDefinition(
                    id,
                    category,
                    name,
                    description,
                    requiresAdministrator,
                    restartRecommended),
                settings);

        private static RegistryLabSetting Dword(
            RegistryHive hive,
            string path,
            string name,
            int value) =>
            new(hive, path, name, RegistryValueKind.DWord, value);

        private static RegistryLabSetting Text(
            RegistryHive hive,
            string path,
            string name,
            string value) =>
            new(hive, path, name, RegistryValueKind.String, value);

        private sealed record RegistryLab(
            ToolToggleDefinition Definition,
            IReadOnlyList<RegistryLabSetting> Settings);

        private sealed record RegistryLabSetting(
            RegistryHive Hive,
            string Path,
            string Name,
            RegistryValueKind Kind,
            object Value);
    }
}
