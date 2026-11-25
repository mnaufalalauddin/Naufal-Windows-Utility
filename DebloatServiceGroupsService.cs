using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal sealed class DebloatServiceGroupsService : IToolToggleService, ICatalogEffectSource
    {
        private const string BackupRoot =
            @"Software\Naufal Windows Tech\Powertoys\Backups\DebloatServiceGroups";

        private readonly NativeCommandRunner _commandRunner = new();
        private static readonly IReadOnlyDictionary<string, ServiceGroup> Catalog =
            CreateCatalog().ToDictionary(
                item => item.Definition.Id,
                StringComparer.OrdinalIgnoreCase);

        public string DescribeRecovery(ToolToggleDefinition definition) =>
            CatalogRecovery.Snapshot($@"{BackupRoot}\{definition.Id}", "No current snapshot found. Restore uses documented startup defaults for supported services. A disabled service with an unknown default needs its original settings.");

        public IReadOnlyList<ToolToggleDefinition> GetDefinitions() =>
            Catalog.Values.Select(item => item.Definition).ToArray();

        public IReadOnlyList<CatalogEffect> GetEffects(ToolToggleDefinition definition)
        {
            var group = Catalog[definition.Id];
            return group.Services.Select(name => CatalogEffect.Registry("HKLM", @"SYSTEM\CurrentControlSet\Services\" + name, "Start", "DWord", 4))
                .Concat(group.Settings.Select(s => CatalogEffect.Registry("HKLM", s.Path, s.Name, "DWord", s.Value))).ToArray();
        }

        public async Task<ToolToggleState> ReadStateAsync(ToolToggleDefinition definition)
        {
            try
            {
                ServiceGroup group = Catalog[definition.Id];
                List<string> actual = new();
                int installed = 0;
                int disabled = 0;
                foreach (string serviceName in group.Services)
                {
                    using RegistryKey? serviceKey = OpenKey(RegistryHive.LocalMachine, ServicePath(serviceName), writable: false);
                    if (serviceKey is null)
                    {
                        continue;
                    }
                    int start = Convert.ToInt32(serviceKey.GetValue("Start") ??
                        throw new InvalidOperationException($"Service '{serviceName}' exists but its startup configuration could not be read."), CultureInfo.InvariantCulture);
                    installed++;
                    if (start == 4)
                    {
                        disabled++;
                    }
                    string runtime = GamingLiveStatusService.TryReadServiceState(serviceName, out string serviceState)
                        ? serviceState
                        : "Unknown";
                    actual.Add($"{serviceName}={ServiceRestoreSnapshot.FormatStart(start)} / {runtime}");
                }

                int registryMatches = 0;
                foreach (MachineSetting setting in group.Settings)
                {
                    int? value = ReadDword(RegistryHive.LocalMachine, setting.Path, setting.Name);
                    if (value == setting.Value)
                    {
                        registryMatches++;
                    }
                    actual.Add($"{setting.Name}={FormatNullable(value)}");
                }

                bool available = installed > 0 || group.Settings.Count > 0;
                bool applied = available &&
                               disabled == installed &&
                               registryMatches == group.Settings.Count;
                return new ToolToggleState(
                    applied,
                    available,
                    actual.Count == 0 ? "No matching service is installed" : string.Join(", ", actual),
                    available ? string.Empty : "This service group is not installed on this PC.",
                    HasAppliedParts: disabled > 0 || registryMatches > 0,
                    UnavailableOnThisPc: !available);
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
                ServiceGroup group = Catalog[definition.Id];
                bool restoreVerified = false;
                if (targetOn)
                {
                    await ApplyAsync(group);
                }
                else
                {
                    restoreVerified = await RestoreSmartAsync(group);
                }

                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable && (targetOn
                    ? after.IsOn
                    : restoreVerified);
                if (!targetOn && verified) DeleteBackup(group.Definition.Id);
                return new ToolToggleOperationResult(
                    verified,
                    verified,
                    verified
                        ? $"{definition.Name} is now {(targetOn ? "DISABLED" : "RESTORED")}. A reboot is recommended."
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
                ServiceGroup group = Catalog[definition.Id];
                foreach (string serviceName in group.Services)
                {
                    string path = ServicePath(serviceName);
                    int? current = ReadDword(
                        RegistryHive.LocalMachine,
                        path,
                        "Start");
                    if (!current.HasValue)
                    {
                        continue;
                    }

                    if (!DocumentedWindowsDefaults.TryGetValue(
                            serviceName,
                            out int defaultStart))
                    {
                        if (current != 4)
                        {
                            continue;
                        }
                        throw new InvalidOperationException(
                            $"No reliable Windows/vendor default is registered for disabled service '{serviceName}'. The application will not guess.");
                    }

                    int? delayed = ReadDword(
                        RegistryHive.LocalMachine,
                        path,
                        "DelayedAutoStart");
                    ServiceStartupRestorePlan plan =
                        ServiceRestoreSnapshot.CreateWindowsDefaultPlan(defaultStart, delayed);
                    plan = ServiceRestoreSnapshot.ApplyDocumentedRuntimePolicy(serviceName, plan);
                    await ConfigureServiceStartupAsync(
                        serviceName,
                        plan,
                        // A documented-default restore preserves the existing
                        // delayed-start metadata exactly as the reference app
                        // does; rewriting a protected value can cause a false
                        // Access Denied after sc.exe already succeeded.
                        restoreDelayedAutoStart: false);
                }

                foreach (MachineSetting setting in group.Settings)
                {
                    WriteRestoredValue(RegistryHive.LocalMachine, setting.Path, setting.Name, null, null);
                }
                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable && !after.IsOn && VerifyWindowsDefaults(group);
                if (verified) DeleteBackup(group.Definition.Id);
                return new ToolToggleOperationResult(
                    verified,
                    verified,
                    verified
                        ? $"{definition.Name} was returned to documented Windows defaults."
                        : $"Windows-default service read-back failed. Actual: {after.ActualValue}",
                    after);
            }
            catch (Exception exception)
            {
                ToolToggleState state = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(false, false, exception.Message, state);
            }
        }

        private async Task ApplyAsync(ServiceGroup group)
        {
            foreach (string serviceName in group.Services)
            {
                string path = ServicePath(serviceName);
                if (ReadDword(RegistryHive.LocalMachine, path, "Start") is null)
                {
                    continue;
                }

                CaptureValue(group.Definition.Id, $"{serviceName}.Start", RegistryHive.LocalMachine, path, "Start");
                CaptureValue(group.Definition.Id, $"{serviceName}.Delayed", RegistryHive.LocalMachine, path, "DelayedAutoStart");
                CaptureBoolean(group.Definition.Id, $"{serviceName}.Running", await IsServiceRunningAsync(serviceName));
                await TryStopServiceAsync(serviceName);
                await ConfigureServiceStartupAsync(
                    serviceName,
                    new ServiceStartupRestorePlan(4, null, false, true, false, false));
            }

            foreach (MachineSetting setting in group.Settings)
            {
                CaptureValue(group.Definition.Id, $"Registry.{setting.Name}", RegistryHive.LocalMachine, setting.Path, setting.Name);
                SetDword(RegistryHive.LocalMachine, setting.Path, setting.Name, setting.Value);
            }
        }

        private async Task<bool> RestoreSmartAsync(ServiceGroup group)
        {
            IReadOnlyList<ServiceRestoreWorkItem> workItems = BuildRestorePlan(group);
            foreach (string serviceName in group.Services)
            {
                ServiceRestoreWorkItem? workItem = workItems
                    .FirstOrDefault(item => item.ServiceName.Equals(
                        serviceName,
                        StringComparison.OrdinalIgnoreCase));
                // A consumed reference snapshot may verify an unchanged PC on
                // repeat Restore, but must never replay stale values after drift.
                if (workItem is null || workItem.PreviousSnapshot?.AlreadyRestored == true)
                {
                    continue;
                }
                await ConfigureServiceStartupAsync(
                    serviceName,
                    workItem.Plan,
                    restoreDelayedAutoStart: !workItem.HasSnapshot);
                if (workItem.HasSnapshot)
                {
                    RestoreValue(
                        group.Definition.Id,
                        $"{serviceName}.Delayed",
                        RegistryHive.LocalMachine,
                        ServicePath(serviceName),
                        "DelayedAutoStart");
                }
            }

            foreach (MachineSetting setting in group.Settings)
            {
                string tag = $"Registry.{setting.Name}";
                if (HasCapturedValue(group.Definition.Id, tag))
                {
                    RestoreValue(group.Definition.Id, tag, RegistryHive.LocalMachine, setting.Path, setting.Name);
                }
                else
                {
                    WriteRestoredValue(RegistryHive.LocalMachine, setting.Path, setting.Name, null, null);
                }
            }

            bool verified = await VerifyRestorePlanAsync(group, workItems);
            if (!verified)
                throw new InvalidOperationException(
                    workItems.Any(item => item.PreviousSnapshot?.AlreadyRestored == true)
                        ? "Service state changed after its previous snapshot was already restored. The old snapshot was not replayed. A fresh native capture is required before restoring changed settings."
                        : "Service restore read-back did not match the documented Windows defaults or the saved vendor-service snapshot. Backup retained.");
            foreach (ServiceRestoreWorkItem item in workItems)
                item.PreviousSnapshot?.MarkRestored();
            return true;
        }

        private static IReadOnlyList<ServiceRestoreWorkItem> BuildRestorePlan(
            ServiceGroup group)
        {
            List<ServiceRestoreWorkItem> plans = new();
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                $@"{BackupRoot}\{group.Definition.Id}",
                writable: false);

            foreach (string serviceName in group.Services)
            {
                string path = ServicePath(serviceName);
                int? currentStart = ReadDword(
                    RegistryHive.LocalMachine,
                    path,
                    "Start");
                if (!currentStart.HasValue)
                {
                    continue;
                }

                bool hasSnapshot = backup is not null &&
                    RegistrySnapshotCommit.IsCaptured(
                        key => backup.GetValue(key),
                        $"{serviceName}.Start");
                bool hasDefault = DocumentedWindowsDefaults.TryGetValue(
                    serviceName,
                    out int defaultStart);

                if (hasSnapshot)
                {
                    ServiceRestoreSnapshot.Validate(
                        key => backup!.GetValue(key),
                        serviceName,
                        installed: true);
                    int savedStart = ReadRequiredBackupDword(
                        backup!,
                        $"{serviceName}.Start");
                    int? delayed = ReadOptionalBackupDword(
                        backup!,
                        $"{serviceName}.Delayed");
                    bool wasRunning = ServiceRestoreSnapshot.ReadRunningState(
                        backup!.GetValue($"{serviceName}.Running"),
                        serviceName);
                    ServiceStartupRestorePlan plan = ServiceRestoreSnapshot.CreatePlan(
                            savedStart,
                            delayed,
                            wasRunning,
                            hasDefault ? defaultStart : null);
                    plans.Add(new ServiceRestoreWorkItem(
                        serviceName,
                        ServiceRestoreSnapshot.ApplyDocumentedRuntimePolicy(serviceName, plan),
                        HasSnapshot: true));
                    continue;
                }

                if (backup?.GetValueNames().Any(name => name.StartsWith(
                        serviceName + ".", StringComparison.OrdinalIgnoreCase)) == true)
                    throw new InvalidDataException($"The native snapshot for '{serviceName}' is incomplete. Backup retained.");

                if (hasDefault)
                {
                    int? delayed = ReadDword(
                        RegistryHive.LocalMachine,
                        path,
                        "DelayedAutoStart");
                    ServiceStartupRestorePlan plan =
                        ServiceRestoreSnapshot.CreateWindowsDefaultPlan(
                            defaultStart,
                            delayed);
                    plans.Add(new ServiceRestoreWorkItem(
                        serviceName,
                        ServiceRestoreSnapshot.ApplyDocumentedRuntimePolicy(serviceName, plan),
                        HasSnapshot: false));
                    continue;
                }

                // Native captures take precedence. Recover the reference app's
                // exact Start/Delayed/Running data only when no native fields exist.
                PreviousServiceSnapshot? previous = PreviousServiceSnapshot.ReadForRestore(serviceName);
                if (previous is not null)
                {
                    plans.Add(new ServiceRestoreWorkItem(serviceName,
                        ServiceRestoreSnapshot.CreatePlan(previous.Start, previous.Delayed, previous.WasRunning, null),
                        HasSnapshot: false, PreviousSnapshot: previous));
                    continue;
                }

                if (currentStart == 4)
                {
                    throw new InvalidOperationException(
                        $"No exact original snapshot exists for disabled service '{serviceName}', and no reliable Windows/vendor default is registered. The application will not guess.");
                }
            }

            return plans;
        }

        private async Task ConfigureServiceStartupAsync(
            string serviceName,
            ServiceStartupRestorePlan plan,
            bool restoreDelayedAutoStart = false)
        {
            string startMode = ServiceRestoreSnapshot.ToScStartMode(
                plan.TargetStart,
                plan.DelayedAutoStart);
            int? beforeStart = ReadDword(RegistryHive.LocalMachine, ServicePath(serviceName), "Start");
            int? beforeDelayed = ReadDword(RegistryHive.LocalMachine, ServicePath(serviceName), "DelayedAutoStart");
            NativeCommandResult configure = new(0, "Startup configuration already matches the target.", "", false, TimeSpan.Zero);
            if (ServiceRestoreSnapshot.NeedsStartupConfiguration(beforeStart, beforeDelayed, plan))
                configure = await _commandRunner.RunAsync(
                    "sc.exe",
                    new[] { "config", serviceName, "start=", startMode },
                    TimeSpan.FromSeconds(25));

            await Task.Delay(120);
            int? actualStart = ReadDword(
                RegistryHive.LocalMachine,
                ServicePath(serviceName),
                "Start");
            if (actualStart != plan.TargetStart)
            {
                try
                {
                    SetDword(
                        RegistryHive.LocalMachine,
                        ServicePath(serviceName),
                        "Start",
                        plan.TargetStart);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        BuildServiceConfigurationError(
                            serviceName,
                            plan.TargetStart,
                            configure,
                            $"Registry fallback failed: {exception.Message}"),
                        exception);
                }
            }

            if (restoreDelayedAutoStart)
            {
                WriteRestoredValue(RegistryHive.LocalMachine, ServicePath(serviceName),
                    "DelayedAutoStart", plan.DelayedAutoStart,
                    plan.DelayedAutoStart.HasValue ? RegistryValueKind.DWord : null);
            }

            await Task.Delay(120);
            actualStart = ReadDword(
                RegistryHive.LocalMachine,
                ServicePath(serviceName),
                "Start");
            if (actualStart != plan.TargetStart)
            {
                throw new InvalidOperationException(
                    BuildServiceConfigurationError(
                        serviceName,
                        plan.TargetStart,
                        configure,
                        $"Registry read-back was {FormatNullable(actualStart)}."));
            }

            NativeCommandResult? runtimeCommand = null;
            if (plan.ShouldStart)
            {
                runtimeCommand = await TryStartServiceAsync(serviceName);
            }
            else if (plan.ShouldStop)
            {
                await TryStopServiceAsync(serviceName);
            }

            if (plan.VerifyRuntime)
            {
                string expected = plan.ShouldStart ? "Running" : "Stopped";
                if (!await WaitForServiceStateAsync(serviceName, expected))
                {
                    GamingLiveStatusService.TryReadServiceState(
                        serviceName,
                        out string actualState);
                    string commandDetail = runtimeCommand.HasValue &&
                                           !string.IsNullOrWhiteSpace(runtimeCommand.Value.CombinedOutput)
                        ? $" sc.exe: {runtimeCommand.Value.CombinedOutput}"
                        : string.Empty;
                    throw new InvalidOperationException(
                        $"Service '{serviceName}' startup type was restored to {ServiceRestoreSnapshot.FormatStart(plan.TargetStart)}, but its runtime state did not verify. Expected {expected}; actual {actualState}.{commandDetail}");
                }
            }
        }

        private static string BuildServiceConfigurationError(
            string serviceName,
            int targetStart,
            NativeCommandResult result,
            string detail)
        {
            string output = string.IsNullOrWhiteSpace(result.CombinedOutput)
                ? "No sc.exe output."
                : result.CombinedOutput;
            return $"Service '{serviceName}' could not be configured as {ServiceRestoreSnapshot.FormatStart(targetStart)} (Start={targetStart}). sc.exe exit={result.ExitCode}. {detail} {output}";
        }

        private static async Task<bool> WaitForServiceStateAsync(
            string serviceName,
            string expected)
        {
            // Some services need several seconds while Service Control Manager
            // starts their dependencies. A strict restore must wait for the
            // requested runtime state instead of treating the startup type as
            // sufficient evidence of success.
            for (int attempt = 0; attempt < 60; attempt++)
            {
                if (GamingLiveStatusService.TryReadServiceState(
                        serviceName,
                        out string state) &&
                    state.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (attempt < 59)
                {
                    await Task.Delay(250);
                }
            }
            return false;
        }

        private async Task<bool> VerifyRestorePlanAsync(
            ServiceGroup group,
            IReadOnlyList<ServiceRestoreWorkItem> workItems)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                bool matches = true;
                int checkedItems = 0;
                foreach (ServiceRestoreWorkItem item in workItems)
                {
                    checkedItems++;
                    matches &= ReadDword(
                        RegistryHive.LocalMachine,
                        ServicePath(item.ServiceName),
                        "Start") == item.Plan.TargetStart;
                    if (item.HasSnapshot)
                    {
                        matches &= MatchesSavedValue(
                            group.Definition.Id,
                            $"{item.ServiceName}.Delayed",
                            ServicePath(item.ServiceName),
                            "DelayedAutoStart");
                    }
                    else
                    {
                        matches &= ReadDword(
                            RegistryHive.LocalMachine,
                            ServicePath(item.ServiceName),
                            "DelayedAutoStart") == item.Plan.DelayedAutoStart;
                    }

                    if (item.Plan.VerifyRuntime)
                    {
                        string expected = item.Plan.ShouldStart ? "Running" : "Stopped";
                        matches &= GamingLiveStatusService.TryReadServiceState(
                            item.ServiceName,
                            out string state) &&
                            state.Equals(expected, StringComparison.OrdinalIgnoreCase);
                    }
                }

                foreach (MachineSetting setting in group.Settings)
                {
                    checkedItems++;
                    string tag = $"Registry.{setting.Name}";
                    if (HasCapturedValue(group.Definition.Id, tag))
                    {
                        matches &= MatchesSavedValue(
                            group.Definition.Id,
                            tag,
                            setting.Path,
                            setting.Name);
                    }
                    else
                    {
                        matches &= ReadDword(
                            RegistryHive.LocalMachine,
                            setting.Path,
                            setting.Name) is null;
                    }
                }

                if (matches && checkedItems > 0)
                {
                    return true;
                }
                if (attempt < 19)
                {
                    await Task.Delay(250);
                }
            }
            return false;
        }

        private async Task<bool> IsServiceRunningAsync(string serviceName)
        {
            if (!GamingLiveStatusService.TryReadServiceState(serviceName, out string state) ||
                state is not ("Running" or "Stopped"))
                throw new InvalidOperationException($"Cannot capture a stable state for {serviceName}: {state}.");
            return state == "Running";
        }

        private static bool MatchesSavedValue(string id, string tag, string path, string name)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{id}");
            if (backup is null || !RegistrySnapshotCommit.IsCaptured(key => backup.GetValue(key), tag)) return false;
            using RegistryKey? source = OpenKey(RegistryHive.LocalMachine, path, writable: false);
            object? value = source?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            bool existed = Convert.ToInt32(backup.GetValue($"{tag}.Exists"), CultureInfo.InvariantCulture) == 1;
            if (!existed) return value is null;
            if (source is null || value is null) return false;
            RegistryValueKind kind = source.GetValueKind(name);
            return string.Equals(kind.ToString(), backup.GetValue($"{tag}.Kind") as string, StringComparison.Ordinal) &&
                string.Equals(Serialize(value, kind), backup.GetValue($"{tag}.Value") as string, StringComparison.Ordinal);
        }

        private static bool VerifyWindowsDefaults(ServiceGroup group)
        {
            foreach (string serviceName in group.Services)
            {
                int? current = ReadDword(RegistryHive.LocalMachine, ServicePath(serviceName), "Start");
                if (!current.HasValue) continue;
                if (DocumentedWindowsDefaults.TryGetValue(serviceName, out int expected))
                {
                    if (current != expected) return false;
                }
                else if (current == 4) return false;
            }
            foreach (MachineSetting setting in group.Settings)
            {
                using RegistryKey? source = OpenKey(RegistryHive.LocalMachine, setting.Path, writable: false);
                if (source?.GetValue(setting.Name) is not null) return false;
            }
            return true;
        }

        private async Task TryStopServiceAsync(string serviceName)
        {
            await _commandRunner.RunAsync(
                "sc.exe",
                new[] { "stop", serviceName },
                TimeSpan.FromSeconds(20));
        }

        private async Task<NativeCommandResult> TryStartServiceAsync(string serviceName)
        {
            return await _commandRunner.RunAsync(
                "sc.exe",
                new[] { "start", serviceName },
                TimeSpan.FromSeconds(20));
        }

        private static void CaptureValue(
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

        private static void RestoreValue(
            string id,
            string tag,
            RegistryHive hive,
            string path,
            string name)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{id}", writable: false);
            if (backup is null || !RegistrySnapshotCommit.IsCaptured(key => backup.GetValue(key), tag))
            {
                return;
            }

            bool existed = Convert.ToInt32(backup.GetValue($"{tag}.Exists", 0), CultureInfo.InvariantCulture) == 1;
            if (!existed)
            {
                WriteRestoredValue(hive, path, name, null, null);
                return;
            }

            string kindText = Convert.ToString(backup.GetValue($"{tag}.Kind"), CultureInfo.InvariantCulture)
                ?? RegistryValueKind.String.ToString();
            RegistryValueKind kind = Enum.TryParse(kindText, out RegistryValueKind parsed)
                ? parsed
                : RegistryValueKind.String;
            string value = Convert.ToString(backup.GetValue($"{tag}.Value"), CultureInfo.InvariantCulture)
                ?? string.Empty;
            WriteRestoredValue(hive, path, name, Deserialize(value, kind), kind);
        }

        private static void WriteRestoredValue(RegistryHive hive, string path, string name,
            object? expected, RegistryValueKind? expectedKind)
        {
            RegistryRestoreWrite.Apply(() =>
            {
                using RegistryKey? source = OpenKey(hive, path, writable: false);
                object? value = source?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                RegistryValueKind? kind = value is null ? null : source!.GetValueKind(name);
                return (value is null ? null : Serialize(value, kind!.Value), kind);
            }, expected is null ? null : Serialize(expected, expectedKind!.Value), expectedKind, () =>
            {
                if (expected is null)
                {
                    using RegistryKey? key = OpenKey(hive, path, writable: true);
                    key?.DeleteValue(name, throwOnMissingValue: false);
                }
                else
                {
                    using RegistryKey key = CreateKey(hive, path);
                    key.SetValue(name, expected, expectedKind!.Value);
                }
            });
        }

        private static bool HasCapturedValue(string id, string tag)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                $@"{BackupRoot}\{id}",
                writable: false);
            return backup is not null && RegistrySnapshotCommit.IsCaptured(
                key => backup.GetValue(key),
                tag);
        }

        private static int ReadRequiredBackupDword(RegistryKey backup, string tag)
        {
            int? value = ReadOptionalBackupDword(backup, tag);
            return value ?? throw new InvalidDataException(
                $"The saved DWord value for {tag} is missing or invalid. Restore was not started.");
        }

        private static int? ReadOptionalBackupDword(RegistryKey backup, string tag)
        {
            if (!RegistrySnapshotCommit.IsCaptured(
                    key => backup.GetValue(key),
                    tag))
            {
                return null;
            }
            if (Convert.ToInt32(
                    backup.GetValue($"{tag}.Exists", 0),
                    CultureInfo.InvariantCulture) != 1)
            {
                return null;
            }
            string text = Convert.ToString(
                backup.GetValue($"{tag}.Value"),
                CultureInfo.InvariantCulture) ?? string.Empty;
            return int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value)
                ? value
                : throw new InvalidDataException(
                    $"The saved DWord value for {tag} is invalid. Restore was not started.");
        }

        private static void CaptureBoolean(string id, string name, bool value)
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey($@"{BackupRoot}\{id}", writable: true);
            if (backup.GetValue(name) is null)
            {
                backup.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
                backup.Flush();
            }
            else ServiceRestoreSnapshot.ReadRunningState(backup.GetValue(name), name);
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

        private static string ServicePath(string serviceName) =>
            $@"SYSTEM\CurrentControlSet\Services\{serviceName}";

        private static string FormatNullable(int? value) =>
            value?.ToString(CultureInfo.InvariantCulture) ?? "<absent>";

        private static readonly IReadOnlyDictionary<string, int> DocumentedWindowsDefaults =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["BITS"] = 3,
                ["wuauserv"] = 3,
                ["cryptsvc"] = 2,
                ["DPS"] = 2,
                ["WdiServiceHost"] = 3,
                ["MapsBroker"] = 2,
                ["lfsvc"] = 3,
                ["WbioSrvc"] = 3,
                ["WMPNetworkSvc"] = 3,
                ["icssvc"] = 3,
                ["SessionEnv"] = 3,
                ["TermService"] = 3,
                ["UmRdpService"] = 3,
                ["RetailDemo"] = 2,
                ["RemoteAccess"] = 4,
                ["RasMan"] = 3,
                ["RemoteRegistry"] = 2,
                ["TrkWks"] = 2,
                ["WerSvc"] = 3,
                ["wercplsupport"] = 3,
                ["DiagTrack"] = 2,
                ["Fax"] = 3,
                ["Spooler"] = 2,
                ["DoSvc"] = 2,
                ["XblGameSave"] = 3,
                ["XblAuthManager"] = 3,
                ["XboxGipSvc"] = 3,
                ["XboxNetApiSvc"] = 3,
                ["SensorDataService"] = 3,
                ["SensrSvc"] = 3,
                ["SensorService"] = 3,
                ["SysMain"] = 2,
                ["WSearch"] = 3
            };

        private static IReadOnlyList<ServiceGroup> CreateCatalog() => new[]
        {
            Group("NvidiaTelemetryService", "Experimental", "NVIDIA Telemetry",
                "Disables the NVIDIA telemetry container and sets the Control Panel telemetry preference to opt out. Driver display functions remain installed, but NVIDIA usage reporting and related background collection stop.",
                new[] { "NvTelemetryContainer" },
                new MachineSetting(@"SOFTWARE\NVIDIA Corporation\NvControlPanel2\Client", "OptInOrOutPreference", 0)),
            Group("AmdExternalEventsService", "Experimental", "AMD External Events Utility",
                "Disables AMD's external-event service used for display-change notifications, hotkeys, and some Radeon software features. Core graphics rendering remains available, but driver UI events may stop responding.",
                new[] { "AMD External Events Utility" }),
            Group("IntelGraphicsControlServices", "Experimental", "Intel Graphics Control Center Background Services",
                "Disables detected Intel Graphics Command Center helper services. Intel graphics output remains available, while tray integration, profile synchronization, hotkeys, and background control-center tasks may stop.",
                new[] { "igccservice", "cplspcon", "cphs", "OneApp.IGCC.WinService" }),
            Group("IntelDriverSupportAssistant", "Experimental", "Intel Driver & Support Assistant Services",
                "Disables Intel Driver & Support Assistant scanning and update services. Installed Intel drivers continue to work, but automatic Intel hardware detection and update notifications stop.",
                new[] { "DSAService", "DSAUpdateService" }),
            Group("IntelJhiService", "HIGH RISK", "Intel Management Engine JHI Service",
                "Disables the Intel Dynamic Application Loader/Host Interface service. Software that relies on Intel Management Engine security, authentication, or enterprise capabilities can stop working.",
                new[] { "jhi_service" }),
            Group("IntelRapidStorageService", "HIGH RISK", "Intel Rapid Storage Middleware Service",
                "Disables Intel Rapid Storage middleware monitoring. Storage drivers remain installed, but Intel RST/Optane status, notifications, and management workflows may become unavailable.",
                new[] { "RstMwService" }),
            Group("IntelXtuService", "Experimental", "Intel Extreme Tuning Utility Service",
                "Disables the Intel Extreme Tuning Utility background service. XTU monitoring, profile application, and tuning controls will not operate while the service is disabled.",
                new[] { "XTU3SERVICE" }),
            Group("IntelDynamicTuningService", "HIGH RISK", "Intel Dynamic Tuning / ESIF Service",
                "Disables Intel Dynamic Tuning/ESIF thermal and power coordination. On supported laptops this can affect fan, temperature, battery, and processor power-limit behavior.",
                new[] { "esif_vsec" }),
            Group("WindowsSearchService", "HIGH RISK", "Windows Search Indexing",
                "Disables the Windows Search indexing service. File, Start, Settings, and Outlook searches can become slower or incomplete because the search index is no longer maintained.",
                new[] { "WSearch" }),
            Group("SysMainService", "HIGH RISK", "SysMain / Application Prefetching",
                "Disables SysMain and sets both Prefetcher and Superfetch registry controls to 0. This can change application/startup caching behavior and may increase load times, especially on frequently used systems.",
                new[] { "SysMain" },
                new MachineSetting(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnablePrefetcher", 0),
                new MachineSetting(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnableSuperfetch", 0)),
            Group("BitsService", "HIGH RISK", "Background Intelligent Transfer Service (BITS)",
                "Disables the background transfer engine used by Windows servicing and other Microsoft applications. Downloads queued through BITS stop until the service is restored.",
                new[] { "BITS" }),
            Group("MediaNetworkSharing", "Safe", "Windows Media Network Sharing",
                "Disables Windows Media Player library sharing to network devices. Local media playback remains available; DLNA/media-streaming discovery from this PC stops.",
                new[] { "WMPNetworkSvc" }),
            Group("MobileHotspotService", "HIGH RISK", "Windows Mobile Hotspot",
                "Disables Internet Connection Sharing support used by Windows Mobile Hotspot. This PC can no longer share its network connection through the built-in hotspot feature.",
                new[] { "icssvc" }),
            Group("RemoteDesktopServices", "HIGH RISK", "Remote Desktop Services",
                "Disables Remote Desktop configuration, session, and redirection services. Incoming Remote Desktop connections and dependent remote-session features will stop working.",
                new[] { "SessionEnv", "TermService", "UmRdpService" }),
            Group("VpnRoutingServices", "HIGH RISK", "VPN / Dial-up / Routing Services",
                "Disables Remote Access and Connection Manager services. Windows VPN, dial-up, and routing connections can no longer be established while these services are disabled.",
                new[] { "RemoteAccess", "RasMan" }),
            Group("RemoteRegistryService", "Experimental", "Remote Registry",
                "Disables remote registry access from other computers. Local Registry Editor and normal local registry access remain available.",
                new[] { "RemoteRegistry" }),
            Group("DistributedLinkTracking", "Experimental", "Distributed Link Tracking",
                "Disables maintenance of links to files moved between NTFS volumes or network domains. Shortcuts to moved files may no longer be repaired automatically.",
                new[] { "TrkWks" }),
            Group("NetworkDataUsageMonitor", "HIGH RISK", "Network Data Usage Monitoring",
                "Disables the Windows Network Data Usage Monitoring driver service. Per-application data-usage statistics and software that reads NDU counters can stop updating.",
                new[] { "Ndu" }),
            Group("PrintingFaxServices", "HIGH RISK", "Printing & Fax Services",
                "Disables the Print Spooler and Fax services. Local/network printing, printer discovery, print queues, and Windows Fax features will be unavailable.",
                new[] { "Spooler", "Fax" }),
            Group("LegacyHomeGroupServices", "Safe", "Legacy HomeGroup Services",
                "Disables legacy HomeGroup listener/provider services when present. Modern Windows file sharing is unaffected; obsolete HomeGroup discovery stops.",
                new[] { "HomeGroupListener", "HomeGroupProvider" }),
            Group("WindowsBiometricService", "HIGH RISK", "Windows Biometric Service",
                "Disables the Windows biometric framework service. Fingerprint, face, or other biometric sign-in and applications using Windows biometrics can stop working.",
                new[] { "WbioSrvc" }),
            Group("RetailDemoService", "Safe", "Retail Demo Service",
                "Disables the Retail Demo service used on showroom devices. Normal consumer and business Windows installations do not require retail demonstration mode.",
                new[] { "RetailDemo" })
        };

        private static ServiceGroup Group(
            string id,
            string risk,
            string name,
            string description,
            string[] services,
            params MachineSetting[] settings) =>
            new(
                new ToolToggleDefinition(
                    id,
                    $"Services / {risk}",
                    name,
                    description,
                    true,
                    true,
                    Warning: WarningFor(id)),
                services,
                settings);

        private static string WarningFor(string id) => id switch
        {
            "BitsService" =>
                "WARNING: Disabling BITS can break Windows Update downloads and Microsoft Store background transfers. Restore BITS before repairing or installing Windows updates.",
            "PrintingFaxServices" =>
                "WARNING: Applying this option disables Print Spooler and Fax. You will not be able to print to local or network printers, process queued print jobs, discover printers, or use Windows Fax until the services are restored.",
            _ => string.Empty
        };

        private sealed record ServiceGroup(
            ToolToggleDefinition Definition,
            IReadOnlyList<string> Services,
            IReadOnlyList<MachineSetting> Settings);

        private sealed record ServiceRestoreWorkItem(
            string ServiceName,
            ServiceStartupRestorePlan Plan,
            bool HasSnapshot,
            PreviousServiceSnapshot? PreviousSnapshot = null);

        private sealed record MachineSetting(string Path, string Name, int Value);
    }
}
