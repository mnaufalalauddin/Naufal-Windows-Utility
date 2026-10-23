using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal sealed class WindowsAiService : IToolToggleService
    {
        private const string Id = "WindowsAI";
        private const string BackupPath =
            @"Software\Naufal Windows Tech\Powertoys\Backups\WindowsAI";
        private const string AiMachinePath =
            @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI";
        private const string AiUserPath =
            @"Software\Policies\Microsoft\Windows\WindowsAI";

        private readonly NativeCommandRunner _commandRunner = new();

        private static readonly ToolToggleDefinition Definition = new(
            Id,
            "Components / HIGH IMPACT",
            "Disable Windows Artificial Intelligence",
            "Applies the Windows AI, Copilot, and Recall policy bundle, disables WSAIFabricSvc, removes removable Copilot packages, and removes the Recall payload when supported. Restore returns captured policy and service state; removed packages or feature payloads may require Microsoft Store or Windows Update to reinstall.",
            true,
            true);

        private static readonly IReadOnlyList<AiPolicySetting> PolicySettings =
            CreatePolicySettings();

        public IReadOnlyList<ToolToggleDefinition> GetDefinitions() =>
            new[] { Definition };

        public async Task<ToolToggleState> ReadStateAsync(ToolToggleDefinition definition)
        {
            try
            {
                int matched = 0;
                List<string> mismatches = new();
                foreach (AiPolicySetting setting in PolicySettings)
                {
                    object? actual = ReadValue(setting.Hive, setting.Path, setting.Name);
                    bool match = actual is not null && string.Equals(
                        Convert.ToString(actual, CultureInfo.InvariantCulture),
                        Convert.ToString(setting.Value, CultureInfo.InvariantCulture),
                        StringComparison.Ordinal);
                    if (match)
                    {
                        matched++;
                    }
                    else if (mismatches.Count < 5)
                    {
                        mismatches.Add($"{setting.Name}={Convert.ToString(actual, CultureInfo.InvariantCulture) ?? "<absent>"}");
                    }
                }

                int? serviceStart = ReadDword(
                    RegistryHive.LocalMachine,
                    @"SYSTEM\CurrentControlSet\Services\WSAIFabricSvc",
                    "Start");
                bool serviceDisabled = serviceStart is null or 4;
                IReadOnlyList<Package> copilotPackages = FindTargetPackages()
                    .Where(package => IsCopilotPackage(package.Id.Name))
                    .ToList();
                RecallState recall = await ReadRecallStateAsync();
                bool recallDisabled = recall is RecallState.Disabled or RecallState.NotPresent;
                bool applied = matched == PolicySettings.Count &&
                               serviceDisabled &&
                               copilotPackages.Count == 0 &&
                               recallDisabled;
                string mismatchText = mismatches.Count == 0
                    ? string.Empty
                    : $"; mismatch: {string.Join(", ", mismatches)}";
                return new ToolToggleState(
                    applied,
                    true,
                    $"Policies={matched}/{PolicySettings.Count}; WSAIFabricSvc={FormatNullable(serviceStart)}; " +
                    $"CopilotPackages={copilotPackages.Count}; Recall={recall}{mismatchText}");
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
                    await ApplyAsync();
                }
                else
                {
                    using RegistryKey? snapshot = Registry.CurrentUser.OpenSubKey(BackupPath, writable: false);
                    if (snapshot is null)
                    {
                        ToolToggleOperationResult fallback = await RestoreWindowsDefaultAsync(definition);
                        return fallback with { Message = "No original backup was found. " + fallback.Message, DefaultFallbackHandled = true };
                    }
                    await RestoreAsync();
                }

                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable && (!targetOn || after.IsOn);
                if (!targetOn && verified)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        BackupPath,
                        throwOnMissingSubKey: false);
                }
                string message;
                if (verified && targetOn)
                {
                    message = "Windows AI policy, service, removable Copilot package, and Recall targets were applied and verified.";
                }
                else if (verified)
                {
                    message = "Captured Windows AI policies and service state were restored. Removed app/feature payloads are not silently reinstalled.";
                }
                else
                {
                    message = $"Verification did not match the requested state. Actual: {after.ActualValue}";
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
                await RestoreWindowsDefaultsAsync();
                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable && !after.IsOn;
                if (verified)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        BackupPath,
                        throwOnMissingSubKey: false);
                }
                return new ToolToggleOperationResult(
                    verified,
                    verified,
                    verified
                        ? "Windows AI policy overrides were removed and WSAIFabricSvc was returned to Manual where installed. Removed packages and Recall payloads are not silently reinstalled."
                        : $"Windows-default restore did not verify. Actual: {after.ActualValue}",
                    after);
            }
            catch (Exception exception)
            {
                ToolToggleState state = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(false, false, exception.Message, state);
            }
        }

        private async Task ApplyAsync()
        {
            DebloatJournal.RequireNoGranularAi();
            var store = await CopilotStoreService.ProbeAsync();
            if (store?.InventoryError is string inventoryError) throw new InvalidOperationException(inventoryError);
            RecallState recall = await ReadRecallStateAsync();
            if (recall == RecallState.Unknown) throw new InvalidOperationException("Recall availability could not be verified. No AI change was started.");
            foreach (AiPolicySetting setting in PolicySettings)
                CaptureValue(PolicyTag(setting), setting.Hive, setting.Path, setting.Name);
            foreach (AiPolicySetting setting in PolicySettings)
            {
                using RegistryKey key = CreateKey(setting.Hive, setting.Path);
                key.SetValue(setting.Name, setting.Value, setting.Kind);
            }

            await CaptureAndDisableAiServiceAsync();
            await RemoveTargetPackagesAsync();

            await CopilotStoreService.RemoveAsync(null, _ => Task.CompletedTask);
            if (recall == RecallState.Enabled)
            {
                NativeCommandResult disableRecall = await _commandRunner.RunAsync(
                    "dism.exe",
                    new[]
                    {
                        "/English",
                        "/Online",
                        "/Disable-Feature",
                        "/FeatureName:Recall",
                        "/Remove",
                        "/NoRestart"
                    },
                    TimeSpan.FromMinutes(8));
                if (disableRecall.TimedOut || disableRecall.ExitCode is not (0 or 3010))
                {
                    throw new InvalidOperationException(
                        $"Recall feature removal failed: {disableRecall.CombinedOutput}");
                }
            }
        }

        private async Task RestoreAsync()
        {
            DebloatJournal.RequireNoGranularAi();
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(BackupPath, writable: false);
            if (backup is null) { await RestoreWindowsDefaultsAsync(); return; }
            var tags = PolicySettings.Select(PolicyTag).ToList();
            const string path = @"SYSTEM\CurrentControlSet\Services\WSAIFabricSvc";
            bool serviceCaptured = backup.GetValue("Service.Start.Captured") is not null;
            bool installed = ReadDword(RegistryHive.LocalMachine, path, "Start").HasValue;
            if (installed || serviceCaptured)
            {
                tags.Add("Service.Start");
                tags.Add("Service.Delayed");
            }
            RegistryRestorePlan.RequireTags(key => backup.GetValue(key), tags, Deserialize);
            bool running = installed || serviceCaptured ?
                ServiceRestoreSnapshot.ReadRunningState(backup.GetValue("Service.Running"), "WSAIFabricSvc") : false;
            int? start = installed || serviceCaptured ? ReadBackupDword("Service.Start") : null;
            int? delayed = installed || serviceCaptured ? ReadBackupDword("Service.Delayed") : null;
            string? mode = installed || serviceCaptured ?
                ServiceRestoreSnapshot.ToScStartMode(start ?? throw new InvalidOperationException("Missing original service startup value."), delayed) : null;
            if (running && start == 4) throw new InvalidOperationException("The saved service state is inconsistent: disabled and running. Backup retained.");
            foreach (AiPolicySetting setting in PolicySettings)
                RestoreValue(PolicyTag(setting), setting.Hive, setting.Path, setting.Name);
            if (installed)
            {
                int? current = ReadDword(RegistryHive.LocalMachine, path, "Start");
                int? currentDelayed = ReadDword(RegistryHive.LocalMachine, path, "DelayedAutoStart");
                if (current != start || (start == 2 && (delayed == 1) != (currentDelayed == 1)))
                    await RequireCommandSuccessAsync("sc.exe", new[] { "config", "WSAIFabricSvc", "start=", mode! });
                RestoreValue("Service.Start", RegistryHive.LocalMachine, path, "Start");
                RestoreValue("Service.Delayed", RegistryHive.LocalMachine, path, "DelayedAutoStart");
                await ServiceRestoreRuntime.EnsureAsync("WSAIFabricSvc", running, _commandRunner);
            }
            else if (serviceCaptured)
                throw new InvalidOperationException("The saved WSAIFabricSvc service is no longer installed. Its backup was retained; no service registry key was recreated.");
        }

        private async Task RestoreWindowsDefaultsAsync()
        {
            DebloatJournal.RequireNoGranularAi();
            foreach (AiPolicySetting setting in PolicySettings)
            {
                RegistryRestorePlan.Execute(new[] { new RestoreRegistryValue(new(setting.Hive, setting.Path, setting.Name), null, null) });
            }

            const string servicePath = @"SYSTEM\CurrentControlSet\Services\WSAIFabricSvc";
            if (ReadDword(RegistryHive.LocalMachine, servicePath, "Start").HasValue)
            {
                await RequireCommandSuccessAsync(
                    "sc.exe",
                    new[] { "config", "WSAIFabricSvc", "start=", "demand" });
                if (ReadDword(RegistryHive.LocalMachine, servicePath, "Start") != 3)
                    throw new InvalidOperationException("WSAIFabricSvc Manual startup could not be verified.");
            }

        }

        private async Task CaptureAndDisableAiServiceAsync()
        {
            const string path = @"SYSTEM\CurrentControlSet\Services\WSAIFabricSvc";
            if (ReadDword(RegistryHive.LocalMachine, path, "Start") is null)
            {
                return;
            }
            CaptureValue("Service.Start", RegistryHive.LocalMachine, path, "Start");
            CaptureValue("Service.Delayed", RegistryHive.LocalMachine, path, "DelayedAutoStart");
            CaptureBoolean(
                "Service.Running",
                ServiceRestoreRuntime.ReadStable("WSAIFabricSvc"));
            await _commandRunner.RunAsync(
                "sc.exe",
                new[] { "stop", "WSAIFabricSvc" },
                TimeSpan.FromSeconds(25));
            await RequireCommandSuccessAsync(
                "sc.exe",
                new[] { "config", "WSAIFabricSvc", "start=", "disabled" });
        }

        private static IReadOnlyList<Package> FindTargetPackages()
        {
            PackageManager manager = new();
            return manager.FindPackagesForUser(string.Empty)
                .Where(package =>
                    IsCopilotPackage(package.Id.Name) && !package.IsFramework && !package.IsResourcePackage &&
                    package.Id.FamilyName.Equals("Microsoft.Copilot_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase))
                .GroupBy(package => package.Id.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private static bool IsCopilotPackage(string packageName) =>
            packageName.Equals("Microsoft.Copilot", StringComparison.OrdinalIgnoreCase);

        private static async Task RemoveTargetPackagesAsync()
        {
            PackageManager manager = new();
            foreach (Package package in FindTargetPackages())
            {
                DeploymentResult result = await DeploymentOperationTimeout.AwaitAsync(
                    () => manager.RemovePackageAsync(
                        package.Id.FullName,
                        RemovalOptions.None),
                    $"Removing {package.Id.Name}");
                if (result.ExtendedErrorCode is not null &&
                    result.ExtendedErrorCode.HResult < 0)
                {
                    throw new InvalidOperationException(
                        $"Unable to remove {package.Id.Name}: {result.ErrorText} " +
                        $"(0x{result.ExtendedErrorCode.HResult:X8})");
                }
            }
        }

        private async Task<RecallState> ReadRecallStateAsync()
        {
            NativeCommandResult result = await _commandRunner.RunAsync(
                "dism.exe",
                new[] { "/English", "/Online", "/Get-FeatureInfo", "/FeatureName:Recall" },
                TimeSpan.FromSeconds(45));
            if (result.TimedOut) return RecallState.Unknown;
            if (result.ExitCode != 0)
            {
                return result.ExitCode == unchecked((int)0x800F080C) ||
                    result.CombinedOutput.Contains("0x800f080c", StringComparison.OrdinalIgnoreCase)
                    ? RecallState.NotPresent : RecallState.Unknown;
            }
            if (result.StandardOutput.Contains("State : Enabled", StringComparison.OrdinalIgnoreCase))
            {
                return RecallState.Enabled;
            }
            if (result.StandardOutput.Contains("State : Disabled", StringComparison.OrdinalIgnoreCase) ||
                result.StandardOutput.Contains("Disabled with Payload Removed", StringComparison.OrdinalIgnoreCase))
            {
                return RecallState.Disabled;
            }
            return RecallState.Unknown;
        }

        private async Task RequireCommandSuccessAsync(
            string executable,
            IReadOnlyList<string> arguments)
        {
            NativeCommandResult result = await _commandRunner.RunAsync(
                executable,
                arguments,
                TimeSpan.FromSeconds(30));
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
            if (Convert.ToInt32(backup?.GetValue($"{tag}.Exists", 0) ?? 0, CultureInfo.InvariantCulture) != 1)
            {
                return null;
            }
            string value = Convert.ToString(backup?.GetValue($"{tag}.Value"), CultureInfo.InvariantCulture)
                ?? string.Empty;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : null;
        }

        private static object? ReadValue(RegistryHive hive, string path, string name)
        {
            using RegistryKey? key = OpenKey(hive, path, writable: false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        private static int? ReadDword(RegistryHive hive, string path, string name)
        {
            object? value = ReadValue(hive, path, name);
            return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
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

        private static string PolicyTag(AiPolicySetting setting)
        {
            uint hash = 2166136261;
            foreach (char character in $"{setting.Hive}|{setting.Path}|{setting.Name}")
            {
                hash ^= character;
                hash *= 16777619;
            }
            return $"Policy.{hash:X8}";
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

        private static string FormatNullable(int? value) =>
            value?.ToString(CultureInfo.InvariantCulture) ?? "not installed";

        private static IReadOnlyList<AiPolicySetting> CreatePolicySettings()
        {
            List<AiPolicySetting> settings = new()
            {
                Text(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "SettingsPageVisibility", "hide:aicomponents"),
                Dword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1)
            };
            (string Name, int Value)[] policies =
            {
                ("AllowRecallEnablement", 0),
                ("DisableAIDataAnalysis", 1),
                ("DisableClickToDo", 1),
                ("DisableCocreator", 1),
                ("DisableGenerativeFill", 1),
                ("DisableImageCreator", 1),
                ("DisableRecallDataProviders", 1),
                ("DisableRemoteAgentConnectors", 1),
                ("DisableSettingsAgent", 1),
                ("DisableAgentConnectors", 1),
                ("DisableAgentWorkspaces", 2),
                ("RemoveMicrosoftCopilotApp", 1)
            };
            foreach ((string name, int value) in policies)
            {
                settings.Add(Dword(RegistryHive.LocalMachine, AiMachinePath, name, value));
                settings.Add(Dword(RegistryHive.CurrentUser, AiUserPath, name, value));
            }
            settings.Add(Dword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1));
            settings.Add(Dword(RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1));
            settings.Add(Dword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\EdgeUpdate", "Install{C50565E9-CCCF-44B4-BA15-5AC5C6569197}", 0));
            return settings;
        }

        private static AiPolicySetting Dword(
            RegistryHive hive,
            string path,
            string name,
            int value) =>
            new(hive, path, name, RegistryValueKind.DWord, value);

        private static AiPolicySetting Text(
            RegistryHive hive,
            string path,
            string name,
            string value) =>
            new(hive, path, name, RegistryValueKind.String, value);

        private sealed record AiPolicySetting(
            RegistryHive Hive,
            string Path,
            string Name,
            RegistryValueKind Kind,
            object Value);

        private enum RecallState
        {
            Unknown,
            Enabled,
            Disabled,
            NotPresent
        }
    }
}
