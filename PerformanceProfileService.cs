using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal enum PerformanceProfileKind
    {
        CompetitiveGaming,
        OptimizedGaming,
        Balanced
    }

    internal readonly record struct PerformanceProfileApplyResult(
        bool Success,
        bool RolledBack,
        string ProfileName,
        string Message,
        bool RestartRequired = false);

    internal sealed class PerformanceProfileService
    {
        private const string BalancedTemplateGuid =
            "381b4222-f694-41f0-9685-ff5bb260df2e";
        private const string UltimateTemplateGuid =
            "e9a42b02-d5df-448d-aa00-03f14749eb61";
        private const string ManagedBalancedGuid =
            "b7c16f06-cf4d-4ad3-952e-46bf32f5072a";
        private const string ManagedUltimateGuid =
            "fe36c5e4-d35d-45be-874b-52fd9a85777c";

        private const string MmcssPriorityPath =
            @"SYSTEM\CurrentControlSet\Control\PriorityControl";
        private const string MmcssSystemProfilePath =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        private const string MmcssGamesPath =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";

        private static readonly Regex GuidPattern = new(
            @"(?i)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
            RegexOptions.CultureInvariant);

        private readonly GamingStatusService _statusService = new();
        private readonly PerformanceProfileExtendedService _extendedProfileService = new();
#if PROFILE_LIVE_AUDIT
        // Compiled only into the opt-in integration test executable, never the app.
        internal Action<ProfileVerificationResult>? VerifiedAuditCheckpoint { get; set; }
#endif

        public static bool IsAdministrator()
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public static string GetDisplayName(PerformanceProfileKind profile)
        {
            return profile switch
            {
                PerformanceProfileKind.CompetitiveGaming => "Competitive Gaming",
                PerformanceProfileKind.OptimizedGaming => "Optimized Gaming",
                PerformanceProfileKind.Balanced => "Balanced",
                _ => "Unknown"
            };
        }

        public static string GetConfirmationDescription(PerformanceProfileKind profile)
        {
            string powerPlan = profile == PerformanceProfileKind.Balanced
                ? "Windows Balanced"
                : "Ultimate Performance";

            return
                $"Profile: {GetDisplayName(profile)}\n" +
                $"Power plan: {powerPlan}\n" +
                "Scope: MMCSS, processor power policy, BCD/timer, TCP/QoS, and RSC\n\n" +
                "A transaction snapshot is kept in memory and restored automatically if any verification stage fails. " +
                "Game DVR remains independently controlled by Gaming Tweaks.";
        }

        public async Task<PerformanceProfileApplyResult> ApplyAsync(
            PerformanceProfileKind profile)
        {
            string profileName = GetDisplayName(profile);
            Stopwatch transactionClock = Stopwatch.StartNew();
            ProfileVerificationResult? fullVerification = null;
            bool? extendedRollbackSucceeded = null;
            bool extendedRollbackAttempted = false;

            if (!IsAdministrator())
            {
                return new PerformanceProfileApplyResult(
                    false,
                    false,
                    profileName,
                    "Administrator rights are required. Close the app, run Visual Studio " +
                    "as Administrator, then start the app again.");
            }

            string originalPowerGuid = await GetActivePowerGuidAsync();
            if (!Guid.TryParse(originalPowerGuid, out _))
            {
                return new PerformanceProfileApplyResult(
                    false,
                    false,
                    profileName,
                    "The current Windows power plan could not be read. No changes were made.");
            }

            IReadOnlyList<RegistryValueSnapshot> registrySnapshot;
            try
            {
                registrySnapshot = CaptureMmcssSnapshot();
            }
            catch (Exception exception)
            {
                return new PerformanceProfileApplyResult(
                    false,
                    false,
                    profileName,
                    $"MMCSS preflight failed. No changes were made.\n\n{exception.Message}");
            }

            PowerSchemeTarget? target = null;
            bool registryMayHaveChanged = false;

            try
            {
                target = await EnsureTargetPowerSchemeAsync(profile);

                registryMayHaveChanged = true;
                WriteMmcssProfile(GetDefinition(profile));

                CommandResult activation = await RunPowerCfgAsync(
                    "/setactive",
                    target.Value.Guid);

                if (!activation.Success)
                {
                    throw new InvalidOperationException(
                        "Windows rejected the power plan activation. " +
                        ValueOrFallback(activation.Error, activation.Output));
                }

                GamingStatusSnapshot verification =
                    await _statusService.ReadSnapshotAsync();
                string activeGuid = NormalizeGuid(verification.PowerPlanGuid);
                string expectedGuid = NormalizeGuid(target.Value.Guid);

                if (!string.Equals(
                        verification.Mmcss.Profile,
                        profileName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"MMCSS verification expected '{profileName}' but detected " +
                        $"'{verification.Mmcss.Profile}'.");
                }

                if (!string.Equals(activeGuid, expectedGuid, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Power plan verification expected {expectedGuid} but detected {activeGuid}.");
                }

                ExtendedProfileApplyResult extended =
                    await _extendedProfileService.ApplyAsync(
                        profile,
                        target.Value.Guid,
                        async () =>
                        {
                            GamingLiveStatusSnapshot full = await new GamingLiveStatusService().ReadSnapshotAsync();
                            PerformanceProfileState? state = full.PerformanceProfile;
                            fullVerification = state?.Best;
                            if (state is null || !state.Verified || state.Profile != profileName)
                            {
                                string details = state is null ? "Profile data unavailable." :
                                    string.Join("\n", state.Best.Checks.Where(check => !check.Pass)
                                        .Select(check => $"{check.Name}: expected {check.Expected}; actual {check.Actual}"));
                                throw new InvalidOperationException("Full profile verification failed.\n" + details);
                            }
#if PROFILE_LIVE_AUDIT
                            VerifiedAuditCheckpoint?.Invoke(fullVerification!);
#endif
                        });
                extendedRollbackAttempted = extended.RolledBack;
                extendedRollbackSucceeded = extended.RollbackSucceeded;
                if (!extended.Success)
                {
                    throw new InvalidOperationException(extended.Message);
                }

                Dictionary<string, string> known = PerformanceProfileVerificationService.ReadKnownPowerGuids();
                known[profileName] = target.Value.Guid;
                string saveWarning = PerformanceProfileTransaction.Save(PerformanceProfileTransaction.StatePath,
                    profileName, true, transactionClock.Elapsed.TotalSeconds, extended.RestartRequired,
                    target.Value.Guid, known, false, null, fullVerification, null);
                return new PerformanceProfileApplyResult(
                    true,
                    false,
                    profileName,
                    $"{profileName} configuration was applied and verified.\n\n" +
                    $"Active power plan: {verification.PowerPlanName}\n" +
                    "MMCSS: VERIFIED\n" +
                    "CPU policy: VERIFIED\n" +
                    "BCD/timer: VERIFIED\n" +
                    "TCP/QoS/RSC: VERIFIED\n\n" +
                    (extended.RestartRequired
                        ? "Restart Windows so the changed BCD/timer settings become authoritative."
                        : "BCD/timer settings were unchanged; no restart is required for those settings.") +
                    "\n\n" + PerformanceProfileTransaction.Format(fullVerification) +
                    (saveWarning.Length > 0 ? "\n\nWARNING: " + saveWarning : ""),
                    RestartRequired: extended.RestartRequired);
            }
            catch (Exception exception)
            {
                RollbackResult rollback = await RollbackAsync(
                    registryMayHaveChanged ? registrySnapshot : Array.Empty<RegistryValueSnapshot>(),
                    originalPowerGuid,
                    target);

                bool allRollbackSucceeded = rollback.Success &&
                    (!extendedRollbackAttempted || extendedRollbackSucceeded == true);
                string saveWarning = PerformanceProfileTransaction.Save(PerformanceProfileTransaction.StatePath,
                    profileName, false, transactionClock.Elapsed.TotalSeconds, false, target?.Guid,
                    PerformanceProfileVerificationService.ReadKnownPowerGuids(),
                    rollback.Attempted || extendedRollbackAttempted,
                    rollback.Attempted || extendedRollbackAttempted ? allRollbackSucceeded : null,
                    fullVerification, exception.Message);
                string rollbackText = rollback.Success
                    ? "MMCSS and active power-plan rollback completed successfully."
                    : "MMCSS/power-plan rollback was incomplete:\n" + rollback.Details;

                return new PerformanceProfileApplyResult(
                    false,
                    rollback.Attempted,
                    profileName,
                    $"The profile could not be applied.\n\n{exception.Message}\n\n{rollbackText}" +
                    "\n\n" + PerformanceProfileTransaction.Format(fullVerification) +
                    (saveWarning.Length > 0 ? "\n\nWARNING: " + saveWarning : ""));
            }
        }

        private static ProfileDefinition GetDefinition(PerformanceProfileKind profile)
        {
            return profile switch
            {
                PerformanceProfileKind.CompetitiveGaming => new ProfileDefinition(
                    0x24, 1, 2, 8, "High", "High", 10000, 1, 1),
                PerformanceProfileKind.OptimizedGaming => new ProfileDefinition(
                    0x18, 20, 4, 6, "Medium", "High", 10000, 0, 0),
                PerformanceProfileKind.Balanced => new ProfileDefinition(
                    0x02, 20, 2, 8, "Medium", "Normal", 10000, 0, 0),
                _ => throw new ArgumentOutOfRangeException(nameof(profile))
            };
        }

        private static IReadOnlyList<RegistryValueSnapshot> CaptureMmcssSnapshot()
        {
            return new[]
            {
                CaptureRegistryValue(MmcssPriorityPath, "Win32PrioritySeparation"),
                CaptureRegistryValue(MmcssSystemProfilePath, "SystemResponsiveness"),
                CaptureRegistryValue(MmcssGamesPath, "Priority"),
                CaptureRegistryValue(MmcssGamesPath, "GPU Priority"),
                CaptureRegistryValue(MmcssGamesPath, "Scheduling Category"),
                CaptureRegistryValue(MmcssGamesPath, "SFIO Priority"),
                CaptureRegistryValue(MmcssGamesPath, "Clock Rate"),
                CaptureRegistryValue(MmcssSystemProfilePath, "NoLazyMode"),
                CaptureRegistryValue(MmcssSystemProfilePath, "AlwaysOn")
            };
        }

        private static RegistryValueSnapshot CaptureRegistryValue(
            string path,
            string name)
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path, writable: false);
            if (key is null)
            {
                throw new InvalidOperationException($"Required registry path is unavailable: HKLM\\{path}");
            }

            bool exists = Array.Exists(
                key.GetValueNames(),
                valueName => string.Equals(valueName, name, StringComparison.OrdinalIgnoreCase));

            return exists
                ? new RegistryValueSnapshot(path, name, true, key.GetValue(name), key.GetValueKind(name))
                : new RegistryValueSnapshot(path, name, false, null, RegistryValueKind.None);
        }

        private static void WriteMmcssProfile(ProfileDefinition definition)
        {
            SetRegistryValue(
                MmcssPriorityPath,
                "Win32PrioritySeparation",
                definition.Win32PrioritySeparation,
                RegistryValueKind.DWord);
            SetRegistryValue(
                MmcssSystemProfilePath,
                "SystemResponsiveness",
                definition.SystemResponsiveness,
                RegistryValueKind.DWord);
            SetRegistryValue(
                MmcssGamesPath,
                "Priority",
                definition.GamesPriority,
                RegistryValueKind.DWord);
            SetRegistryValue(
                MmcssGamesPath,
                "GPU Priority",
                definition.GpuPriority,
                RegistryValueKind.DWord);
            SetRegistryValue(
                MmcssGamesPath,
                "Scheduling Category",
                definition.SchedulingCategory,
                RegistryValueKind.String);
            SetRegistryValue(
                MmcssGamesPath,
                "SFIO Priority",
                definition.SfioPriority,
                RegistryValueKind.String);
            SetRegistryValue(
                MmcssGamesPath,
                "Clock Rate",
                definition.ClockRate,
                RegistryValueKind.DWord);
            SetRegistryValue(
                MmcssSystemProfilePath,
                "NoLazyMode",
                definition.NoLazyMode,
                RegistryValueKind.DWord);
            SetRegistryValue(
                MmcssSystemProfilePath,
                "AlwaysOn",
                definition.AlwaysOn,
                RegistryValueKind.DWord);
        }

        private static void SetRegistryValue(
            string path,
            string name,
            object value,
            RegistryValueKind kind)
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path, writable: true);
            if (key is null)
            {
                throw new UnauthorizedAccessException(
                    $"Cannot open HKLM\\{path} for writing. Administrator rights are required.");
            }

            key.SetValue(name, value, kind);
        }

        private static async Task<PowerSchemeTarget> EnsureTargetPowerSchemeAsync(
            PerformanceProfileKind profile)
        {
            string templateGuid = profile == PerformanceProfileKind.Balanced
                ? BalancedTemplateGuid
                : UltimateTemplateGuid;
            string expectedName = profile == PerformanceProfileKind.Balanced
                ? "Balanced"
                : "Ultimate Performance";
            string managedGuid = profile == PerformanceProfileKind.Balanced
                ? ManagedBalancedGuid
                : ManagedUltimateGuid;

            CommandResult listResult = await RunPowerCfgAsync("/list");
            if (!listResult.Success)
            {
                throw new InvalidOperationException(
                    "Windows power schemes could not be enumerated. " +
                    ValueOrFallback(listResult.Error, listResult.Output));
            }

            // Apply and Verify must choose the same installed plan, including a
            // valid historical plan GUID when multiple Ultimate plans exist.
            string? existingGuid = PerformanceProfileVerificationService.ResolveTarget(
                GetDisplayName(profile), new GamingLiveCommandResult(0, listResult.Output, listResult.Error),
                PerformanceProfileVerificationService.ReadKnownPowerGuids());

            if (Guid.TryParse(existingGuid, out _))
            {
                return new PowerSchemeTarget(existingGuid!, false);
            }

            CommandResult duplicateResult = await RunPowerCfgAsync(
                "/duplicatescheme",
                templateGuid,
                managedGuid);

            if (!duplicateResult.Success)
            {
                throw new InvalidOperationException(
                    $"The {expectedName} power plan is missing and could not be created. " +
                    ValueOrFallback(duplicateResult.Error, duplicateResult.Output));
            }

            CommandResult verificationList = await RunPowerCfgAsync("/list");
            string createdGuid = verificationList.Success
                ? FindPowerSchemeGuid(
                    verificationList.Output,
                    managedGuid,
                    managedGuid,
                    expectedName)
                : string.Empty;

            if (!Guid.TryParse(createdGuid, out _))
            {
                throw new InvalidOperationException(
                    $"Windows created a power plan but its GUID could not be verified. Output: " +
                    ValueOrFallback(duplicateResult.Output, "none"));
            }

            return new PowerSchemeTarget(createdGuid, true);
        }

        private static string FindPowerSchemeGuid(
            string listOutput,
            string templateGuid,
            string managedGuid,
            string expectedName)
        {
            foreach (string line in listOutput.Split(
                         new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                Match match = GuidPattern.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                if (string.Equals(
                        match.Value,
                        templateGuid,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        match.Value,
                        managedGuid,
                        StringComparison.OrdinalIgnoreCase) ||
                    line.Contains(expectedName, StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }
            }

            return string.Empty;
        }

        private static async Task<string> GetActivePowerGuidAsync()
        {
            CommandResult result = await RunPowerCfgAsync("/getactivescheme");
            Match match = GuidPattern.Match(result.Output);
            return result.Success && match.Success ? match.Value : string.Empty;
        }

        private static async Task<RollbackResult> RollbackAsync(
            IReadOnlyList<RegistryValueSnapshot> registrySnapshot,
            string originalPowerGuid,
            PowerSchemeTarget? target)
        {
            bool attempted = registrySnapshot.Count > 0 || target.HasValue;
            List<string> errors = new();

            if (registrySnapshot.Count > 0)
            {
                foreach (RegistryValueSnapshot value in registrySnapshot)
                {
                    try
                    {
                        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                            value.Path,
                            writable: true);
                        if (key is null)
                        {
                            throw new InvalidOperationException(
                                $"Cannot reopen HKLM\\{value.Path}.");
                        }

                        if (value.Existed)
                        {
                            key.SetValue(value.Name, value.Value!, value.Kind);
                        }
                        else
                        {
                            key.DeleteValue(value.Name, throwOnMissingValue: false);
                        }
                    }
                    catch (Exception exception)
                    {
                        errors.Add($"Registry {value.Name}: {exception.Message}");
                    }
                }
            }

            CommandResult restorePower = await RunPowerCfgAsync(
                "/setactive",
                originalPowerGuid);
            if (!restorePower.Success)
            {
                errors.Add(
                    "Power plan: " + ValueOrFallback(restorePower.Error, restorePower.Output));
            }

            if (target is { Created: true } && restorePower.Success)
            {
                CommandResult deleteCreated = await RunPowerCfgAsync(
                    "/delete",
                    target.Value.Guid);
                if (!deleteCreated.Success)
                {
                    errors.Add(
                        "Temporary power plan cleanup: " +
                        ValueOrFallback(deleteCreated.Error, deleteCreated.Output));
                }
            }

            return new RollbackResult(
                attempted,
                attempted && errors.Count == 0,
                string.Join("\n", errors));
        }

        private static async Task<CommandResult> RunPowerCfgAsync(params string[] arguments)
        {
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe");
            try
            {
                NativeCommandResult result = await new NativeCommandRunner().RunAsync(executable, arguments, TimeSpan.FromSeconds(10));
                return new CommandResult(!result.TimedOut && result.ExitCode == 0, result.StandardOutput.Trim(), result.StandardError.Trim());
            }
            catch (Exception exception)
            {
                return new CommandResult(false, string.Empty, exception.Message);
            }
        }

        private static string NormalizeGuid(string value)
        {
            return Guid.TryParse(value, out Guid parsed)
                ? parsed.ToString("D")
                : value.Trim();
        }

        private static string ValueOrFallback(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private readonly record struct ProfileDefinition(
            int Win32PrioritySeparation,
            int SystemResponsiveness,
            int GamesPriority,
            int GpuPriority,
            string SchedulingCategory,
            string SfioPriority,
            int ClockRate,
            int NoLazyMode,
            int AlwaysOn);

        private readonly record struct RegistryValueSnapshot(
            string Path,
            string Name,
            bool Existed,
            object? Value,
            RegistryValueKind Kind);

        private readonly record struct PowerSchemeTarget(string Guid, bool Created);

        private readonly record struct RollbackResult(
            bool Attempted,
            bool Success,
            string Details);

        private readonly record struct CommandResult(
            bool Success,
            string Output,
            string Error);
    }
}
