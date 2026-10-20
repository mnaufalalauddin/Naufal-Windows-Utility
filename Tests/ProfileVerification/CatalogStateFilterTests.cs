using Naufal_Windows_Tech_s_Powertoys;

internal static class CatalogStateFilterTests
{
    internal static void Run(Action<bool, string> assert)
    {
        (ToolToggleState? State, CatalogStateView Expected)[] cases =
        [
            (new(true, true, "Applied"), CatalogStateView.On),
            (new(false, true, "Default"), CatalogStateView.Off),
            (new(false, true, "Mixed", HasAppliedParts: true), CatalogStateView.Partial),
            (new(true, true, "Applied", HasAppliedParts: true), CatalogStateView.On),
            (ToolToggleState.Unavailable("Not on this PC"), CatalogStateView.Unavailable),
            (new(false, false, "Unknown", "Access denied"), CatalogStateView.NotVerified),
            (new(true, false, "Old value", "Timeout"), CatalogStateView.NotVerified),
            (null, CatalogStateView.NotVerified)
        ];
        foreach (var item in cases)
        {
            assert(CatalogStateFilter.Classify(item.State, true) == item.Expected, "Catalog filter classifies backend state: " + item.Expected);
            assert(CatalogStateFilter.Classify(item.State, false) == CatalogStateView.NotVerified, "Stale/pending scan never contributes ON/OFF/unavailable evidence");
            foreach (var view in Enum.GetValues<CatalogStateView>())
            {
                assert(CatalogStateFilter.Includes(view, item.State, true) == (view == CatalogStateView.All || view == item.Expected), "Filter partition: " + view + "/" + item.Expected);
                assert(CatalogStateFilter.Includes(view, item.State, false) == (view is CatalogStateView.All or CatalogStateView.NotVerified), "Stale filter partition: " + view);
            }
        }

        ToolToggleDefinition[] definitions =
        [
            new("On", "Safe", "ON item", "fixture", false, false),
            new("Off", "Safe", "OFF item", "fixture", false, false),
            new("Partial", "Advanced", "Mixed item", "fixture", false, false),
            new("Unavailable", "Safe", "Absent item", "fixture", false, false),
            new("Error", "Safe", "Unknown item", "fixture", false, false)
        ];
        var states = new Dictionary<string, ToolToggleState>
        {
            ["On"] = new(true, true, "Applied"), ["Off"] = new(false, true, "Default"),
            ["Partial"] = new(false, true, "Mixed", HasAppliedParts: true),
            ["Unavailable"] = ToolToggleState.Unavailable("fixture"), ["Error"] = new(false, false, "Unknown", "denied")
        };
        CatalogSelectionPlan Plan(CatalogStateView view, bool scanCurrent = true) => CatalogSelectionPlan.Create(
            definitions, states, d => CatalogStateFilter.Includes(view, states[d.Id], scanCurrent));
        var on = Plan(CatalogStateView.On);
        assert(on.Selected.Select(d => d.Id).SequenceEqual(["On"]) && on.ToApply.Count == 0 && on.ToRestore.Count == 1, "ON filter scopes selection/restore to visible ON row");
        var off = Plan(CatalogStateView.Off);
        assert(off.Selected.Select(d => d.Id).SequenceEqual(["Off"]) && off.ToApply.Count == 1 && off.ToRestore.Count == 0, "OFF filter excludes mixed, missing and failed probes");
        var partial = Plan(CatalogStateView.Partial);
        assert(partial.Selected.Count == 1 && partial.ToApply.Count == 1 && partial.ToRestore.Count == 1, "Partial row retains both existing apply and saved-restore semantics");
        assert(Plan(CatalogStateView.Unavailable).Selected.Count == 0 && Plan(CatalogStateView.NotVerified).Selected.Count == 0, "Unavailable and failed-read rows remain non-actionable");
        assert(Plan(CatalogStateView.On, false).Selected.Count == 0 && Plan(CatalogStateView.Off, false).Selected.Count == 0, "Stale ON/OFF view cannot dispatch hidden old-state selections");
        states["Off"] = new(true, true, "Applied after operation");
        assert(Plan(CatalogStateView.Off).Selected.Count == 0 && Plan(CatalogStateView.On).Selected.Count == 2, "Verified read-back changes filter membership after operation");
        assert(Plan(CatalogStateView.All).Selected.Count == 3, "All view retains full available catalog scope");

        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string main = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs")).Replace("\r\n", "\n");
        string shared = main[main.IndexOf("private async Task ShowToggleCatalogDialogAsync", StringComparison.Ordinal)..main.IndexOf("private static void UpdateCatalogStateText", StringComparison.Ordinal)];
        assert(shared.Contains("Header = \"Show options\"") && shared.Contains("stateFilter.SelectionChanged +="), "Shared dialog exposes filter picker");
        assert(shared.Contains("definition => IsVisibleOption(definition) && selectors[definition.Id].IsChecked == true"), "Selected action dispatch excludes hidden rows");
        assert(shared.Contains("IsVisibleOption(definition) && currentStates[definition.Id].IsAvailable && predicate(definition)"), "Select safe/advanced/all intersect the active filter");
        assert(shared.Contains("items, currentStates, IsVisibleOption") && shared.Contains("currentStates,\n                    IsVisibleOption"), "Apply and restore including default bulk scopes are filter restricted");
        assert(shared.Contains("if (!show || !statesLoaded) selectors[definition.Id].IsChecked = false"), "Hidden and stale selections are cleared");
        assert(shared.Contains("stateFilter.IsEnabled = !busy") && shared.Contains("individualActionGate.IsBusy || updatingStateFilter"), "Filter cannot change during confirmation or active work");
        assert(shared.Contains("CurrentStateFilter() == CatalogStateView.All") && shared.Contains("actionOnlyCards"), "Action-only tools are not misclassified ON/OFF");
        assert(shared.Contains("UpdateStateFilter();\r\n                if (!statesLoaded)") || shared.Contains("UpdateStateFilter();\n                if (!statesLoaded)"), "Summary refresh reapplies current state filter");
        string entryPoints = main[main.IndexOf("private async void GamingTweaksButton_Click", StringComparison.Ordinal)..main.IndexOf("private async Task ShowToggleCatalogDialogAsync", StringComparison.Ordinal)];
        foreach (string title in new[] { "Essential Windows Tweaks", "Gaming Tweaks", "Advanced Windows Tweaks & De-Bloat" })
            assert(entryPoints.Contains(title), "Requested catalog uses shared filtered dialog: " + title);
    }
}
