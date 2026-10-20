using Naufal_Windows_Tech_s_Powertoys;

internal static class WindowsUpdateWorkflowTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
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
                        return Task.FromResult(new NativeCommandResult(5, "", "fixture access denied", false, TimeSpan.Zero));
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
