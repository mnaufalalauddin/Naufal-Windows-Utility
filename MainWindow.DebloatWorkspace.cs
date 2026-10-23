using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys;

public sealed partial class MainWindow
{
    private bool _debloatWorkspaceInitialized;
    private bool _debloatWorkspaceBusy;
    private readonly DebloatWorkspaceService _debloatWorkspace = new();
    private readonly List<Control> _debloatWorkspacePanels = [];
    private readonly Dictionary<string, string> _desktopUninstallLaunched = [];

    private static TextBlock WorkspaceText(string text) => DebloatWorkspaceView.Text(text);

    partial void OnAdvancedActivated()
    {
        if (_debloatWorkspaceInitialized) return;
        _debloatWorkspaceInitialized = true;
        AdvancedPage.Children.Add(new TextBlock { Text = "De-bloat workspace", FontSize = 24, TextWrapping = TextWrapping.Wrap });
        AdvancedPage.Children.Add(WorkspaceText("Review one change at a time. Scanning never removes apps. Configuration verification is not a performance benchmark or a guarantee of app behavior."));
        AddWorkspaceSection("Individual Windows AI controls", "Separate Copilot, Recall, Click to Do, Notepad AI and Paint AI. The combined Windows AI option remains in Advanced Windows Tweaks & De-Bloat.", LoadAiWorkspace);
        AddWorkspaceSection("Desktop / OEM apps", "Installed Win32 apps, A–Z. Publisher metadata is not a trust verdict. Purpose is not inferred from the name; consult the vendor. Removing OEM thermal, hotkey, audio, power or recovery tools can break hardware features.", LoadDesktopWorkspace);
        AddWorkspaceSection("Startup Apps Manager", "Disable or restore current-account Run registrations. This does not stop a running app. Machine-wide entries, startup folders and packaged startup tasks are managed through Windows Settings. No blanket service disabling.", LoadStartupWorkspace);
        AddWorkspaceSection("Per-app background controls", "Select supported packaged apps to deny background activity for ALL accounts on this PC. Notifications, sync, alarms and background transfers may stop. Desktop/full-trust apps need their own controls. Restart affected apps to check behavior.", LoadBackgroundWorkspace);
        AddWorkspaceSection("Reinstalled app detection", "Compare the current account with removals verified by this version of the utility. Older removals without a verified history are not inferred. Restoring an app intentionally clears its removal marker. Nothing is automatically uninstalled.", LoadReappearedWorkspace);
        AddWorkspaceSection("Provisioned apps · new Windows accounts", "ADVANCED: changes which apps Windows provisions for NEW accounts. Existing account installations remain separate. A log is not a payload backup; recovery may require licensed original packages, dependencies and Windows recovery media. Have a VM snapshot or system backup first.", LoadProvisionedWorkspace);
        UiDisplaySettings.Apply(AdvancedPage);
    }

    private void AddWorkspaceSection(string title, string description,
        Func<StackPanel, TextBlock, Task> load)
    {
        var section = new DebloatWorkspaceSection(title, description);
        var panel = section.Panel; var rows = section.Rows; var status = section.Status; var reload = section.Reload;
        _debloatWorkspacePanels.Add(reload); _debloatWorkspacePanels.Add(section.Viewer);
        reload.Click += async (_, _) => await WorkspaceRun(status, async () =>
        {
            rows.Children.Clear();
            await load(rows, status);
            UiDisplaySettings.Apply(panel);
        });
        AdvancedPage.Children.Add(section.View);
    }

