using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Naufal_Windows_Tech_s_Powertoys;

public sealed partial class MainWindow
{
    private async Task CheckNetworkSettingsAsync()
    {
        NetworkSettingsView view = new();
        view.WifiSelected = _ => Task.CompletedTask;
        view.ApplyRequested = _ => Task.CompletedTask;
        view.RestoreRequested = _ => Task.CompletedTask;
        view.OpenToolRequested = _ => Task.CompletedTask;
        view.AdditionalRequested = (_, _, _) => Task.CompletedTask;
        NetworkPage.Content = view;
        var introduction = view.Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
        Check(introduction.Any(t => t.Contains("save, reconnect, roaming and recovery have been tested")), "network introduction reflects bounded live Wi-Fi evidence");
        Check(!introduction.Any(t => t.Contains("not yet hardware-tested")), "obsolete blanket no-hardware-test claim removed");
        Check(introduction.Any(t => t.Contains("Ethernet or Wi-Fi SSID changes")), "snapshot notice covers both persistence scopes");
        NetworkAdapterState ethernet = new() { Id = "d4e71239-cbbc-4f4e-9f68-e858cb5b0579", InterfaceType = 6, Name = "Ethernet — TEST FIXTURE", Description = "Example 2.5 Gb Ethernet adapter", Status = "Up", Mac = "02-00-00-00-00-01", LinkSpeed = "2.5 Gbps", Category = "Public", IPv4 = new() { Dhcp = "Enabled", DnsAutomatic = true, Addresses = [new() { Address = "192.0.2.20", Prefix = 24 }], Gateways = ["192.0.2.1"], Dns = ["192.0.2.53"] } };
        view.SetAdapters([ethernet]);
        MainNavigation.SelectedItem = MainNavigation.MenuItems[5];
        int refreshes = 0;
        view.RefreshRequested = () => { refreshes++; return Task.CompletedTask; };
        foreach (int width in new[] { 1280, 800, 480 })
        foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        foreach (int scale in new[] { 100, 200 })
        {
            _case = $"network width={width}, theme={theme}, scale={scale}";
            AppWindow.Resize(new SizeInt32(width, 900));
            if (UiDisplaySettings.Theme != theme) UiDisplaySettings.ToggleTheme();
            UiDisplaySettings.SetTextScale(scale); UiDisplaySettings.Apply(RootLayout);
            if (MainNavigation.DisplayMode != NavigationViewDisplayMode.Expanded) MainNavigation.IsPaneOpen = false;
            await Task.Delay(80); RootLayout.UpdateLayout();
            Check(view.ActualWidth > 0 && view.ActualWidth <= RootLayout.ActualWidth, "network editor fits page");
            Check(view.GetChanges().AdapterId == "d4e71239-cbbc-4f4e-9f68-e858cb5b0579", "adapter picker preserves stable identity");
            Check(!NetworkSettingsValidation.HasChanges(view.GetChanges()), "fresh editor defaults to leave unchanged");
            var refresh = VisualDescendants<Button>(view).First(b => b.Content is TextBlock { Text: "Refresh / discard edits" });
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(refresh);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Task.Delay(10); Check(refreshes > 0, "Refresh calls injected handler only");
            view.Busy(true);
            Check(!refresh.IsEnabled && VisualDescendants<ComboBox>(view).All(c => !c.IsEnabled), "busy editor blocks adapter/mode changes");
            view.Busy(false);
            foreach (var field in VisualDescendants<TextBox>(view)) Check(field.ActualWidth <= view.ActualWidth + 1, "text field remains within editor width");
            if (width == 1280 && scale == 100) await SaveDiskPreviewAsync(RootLayout, "network-settings-" + theme.ToString().ToLowerInvariant() + ".png");
        }
        _case = "network direct editor behavior";
        // Only mock callbacks execute; this test never applies network configuration.
        UiDisplaySettings.SetTextScale(100);
        AppWindow.Resize(new SizeInt32(1280, 900));
        await Task.Delay(80); RootLayout.UpdateLayout();
        var adapterPicker = VisualDescendants<ComboBox>(view).First(c => c.Header is string h && h.StartsWith("Active Ethernet"));
        view.SetAdapters([ethernet, ethernet with { Id = Guid.NewGuid().ToString(), Status = "Disconnected" }, ethernet with { Id = Guid.NewGuid().ToString(), InterfaceType = 0 }]);
        Check(adapterPicker.Items.Count == 1, "only active known physical adapter types are offered");
        Check(VisualDescendants<TextBox>(view).Any(t => t.Header is string h && h == "IP address" && t.Text == "192.0.2.20" && t.IsReadOnly), "current DHCP IP appears in main editor");
        Check(VisualDescendants<TextBox>(view).Any(t => t.Header is string h && h == "Subnet mask" && t.Text == "255.255.255.0"), "current subnet mask is shown");
        var save = VisualDescendants<Button>(view).First(b => b.Content is TextBlock { Text: "Save to Windows…" });
        Check(!save.IsEnabled, "unchanged form cannot save");
        var dnsMode = VisualDescendants<ComboBox>(view).First(c => c.Header is string h && h == "DNS assignment");
        dnsMode.SelectedItem = "Manual";
        var preferred = VisualDescendants<TextBox>(view).First(t => t.Header is string h && h == "Preferred DNS address");
        preferred.Text = "192.0.2.54";
        Check(save.IsEnabled && view.GetChanges().IPv4.DnsMode == "Manual" && view.GetChanges().IPv4.AddressMode == "Unchanged", "DNS edit enables save without rewriting address");
        int saves = 0;
        view.ApplyRequested = changes => { saves++; Check(changes.IPv4.PreferredDns == "192.0.2.54", "save passes edited DNS to injected backend"); return Task.CompletedTask; };
        var savePeer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(save);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)savePeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Task.Delay(10);
        Check(saves == 1, "Save to Windows invokes apply once, not local library storage");
        view.Busy(true); Check(!save.IsEnabled && preferred.IsReadOnly, "save and fields locked during operation"); view.Busy(false);
        view.SetAdapters([ethernet with { InterfaceType = 71, Name = "Wi-Fi — TEST FIXTURE" }]);
        Check(!NetworkSettingsValidation.HasChanges(view.GetChanges()), "adapter switch discards previous adapter edits");
        foreach (var expander in VisualDescendants<Expander>(view).ToArray()) expander.IsExpanded = true;
        await Task.Delay(100); RootLayout.UpdateLayout();
        // Additional inner expanders are realized after the outer expander is opened.
        foreach (var expander in VisualDescendants<Expander>(view).ToArray()) expander.IsExpanded = true;
        await Task.Delay(100); RootLayout.UpdateLayout();
        view.Busy(true); view.Busy(false);
        foreach (string label in new[] { "Save to Windows…", "Set WINS servers…", "Add persistent route…" })
            Check(!VisualDescendants<Button>(view).First(b => b.Content is TextBlock t && t.Text == label).IsEnabled, "Wi-Fi global mutation blocked after busy reset: " + label);
        Check(VisualDescendants<ComboBox>(view).Where(c => c.Header is string h && h is "IP assignment" or "DNS assignment").All(c => !c.IsEnabled), "Wi-Fi family editors remain read-only without per-SSID writer");
        Check(VisualDescendants<Button>(view).First(b => b.Content is TextBlock { Text: "Open Windows per-SSID settings" }).IsEnabled, "explicit per-SSID Windows handoff remains available");
        var target = new WifiProfileIdentity(ethernet.Id, "Office Windows profile", "4F6666696365", "Office", false);
        view.SetWifiState(new(target, null, null));
        Check(VisualDescendants<ComboBox>(view).Where(c => c.Header is string h && h is "IP assignment" or "DNS assignment").All(c => c.IsEnabled), "supported SSID metadata enables in-page editing");
        Check(!save.IsEnabled, "loading SSID metadata creates no pending write");
        var wifiDns = VisualDescendants<ComboBox>(view).First(c => c.Header is string h && h == "DNS assignment");
        wifiDns.SelectedItem = "Manual";
        var wifiPreferred = VisualDescendants<TextBox>(view).First(t => t.Header is string h && h == "Preferred DNS address");
        wifiPreferred.Text = "192.0.2.53";
        Check(save.IsEnabled && view.GetChanges() is { Scope: "WifiSsid", WifiProfileName: "Office Windows profile", SsidHex: "4F6666696365" }, "Save bound to exact connected SSID and profile");
        Check(view.GetChanges().IPv4.AddressMode == "Unchanged", "SSID DNS-only edit does not rewrite IP");
        int ssidSaves = 0;
        view.ApplyRequested = plan => { ssidSaves++; Check(plan.Scope == "WifiSsid", "SSID Save uses separate backend scope"); return Task.CompletedTask; };
        var wifiSavePeer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(save);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)wifiSavePeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Task.Delay(10);
        Check(ssidSaves == 1, "Wi-Fi Save calls mock persistence handler once");
        view.SetWifiState(null, "Unsupported build fixture");
        Check(!save.IsEnabled && wifiPreferred.IsReadOnly, "unavailable SSID provider blocks stale edits");
        Check(!VisualDescendants<Button>(view).Any(b => b.Content is TextBlock t && (t.Text is "New profile" or "Save profile" or "Activate profile…" || t.Text.StartsWith("Arm one-shot"))), "local profile workflow and automatic activation removed");
        view.SetAdapters([]);
        Check(adapterPicker.Items.Count == 0 && !save.IsEnabled, "no adapter prevents save");
        view.SetAdapters([ethernet]);
        Check(!save.IsEnabled && !NetworkSettingsValidation.HasChanges(view.GetChanges()), "refresh loads current values without pending writes");
        NetworkPage.Content = null;
        MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
    }
}
