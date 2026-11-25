using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal sealed class DebloatNetworkStorageService : IToolToggleService
    {
        private const string BackupRoot =
            @"Software\Naufal Windows Tech\Powertoys\Backups\DebloatNetworkStorage";
        private const string StorageSensePath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy";
        private const string ReserveManagerPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager";

        private static readonly string[] StorageSenseTasks =
        {
            @"\Microsoft\Windows\DiskFootprint\StorageSense",
            @"\Microsoft\Windows\DiskFootprint\Storage Sense"
        };

        private readonly NativeCommandRunner _commandRunner = new();

        private static readonly IReadOnlyList<ToolToggleDefinition> Definitions =
            new[]
            {
                new ToolToggleDefinition(
                    "Teredo",
                    "Network / Experimental",
                    "Teredo Tunneling",
                    "Disables the Teredo IPv6 transition tunnel. OFF returns the netsh Teredo state to Windows default.",
                    true,
                    false),
                new ToolToggleDefinition(
                    "StorageSense",
                    "Storage / Experimental",
                    "Storage Sense",
                    "Disables automatic Storage Sense cleanup and known DiskFootprint Storage Sense tasks; exact master/task state is captured for Restore.",
                    true,
                    false),
                new ToolToggleDefinition(
                    "ReservedStorage",
                    "Storage / HIGH RISK",
                    "Windows Reserved Storage",
                    "Disables Windows Reserved Storage through DISM where supported. This reduces the servicing storage safety margin.",
                    true,
                    true)
            };

        public string DescribeRecovery(ToolToggleDefinition definition) =>
            CatalogRecovery.Snapshot($@"{BackupRoot}\{definition.Id}", "No current snapshot found. Restore uses the Windows default for this network or storage setting.");

        public IReadOnlyList<ToolToggleDefinition> GetDefinitions() => Definitions;

        public async Task<ToolToggleState> ReadStateAsync(ToolToggleDefinition definition)
        {
            try
            {
                return definition.Id switch
                {
                    "Teredo" => await ReadTeredoStateAsync(),
                    "StorageSense" => ReadStorageSenseState(),
                    "ReservedStorage" => await ReadReservedStorageStateAsync(),
                    _ => new ToolToggleState(false, false, "Unknown definition", definition.Id)
                };
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
                return new ToolToggleOperationResult(false, false, "Administrator rights are required.", denied);
            }

            try
            {
                if (targetOn)
                {
                    await ApplyAsync(definition.Id);
                }
                else
                {
                    using RegistryKey? snapshot = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{definition.Id}", writable: false);
                    if (snapshot is null)
                    {
                        ToolToggleOperationResult fallback = await RestoreWindowsDefaultAsync(definition);
                        return fallback with { Message = "No original backup was found. " + fallback.Message, DefaultFallbackHandled = true };
                    }
                    await RestoreAsync(definition.Id);
                }

                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = definition.Id == "Teredo"
                    ? TeredoConfiguration.MatchesTarget(after, targetOn)
                    : after.IsAvailable && (!targetOn || after.IsOn);
                if (!targetOn && verified)
                {
                    DeleteBackup(definition.Id);
                }
                return new ToolToggleOperationResult(
                    verified,
                    verified,
                    verified
                        ? $"{definition.Name} is now {(targetOn ? "DISABLED" : "RESTORED")}."
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
                switch (definition.Id)
                {
                    case "Teredo":
                        await RequireCommandSuccessAsync(
                            "netsh.exe",
                            new[] { "interface", "teredo", "set", "state", "default" },
                            TimeSpan.FromSeconds(25));
                        break;
                    case "StorageSense":
                        using (RegistryKey? key = OpenKey(
                                   RegistryHive.CurrentUser,
                                   StorageSensePath,
                                   writable: true))
                        {
                            key?.DeleteValue("01", throwOnMissingValue: false);
                        }
                        break;
                    case "ReservedStorage":
                        NativeCommandResult enable = await _commandRunner.RunAsync(
                            "dism.exe",
                            new[] { "/English", "/Online", "/Set-ReservedStorageState", "/State:Enabled" },
                            TimeSpan.FromMinutes(3));
                        if (enable.ExitCode != 0)
                        {
                            SetDword(
                                RegistryHive.LocalMachine,
                                ReserveManagerPath,
                                "ShippedWithReserves",
                                1);
                        }
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported network/storage tweak: {definition.Id}");
                }

                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = definition.Id == "Teredo"
                    ? TeredoConfiguration.MatchesTarget(after, disabled: false)
                    : after.IsAvailable && !after.IsOn;
                if (verified)
                {
                    DeleteBackup(definition.Id);
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

        private async Task ApplyAsync(string id)
        {
            switch (id)
            {
                case "Teredo":
                    await RequireCommandSuccessAsync(
                        "netsh.exe",
                        new[] { "interface", "teredo", "set", "state", "disabled" },
                        TimeSpan.FromSeconds(25));
                    break;

                case "StorageSense":
                    CaptureRegistryValue(id, "Master", RegistryHive.CurrentUser, StorageSensePath, "01");
                    SetDword(RegistryHive.CurrentUser, StorageSensePath, "01", 0);
                    foreach (string taskName in StorageSenseTasks)
                    {
                        bool? enabled = await ReadScheduledTaskEnabledAsync(taskName);
                        CaptureTaskState(id, taskName, enabled);
                        if (enabled.HasValue)
                        {
                            await _commandRunner.RunAsync(
                                "schtasks.exe",
                                new[] { "/Change", "/TN", taskName, "/DISABLE" },
                                TimeSpan.FromSeconds(20));
                        }
                    }
                    break;

                case "ReservedStorage":
                    ReservedStorageState state = await QueryReservedStorageAsync();
                    CaptureText(id, "OriginalState", state.ToString());
                    CaptureRegistryValue(id, "ShippedWithReserves", RegistryHive.LocalMachine, ReserveManagerPath, "ShippedWithReserves");
                    NativeCommandResult disable = await _commandRunner.RunAsync(
                        "dism.exe",
                        new[] { "/English", "/Online", "/Set-ReservedStorageState", "/State:Disabled" },
                        TimeSpan.FromMinutes(3));
                    if (disable.ExitCode != 0)
                    {
                        SetDword(RegistryHive.LocalMachine, ReserveManagerPath, "ShippedWithReserves", 0);
                    }
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported network/storage tweak: {id}");
            }
        }

        private async Task RestoreAsync(string id)
        {
            if (id != "Teredo")
            {
                using RegistryKey? backup = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{id}", writable: false);
                RegistryRestorePlan.RequireTags(key => backup?.GetValue(key),
                    new[] { id == "StorageSense" ? "Master" : "ShippedWithReserves" }, Deserialize);
            }
            switch (id)
            {
                case "Teredo":
                    await RequireCommandSuccessAsync(
                        "netsh.exe",
                        new[] { "interface", "teredo", "set", "state", "default" },
                        TimeSpan.FromSeconds(25));
                    break;

                case "StorageSense":
                    EnsureBackupExists(id);
                    foreach (string taskName in StorageSenseTasks) _ = ReadCapturedTaskState(id, taskName);
                    RestoreRegistryValue(id, "Master", RegistryHive.CurrentUser, StorageSensePath, "01");
                    foreach (string taskName in StorageSenseTasks)
                    {
                        bool? enabled = ReadCapturedTaskState(id, taskName);
                        if (!enabled.HasValue)
                        {
                            continue;
                        }
                        await RequireCommandSuccessAsync(
                            "schtasks.exe",
                            new[] { "/Change", "/TN", taskName, enabled.Value ? "/ENABLE" : "/DISABLE" },
                            TimeSpan.FromSeconds(20));
                        if (await ReadScheduledTaskEnabledAsync(taskName) != enabled)
                            throw new InvalidOperationException("Storage Sense task restore did not verify. Backup retained.");
                    }
                    break;

                case "ReservedStorage":
                    EnsureBackupExists(id);
                    string original = ReadCapturedText(id, "OriginalState");
                    if (Enum.TryParse(original, out ReservedStorageState state) &&
                        state is ReservedStorageState.Enabled or ReservedStorageState.Disabled)
                    {
                        await RequireCommandSuccessAsync(
                            "dism.exe",
                            new[]
                            {
                                "/English",
                                "/Online",
                                "/Set-ReservedStorageState",
                                state == ReservedStorageState.Enabled ? "/State:Enabled" : "/State:Disabled"
                            },
                            TimeSpan.FromMinutes(3));
                        if (await QueryReservedStorageAsync() != state)
                            throw new InvalidOperationException("Reserved Storage restore did not verify. Backup retained.");
                    }
                    else throw new InvalidOperationException("The saved Reserved Storage state is invalid. Backup retained.");
                    RestoreRegistryValue(id, "ShippedWithReserves", RegistryHive.LocalMachine, ReserveManagerPath, "ShippedWithReserves");
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported network/storage tweak: {id}");
            }
        }

        private async Task<ToolToggleState> ReadTeredoStateAsync()
        {
            // Default policy can legitimately yield an inactive/disabled tunnel.
            // Verify the configured value, not localized operational netsh output.
            var rows = await Task.Run(() => NativeHardwareData.Query(
                @"ROOT\StandardCimv2", "MSFT_NetTeredoConfiguration", "Type", "PolicyStore"));
            return TeredoConfiguration.ToState(TeredoConfiguration.ReadActiveType(rows));
        }

        private static ToolToggleState ReadStorageSenseState()
        {
            int? value = ReadDword(RegistryHive.CurrentUser, StorageSensePath, "01");
            return new ToolToggleState(
                value == 0,
                true,
                value.HasValue ? $"StoragePolicy 01={value}" : "StoragePolicy 01=<absent>");
        }

        private async Task<ToolToggleState> ReadReservedStorageStateAsync()
        {
            ReservedStorageState state = await QueryReservedStorageAsync();
            int? fallback = ReadDword(
                RegistryHive.LocalMachine,
                ReserveManagerPath,
                "ShippedWithReserves");
            bool available = state != ReservedStorageState.Unsupported || fallback.HasValue;
            bool disabled = state == ReservedStorageState.Disabled || fallback == 0;
            return new ToolToggleState(
                disabled,
                available,
                $"DISM={state}, ShippedWithReserves={FormatNullable(fallback)}",
                available ? string.Empty : "Reserved Storage control is not available on this Windows build.");
        }

        private async Task<ReservedStorageState> QueryReservedStorageAsync()
        {
            NativeCommandResult result = await _commandRunner.RunAsync(
                "dism.exe",
                new[] { "/English", "/Online", "/Get-ReservedStorageState" },
                TimeSpan.FromSeconds(50));
            if (result.ExitCode != 0)
            {
                return ReservedStorageState.Unsupported;
            }
            if (result.StandardOutput.Contains("disabled", StringComparison.OrdinalIgnoreCase))
            {
                return ReservedStorageState.Disabled;
            }
            if (result.StandardOutput.Contains("enabled", StringComparison.OrdinalIgnoreCase))
            {
                return ReservedStorageState.Enabled;
            }
            return ReservedStorageState.Unknown;
        }

        private async Task<bool?> ReadScheduledTaskEnabledAsync(string taskName)
        {
            NativeCommandResult result = await _commandRunner.RunAsync(
                "schtasks.exe",
                new[] { "/Query", "/TN", taskName, "/XML" },
                TimeSpan.FromSeconds(20));
            if (result.ExitCode != 0)
            {
                return null;
            }
            if (result.StandardOutput.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (result.StandardOutput.Contains("<Enabled>true</Enabled>", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return null;
        }

        private async Task RequireCommandSuccessAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout)
        {
            NativeCommandResult result = await _commandRunner.RunAsync(executable, arguments, timeout);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.CombinedOutput)
                        ? $"{executable} exited with code {result.ExitCode}."
                        : result.CombinedOutput);
            }
        }

        private static void CaptureRegistryValue(
            string id,
            string tag,
            RegistryHive hive,
            string path,
            string name)
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey($@"{BackupRoot}\{id}", writable: true);
            if (RegistrySnapshotCommit.IsCaptured(key => backup.GetValue(key), tag))
            {
                return;
            }
            using RegistryKey? source = OpenKey(hive, path, writable: false);
            RegistrySnapshotCommit.Capture(backup, tag, source, name, Serialize);
        }

        private static void RestoreRegistryValue(string id, string tag, RegistryHive hive, string path, string name)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{id}", writable: false);
            if (backup is null) throw new InvalidOperationException("The original snapshot is missing. Backup retained.");
            RegistryRestorePlan.RestoreTagged(key => backup.GetValue(key), tag, new(hive, path, name), Deserialize);
        }

        private static void CaptureTaskState(string id, string taskName, bool? enabled)
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey($@"{BackupRoot}\{id}", writable: true);
            string tag = TaskTag(taskName);
            if (backup.GetValue($"{tag}.Captured") is null)
            {
                backup.SetValue($"{tag}.State", enabled.HasValue ? (enabled.Value ? 1 : 0) : -1, RegistryValueKind.DWord);
                backup.Flush();
                backup.SetValue($"{tag}.Captured", 1, RegistryValueKind.DWord);
            }
        }

        private static bool? ReadCapturedTaskState(string id, string taskName)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{id}", writable: false);
            if (backup?.GetValue($"{TaskTag(taskName)}.Captured") is not int captured || captured != 1 ||
                backup.GetValue($"{TaskTag(taskName)}.State") is not int state || state is not (-1 or 0 or 1))
                throw new InvalidOperationException("The Storage Sense task snapshot is incomplete. Backup retained.");
            return state switch { 1 => true, 0 => false, _ => null };
        }

        private static void CaptureText(string id, string name, string value)
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey($@"{BackupRoot}\{id}", writable: true);
            if (backup.GetValue(name) is null)
            {
                backup.SetValue(name, value, RegistryValueKind.String);
            }
        }

        private static string ReadCapturedText(string id, string name)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{id}", writable: false);
            return Convert.ToString(backup?.GetValue(name), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static void EnsureBackupExists(string id)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{id}", writable: false);
            if (backup is null)
            {
                throw new InvalidOperationException("The exact original-state snapshot is unavailable.");
            }
        }

        private static void DeleteBackup(string id)
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(BackupRoot, writable: true);
            root?.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
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

        private static string TaskTag(string taskName)
        {
            uint hash = 2166136261;
            foreach (char character in taskName)
            {
                hash ^= character;
                hash *= 16777619;
            }
            return $"Task.{hash:X8}";
        }

        private static string FirstMeaningfulLine(string text) =>
            text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0) ?? "Unknown";

        private static string FormatNullable(int? value) =>
            value?.ToString(CultureInfo.InvariantCulture) ?? "<absent>";

        private enum ReservedStorageState
        {
            Unknown,
            Enabled,
            Disabled,
            Unsupported
        }
    }
}