    private async Task WorkspaceRun(TextBlock status, Func<Task> action, string? confirmation = null, string label = "Apply")
    {
        if (_debloatWorkspaceBusy) return;
        _debloatWorkspaceBusy = true;
        foreach (var panel in _debloatWorkspacePanels) panel.IsEnabled = false;
        TaskActivityService.TaskActivityLease? lease = null;
        try
        {
            if (confirmation is not null)
            {
                if (!await ShowConfirmationWindowAsync(label, confirmation, label)) return;
                lease = await AcquireManagedTaskAsync("DebloatWorkspace", label,
                    ["SystemMutation", "AppxDeployment", "Catalog:Advanced Windows Tweaks & De-Bloat"]);
                if (lease is null) return;
            }
            status.Text = "Working…";
            await action();
            lease?.Complete(status.Text == "Cancelled." ? "CANCELLED" : "COMPLETED", status.Text);
        }
        catch (Exception exception)
        {
            status.Text = "Not completed: " + exception.Message;
            lease?.Complete("FAILED", status.Text);
        }
        finally
        {
            lease?.Dispose();
            _debloatWorkspaceBusy = false;
            foreach (var panel in _debloatWorkspacePanels) panel.IsEnabled = true;
            RefreshManagedTaskHeader();
        }
    }
    private static StackPanel WorkspaceCard(StackPanel rows, string name, string details)
        => DebloatWorkspaceView.Card(rows, name, details);
    private static Button WorkspaceButton(StackPanel parent, string text, Func<Task> action, bool enabled = true)
    {
        Button button = DebloatWorkspaceView.Button(text); button.IsEnabled = enabled;
        button.Click += async (_, _) => await action();
        parent.Children.Add(button);
        return button;
    }
    private static void WorkspaceSearch(StackPanel rows, List<(FrameworkElement Row, string Text)> items)
    {
        TextBox search = new() { PlaceholderText = "Search name, publisher or identity", Header = "Filter list" };
        search.TextChanged += (_, _) =>
        {
            foreach (var item in items) item.Row.Visibility = item.Text.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        };
        rows.Children.Insert(0, search);
    }

    private async Task LoadAiWorkspace(StackPanel rows, TextBlock status)
    {
        var copilot = WorkspaceCard(rows, "Copilot app only", "Uninstall for the current account using the existing verified app-removal workflow. Does not disable Recall, Paint or Notepad. App data may be lost. Restore requires Microsoft Store availability.");
        foreach (bool restore in new[] { false, true })
            WorkspaceButton(copilot, restore ? "Restore Copilot app" : "Uninstall Copilot app", () => WorkspaceRun(status, async () =>
            {
                if (!await ConfirmCopilotSourceAsync(this)) { status.Text = "Cancelled."; return; }
                using var consent = CopilotSourceConsent.BeginConfirmedOperation();
                var service = new BuiltInAppsService();
                var result = await BuiltInAppsRemoval.RunAsync(service, ["Copilot"], null, restore);
                if (!result.Success) throw new InvalidOperationException(result.Report);
                status.Text = result.Report;
            }, restore ? BuiltInAppsCatalog.StoreConsent + " " + BuiltInAppsCatalog.RestoreNotice : BuiltInAppsCatalog.Warning,
                restore ? "Restore Copilot" : "Uninstall Copilot"));
        foreach (var control in DebloatWorkspacePolicy.AiControls)
        {
            var state = await Task.Run(() => (Reason: DebloatWorkspaceService.AiUnavailable(control),
                Status: _debloatWorkspace.AiStatus(control), Saved: _debloatWorkspace.HasSnapshot("ai-" + control.Id)));
            var card = WorkspaceCard(rows, control.Name, control.Warning + "\n" + state.Status +
                (state.Reason is null ? "" : "\nUnavailable: " + state.Reason));
            foreach (bool restore in new[] { false, true })
                WorkspaceButton(card, restore ? "Restore original policy" : "Disable AI feature", () => WorkspaceRun(status, async () =>
                {
                    await Task.Run(() => _debloatWorkspace.ChangeAi(control, restore));
                    status.Text = "Configuration read-back verified. " + control.Warning + " Reload to see the current state.";
                }, control.Name + ": " + control.Warning + "\nScope: all Windows accounts. Original policy snapshot is retained before changes.",
                    restore ? "Restore original policy" : "Disable " + control.Name), restore ? state.Saved : state.Reason is null);
        }
        status.Text = "AI configuration loaded. Existing policy ownership is checked again before any write.";
    }

