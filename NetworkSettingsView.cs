using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed partial class NetworkSettingsView : StackPanel
{
    internal Func<Task>? RefreshRequested;
    internal Func<string, Task>? WifiSelected;
    internal WifiSsidState? WifiState { get; private set; }
    internal string SelectedAdapterId => (_adapters.SelectedItem as NetworkAdapterState)?.Id ?? "";
    internal bool IsWifi => _adapters.SelectedItem is NetworkAdapterState { InterfaceType: 71 };
    private string _wifiError = "Read the connected SSID before editing.";
    internal Func<NetworkSettingsProfile, Task>? ApplyRequested;
    internal Func<string, Task>? RestoreRequested;
    internal Func<string, Task>? OpenToolRequested;
    internal Func<string, string, NetworkAdditionalSettings, Task>? AdditionalRequested;
    private readonly NetworkAdditionalView _additional = new();
    private readonly ComboBox _adapters = Combo("Active Ethernet / Wi-Fi adapter", []), _snapshots = Combo("Recovery snapshot", []);
    private readonly ComboBox _category = Combo("Network location", ["Unchanged", "Public", "Private"]);
    private string _originalCategory = "Unchanged";
    private readonly FamilyEditor _v4 = new(false), _v6 = new(true);
    private NetworkFamilySettings _originalV4 = new(), _originalV6 = new();
    private readonly TextBlock _info = Text("Reading active adapters…"), _scope = Text(""), _summary = Text(""), _status = Text("No settings have been changed.");
    private readonly TextBlock _details = Text("");
    private readonly Button _save, _wifiSettings;
    private readonly List<Control> _controls = [];
    private bool _loading, _busy;

    internal NetworkSettingsView()
    {
        Spacing = 16;
        Children.Add(new TextBlock { Text = "Network Settings Manager", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        Children.Add(Text("Edit IP and DNS settings and save directly to Windows. Ethernet targets the adapter; Wi-Fi targets only the connected SSID. Per-SSID support is build-limited; save, reconnect, roaming and recovery have been tested on a supported host, not every device."));
        Grid actions = ResponsiveGrid(190);
        actions.Children.Add(Action("Refresh / discard edits", () => RefreshRequested?.Invoke() ?? Task.CompletedTask));
        _save = Action("Save to Windows…", () => ApplyRequested?.Invoke(GetChanges()) ?? Task.CompletedTask);
        actions.Children.Add(_save); Children.Add(actions);
        Children.Add(_status);
        StackPanel connection = new() { Spacing = 10 };
        connection.Children.Add(_adapters); connection.Children.Add(_info); connection.Children.Add(_scope);
        _wifiSettings = Action("Open Windows per-SSID settings", () => OpenToolRequested?.Invoke("wifi-ssid") ?? Task.CompletedTask);
        connection.Children.Add(_wifiSettings);
        connection.Children.Add(new Expander { Header = "All current addresses / diagnostics", Content = _details, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        Children.Add(Card("Active connection", connection));
        Grid families = ResponsiveGrid(390);
        families.Children.Add(Card("IPv4 · IP address & DNS", _v4));
        families.Children.Add(Card("IPv6 · IP address & DNS", _v6));
        Children.Add(families);
        StackPanel location = new() { Spacing = 8 };
        location.Children.Add(Text("Optional Ethernet setting. Private permits discovery on trusted networks; use Public on unfamiliar networks. Changes are included in Save to Windows. Domain-managed connections are not overridden."));
        location.Children.Add(_category);
        Children.Add(new Expander { Header = "Network location · Public / Private", Content = location, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        Children.Add(Text("Automatic fields show current reported values and are read-only. Choose Manual to edit. Unchanged preserves complex or unreadable settings. IPv6 uses a prefix length instead of a subnet mask. DNS encryption is not changed."));
        Children.Add(Text("Saving can disconnect this PC. Use the local console. A recovery snapshot is saved before Ethernet or Wi-Fi SSID changes. Save verifies stored settings; reconnect and check connectivity before relying on them. Ethernet live Apply/rollback still requires hardware validation."));
        Children.Add(new Expander { Header = "Review pending changes", Content = _summary, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        StackPanel recovery = new() { Spacing = 10 };
        recovery.Children.Add(Text("Recovery restores only the settings included in that save, not a previous DHCP lease or guaranteed connectivity."));
        recovery.Children.Add(_snapshots);
        recovery.Children.Add(Action("Restore selected snapshot…", () => _snapshots.SelectedItem is SnapshotItem item ? RestoreRequested?.Invoke(item.Path) ?? Task.CompletedTask : Task.CompletedTask));
        Children.Add(new Expander { Header = "Recovery", Content = recovery, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        Grid tools = ResponsiveGrid(210);
        foreach (var (label, target) in new[] { ("Wi-Fi connections", "wifi"), ("Adapter / advanced TCP-IP", "adapters"), ("Printers", "printers"), ("Workgroup / computer name", "computer"), ("Network drives", "drives"), ("Driver / Device Manager", "devices"), ("Proxy settings", "proxy"), ("VPN / connections", "vpn"), ("Firewall settings", "firewall") })
            tools.Children.Add(Action("Open " + label, () => OpenToolRequested?.Invoke(target) ?? Task.CompletedTask));
        Children.Add(new Expander { Header = "Windows network tools", Content = tools, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        Children.Add(new Expander { Header = "Additional settings", Content = _additional, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        _additional.Requested = (action, settings) => AdditionalRequested?.Invoke(action, GetChanges().AdapterId, settings) ?? Task.CompletedTask;
        _controls.AddRange([_adapters, _snapshots, _category]);
        _adapters.SelectionChanged += async (_, _) => { if (_loading) return; LoadAdapter(); if (IsWifi && !_busy && WifiSelected is not null) await WifiSelected(SelectedAdapterId); };
        _category.SelectionChanged += (_, _) => UpdateSummary();
        _v4.Changed += UpdateSummary; _v6.Changed += UpdateSummary;
        LoadAdapter();
    }
    internal static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    internal static TextBox Field(string header, string value = "") => new() { Header = header, Text = value, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0, MaxLength = 2000, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 127, 140, 157)), CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 237, 242, 248)) };
    internal static ComboBox Combo(string header, string[] options)
    {
        ComboBox result = new() { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 127, 140, 157)), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 237, 242, 248)) };
        foreach (string value in options) result.Items.Add(value);
        if (options.Length > 0) result.SelectedIndex = 0;
        return result;
    }
    internal static Border Card(string title, UIElement child)
    {
        StackPanel content = new() { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }); content.Children.Add(child);
        return new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 128, 144, 164)), Child = content };
    }
    internal static Grid ResponsiveGrid(double minimum)
    {
        Grid grid = new() { ColumnSpacing = 12, RowSpacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.SizeChanged += (_, _) =>
        {
            if (grid.ActualWidth <= 0 || grid.Children.Count == 0) return;
            int columns = Math.Clamp((int)(grid.ActualWidth / (minimum * UiDisplaySettings.GeometryScale)), 1, grid.Children.Count), rows = (grid.Children.Count + columns - 1) / columns;
            if (grid.ColumnDefinitions.Count != columns || grid.RowDefinitions.Count != rows)
            {
                grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
                for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                for (int i = 0; i < rows; i++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            }
            for (int i = 0; i < grid.Children.Count; i++) { Grid.SetRow((FrameworkElement)grid.Children[i], i / columns); Grid.SetColumn((FrameworkElement)grid.Children[i], i % columns); }
        };
        return grid;
    }
    private Button Action(string label, Func<Task> action)
    {
        Button button = new() { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0, Padding = new Thickness(12, 8, 12, 8) };
        _controls.Add(button);
        button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { Status("Not completed: " + ex.Message); } };
        return button;
    }
    internal void Status(string message) => _status.Text = message;
    internal void Busy(bool busy)
    {
        _busy = busy;
        foreach (var c in _controls) c.IsEnabled = !busy;
        _additional.Busy(busy); UpdateScope();
    }
    private void UpdateScope()
    {
        bool ethernet = _adapters.SelectedItem is NetworkAdapterState { InterfaceType: 6, Status: "Up" };
        bool wifi = _adapters.SelectedItem is NetworkAdapterState { InterfaceType: 71 };
        bool ssid = wifi && WifiState is not null;
        _v4.Busy(_busy || !(ethernet || ssid)); _v6.Busy(_busy || !(ethernet || ssid));
        _category.IsEnabled = !_busy && ethernet;
        _additional.AdapterScope(ethernet);
        _wifiSettings.Visibility = wifi ? Visibility.Visible : Visibility.Collapsed;
        _scope.Text = ethernet ? "Ethernet · changes apply only to this adapter. Editing does not write anything until you save."
            : ssid ? "Wi-Fi SSID · " + WifiState!.Target.DisplaySsid + " · saved Windows IP/DNS configuration. Save affects this SSID only, not global adapter properties. Automatic values may be learned after reconnecting."
            : wifi ? "Wi-Fi per-SSID editor unavailable: " + _wifiError + " Global adapter writes remain blocked."
            : "No active physical Ethernet or Wi-Fi adapter selected. Connect an adapter, then refresh.";
        UpdateSummary();
    }
    internal void SetAdapters(NetworkAdapterState[] adapters)
    {
        string? previous = (_adapters.SelectedItem as NetworkAdapterState)?.Id;
        _loading = true;
        try
        {
            _adapters.Items.Clear();
            foreach (var a in adapters.Where(a => a.Status == "Up" && a.InterfaceType is 6 or 71).OrderBy(a => a.Name))
                _adapters.Items.Add(a);
            _adapters.SelectedItem = _adapters.Items.Cast<NetworkAdapterState>().FirstOrDefault(a => NetworkSettingsService.SameId(a.Id, previous ?? "")) ?? _adapters.Items.FirstOrDefault();
        }
        finally { _loading = false; }
        LoadAdapter();
    }
    private void LoadAdapter()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            WifiState = null;
            _wifiError = "Reading connected SSID…";
            var a = _adapters.SelectedItem as NetworkAdapterState;
            _originalV4 = NetworkEditorPolicy.FromState(a?.IPv4 ?? new(), false);
            _originalV6 = NetworkEditorPolicy.FromState(a?.IPv6 ?? new(), true);
            _originalCategory = a?.Category is "Public" or "Private" ? a.Category : "Unchanged";
            _category.SelectedItem = _originalCategory;
            _v4.Load(_originalV4); _v6.Load(_originalV6); _additional.Load(new());
            _info.Text = a is null ? "No active adapter found." : $"{a.Description}\n{a.Status} · {a.LinkSpeed} · {a.Category}\nMAC  {a.Mac} · Interface index {a.Index}";
            _details.Text = a is null ? "" : $"IPv4  {string.Join(", ", a.IPv4.Addresses.Select(x => x.Address + "/" + x.Prefix))}\nGateway  {string.Join(", ", a.IPv4.Gateways)}\nDNS  {string.Join(", ", a.IPv4.Dns)}\n{a.IPv4.ReadError}\nIPv6  {string.Join(", ", a.IPv6.Addresses.Select(x => x.Address + "/" + x.Prefix))}\nGateway  {string.Join(", ", a.IPv6.Gateways)}\nDNS  {string.Join(", ", a.IPv6.Dns)}\n{a.IPv6.ReadError}\nAdapter ID  {a.Id}";
        }
        finally { _loading = false; }
        UpdateScope();
    }
    internal NetworkSettingsProfile GetChanges() => new()
    {
        Name = "Direct IP/DNS settings", Group = "",
        AdapterId = (_adapters.SelectedItem as NetworkAdapterState)?.Id ?? "",
        Scope = IsWifi ? "WifiSsid" : "Adapter",
        WifiProfileName = WifiState?.Target.ProfileName ?? "",
        SsidHex = WifiState?.Target.SsidHex ?? "",
        Category = (string)_category.SelectedItem == _originalCategory ? "Unchanged" : (string)_category.SelectedItem,
        IPv4 = NetworkEditorPolicy.Changes(_originalV4, _v4.Value()),
        IPv6 = NetworkEditorPolicy.Changes(_originalV6, _v6.Value())
    };
    private void UpdateSummary()
    {
        if (_loading) return;
        var changes = GetChanges();
        bool pending = NetworkSettingsValidation.HasChanges(changes);
        _save.IsEnabled = !_busy && pending && (_adapters.SelectedItem is NetworkAdapterState { InterfaceType: 6, Status: "Up" } || IsWifi && WifiState is not null);
        _summary.Text = pending ? NetworkSettingsValidation.Summary(changes) : "No pending changes. Only edited sections will be written.";
    }
    internal void SetWifiState(WifiSsidState? state, string error = "")
    {
        if (state is not null && (!IsWifi || !NetworkSettingsService.SameId(state.Target.AdapterId, SelectedAdapterId))) return;
        _loading = true;
        try
        {
            WifiState = null; _wifiError = error;
            if (state is not null)
            {
                var v4 = WifiSsidCodec.Decode(state.IPv4, false); var v6 = WifiSsidCodec.Decode(state.IPv6, true);
                _originalV4 = v4; _originalV6 = v6; _v4.Load(v4); _v6.Load(v6);
                _originalCategory = "Unchanged"; _category.SelectedItem = "Unchanged";
                WifiState = state;
            }
        }
        finally { _loading = false; UpdateScope(); }
    }
    internal void SetSnapshots(string[] paths)
    {
        _snapshots.Items.Clear();
        foreach (var path in paths) _snapshots.Items.Add(new SnapshotItem(path));
        if (paths.Length > 0) _snapshots.SelectedIndex = 0;
    }
    private sealed record SnapshotItem(string Path) { public override string ToString() => System.IO.Path.GetFileNameWithoutExtension(Path); }
    private sealed partial class FamilyEditor : StackPanel
    {
        private readonly ComboBox _mode = Combo("IP assignment", ["Unchanged", "Automatic", "Manual"]), _dns = Combo("DNS assignment", ["Unchanged", "Automatic", "Manual"]);
        private readonly TextBox _address = Field("IP address"), _subnet, _gateway = Field("Gateway (optional)"), _preferred = Field("Preferred DNS address"), _alternate = Field("Alternate DNS address (optional)");
        private bool _busy;
        internal event Action? Changed;
        internal FamilyEditor(bool ipv6)
        {
            Spacing = 10;
            _subnet = Field(ipv6 ? "Subnet prefix length (1–128)" : "Subnet mask");
            foreach (Control c in new Control[] { _mode, _address, _subnet, _gateway, _dns, _preferred, _alternate })
            {
                Children.Add(c);
                if (c is TextBox box) box.TextChanged += (_, _) => Changed?.Invoke();
            }
            _mode.SelectionChanged += (_, _) => Update();
            _dns.SelectionChanged += (_, _) => Update();
            Update();
        }
        private void Update()
        {
            _mode.IsEnabled = _dns.IsEnabled = !_busy;
            _address.IsReadOnly = _subnet.IsReadOnly = _gateway.IsReadOnly = _busy || (string)_mode.SelectedItem != "Manual";
            _preferred.IsReadOnly = _alternate.IsReadOnly = _busy || (string)_dns.SelectedItem != "Manual";
            Changed?.Invoke();
        }
        internal void Busy(bool busy) { _busy = busy; Update(); }
        internal NetworkFamilySettings Value() => new()
        {
            AddressMode = (string)_mode.SelectedItem, Address = _address.Text.Trim(), Subnet = _subnet.Text.Trim(), Gateway = _gateway.Text.Trim(),
            DnsMode = (string)_dns.SelectedItem, PreferredDns = _preferred.Text.Trim(), AlternateDns = _alternate.Text.Trim()
        };
        internal void Load(NetworkFamilySettings f)
        {
            _mode.SelectedItem = f.AddressMode; _dns.SelectedItem = f.DnsMode;
            _address.Text = f.Address; _subnet.Text = f.Subnet; _gateway.Text = f.Gateway;
            _preferred.Text = f.PreferredDns; _alternate.Text = f.AlternateDns;
            Update();
        }
    }
}
