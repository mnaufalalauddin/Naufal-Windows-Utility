using Naufal_Windows_Tech_s_Powertoys;

internal static class WindowsUpdateWorkflowTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        var queries = new List<string[]>();
        var stamp = new DateTimeOffset(2026, 1, 2, 11, 4, 5, TimeSpan.FromHours(7));
        var activity = await WindowsUpdateRepairService.ReadRetryActivityAsync(stamp, (exe, args, _) =>
        {
            queries.Add(args.ToArray());
            assert(Path.GetFileName(exe) == "wevtutil.exe", "Retry check only queries the local Windows event log");
            return Task.FromResult(args[1] == "System"
                ? new NativeCommandResult(0, "Fixture KB: download failed", "", false, TimeSpan.Zero)
                : new NativeCommandResult(0, "", "", false, TimeSpan.Zero));
        });
        assert(queries.Count == 2 && queries.All(q => q[0] == "qe" && q[2].Contains("2026-01-02T04:04:05.000Z") && q[2].Contains("WindowsUpdateClient")),
            "Retry check restricts both channels to the repair completion time and update provider");
        assert(activity.Contains("No Windows Update events") && activity.Contains("Fixture KB: download failed"), "Missing and failed downloads are distinguishable");
        var denied = await WindowsUpdateRepairService.ReadRetryActivityAsync(stamp, (_, _, _) =>
            Task.FromResult(new NativeCommandResult(5, "", "Access denied", false, TimeSpan.Zero)));
        assert(denied.Contains("Access denied") && !denied.Contains("No Windows Update events"), "Read failures do not masquerade as empty event logs");
        using (var f = new Fixture())
        {
            var result = await f.Run();
            assert(result.Success && result.WarningCount == 0, "Update repair completes all six stages with verified core services");
            assert(f.Backups == 3 && f.CacheFiles == 0, "Update workflow preserves all three original caches as backups");
            assert(f.States.Values.All(s => s == "Running"), "Update workflow restarts the three required services");
            assert(f.Commands.All(c => !c.Contains("DoSvc") && !c.Contains("UsoSvc")), "Protected update coordinators are never queried/stopped/configured by reset");
            assert(result.Report.Contains("Download completion was not tested") && result.Report.Contains("Check for updates"), "Local repair success does not claim successful online download");
            assert(f.Progress.Any(p => p.StageIndex == 6 && p.Status == "PASS"), "Final stage passes only on verified repair");
        }
        using (var f = new Fixture { Admin = false })
        {
            var result = await f.Run();
            assert(!result.Success && f.Commands.Count == 0 && f.Backups == 0, "Non-admin repair performs no commands or cache changes");
        }
        using (var f = new Fixture())
        {
            f.Starts["wuauserv"] = null;
            var result = await f.Run();
            assert(!result.Success && f.Commands.Count == 0 && f.CacheFiles == 3, "Missing required configuration aborts before stopping services or touching caches");
        }
        using (var f = new Fixture())
        {
            f.Starts["wuauserv"] = 4;
            f.FailAction = "config";
            var result = await f.Run();
            assert(!result.Success && f.Backups == 0 && f.Starts["wuauserv"] == 4, "Denied startup repair has no registry fallback or cache mutation");
            assert(!f.Commands.Any(c => c.StartsWith("start ")) && result.Report.Contains("fixture access denied"), "Prerequisite failure does not start unrelated services and preserves native diagnostics");
        }
        using (var f = new Fixture())
        {
            f.Starts["BITS"] = 4;
            var result = await f.Run();
            assert(result.Success && f.Starts["BITS"] == 3, "Disabled BITS is repaired through verified service configuration");
        }
        using (var f = new Fixture { FailAction = "stop", FailService = "wuauserv" })
        {
            var result = await f.Run();
            assert(!result.Success && f.Backups == 0 && f.CacheFiles == 3, "Denied stop prevents all cache renames");
            assert(f.States["BITS"] == "Running" && !f.Commands.Contains("start cryptsvc"), "Stop failure recovers only previously running services whose stop was attempted");
            assert(result.Report.Contains("exit=5") && result.Report.Contains("fixture access denied"), "Service error code and command output are retained in repair report");
            assert(f.Progress.Any(p => p.StageIndex == 2 && p.Status == "FAILED") && f.Progress.Any(p => p.StageIndex == 6 && p.Status == "SKIPPED"), "Stop failure never certifies later stages");
        }
        using (var f = new Fixture { FailAction = "start", FailService = "BITS" })
        {
            var result = await f.Run();
            assert(!result.Success && f.Backups == 3, "Failed restart cannot produce completed repair even after cache reset");
            assert(f.States["cryptsvc"] == "Running" && f.States["wuauserv"] == "Running", "Recovery continues for other services after one failed restart");
        }
        using (var f = new Fixture { FailAction = "stop", FailService = "cryptsvc", FailureExitCode = 1051 })
        {
            var result = await f.Run();
            assert(f.Backups == 2 && f.CacheFiles == 1, "CryptSvc 1051 still permits guarded download/database reset while leaving catroot2 intact");
            assert(!result.Success && result.PartialRepair && result.WarningCount > 0 && result.Report.Contains("REPAIR PARTIAL"), "Skipped catalog reset never claims full repair success");
            assert(f.Progress.Any(p => p.StageIndex == 4 && p.Status == "SKIPPED"), "Blocked catalog stage is explicitly skipped, not passed");
            assert(f.States.Values.All(s => s == "Running"), "Limited repair verifies all required services running");
            assert(f.Commands.Count(c => c == "stop cryptsvc") == 1, "Dependent-service refusal is not retried for every cache");
            assert(f.Commands.All(c => c.Split(' ')[1] is "BITS" or "wuauserv" or "cryptsvc"), "No dependent or security service is stopped to work around 1051");
        }
        foreach (int code in new[] { 5, 1060, 1051 })
        using (var f = new Fixture { FailAction = "stop", FailService = "cryptsvc", FailureExitCode = code, FailureTimedOut = code == 1051 })
        {
            var result = await f.Run();
            assert(!result.Success && !result.PartialRepair && f.Backups == 0, "Only confirmed non-timeout 1051 can authorize limited repair: " + code);
            assert(f.States.Values.All(s => s == "Running"), "Rejected catalog stop recovers download services: " + code);
        }
        using (var f = new Fixture { FailAction = "stop", FailService = "cryptsvc", FailureExitCode = 1051, FailureState = "Unknown" })
        {
            var result = await f.Run();
            assert(!result.Success && !result.PartialRepair && f.Backups == 0, "1051 with unreadable runtime cannot authorize limited repair");
        }
        using (var f = new Fixture { FailAction = "stop", FailService = "cryptsvc", FailureExitCode = 1051 })
        {
            f.OnStage = stage => { if (stage == 6) f.States["cryptsvc"] = "Unknown"; };
            var result = await f.Run();
            assert(!result.Success && !result.PartialRepair && f.Backups == 2, "Failed final service verification overrides partial-repair outcome");
        }
        using (var f = new Fixture { FailAction = "stop", FailService = "cryptsvc", FailureExitCode = 1051 })
        {
            f.OnStage = stage => { if (stage == 3) f.States["wuauserv"] = "Running"; };
            var result = await f.Run();
            assert(result.PartialRepair && f.Commands.Count(c => c == "stop wuauserv") == 2, "Limited repair still rechecks and stops download services before cache rename");
        }
        foreach (string state in new[] { "Unknown", "NotFound" })
        using (var f = new Fixture())
        {
            f.States["wuauserv"] = state;
            var result = await f.Run();
            assert(!result.Success && f.Backups == 0 && f.CacheFiles == 3, "Unreadable/missing runtime cannot authorize cache changes: " + state);
            assert(f.States["BITS"] == "Running", "Earlier stopped service recovered when later runtime is unreadable: " + state);
        }
        using (var f = new Fixture())
        {
            f.OnStage = stage => { if (stage == 3) f.States["BITS"] = "Running"; };
            var result = await f.Run();
            assert(result.Success && f.Commands.Count(c => c == "stop BITS") == 2, "Service that restarts before cache reset is stopped and rechecked");
        }
        using (var f = new Fixture())
        {
            f.OnStage = stage => { if (stage == 6) f.Starts["wuauserv"] = 4; };
            var result = await f.Run();
            assert(!result.Success && result.Report.Contains("Repair could not be verified"), "Disabled startup read-back cannot produce successful repair");
        }
        foreach (string pending in new[] { "Stop Pending", "StopPending" })
        using (var f = new Fixture { FailAction = "stop", FailService = "BITS", FailureState = pending })
        {
            int polls = 0;
            f.OnPoll = () => { if (++polls == 2) f.States["BITS"] = "Stopped"; };
            var result = await f.Run();
            assert(result.Success, "Rejected stop with an in-flight transition uses read-back rather than false failure: " + pending);
        }
        using (var f = new Fixture())
        {
            f.OnStage = stage => { if (stage == 6) f.States["BITS"] = "Unknown"; };
            var result = await f.Run();
            assert(!result.Success && result.Report.Contains("Repair could not be verified"), "Final read-back failure is failure, not success with warnings");
        }
        using (var f = new Fixture())
        {
            f.OnStage = stage => { if (stage == 4) File.WriteAllText(Path.Combine(f.Root, "System32", "catroot2", "lock.txt"), "fixture"); };
            // An actual open handle exercises the locked-cache recovery path, in a temp fixture only.
            using var handle = new FileStream(Path.Combine(f.Root, "System32", "catroot2", "fixture.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
            var result = await f.Run();
            assert(!result.Success && f.Backups == 2 && f.CacheFiles == 1, "Locked final cache reports partial reset and preserves previous backups");
            assert(f.States.Values.All(s => s == "Running"), "Cache failure still recovers services stopped by this repair");
        }
        using (var f = new Fixture())
        using (var cancellation = new CancellationTokenSource())
        {
            f.OnStage = stage => { if (stage == 3) cancellation.Cancel(); };
            var result = await f.Run(cancellation.Token);
            assert(!result.Success && f.Backups == 0 && f.States.Values.All(s => s == "Running"), "Cancellation after service stops recovers with an independent token without renaming caches");
        }
    }

    private sealed class Fixture : IDisposable, IProgress<MaintenanceProgressUpdate>
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "nwu-update-workflow-" + Guid.NewGuid().ToString("N"));
        internal readonly Dictionary<string, int?> Starts = new(StringComparer.OrdinalIgnoreCase) { ["BITS"] = 3, ["wuauserv"] = 3, ["cryptsvc"] = 2 };
        internal readonly Dictionary<string, string> States = new(StringComparer.OrdinalIgnoreCase) { ["BITS"] = "Running", ["wuauserv"] = "Running", ["cryptsvc"] = "Running" };
        internal readonly List<string> Commands = new();
        internal readonly List<MaintenanceProgressUpdate> Progress = new();
        internal bool Admin = true;
        internal string? FailAction, FailService;
        internal string? FailureState;
        internal int FailureExitCode = 5;
        internal bool FailureTimedOut;
        internal Action<int>? OnStage;
        internal Action? OnPoll;
        internal int Backups => Directory.GetDirectories(Root, "*.bak-wpt-*", SearchOption.AllDirectories).Length;
        internal int CacheFiles => new[] { @"SoftwareDistribution\DataStore", @"SoftwareDistribution\Download", @"System32\catroot2" }.Count(p => File.Exists(Path.Combine(Root, p, "fixture.txt")));
        internal Fixture()
        {
            foreach (string cache in new[] { @"SoftwareDistribution\DataStore", @"SoftwareDistribution\Download", @"System32\catroot2" })
            {
                string path = Path.Combine(Root, cache);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "fixture.txt"), "test cache");
            }
        }
        internal Task<MaintenanceOperationResult> Run(CancellationToken token = default)
        {
            var operations = new UpdateRepairOperations(() => Admin, Root, s => Starts[s],
                (s, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(States[s]); },
                (_, args, _, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    string action = args[0], service = args[1];
                    Commands.Add(action + " " + service);
                    if (action == FailAction && (FailService is null || service.Equals(FailService, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (FailureState is not null) States[service] = FailureState;
                        return Task.FromResult(new NativeCommandResult(FailureExitCode, "", "fixture access denied", FailureTimedOut, TimeSpan.Zero));
                    }
                    if (action == "config") Starts[service] = args[3] == "auto" ? 2 : 3;
                    else States[service] = action == "start" ? "Running" : "Stopped";
                    return Task.FromResult(new NativeCommandResult(0, "fixture success", "", false, TimeSpan.Zero));
                }, (_, _, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                ct => { ct.ThrowIfCancellationRequested(); OnPoll?.Invoke(); return Task.CompletedTask; });
            return new WindowsUpdateRepairService(operations).RunAsync(this, token);
        }
        public void Report(MaintenanceProgressUpdate value)
        {
            Progress.Add(value);
            if (value.Status == "RUNNING") OnStage?.Invoke(value.StageIndex);
        }
        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            if (!Path.GetFileName(resolved).StartsWith("nwu-update-workflow-", StringComparison.Ordinal) ||
                !string.Equals(Path.GetDirectoryName(resolved), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(resolved, true);
        }
    }
}
