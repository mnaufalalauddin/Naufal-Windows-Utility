using Naufal_Windows_Tech_s_Powertoys;

internal static class RecoveredCatalogFeaturesTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        var actions = new FakeActions();
        ToolToggleState state = new(false, true, "Original=7");
        var bulk = new EssentialBulkActionsService(actions, _ => Task.FromResult(state), id => "Fixture backup: " + id);
        var definitions = bulk.GetDefinitions();
        var observed = await CatalogStateReader.ReadAsync(bulk, definitions[0], TimeSpan.FromSeconds(5));
        assert(!observed.IsOn && observed.RecoverySource == "Fixture backup: IconCache", "Not applied retains both the measured state and the owning action's recovery source");
        // Hand-reviewed from V78.ps1 Essential bulk registration, not derived
        // from the production allow-list under test.
        assert(definitions.Select(row => row.Id).SequenceEqual(new[] { "IconCache", "NtfsPerformance", "StoragePowerLatency" }),
            "Essential restores all three original bulk actions, no cleanup or settings launchers");
        assert(definitions.Single(row => row.Id == "IconCache").SelectionTier == ToolToggleTier.Safe, "IconCache safe scope");
        assert(definitions.Where(row => row.Id != "IconCache").All(row => row.SelectionTier == ToolToggleTier.Advanced), "Storage actions advanced scope");
        foreach (var definition in definitions)
        {
            foreach (bool selected in new[] { false, true })
            foreach (bool applied in new[] { false, true })
            {
                var states = definitions.ToDictionary(row => row.Id, _ => new ToolToggleState(applied, true, "fixture"));
                var plan = CatalogSelectionPlan.Create(definitions, states, row => selected && row.Id == definition.Id);
                assert(plan.ToApply.Count == (selected && !applied ? 1 : 0), "Recovered bulk Apply selection " + definition.Id);
                assert(plan.ToRestore.Count == (selected && applied ? 1 : 0), "Recovered bulk Restore selection " + definition.Id);
            }
            state = new(true, true, "Applied=1");
            var appliedResult = await bulk.SetStateAsync(definition, true);
            assert(appliedResult.Success && appliedResult.Verified && actions.Last == "apply:" + definition.Id, "Bulk dispatch apply " + definition.Id);
            var restored = await bulk.SetStateAsync(definition, false);
            assert(restored.Success && restored.Verified && actions.Last == "restore:" + definition.Id, "Restore can verify a captured ON state " + definition.Id);
            state = new(false, true, "Mismatch");
            assert(!(await bulk.SetStateAsync(definition, true)).Verified, "Action success alone cannot certify ON " + definition.Id);
            state = new(false, false, "Unreadable", "Access denied");
            assert(!(await bulk.SetStateAsync(definition, false)).Verified, "Read failure never verifies restored action " + definition.Id);
            actions.Success = false;
            state = new(true, true, "Applied=1");
            assert(!(await bulk.SetStateAsync(definition, true)).Success, "Failed action is not hidden by matching state " + definition.Id);
            actions.Success = true;
            actions.Calls = 0;
            state = ToolToggleState.Unavailable("Absent hardware");
            var skipped = await CatalogOperationRunner.ExecuteAsync(bulk, definition, CatalogOperation.Apply);
            assert(skipped.SkippedUnavailable && actions.Calls == 0, "Queue recheck prevents unavailable action write " + definition.Id);
            assert(skipped.BeforeState == state, "Unavailable preflight state retained " + definition.Id);
        }
        bool unknownRejected = false;
        try { await bulk.SetStateAsync(new("TempFiles", "", "", "", false, false), true); }
        catch (KeyNotFoundException) { unknownRejected = true; }
        assert(unknownRejected, "Cleanup cannot enter bulk backend by supplying an arbitrary ID");

        var result = new ToolToggleOperationResult(true, true, "Verified against original snapshot.",
            new(false, true, "Value=7"), BeforeState: new(true, true, "Value=1"));
        string text = CatalogVerificationReport.FormatResult("Example", "Restoring", result);
        assert(text.Contains("Before: ON — Value=1") && text.Contains("After: OFF — Value=7"), "Before/after actual values retained separately");
        assert(text.Contains("Verified against original snapshot."), "Backend detail is not discarded");
        string failed = CatalogVerificationReport.FormatResult("Example", "Applying", result with { Success = false });
        assert(failed.Contains("FAILED / NOT VERIFIED") && !failed.Contains("Result: VERIFIED"), "Report cannot turn failed result into verified");
        string absent = CatalogVerificationReport.FormatResult("Example", "Applying", result with
            { Success = false, Verified = false, SkippedUnavailable = true, State = ToolToggleState.Unavailable("Not installed") });
        assert(absent.Contains("Result: UNAVAILABLE") && absent.Contains("Not installed"), "Unavailable report remains neutral");
        var progress = new CatalogProgressState(new[] { "done", "working", "waiting" });
        progress.Update("done", "COMPLETED", 100, text);
        progress.Update("working", "RUNNING", null, "Waiting for Windows");
        string report = CatalogVerificationReport.Build("Restoring", new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(7)),
            TimeSpan.FromHours(25), "Still running", progress.Items.Select(pair => (pair.Key, pair.Key, pair.Value)));
        assert(report.Contains("Elapsed: 25:00:00") && report.Contains("+07:00"), "Export preserves elapsed days and start offset");
        assert(report.Contains("[COMPLETED] done") && report.Contains("[RUNNING] working") && report.Contains("[WAITING] waiting"), "Export includes every state, not just successful rows");
        assert(report.Contains("Before: ON — Value=1"), "Export includes detailed verification");
        progress.Finish(false, "Batch stopped");
        string final = CatalogVerificationReport.Build("Restoring", DateTimeOffset.Now, TimeSpan.Zero, "Failed",
            progress.Items.Select(pair => (pair.Key, pair.Key, pair.Value)));
        assert(final.Contains("[FAILED] working") && final.Contains("[SKIPPED] waiting"), "Export differentiates failed and unstarted work");
    }

    private sealed class FakeActions : IToolActionService
    {
        public bool Success = true;
        public string Last = "";
        public int Calls;
        public IReadOnlyList<ToolActionDefinition> GetActions() => new[] { "IconCache", "NtfsPerformance", "StoragePowerLatency", "TempFiles" }
            .Select(id => new ToolActionDefinition(id, "Test", id, "Fixture", "Apply", true, id != "TempFiles")).ToArray();
        public Task<ToolActionResult> RunAsync(ToolActionDefinition action) { Calls++; Last = "apply:" + action.Id; return Task.FromResult(new ToolActionResult(Success, "fixture")); }
        public Task<ToolActionResult> RestoreAsync(ToolActionDefinition action) { Calls++; Last = "restore:" + action.Id; return Task.FromResult(new ToolActionResult(Success, "fixture")); }
    }
}
