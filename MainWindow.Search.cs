using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys;

public sealed partial class MainWindow
{
    private sealed record SearchDestination(string Name, string Section, string Keywords, Func<Task> Open)
    {
        public string Label => $"{Name}  ·  {Section}";
    }

    private readonly List<SearchDestination> _searchIndex = new();
    private Dictionary<string, SearchDestination> _searchResults = new();

    private IToolActionService EssentialCatalogActions() => new FilteredToolActionService(
        _essentialActionsService, "IconCache", "TempFiles", "NtfsPerformance", "StoragePowerLatency",
        "DriverOptionalUpdates", "VirtualMemorySettings", "WindowsTroubleshoot", "DesktopBackground");

    private static bool MatchesSearch(string query, params string[] fields) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(word => fields.Any(field => field.Contains(word, StringComparison.OrdinalIgnoreCase)));

    private void InitializeHomeSearch()
    {
        HomeSearch.TextChanged += (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            if (_searchIndex.Count == 0) BuildSearchIndex();
            string query = sender.Text.Trim();
            _searchResults = _searchIndex.Where(hit => query.Length > 0 &&
                    MatchesSearch(query, hit.Name, hit.Section, hit.Keywords))
                .OrderByDescending(hit => hit.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(hit => hit.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(hit => hit.Name.StartsWith("Preferred DNS", StringComparison.Ordinal) && query.Contains("dns", StringComparison.OrdinalIgnoreCase))
                .ThenBy(hit => hit.Name, StringComparer.OrdinalIgnoreCase)
                .GroupBy(hit => hit.Label)
                .SelectMany(group => group.Select((hit, index) =>
                    new KeyValuePair<string, SearchDestination>(index == 0 ? group.Key : $"{group.Key} ({index + 1})", hit)))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            sender.ItemsSource = _searchResults.Keys.ToArray();
            SearchFeedback.Text = "No matching settings or tools.";
            SearchFeedback.Visibility = query.Length > 0 && _searchResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        HomeSearch.QuerySubmitted += async (sender, args) =>
        {
            SearchDestination? hit = args.ChosenSuggestion is string label && _searchResults.TryGetValue(label, out var selected)
                ? selected : _searchResults.Values.FirstOrDefault();
            if (hit is null) return;
            sender.Text = hit.Name;
            sender.IsSuggestionListOpen = false;
            await hit.Open();
        };
    }

    private void BuildSearchIndex()
    {
        void AddCatalog(string section, IToolToggleService toggles, IToolActionService actions, Func<string, Task> open)
        {
            var toggleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in toggles.GetDefinitions())
            {
                toggleIds.Add(item.Id);
                _searchIndex.Add(new(item.Name, section, $"{item.Category} {item.Description} {item.Id}", () => open(item.Id)));
            }
            foreach (var item in actions.GetActions().Where(item => !toggleIds.Contains(item.Id)))
                _searchIndex.Add(new(item.Name, section, $"{item.Category} {item.Description} {item.Id}", () => open(item.Id)));
        }
        AddCatalog("Essential Windows Tweaks", EssentialCatalog(), EssentialCatalogActions(), OpenEssentialTweaksAsync);
        AddCatalog("Gaming Tweaks", GamingCatalog(), _gamingActionsService, OpenGamingTweaksAsync);
        AddCatalog("Advanced Windows Tweaks & De-Bloat", AdvancedCatalog(), new BuiltInAppsService(), OpenDebloatAsync);

        void AddPageTarget(string name, string section, string tag, FrameworkElement target, string keywords = "")
        {
            _searchIndex.Add(new(name, section, keywords, () =>
            {
                MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>().First(x => Equals(x.Tag, tag));
                DispatcherQueue.TryEnqueue(() =>
                {
                    _focusStartupFilter = name == "Startup Apps Manager";
                    if (target is Expander expander) expander.IsExpanded = true;
                    target.StartBringIntoView();
                    if (target is Control control) control.Focus(FocusState.Programmatic);
                    FocusStartupFilter();
                });
                return Task.CompletedTask;
            }));
        }
        // Build the lazy workspace sections without opening them or scanning Windows.
        OnAdvancedActivated();
        foreach (var (panel, tag, section) in new[] {
            (RepairPage, "Repair", "System Repair"), (InfoPage, "Info", "System Info"),
            (SecurityPage, "Security", "Windows Security"), (AdvancedPage, "Advanced", "Advanced Windows Tweaks") })
        {
            AddPageTarget(section, "Navigation", tag, panel);
            foreach (var element in panel.Children)
            {
                if (element is Button { Content: string name } button) AddPageTarget(name, section, tag, button);
                if (element is Expander expander)
                {
                    string? heading = expander.Header as string ?? (expander.Header as TextBlock)?.Text;
                    if (heading is not null) AddPageTarget(heading, section, tag, expander,
                        heading == "Startup Apps Manager" ? "matikan aplikasi saat startup nonaktifkan disable autostart login sign-in" : "");
                }
            }
        }
        foreach (bool wifi in new[] { true, false })
        foreach (bool ipv6 in new[] { false, true })
        foreach (string field in new[] { "Preferred DNS", "Alternate DNS", "IP address", ipv6 ? "Prefix length" : "Subnet mask", "Gateway" })
        {
            string adapter = wifi ? "Wi-Fi" : "Ethernet";
            string family = ipv6 ? "IPv6" : "IPv4";
            string aliases = wifi ? "wifi wi fi wireless ssid" : "wired lan kabel";
            _searchIndex.Add(new($"{field} · {adapter} {family}", "Network Settings Manager",
                $"ubah ganti atur setel alamat change edit set {aliases}", async () =>
                {
                    MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>().First(x => Equals(x.Tag, "Network"));
                    OnNetworkActivated();
                    await _initialAdapterScan;
                    if (!_isClosed && NetworkPage.Visibility == Visibility.Visible)
                        await _networkSettingsView!.FocusAddressFieldAsync(wifi, ipv6, field);
                }));
        }
        AddPageTarget("Network Settings Manager", "Navigation", "Network", NetworkPage, "IPv4 IPv6 DNS gateway subnet Ethernet Wi-Fi SSID");
        AddPageTarget("Live telemetry", "Home", "Home", LiveChartsGrid, "CPU RAM memory GPU network RX TX usage graphs");
        AddPageTarget("Performance profile", "Home", "Home", ProfileChoicesGrid, "Competitive Optimized Balanced gaming power");
        AddPageTarget("Legacy Windows Panels", "Home", "Home", LegacyWindowsPanelsButton);
    }
}
