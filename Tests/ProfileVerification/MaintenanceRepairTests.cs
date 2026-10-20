using Naufal_Windows_Tech_s_Powertoys;

internal static class MaintenanceRepairTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        const string package = "Microsoft.WindowsStore_22607.1401.8.0_x64__8wekyb3d8bbwe";
        string script = StoreResetCommand.BuildScript(package);
        assert(script.Contains("Appx\\Reset-AppxPackage -Package $store[0].PackageFullName") && script.Contains(package), "Store reset invokes the OS cmdlet for the exact selected package");
        assert(!script.Contains("-AllUsers") && !script.Contains("Remove-AppxPackage") && !script.Contains("Bypass"), "Store reset stays current-user and does not uninstall/bypass policies");
        assert(script.Contains("$store.Count -ne 1") && !script.Contains("$verified"), "Store reset validates its target but defers post-reset registration to the final repair stage");
        assert(script.IndexOf("[Console]::WriteLine", StringComparison.Ordinal) > script.IndexOf("Appx\\Reset-AppxPackage -Package", StringComparison.Ordinal) &&
            script.Contains("-Confirm:$false -ErrorAction Stop"), "Store completion marker follows a successful terminating-error-aware command");
        foreach (string invalid in new[] { "", "Microsoft.WindowsStore", package + "' ; bad", package + "\n", "Other.App_1.0.0.0_x64__8wekyb3d8bbwe", "Microsoft.WindowsStore_1.0.0.0_x64__unknown", "*", "../Store" })
            assert(Throws<ArgumentException>(() => StoreResetCommand.BuildScript(invalid)), "Store reset rejects out-of-scope/injected package names");
        var success = new NativeCommandResult(0, StoreResetCommand.CompletedMarker + "\r\n", "", false, TimeSpan.Zero);
        StoreResetCommand.ValidateResult(success);
        assert(true, "Store command success requires completion marker");
        foreach (var failed in new[] { success with { ExitCode = 1 }, success with { StandardOutput = "" }, success with { StandardOutput = "not " + StoreResetCommand.CompletedMarker } })
            assert(Throws<InvalidOperationException>(() => StoreResetCommand.ValidateResult(failed)), "Store exit failure/missing exact completion cannot report PASS");
        assert(Throws<TimeoutException>(() => StoreResetCommand.ValidateResult(success with { TimedOut = true })), "Store timeout cannot be promoted to success");
        foreach (Exception fatal in new Exception[] { new TimeoutException(), new OperationCanceledException(), new PendingDeploymentException("still pending") })
        {
            bool propagated = false;
            try { await StoreResetStage.RunAsync(package, _ => Task.FromException(fatal)); }
            catch (Exception exception) { propagated = ReferenceEquals(exception, fatal); }
            assert(propagated, "Unconfirmed Store deployment aborts the repair before cache cleanup/registration");
        }
        assert((await StoreResetStage.RunAsync(package, _ => Task.FromException(new InvalidOperationException("package in use")))).Contains("package in use"), "Known terminal reset failure stays an explicit warning");
        await VerifyRepairReadBackAsync(assert);
        await WindowsUpdateWorkflowTests.RunAsync(assert);

        // Real filesystem operations are confined to a unique disposable test fixture.
        string root = Path.Combine(Path.GetTempPath(), "wpt-cache-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int checks = 0, moves = 0;
        Task Stopped(CancellationToken token) { token.ThrowIfCancellationRequested(); checks++; return Task.CompletedTask; }
        var logs = new List<string>();
        try
        {
            string cache = @"SoftwareDistribution\DataStore";
            string data = Path.Combine(root, cache);
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "fixture.txt"), "original cache");
            string old = Path.Combine(root, "SoftwareDistribution.old");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "keep.txt"), "previous backup");
            var moved = await UpdateCacheReset.RunAsync(root, cache, Stopped, logs.Add, default);
            assert(checks == 1 && moved.Backup is not null && File.ReadAllText(Path.Combine(moved.Backup!, "fixture.txt")) == "original cache", "Update cache moved only after stop verification and backup contents preserved");
            assert(Directory.Exists(Path.Combine(root, "SoftwareDistribution")) && File.ReadAllText(Path.Combine(old, "keep.txt")) == "previous backup", "Update root and historical backup are untouched");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "fixture.txt"), "new cache");
            var repeated = await UpdateCacheReset.RunAsync(root, cache, Stopped, logs.Add, default);
            assert(repeated.Backup != moved.Backup && Directory.Exists(moved.Backup), "Repeated repair uses a different backup and never deletes old data");

            Directory.CreateDirectory(data);
            checks = moves = 0;
            var retried = await UpdateCacheReset.RunAsync(root, cache, Stopped, logs.Add, default,
                (source, destination) => { if (++moves < 3) throw new UnauthorizedAccessException("simulated open handle"); Directory.Move(source, destination); },
                _ => Task.CompletedTask);
            assert(checks == 3 && moves == 3 && Directory.Exists(retried.Backup), "Transient cache lock rechecks stopped services before each bounded retry");

            Directory.CreateDirectory(data);
            checks = moves = 0;
            bool denied = false;
            try
            {
                await UpdateCacheReset.RunAsync(root, cache, Stopped, logs.Add, default,
                    (_, _) => { moves++; throw new UnauthorizedAccessException("denied"); }, _ => Task.CompletedTask);
            }
            catch (IOException exception) { denied = exception.Message.Contains("three attempts") && exception.Message.Contains("preserved"); }
            assert(denied && moves == 3 && checks == 3 && Directory.Exists(data) && Directory.Exists(old), "Persistent deny is bounded, actionable and non-destructive");

            moves = 0;
            bool stopBlocked = false;
            try
            {
                await UpdateCacheReset.RunAsync(root, cache, _ => Task.FromException(new InvalidOperationException("still running")), logs.Add, default,
                    (_, _) => moves++);
            }
            catch (InvalidOperationException) { stopBlocked = true; }
            assert(stopBlocked && moves == 0, "Failed service-stop gate prevents every filesystem mutation");
            bool cancelled = false;
            try { await UpdateCacheReset.RunAsync(root, cache, Stopped, logs.Add, new CancellationToken(true), (_, _) => moves++); }
            catch (OperationCanceledException) { cancelled = true; }
            assert(cancelled && moves == 0, "Cancelled cache reset never renames a directory");
            assert(Throws<InvalidOperationException>(() => UpdateCacheReset.ValidateAttributes(FileAttributes.Directory | FileAttributes.ReparsePoint, "fixture")), "Cache reset rejects junction/symlink attributes");
            assert(Throws<InvalidOperationException>(() => UpdateCacheReset.ValidateAttributes(FileAttributes.Normal, "fixture")), "Cache reset rejects file targets");
            foreach (string unsafePath in new[] { "", "SoftwareDistribution", @"..\other", @"System32", @"System32\catroot", data })
            {
                bool refused = false;
                try { await UpdateCacheReset.RunAsync(root, unsafePath, Stopped, logs.Add, default); }
                catch (ArgumentException) { refused = true; }
                assert(refused, "Update cache target allow-list rejects broad and escaped paths");
            }
            var absent = await UpdateCacheReset.RunAsync(root, @"System32\catroot2", Stopped, logs.Add, default);
            assert(absent.Backup is null, "Absent cache does not fabricate a successful backup");
            bool noBackup = false;
            try { await UpdateCacheReset.RunAsync(root, cache, Stopped, logs.Add, default, (_, _) => { }); }
            catch (InvalidOperationException) { noBackup = true; }
            assert(noBackup, "A move without a resulting backup cannot pass verification");
        }
        finally
        {
            // This is the exact unique fixture allocated above, never a system cache.
            if (!Path.GetFileName(root).StartsWith("wpt-cache-regression-", StringComparison.Ordinal) ||
                !string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected cache test cleanup target.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyRepairReadBackAsync(Action<bool, string> assert)
    {
        var healthy = new StoreRegistrationState(StoreRegistrationVerification.StoreFamily,
            "Microsoft.WindowsStore_22608.1401.0.0_x64__8wekyb3d8bbwe", true, true);
        int reads = 0, delays = 0;
        List<string> logs = new();
        Task Delay(CancellationToken token) { token.ThrowIfCancellationRequested(); delays++; return Task.CompletedTask; }
        var verified = await StoreRegistrationVerification.VerifyAsync(() => { reads++; return healthy; }, logs.Add, default, Delay);
        assert(verified == healthy && reads == 1 && delays == 0, "Final Store verification accepts healthy current registration without waiting");
        assert(verified.FullName.Contains("22608"), "Final Store verification permits a newer version in the same trusted family");

        reads = delays = 0;
        verified = await StoreRegistrationVerification.VerifyAsync(() => ++reads < 3 ? null : healthy, logs.Add, default, Delay);
        assert(verified == healthy && reads == 3 && delays == 2, "Transiently absent Store registration is retried after re-registration");
        reads = delays = 0;
        verified = await StoreRegistrationVerification.VerifyAsync(() => ++reads == 1 ? throw new InvalidOperationException("registration busy") : healthy, logs.Add, default, Delay);
        assert(verified == healthy && reads == 2 && delays == 1 && logs.Any(line => line.Contains("registration busy")), "Transient metadata read error retries and remains visible in the log");

        foreach (var (state, expected) in new (StoreRegistrationState?, string)[]
        {
            (null, "not registered"),
            (healthy with { FamilyName = "Other.App_8wekyb3d8bbwe" }, "does not match"),
            (healthy with { FamilyName = "Microsoft.WindowsStore_untrusted" }, "does not match"),
            (healthy with { ManifestExists = false }, "AppXManifest.xml"),
            (healthy with { Healthy = false }, "not in a healthy")
        })
        {
            reads = delays = 0;
            bool failed = false;
            try { await StoreRegistrationVerification.VerifyAsync(() => { reads++; return state; }, logs.Add, default, Delay); }
            catch (InvalidOperationException exception) { failed = exception.Message.Contains(expected); }
            assert(failed && reads == StoreRegistrationVerification.MaximumAttempts && delays == reads - 1,
                "Store cannot pass without identity, manifest and health after bounded checks: " + expected);
        }
        reads = delays = 0;
        bool denied = false;
        try { await StoreRegistrationVerification.VerifyAsync(() => { reads++; throw new UnauthorizedAccessException("access denied"); }, logs.Add, default, Delay); }
        catch (InvalidOperationException exception) { denied = exception.Message.Contains("access denied"); }
        assert(denied && reads == StoreRegistrationVerification.MaximumAttempts, "Unverifiable Store metadata is a real failure, not a skipped success");
        reads = 0;
        bool cancelled = false;
        try { await StoreRegistrationVerification.VerifyAsync(() => { reads++; return healthy; }, logs.Add, new CancellationToken(true), Delay); }
        catch (OperationCanceledException) { cancelled = true; }
        assert(cancelled && reads == 0, "Cancelled final Store verification does not query metadata");
        using var cancellation = new CancellationTokenSource();
        reads = 0;
        cancelled = false;
        try
        {
            await StoreRegistrationVerification.VerifyAsync(() => { reads++; return null; }, logs.Add, cancellation.Token,
                token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        }
        catch (OperationCanceledException) { cancelled = true; }
        assert(cancelled && reads == 1, "Store retry observes cancellation before a second probe");

        foreach (string name in new[] { "BITS", "bits", "DoSvc", "dosvc", "wuauserv", "cryptsvc" })
        {
            bool flexible = name.Equals("BITS", StringComparison.OrdinalIgnoreCase) || name.Equals("DoSvc", StringComparison.OrdinalIgnoreCase);
            int fallback = name.Equals("BITS", StringComparison.OrdinalIgnoreCase) || name == "wuauserv" ? 3 : 2;
            assert(UpdateServiceStartupPolicy.RepairStart(name) == fallback, "Update repair fallback is scoped to the known service: " + name);
            foreach (int? start in new int?[] { null, -1, 0, 1, 2, 3, 4, 5 })
            {
                bool accepted = flexible ? start is 2 or 3 : start == fallback;
                assert(UpdateServiceStartupPolicy.IsOperationalStart(name, start) == accepted,
                    $"Startup validation preserves only operational modes for {name} Start={start}");
                assert((UpdateServiceStartupPolicy.Verify(name, start, "Running").Count == 0) == accepted,
                    $"Running alone cannot hide missing/disabled/invalid startup for {name} Start={start}");
            }
            foreach (string runtime in new[] { "Stopped", "StartPending", "StopPending", "Paused", "Unknown", "NotFound", "" })
                assert(UpdateServiceStartupPolicy.Verify(name, fallback, runtime).Any(issue => issue.Contains("expected Running")),
                    $"Enabled startup alone cannot pass runtime verification for {name}: {runtime}");
        }
        assert(UpdateServiceStartupPolicy.Verify("BITS", 2, "Running").Count == 0 &&
            UpdateServiceStartupPolicy.Verify("DoSvc", 3, "Running").Count == 0,
            "Reported BITS Automatic/DoSvc Manual with Running are valid operational repair outcomes");
        assert(UpdateServiceStartupPolicy.Verify("BITS", 4, "Stopped").Count == 2,
            "Disabled and stopped service preserves both independent failures");
        assert(Throws<ArgumentException>(() => UpdateServiceStartupPolicy.RepairStart("OtherService")) &&
            Throws<ArgumentException>(() => UpdateServiceStartupPolicy.IsOperationalStart("OtherService", 2)),
            "Windows Update startup policy refuses unrelated services instead of guessing defaults");
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
