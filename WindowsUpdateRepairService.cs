using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal readonly record struct MaintenanceOperationResult(
        bool Success,
        int WarningCount,
        string Report);

    // All machine access is supplied together so the complete repair workflow can
    // be regression-tested without touching the host's services or update cache.
    internal sealed record UpdateRepairOperations(
        Func<bool> IsAdministrator,
        string WindowsDirectory,
        Func<string, int?> ReadStart,
        Func<string, CancellationToken, Task<string>> QueryState,
        Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<NativeCommandResult>> RunCommand,
        Func<string, StringBuilder, CancellationToken, Task> RemoveLegacyLock,
        Func<CancellationToken, Task> PollDelay);

    internal sealed partial class WindowsUpdateRepairService
    {
        // Microsoft's cache-reset sequence uses these three services. Do not make
        // stopping protected update-coordination services a prerequisite or alter
        // their configuration, permissions, process, or scheduled tasks.
        private static readonly string[] RepairServices = { "BITS", "wuauserv", "cryptsvc" };

        private static readonly NativeCommandRunner CommandRunner = new();
        private readonly UpdateRepairOperations _operations;

        public WindowsUpdateRepairService() : this(new UpdateRepairOperations(
            WindowsPrivilegeService.IsAdministrator,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ReadServiceStartValue, QueryNativeServiceStateAsync,
            (file, args, timeout, token) => CommandRunner.RunAsync(file, args, timeout, token),
            RemoveLegacyServiceLockAsync, token => Task.Delay(250, token))) { }

        internal WindowsUpdateRepairService(UpdateRepairOperations operations) => _operations = operations;

        public async Task<MaintenanceOperationResult> RunAsync(
            IProgress<MaintenanceProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!_operations.IsAdministrator())
            {
                return new MaintenanceOperationResult(
                    false,
                    0,
                    "Administrator rights are required. Close the application and start it with Run as administrator.");
            }

            StringBuilder report = new();
            List<string> warnings = new();
            MaintenanceStageTracker stages = new(progress, () => warnings.Count);
            progress = stages;
            string windows = _operations.WindowsDirectory;
            List<UpdateCacheBackup> cacheBackups = new();
            // Record before issuing each stop, including ambiguous command failures.
            HashSet<string> recoveryServices = new(StringComparer.OrdinalIgnoreCase);

            report.AppendLine("WINDOWS UPDATE FIX");
            report.AppendLine($"Started: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            report.AppendLine(new string('=', 74));
            report.AppendLine("Scope: BITS, wuauserv and cryptsvc; DataStore, Download and catroot2 caches.");
            report.AppendLine("UsoSvc/DoSvc are left under Windows control. No service ACL, ownership, network, firewall or proxy reset is performed.");

            try
            {
                AddStage(report, progress, 1, 6, "Repair update-service prerequisites");
                foreach (string serviceName in RepairServices)
                {
                    await RestoreServiceConfigurationAsync(
                        serviceName,
                        report,
                        warnings,
                        cancellationToken);
                }

                AddStage(report, progress, 2, 6, "Stop Windows Update services");
                await EnsureServicesStoppedAsync(report, warnings, recoveryServices, cancellationToken);

                AddStage(report, progress, 3, 6, "Reset SoftwareDistribution cache");
                foreach (string cache in new[] { @"SoftwareDistribution\DataStore", @"SoftwareDistribution\Download" })
                    cacheBackups.Add(await UpdateCacheReset.RunAsync(windows, cache,
                        token => EnsureServicesStoppedAsync(report, warnings, recoveryServices, token),
                        line => report.AppendLine(line), cancellationToken));

                AddStage(report, progress, 4, 6, "Reset catroot2 cache");
                cacheBackups.Add(await UpdateCacheReset.RunAsync(windows, @"System32\catroot2",
                    token => EnsureServicesStoppedAsync(report, warnings, recoveryServices, token),
                    line => report.AppendLine(line), cancellationToken));

                AddStage(report, progress, 5, 6, "Restart Windows Update services");
                foreach (string serviceName in new[] { "cryptsvc", "BITS", "wuauserv" })
                {
                    if (!await SetServiceStateAsync(
                        serviceName,
                        running: true,
                        report,
                        warnings,
                        cancellationToken))
                        throw new InvalidOperationException($"Cache reset is incomplete: {serviceName} could not be restarted. See command details above.");
                }

                AddStage(report, progress, 6, 6, "Verify Windows Update repair");
                List<string> verificationIssues = new();
                foreach (string serviceName in RepairServices)
                {
                    int? actualStart = _operations.ReadStart(serviceName);
                    string runtime = await QueryServiceStateAsync(serviceName, cancellationToken);
                    report.AppendLine(
                        $"VERIFY {serviceName}: Start={(actualStart?.ToString(CultureInfo.InvariantCulture) ?? "MISSING")} " +
                        $"required={UpdateServiceStartupPolicy.RequiredStart(serviceName)}; Runtime={runtime}");
                    foreach (string issue in UpdateServiceStartupPolicy.Verify(serviceName, actualStart, runtime))
                    {
                        warnings.Add(issue);
                        verificationIssues.Add(issue);
                        report.AppendLine("WARNING: " + issue);
                    }
                }
                if (verificationIssues.Count > 0)
                    throw new InvalidOperationException("Repair could not be verified: " + string.Join(" ", verificationIssues));

                foreach (UpdateCacheBackup cache in cacheBackups)
                {
                    if (cache.Backup is null) continue;
                    if (!Directory.Exists(cache.Backup))
                        throw new IOException("Cache backup is missing after reset: " + cache.Backup);
                    report.AppendLine("Verified preserved backup: " + cache.Backup);
                }

                report.AppendLine("Local cache/service checks passed. Open Settings > Windows Update > Check for updates and retry the download.");
                report.AppendLine("Download completion was not tested. Windows will rebuild its cache; this can take time. Backups were retained, not deleted.");
                AppendFinalResult(report, true, warnings, null);
                stages.Complete(true);
                return new MaintenanceOperationResult(true, warnings.Count, report.ToString().TrimEnd());
            }
            catch (Exception exception)
            {
                report.AppendLine($"ERROR: {exception.Message}");
                // Cleanup must run even if the caller cancelled the repair.
                await RecoverServicesAsync(report, warnings, recoveryServices);
                report.AppendLine("No forced termination or automatic reboot was performed. If cache files remain locked, restart Windows manually when safe and retry.");
                report.AppendLine("Use Save TXT to retain this report if the repair or download still fails. A partial cache reset is not a successful repair; existing backups remain in place.");
                AppendFinalResult(report, false, warnings, exception.Message);
                stages.Complete(false);
                return new MaintenanceOperationResult(false, warnings.Count, report.ToString().TrimEnd());
            }
        }

        private async Task RestoreServiceConfigurationAsync(
            string serviceName,
            StringBuilder report,
            List<string> warnings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int? before = _operations.ReadStart(serviceName);
            if (before is null)
            {
                throw new InvalidOperationException($"Cannot read {serviceName} startup configuration; the service may be missing or access was denied. Cache reset has not started.");
            }
            await _operations.RemoveLegacyLock(serviceName, report, cancellationToken);

            if (UpdateServiceStartupPolicy.IsOperationalStart(serviceName, before))
            {
                report.AppendLine($"{serviceName}: Preserved operational Start={before}; required {UpdateServiceStartupPolicy.RequiredStart(serviceName)}. Runtime will be started and verified separately.");
                return;
            }

            int startValue = UpdateServiceStartupPolicy.RepairStart(serviceName);

            string sc = GetSystemTool("sc.exe");
            string startMode = startValue == 2 ? "auto" : "demand";
            NativeCommandResult result = await _operations.RunCommand(
                sc,
                new[] { "config", serviceName, "start=", startMode },
                TimeSpan.FromSeconds(20),
                cancellationToken);

            LogCommand(report, serviceName, "config", result);
            int? after = _operations.ReadStart(serviceName);

            if (UpdateServiceStartupPolicy.IsOperationalStart(serviceName, after))
            {
                report.AppendLine($"{serviceName}: Repaired Start {before} -> {after}; required {UpdateServiceStartupPolicy.RequiredStart(serviceName)}.");
            }
            else
            {
                throw new InvalidOperationException($"Unable to restore {serviceName} Start={startValue}; sc.exe exit {result.ExitCode}. Cache reset has not started. Service permissions were not overridden.");
            }
        }

        private async Task<bool> SetServiceStateAsync(
            string serviceName,
            bool running,
            StringBuilder report,
            List<string> warnings,
            CancellationToken cancellationToken)
        {
            string current = await QueryServiceStateAsync(serviceName, cancellationToken);
            string target = running ? "Running" : "Stopped";
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
            {
                report.AppendLine($"Service already {target.ToLowerInvariant()}: {serviceName}");
                return true;
            }

            string sc = GetSystemTool("sc.exe");
            NativeCommandResult result = await _operations.RunCommand(
                sc,
                new[] { running ? "start" : "stop", serviceName },
                TimeSpan.FromSeconds(20),
                cancellationToken);
            LogCommand(report, serviceName, running ? "start" : "stop", result);

            Stopwatch wait = Stopwatch.StartNew();
            do
            {
                await _operations.PollDelay(cancellationToken);
                current = await QueryServiceStateAsync(serviceName, cancellationToken);
                if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                {
                    report.AppendLine($"{target} service: {serviceName}");
                    return true;
                }
                // A rejected command cannot be fixed by repeatedly sleeping. Still
                // check read-back first to handle races with another service client.
                if (result.TimedOut || (result.ExitCode != 0 && current is not ("StartPending" or "StopPending"))) break;
            }
            while (wait.Elapsed < TimeSpan.FromSeconds(30));

            string warning = $"Service {serviceName} did not reach {target} (sc.exe exit {result.ExitCode}; current: {current}).";
            warnings.Add(warning);
            report.AppendLine($"WARNING: {warning}");
            return false;
        }

        private async Task EnsureServicesStoppedAsync(StringBuilder report, List<string> warnings, HashSet<string> recoveryServices, CancellationToken token)
        {
            string[] services = { "BITS", "wuauserv", "cryptsvc" };
            foreach (string name in services)
            {
                string before = await QueryServiceStateAsync(name, token);
                if (before is "Unknown" or "NotFound")
                    throw new InvalidOperationException($"Cannot read required service {name}: {before}. No cache rename will be attempted.");
                if (before != "Stopped") recoveryServices.Add(name);
                if (!await SetServiceStateAsync(name, false, report, warnings, token))
                    throw new InvalidOperationException($"Cache reset was not started: {name} could not be stopped.");
            }
            // A service can restart while a later service is being stopped.
            // Never rename a cache based only on an earlier sc.exe exit code.
            foreach (string name in services)
            {
                string state = await QueryServiceStateAsync(name, token);
                if (state != "Stopped")
                    throw new InvalidOperationException($"Cache reset was not started: {name} changed to {state}. Let active updates finish, then retry.");
            }
        }

        private async Task<string> QueryServiceStateAsync(string serviceName, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string state = await _operations.QueryState(serviceName, token);
            // The shared native reader uses spaced names; sc.exe uses numeric
            // states. Normalize both paths before evaluating pending transitions.
            return state switch
            {
                "Start Pending" => "StartPending",
                "Stop Pending" => "StopPending",
                "Continue Pending" => "ContinuePending",
                "Pause Pending" => "PausePending",
                _ => state
            };
        }

        private static async Task<string> QueryNativeServiceStateAsync(
            string serviceName,
            CancellationToken cancellationToken)
        {
            if (GamingLiveStatusService.TryReadServiceState(serviceName, out string nativeState)) return nativeState;
            NativeCommandResult result = await CommandRunner.RunAsync(
                GetSystemTool("sc.exe"),
                new[] { "query", serviceName },
                TimeSpan.FromSeconds(10),
                cancellationToken);
            Match match = ServiceStatePattern().Match(result.CombinedOutput);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int state))
            {
                return result.ExitCode == 1060 ? "NotFound" : "Unknown";
            }

            return state switch
            {
                1 => "Stopped",
                2 => "StartPending",
                3 => "StopPending",
                4 => "Running",
                5 => "ContinuePending",
                6 => "PausePending",
                7 => "Paused",
                _ => $"State{state}"
            };
        }

        private async Task RecoverServicesAsync(
            StringBuilder report,
            List<string> warnings,
            HashSet<string> recoveryServices)
        {
            if (recoveryServices.Count == 0) return;
            report.AppendLine("Recovering Windows Update services and verifying runtime states...");
            string[] services = { "cryptsvc", "BITS", "wuauserv" };
            foreach (string serviceName in services)
            {
                if (!recoveryServices.Contains(serviceName)) continue;
                try
                {
                    await SetServiceStateAsync(serviceName, true, report, warnings, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    string warning = $"Recovery {serviceName}: {exception.Message}";
                    warnings.Add(warning);
                    report.AppendLine("WARNING: " + warning);
                }
            }
        }

        private static async Task RemoveLegacyServiceLockAsync(
            string serviceName,
            StringBuilder report,
            CancellationToken cancellationToken)
        {
            string safeName = Regex.Replace(serviceName, "[^A-Za-z0-9_.-]", "_");
            string taskName = $"WPT78_ServiceLock_{safeName}";
            try
            {
                await CommandRunner.RunAsync(
                    GetSystemTool("schtasks.exe"),
                    new[] { "/delete", "/tn", taskName, "/f" },
                    TimeSpan.FromSeconds(10),
                    cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
            }

            string lockFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NaufalWindowsPowertoys",
                $"service-lock-{safeName}.cmd");
            try
            {
                if (File.Exists(lockFile))
                {
                    File.Delete(lockFile);
                    report.AppendLine($"Removed legacy service lock file: {Path.GetFileName(lockFile)}");
                }
            }
            catch (Exception exception)
            {
                report.AppendLine($"WARNING: Could not remove {Path.GetFileName(lockFile)}: {exception.Message}");
            }
        }

        private static int? ReadServiceStartValue(string serviceName)
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}",
                    writable: false);
                object? value = key?.GetValue("Start");
                return value is null
                    ? null
                    : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static string GetSystemTool(string fileName)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                fileName);
        }

        private static void LogCommand(StringBuilder report, string service, string action, NativeCommandResult result)
        {
            report.AppendLine($"sc.exe {action} {service}: exit={result.ExitCode}; timed out={result.TimedOut}");
            if (!string.IsNullOrWhiteSpace(result.CombinedOutput)) report.AppendLine(result.CombinedOutput.Trim());
        }

        private static void AddStage(
            StringBuilder report,
            IProgress<MaintenanceProgressUpdate>? progress,
            int index,
            int count,
            string name)
        {
            MaintenanceProgress.StartStage(progress, index, count, name);
            report.AppendLine();
            report.AppendLine($"[{index}/{count}] {name}");
            report.AppendLine(new string('-', 74));
        }

        private static void AppendFinalResult(
            StringBuilder report,
            bool success,
            IReadOnlyCollection<string> warnings,
            string? error)
        {
            report.AppendLine();
            report.AppendLine(new string('=', 74));
            report.AppendLine(success ? "REPAIR COMPLETED" : "REPAIR FAILED");
            report.AppendLine($"Warnings: {warnings.Count}");
            foreach (string warning in warnings)
            {
                report.AppendLine($"- {warning}");
            }
            if (!string.IsNullOrWhiteSpace(error))
            {
                report.AppendLine($"Error: {error}");
            }
        }

        [GeneratedRegex(@"(?im)STATE\s*:\s*(\d+)")]
        private static partial Regex ServiceStatePattern();
    }
}