    private async Task LoadDesktopWorkspace(StackPanel rows, TextBlock status)
    {
        var apps = await Task.Run(DebloatWorkspaceService.ReadDesktop);
        foreach (var pending in _desktopUninstallLaunched)
            rows.Children.Add(WorkspaceText(pending.Value + (apps.Any(a => a.Identity == pending.Key)
                ? ": Uninstall registration is still present. Finish the vendor's prompts or investigate its result."
                : ": Previously selected uninstall registration is now absent. This does not prove all files/data were removed.")));
        List<(FrameworkElement Row, string Text)> searchable = [];
        foreach (var app in apps)
        {
            var card = WorkspaceCard(rows, app.Name, $"Publisher (reported): {app.Publisher}\nVersion: {app.Version} · Scope: {app.Scope}\nLocation: {app.Location}\n" +
                (app.SystemComponent ? "System component: direct removal blocked." : "Review vendor documentation before removal. No automatic bloat classification.") + "\nRegistered uninstaller: " + app.UninstallCommand);
            searchable.Add(((FrameworkElement)rows.Children.Last(), app.Name + " " + app.Publisher + " " + app.Identity));
            WorkspaceButton(card, "Launch registered uninstaller", () => WorkspaceRun(status, () =>
            {
                DebloatWorkspaceService.LaunchUninstaller(app);
                _desktopUninstallLaunched[app.Identity] = app.Name;
                status.Text = "Uninstaller launched; removal is NOT yet verified. Complete its prompts, then Analyze / reload to check registration. Reinstall through the vendor if needed; no app-data backup was created.";
                return Task.CompletedTask;
            }, $"{app.Name}\nPublisher (reported, not verified): {app.Publisher}\nScope: {app.Scope}\nCommand: {app.UninstallCommand}\nThe vendor uninstaller may remove data or request a restart. It runs independently of this window. Save your work first.", "Launch uninstaller"), !app.SystemComponent && app.UninstallCommand.Length > 0);
        }
        WorkspaceSearch(rows, searchable);
        WorkspaceButton(rows, "Open Windows Installed apps", () => WorkspaceRun(status, async () =>
        { if (!await Windows.System.Launcher.LaunchUriAsync(new("ms-settings:appsfeatures"))) throw new InvalidOperationException("Windows Settings did not open."); status.Text = "Windows Installed apps opened. No removal is claimed."; }));
        status.Text = $"{apps.Length} desktop registrations found. Portable apps without uninstall registration are not included.";
    }

    private async Task LoadStartupWorkspace(StackPanel rows, TextBlock status)
    {
        var apps = await Task.Run(_debloatWorkspace.ReadStartup);
        List<(FrameworkElement Row, string Text)> searchable = [];
        foreach (var app in apps)
        {
            bool saved = _debloatWorkspace.HasSnapshot(app.Id);
            var card = WorkspaceCard(rows, app.Name, $"{(app.DisabledByUtility ? "Disabled by this utility" : "Run registration present (not proof it ran)")}\nPublisher (file metadata): {app.Publisher}\nSource: {app.Source}\nCommand: {app.Command}\n{app.BlockReason}");
            searchable.Add(((FrameworkElement)rows.Children.Last(), app.Name + " " + app.Publisher + " " + app.Command));
            WorkspaceButton(card, saved ? "Enable / restore original registration" : "Disable startup registration", () => WorkspaceRun(status, async () =>
            {
                await Task.Run(() => _debloatWorkspace.ChangeStartup(app, saved));
                status.Text = "Startup registration read-back verified. A running app was not stopped. Reload the list; next-sign-in behavior has not been tested.";
            }, $"{app.Name}\n{app.Command}\nScope: this Windows account's Run registration. Disabling may stop sync, notifications or vendor utilities at sign-in. Restore uses the exact saved registration, not guessed defaults.",
                saved ? "Restore startup" : "Disable startup"), app.BlockReason is null);
        }
        WorkspaceSearch(rows, searchable);
        WorkspaceButton(rows, "Open all Windows startup apps", () => WorkspaceRun(status, async () =>
        { if (!await Windows.System.Launcher.LaunchUriAsync(new("ms-settings:startupapps"))) throw new InvalidOperationException("Windows Settings did not open."); status.Text = "Windows startup settings opened."; }));
        status.Text = $"{apps.Length} Run registrations / saved entries loaded. Services and scheduled tasks are not changed here.";
    }

