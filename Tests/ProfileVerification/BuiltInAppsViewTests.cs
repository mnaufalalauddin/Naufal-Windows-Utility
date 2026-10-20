using Naufal_Windows_Tech_s_Powertoys;

internal static class BuiltInAppsViewTests
{
    internal static void Run(Action<bool, string> check)
    {
        var healthy = new BuiltInAppPackage("Fixture", "Fixture_family", "Fixture_full", false, false);
        var broken = healthy with { Healthy = false };
        var unknown = healthy with { InventoryError = "Inventory denied" };
        check(BuiltInAppsView.Classify([]) == BuiltInAppInstallState.NotInstalled, "Empty verified inventory means not installed");
        check(BuiltInAppsView.Classify([healthy]) == BuiltInAppInstallState.Installed, "Healthy package is installed");
        check(BuiltInAppsView.Classify([broken]) == BuiltInAppInstallState.NeedsRepair, "Registered broken package is not mistaken for absence");
        check(BuiltInAppsView.Classify([unknown]) == BuiltInAppInstallState.NotVerified, "Inventory errors are not absence");
        check(BuiltInAppsView.Classify([healthy, unknown]) == BuiltInAppInstallState.NotVerified, "Incomplete aggregate inventory takes precedence over healthy alias");
        check(BuiltInAppsView.Classify([broken, healthy]) == BuiltInAppInstallState.Installed, "Merged app variants include healthy installed alias");
        check(BuiltInAppsView.Classify([CopilotStorePolicy.Package(CopilotStorePolicy.Deferred)]) == BuiltInAppInstallState.NotVerified, "Deferred Copilot Store check remains unverified without extra network requests");
        check(BuiltInAppsView.Classify([OneDriveAppPolicy.Installed("user", true)]) == BuiltInAppInstallState.Installed, "OneDrive desktop presence uses the same filter model");

        foreach (var filter in Enum.GetValues<BuiltInAppFilter>())
        foreach (var state in Enum.GetValues<BuiltInAppInstallState>())
        {
            bool expected = filter == BuiltInAppFilter.All ||
                (filter == BuiltInAppFilter.Installed && state is BuiltInAppInstallState.Installed or BuiltInAppInstallState.NeedsRepair) ||
                (filter == BuiltInAppFilter.NotInstalled && state == BuiltInAppInstallState.NotInstalled) ||
                (filter == BuiltInAppFilter.NotVerified && state == BuiltInAppInstallState.NotVerified);
            check(BuiltInAppsView.Includes(filter, state) == expected, $"App filter matrix: {filter}/{state}");
        }
        BuiltInAppTarget[] targets = [new("z", "Zulu", []), new("b", "beta", []), new("a", "Alpha", []), new("u", "Unknown", [])];
        var states = new Dictionary<string, BuiltInAppInstallState>
        {
            ["z"] = BuiltInAppInstallState.Installed,
            ["b"] = BuiltInAppInstallState.NotInstalled,
            ["a"] = BuiltInAppInstallState.NeedsRepair
        };
        check(BuiltInAppsView.VisibleIds(targets, states, BuiltInAppFilter.All).SequenceEqual(["a", "b", "u", "z"]), "All apps remain alphabetical, ignoring case");
        check(BuiltInAppsView.VisibleIds(targets, states, BuiltInAppFilter.Installed).SequenceEqual(["a", "z"]), "Installed filter includes repair-needed registration in A-Z order");
        check(BuiltInAppsView.VisibleIds(targets, states, BuiltInAppFilter.NotInstalled).SequenceEqual(["b"]), "Only confirmed absence enters not-installed filter");
        check(BuiltInAppsView.VisibleIds(targets, states, BuiltInAppFilter.NotVerified).SequenceEqual(["u"]), "Missing scan results remain unverified");
        check(BuiltInAppsView.SelectedVisibleIds(targets, states, BuiltInAppFilter.Installed, ["a", "b", "z", "bogus", "z"]).SequenceEqual(["a", "z"]), "Action selection excludes hidden, unapproved and duplicate IDs");
        states["z"] = BuiltInAppInstallState.NotInstalled;
        check(BuiltInAppsView.SelectedVisibleIds(targets, states, BuiltInAppFilter.Installed, ["a", "z"]).SequenceEqual(["a"]), "After uninstall/reload selection respects refreshed inventory");
        states["b"] = BuiltInAppInstallState.Installed;
        check(BuiltInAppsView.VisibleIds(targets, states, BuiltInAppFilter.Installed).SequenceEqual(["a", "b"]), "Restored app enters installed results on reload");
        states.Clear();
        check(BuiltInAppsView.VisibleIds(targets, states, BuiltInAppFilter.Installed).Length == 0 && BuiltInAppsView.VisibleIds(targets, states, BuiltInAppFilter.NotInstalled).Length == 0, "Failed/cleared inventory exposes neither stale installed nor false absent results");
        check(BuiltInAppsView.VisibleIds(targets, states, BuiltInAppFilter.NotVerified).Length == 4, "Failed inventory remains discoverable under Not verified");
        check(BuiltInAppsView.VisibleIds([], states, BuiltInAppFilter.All).Length == 0, "Empty catalog returns empty results safely");

        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string ui = File.ReadAllText(Path.Combine(root, "MainWindow.BuiltInApps.cs"));
        check(ui.Contains("Header = \"Show apps\"") && ui.Contains("filter.SelectionChanged +="), "App filter selector is wired into the dialog");
        check(ui.Contains("pair.Value.Visibility = show ? Visibility.Visible : Visibility.Collapsed") && ui.Contains("if (!show) checks[pair.Key].IsChecked = false"), "Hidden rows are collapsed and deselected");
        check(ui.Contains("string[] ids = SelectedVisibleIds();") && ui.Contains("Content = \"Select visible\""), "Bulk action and select-visible use the filtered app set");
        check(ui.Contains("states[target.Id] = state;") && ui.Contains("states[id] = BuiltInAppInstallState.NotVerified;"), "Refresh invalidates old states before reading new inventory");
        check(ui.Contains("filter.IsEnabled = !busy;") && ui.Contains("scroll.ChangeView(null, 0, null, true)"), "Filter is locked during operations and resets scroll position when changed");
    }
}
