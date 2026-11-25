using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal sealed class GamingBcdService : IToolToggleService
    {
        private const string BackupRoot =
            @"Software\Naufal Windows Tech\Powertoys\Backups\GamingBcd";

        private static readonly IReadOnlyDictionary<string, BcdLab> Catalog =
            CreateCatalog().ToDictionary(item => item.Definition.Id, StringComparer.OrdinalIgnoreCase);

        private readonly NativeCommandRunner _commandRunner = new();

        public string DescribeRecovery(ToolToggleDefinition definition) =>
            CatalogRecovery.Snapshot($@"{BackupRoot}\{definition.Id}", "No current snapshot found. Restore checks the Windows boot default for this setting.");

        public IReadOnlyList<ToolToggleDefinition> GetDefinitions() =>
            Catalog.Values.Select(item => item.Definition).ToArray();

        public async Task<ToolToggleState> ReadStateAsync(ToolToggleDefinition definition)
        {
            try
            {
                BcdLab lab = Catalog[definition.Id];
                Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
                foreach (string target in lab.Settings.Select(setting => setting.Target).Distinct())
                {
                    foreach ((string name, string value) in await ReadTargetAsync(target))
                    {
                        values[$"{target}|{name}"] = value;
                    }
                }

                bool applied = true;
                bool anyApplied = false;
                List<string> actual = new();
                foreach (BcdSetting setting in lab.Settings)
                {
                    values.TryGetValue($"{setting.Target}|{setting.Option}", out string? value);
                    bool matches = BcdRestoreVerification.Matches(setting.Option, setting.Remove ? null : setting.Value, value);
                    applied &= matches;
                    anyApplied |= matches;
                    actual.Add($"{setting.Option}={(value ?? "<default/absent>")}");
                }

                return new ToolToggleState(applied, true, string.Join(", ", actual), HasAppliedParts: anyApplied);
            }
            catch (Exception exception)
            {
                return new ToolToggleState(false, false, "Unable to read BCD", exception.Message);
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
                BcdLab lab = Catalog[definition.Id];
                if (targetOn)
                {
                    await CaptureAsync(lab);
                    foreach (BcdSetting setting in lab.Settings)
                    {
                        await WriteSettingAsync(setting);
                    }
                }
                else
                {
                    using RegistryKey? snapshot = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{definition.Id}", writable: false);
                    if (snapshot is null)
                    {
                        ToolToggleOperationResult fallback = await RestoreWindowsDefaultAsync(definition);
                        return fallback with { Message = "No original backup was found. " + fallback.Message, DefaultFallbackHandled = true };
                    }
                    await RestoreAsync(lab);
                }

                ToolToggleState after = await ReadStateAsync(definition);
                // RestoreAsync verifies the saved values themselves; a saved
                // configuration may legitimately also match the applied state.
                bool verified = after.IsAvailable && (!targetOn || after.IsOn);
                return new ToolToggleOperationResult(
                    verified,
                    verified,
                    verified
                        ? $"{definition.Name} is now {(targetOn ? "APPLIED" : "RESTORED")}. Restart Windows before evaluating the result."
                        : $"BCD read-back did not match the requested state. Actual: {after.ActualValue}",
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
                BcdLab lab = Catalog[definition.Id];
                List<BcdSetting> defaults = new();
                if (definition.Id.Equals("BcdBootTimeout1", StringComparison.OrdinalIgnoreCase))
                {
                    defaults.Add(new BcdSetting(
                        "{bootmgr}",
                        "timeout",
                        "30",
                        Remove: false));
                }
                else
                {
                    foreach (BcdSetting setting in lab.Settings)
                    {
                        defaults.Add(setting with
                        {
                            Value = string.Empty,
                            Remove = true
                        });
                    }
                }

                foreach (BcdSetting setting in defaults) await WriteSettingAsync(setting);
                await VerifySettingsAsync(defaults);

                using (RegistryKey? root = Registry.CurrentUser.OpenSubKey(
                           BackupRoot,
                           writable: true))
                {
                    root?.DeleteSubKeyTree(
                        lab.Definition.Id,
                        throwOnMissingSubKey: false);
                }

                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable;
                return new ToolToggleOperationResult(
                    verified,
                    verified,
                    verified
                        ? $"{definition.Name} was returned to the Windows default."
                        : $"Windows-default BCD read-back failed. Actual: {after.ActualValue}",
                    after);
            }
            catch (Exception exception)
            {
                ToolToggleState state = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(false, false, exception.Message, state);
            }
        }

        private async Task CaptureAsync(BcdLab lab)
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey(
                $@"{BackupRoot}\{lab.Definition.Id}",
                writable: true);
            if (Convert.ToInt32(
                    backup.GetValue("Snapshot.Captured", 0),
                    CultureInfo.InvariantCulture) == 1)
            {
                return;
            }

            Dictionary<string, Dictionary<string, string>> current =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (string target in lab.Settings.Select(setting => setting.Target).Distinct())
            {
                current[target] = await ReadTargetAsync(target);
            }

            backup.SetValue("Snapshot.Count", lab.Settings.Count, RegistryValueKind.DWord);
            for (int index = 0; index < lab.Settings.Count; index++)
            {
                BcdSetting setting = lab.Settings[index];
                string prefix = $"Item.{index}.";
                current[setting.Target].TryGetValue(setting.Option, out string? value);
                backup.SetValue(prefix + "Target", setting.Target, RegistryValueKind.String);
                backup.SetValue(prefix + "Option", setting.Option, RegistryValueKind.String);
                backup.SetValue(prefix + "Exists", value is null ? 0 : 1, RegistryValueKind.DWord);
                backup.SetValue(prefix + "Value", value ?? string.Empty, RegistryValueKind.String);
            }
            backup.SetValue("Snapshot.Captured", 1, RegistryValueKind.DWord);
        }

        private async Task RestoreAsync(BcdLab lab)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                $@"{BackupRoot}\{lab.Definition.Id}",
                writable: false);
            if (backup is null ||
                Convert.ToInt32(
                    backup.GetValue("Snapshot.Captured", 0),
                    CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException(
                    "No original BCD snapshot exists. Apply this option once before using Restore.");
            }

            int count = Convert.ToInt32(
                backup.GetValue("Snapshot.Count", -1),
                CultureInfo.InvariantCulture);
            if (count != lab.Settings.Count)
                throw new InvalidOperationException("The saved BCD snapshot is incomplete. No BCD value was changed.");
            List<BcdSetting> saved = new();
            for (int index = 0; index < count; index++)
            {
                string prefix = $"Item.{index}.";
                string target = Convert.ToString(
                    backup.GetValue(prefix + "Target"),
                    CultureInfo.InvariantCulture) ?? "{current}";
                string option = Convert.ToString(
                    backup.GetValue(prefix + "Option"),
                    CultureInfo.InvariantCulture) ?? string.Empty;
                int existsFlag = Convert.ToInt32(backup.GetValue(prefix + "Exists", -1), CultureInfo.InvariantCulture);
                bool existed = existsFlag == 1;
                string value = Convert.ToString(
                    backup.GetValue(prefix + "Value"),
                    CultureInfo.InvariantCulture) ?? string.Empty;
                if (existsFlag is not (0 or 1) ||
                    !string.Equals(target, lab.Settings[index].Target, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(option, lab.Settings[index].Option, StringComparison.OrdinalIgnoreCase) ||
                    (existed && string.IsNullOrWhiteSpace(value)))
                {
                    throw new InvalidOperationException("The saved BCD snapshot is incomplete.");
                }

                saved.Add(new BcdSetting(
                    target,
                    option,
                    value,
                    Remove: !existed));
            }

            foreach (BcdSetting setting in saved) await WriteSettingAsync(setting);
            await VerifySettingsAsync(saved);

            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(BackupRoot, writable: true);
            root?.DeleteSubKeyTree(lab.Definition.Id, throwOnMissingSubKey: false);
        }

        private async Task VerifySettingsAsync(IReadOnlyList<BcdSetting> settings)
        {
            foreach (string target in settings.Select(setting => setting.Target).Distinct())
            {
                var values = await ReadTargetAsync(target);
                foreach (BcdSetting setting in settings.Where(setting => setting.Target == target))
                {
                    values.TryGetValue(setting.Option, out string? actual);
                    if (!BcdRestoreVerification.Matches(setting.Option, setting.Remove ? null : setting.Value, actual))
                        throw new InvalidOperationException($"BCD verification failed for {setting.Option}. " +
                            $"Actual: {actual ?? "<absent>"}. The original snapshot has been retained.");
                }
            }
        }

        private async Task WriteSettingAsync(BcdSetting setting)
        {
            string? expected = setting.Remove ? null : setting.Value;
            var before = await ReadTargetAsync(setting.Target);
            before.TryGetValue(setting.Option, out string? previous);
            if (BcdRestoreVerification.Matches(setting.Option, expected, previous)) return;
            IReadOnlyList<string> arguments = setting.Remove
                ? new[] { "/deletevalue", setting.Target, setting.Option }
                : new[] { "/set", setting.Target, setting.Option, setting.Value };
            NativeCommandResult result = await _commandRunner.RunAsync(
                "bcdedit.exe",
                arguments,
                TimeSpan.FromSeconds(20));
            var after = await ReadTargetAsync(setting.Target);
            after.TryGetValue(setting.Option, out string? actual);
            if (BcdRestoreVerification.Matches(setting.Option, expected, actual)) return;
            throw new InvalidOperationException(
                $"BCD write/read-back failed for {setting.Option} (exit {result.ExitCode}). Backup retained. {result.CombinedOutput}");
        }

        private async Task<Dictionary<string, string>> ReadTargetAsync(string target)
        {
            NativeCommandResult result = await _commandRunner.RunAsync(
                "bcdedit.exe",
                new[] { "/enum", target },
                TimeSpan.FromSeconds(15));
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.CombinedOutput)
                        ? $"Unable to read BCD target {target}."
                        : result.CombinedOutput);
            }

            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            foreach (string line in result.StandardOutput.Split(
                         new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                int separator = trimmed.IndexOf(' ');
                if (separator <= 0)
                {
                    continue;
                }
                string option = trimmed[..separator].Trim();
                string value = trimmed[separator..].Trim();
                if (!string.IsNullOrWhiteSpace(option) && !string.IsNullOrWhiteSpace(value))
                {
                    values[option] = value;
                }
            }
            return values;
        }

        private static IReadOnlyList<BcdLab> CreateCatalog() => new[]
        {
            Lab("BcdTscSyncEnhanced", "TSC Sync Policy - Enhanced", "High",
                "Sets the active Windows boot entry to tscsyncpolicy=Enhanced, requesting enhanced Time Stamp Counter synchronization for controlled timing tests. Results are hardware/firmware dependent and require a restart.",
                Set("tscsyncpolicy", "Enhanced")),
            Lab("BcdHypervisorOff", "Hypervisor Launch - OFF", "High",
                "Disables hypervisor launch. Hyper-V, VBS, Windows Sandbox, and dependent features stop working until Restore and restart.",
                Set("hypervisorlaunchtype", "Off")),
            Lab("BcdX2ApicEnable", "x2APIC Policy - Enable", "Legacy",
                "Forces the active Windows boot entry to request x2APIC mode. Firmware, hypervisor, and processor support determine the actual result; use this legacy control only for compatibility testing and restart afterward.",
                Set("x2apicpolicy", "Enable")),
            Lab("BcdPaeForceEnable", "PAE - ForceEnable", "Legacy",
                "Forces the legacy Physical Address Extension boot policy. It normally provides no gaming benefit on modern 64-bit Windows and is retained only for controlled legacy compatibility testing.",
                Set("pae", "ForceEnable")),
            Lab("BcdDebugOff", "Kernel Debug + Boot Debug - OFF", "Safe",
                "Explicitly disables kernel debugging and boot debugging for the active Windows boot entry. This removes debugging startup paths but prevents kernel/boot debugger attachment until the captured BCD values are restored.",
                Set("debug", "No"),
                Set("bootdebug", "Off")),
            Lab("BcdSosYes", "SOS Boot Driver Messages - ON", "Legacy",
                "Enables SOS boot output so Windows displays driver names while loading. This is a diagnostic display option rather than a performance optimization and takes effect after restart.",
                Set("sos", "Yes")),
            Lab("BcdHighestMode", "Highest Mode + Remove numproc Cap", "Legacy",
                "Sets highestmode=yes and removes any explicit numproc cap from the active boot entry. Windows can then enumerate available processors normally; the legacy highest-mode flag may be ignored on modern systems.",
                Set("highestmode", "Yes"),
                Remove("numproc")),
            new BcdLab(
                new ToolToggleDefinition(
                    "BcdBootTimeout1",
                    "Boot / BCD / Legacy",
                    "Boot Manager Timeout - 1 second",
                    "Sets only the Windows Boot Manager menu timeout to one second. Multi-boot users will have very little time to select another operating system; the captured timeout is restored by Restore.",
                    true,
                    true),
                new[] { new BcdSetting("{bootmgr}", "timeout", "1", false) })
        };

        private static BcdLab Lab(
            string id,
            string name,
            string risk,
            string description,
            params BcdSetting[] settings) =>
            new(
                new ToolToggleDefinition(
                    id,
                    $"Boot / BCD / {(risk == "High" ? "HIGH RISK" : risk)}",
                    name,
                    description,
                    true,
                    true),
                settings);

        private static BcdSetting Set(string option, string value) =>
            new("{current}", option, value, false);

        private static BcdSetting Remove(string option) =>
            new("{current}", option, string.Empty, true);

        private sealed record BcdLab(
            ToolToggleDefinition Definition,
            IReadOnlyList<BcdSetting> Settings);

        private readonly record struct BcdSetting(
            string Target,
            string Option,
            string Value,
            bool Remove);
    }
}