    private async Task LoadBackgroundWorkspace(StackPanel rows, TextBlock status)
    {
        var apps = await Task.Run(DebloatWorkspaceService.ReadBackground);
        string[] denied = _debloatWorkspace.BackgroundDeniedFamilies();
        List<(CheckBox Check, string Family)> choices = [];
        foreach (var app in apps)
        {
            var card = WorkspaceCard(rows, app.Name, app.Family + "\n" + (denied.Contains(app.Family, StringComparer.OrdinalIgnoreCase)
                ? "Listed in background deny policy.\n" : "Not listed in background deny policy; other settings may still restrict it.\n") +
                (app.BlockReason ?? "Background-task declaration found; policy read-back will be verified, runtime behavior must be checked separately."));
            CheckBox check = new() { Content = WorkspaceText("Deny background activity"), IsEnabled = app.BlockReason is null };
            card.Children.Add(check); choices.Add((check, app.Family));
        }
        WorkspaceButton(rows, "Deny background for selected apps", () => WorkspaceRun(status, async () =>
        {
            string[] selected = choices.Where(c => c.Check.IsChecked == true).Select(c => c.Family).ToArray();
            await Task.Run(() => _debloatWorkspace.ChangeBackground(selected, false));
            status.Text = "Background deny list read-back verified. Restart affected apps. Restore this selection before applying a different selection.";
        }, "Deny background activity for the selected packages across ALL Windows accounts? Notifications, sync, alarms and transfers may stop. This does not stop desktop processes. Existing policy owners are not overridden.", "Deny selected"));
        WorkspaceButton(rows, "Restore original background list", () => WorkspaceRun(status, async () =>
        {
            await Task.Run(() => _debloatWorkspace.ChangeBackground([], true));
            status.Text = "Original background deny list restored and verified. Restart affected apps, then reload.";
        }, "Restore the exact original background deny list saved by this workspace? External changes block restoration instead of being overwritten.", "Restore background"), _debloatWorkspace.HasSnapshot("background"));
        status.Text = $"{apps.Count(a => a.BlockReason is null)}/{apps.Length} packages declare supported background tasks. " +
            (_debloatWorkspace.HasSnapshot("background") ? "Utility snapshot exists; restore before changing the selection." : "No utility background snapshot.");
    }

    private async Task LoadReappearedWorkspace(StackPanel rows, TextBlock status)
    {
        var history = DebloatRemovalHistory.Read();
        var targets = BuiltInAppsCatalog.Targets.Where(t => history.Any(e => e.AppId == t.Id)).ToArray();
        var packages = await new BuiltInAppsService().ReadAsync();
        string[] present = targets.Where(t => packages.Any(p => BuiltInAppsCatalog.Matches(t, p) && p.InventoryError is null)).Select(t => t.Id).ToArray();
        string[] returned = DebloatWorkspacePolicy.Reappeared(history, present);
        foreach (string id in returned)
        {
            var target = targets.Single(t => t.Id == id);
            WorkspaceCard(rows, target.Name, "Present after a verified removal on " + history.Last(e => e.AppId == id).At.ToLocalTime().ToString("g") +
                ". Windows, Store, another installer or a manual reinstall may be responsible; cause was not inferred. Review it in Built-in Windows Apps.");
        }
        int unknown = targets.Count(t => packages.Any(p => BuiltInAppsCatalog.Matches(t, p) && p.InventoryError is not null));
        status.Text = $"Reappeared: {returned.Length}. Unverified app inventories: {unknown}. No automatic removal. History starts with verified operations recorded by this version.";
    }

    private async Task LoadProvisionedWorkspace(StackPanel rows, TextBlock status)
    {
        var apps = await _debloatWorkspace.ReadProvisionedAsync();
        foreach (var app in apps)
        {
            var card = WorkspaceCard(rows, app.DisplayName, app.PackageName + "\n" +
                (app.TargetId is null ? "Read-only: not in the approved deprovisioning allowlist." : "Eligible for explicit new-account deprovisioning. No automatic payload recovery."));
            WorkspaceButton(card, "Remove provisioning for new accounts", () => WorkspaceRun(status, async () =>
            { status.Text = await _debloatWorkspace.RemoveProvisionedAsync(app); },
                $"{app.PackageName}\nADVANCED — NEW ACCOUNTS ONLY. Existing installations are separate. Have a VM snapshot/system backup and recovery media available before continuing. This cannot restore removed app data or guarantee package re-provisioning. No reboot will be triggered.",
                "Confirm backup and deprovision"), app.TargetId is not null);
        }
        status.Text = $"{apps.Length} provisioned packages found; {apps.Count(a => a.TargetId is not null)} allowlisted for explicit removal. This is separate from current-account uninstall.";
    }
}
