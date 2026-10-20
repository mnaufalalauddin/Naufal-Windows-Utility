using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys;

public sealed partial class MainWindow
{
    private NetworkSettingsView? _networkSettingsView;
    partial void OnNetworkActivated()
    {
        if (_networkSettingsView is not null) return;
        NetworkSettingsService service = new(); WifiSsidService wifi = new(); NetworkSettingsView view = new();
        _networkSettingsView = view; NetworkPage.Content = view;
        bool busy = false;
        async Task Run(Func<Task> operation)
        {
            if (busy || _isClosed) return;
            busy = true; view.Busy(true);
            try { await operation(); }
            catch (Exception ex) { view.Status("Not completed: " + ex.Message); }
            finally { busy = false; view.Busy(false); }
        }
        void RefreshSnapshots() => view.SetSnapshots(service.Snapshots().Concat(wifi.Snapshots()).OrderByDescending(p => p).ToArray());
        async Task LoadWifi()
        {
            if (!view.IsWifi) return;
            string id = view.SelectedAdapterId;
            try { view.SetWifiState(await Task.Run(() => wifi.Read(id))); }
            catch (Exception ex) { view.SetWifiState(null, ex.Message); view.Status("SSID settings could not be read: " + ex.Message); }
        }
        view.WifiSelected = _ => Run(LoadWifi);
        view.RefreshRequested = () => Run(async () =>
        {
            view.Status("Reading active adapters and connected SSID. No settings will change...");
            view.SetAdapters(await service.ReadAsync()); RefreshSnapshots();
            view.Status("Scan complete. No configuration was changed.");
            await LoadWifi();
        });
        view.ApplyRequested = profile => Run(async () =>
        {
            NetworkSettingsValidation.Validate(profile);
            if (!NetworkSettingsValidation.HasChanges(profile)) throw new ArgumentException("No pending changes. Edit an IP or DNS section first.");
            bool perSsid = profile.Scope == "WifiSsid";
            var baseline = view.WifiState;
            if (perSsid) { if (baseline is null) throw new InvalidOperationException("Refresh the connected SSID first."); await service.PreflightWifiAsync(profile.AdapterId); }
            else await service.RequireEthernetAsync(profile.AdapterId);
            if (!await ShowConfirmationWindowAsync("Save network settings", NetworkSettingsValidation.Summary(profile) + "\n\nThis may disconnect this PC and remote sessions. Confirm only from the local console. Private permits discovery on this network. A local recovery snapshot is saved first. Complex/policy-managed configurations are blocked. Continue?", "Save to Windows")) return;
            using var lease = await AcquireManagedTaskAsync("NetworkSettings:Apply", "Save network settings", ["SystemMutation", "NetworkSettings"]);
            if (lease is null) return;
            try { view.Status("Applying selected settings..."); string result = perSsid ? await Task.Run(() => wifi.ApplyAsync(profile, baseline!)) : await service.ApplyAsync(profile); view.SetAdapters(await service.ReadAsync()); await LoadWifi(); view.Status(result); lease.Complete("COMPLETED", result); }
            catch (Exception ex) { lease.Complete("FAILED", ex.Message); throw; }
            finally { RefreshSnapshots(); RefreshManagedTaskHeader(); }
        });
        view.RestoreRequested = path => Run(async () =>
        {
            bool perSsid = wifi.Snapshots().Contains(path);
            string description = perSsid ? "SSID: " + wifi.LoadSnapshot(path).Before.Target.DisplaySsid + "\nRestore exact stored IP/DNS metadata for this SSID only. Connect to that original SSID first. Newer external changes are not overwritten."
                : NetworkSettingsValidation.Summary(NetworkSettingsService.RestoreProfile(service.LoadSnapshot(path)));
            if (!await ShowConfirmationWindowAsync("Restore network snapshot", description + "\n\nThis may disconnect the PC. Restore from the local console?", "Restore configuration")) return;
            using var lease = await AcquireManagedTaskAsync("NetworkSettings:Restore", "Restore network snapshot", ["SystemMutation", "NetworkSettings"]);
            if (lease is null) return;
            try { string result = perSsid ? await Task.Run(() => wifi.RestoreAsync(path)) : await service.RestoreAsync(path); view.SetAdapters(await service.ReadAsync()); await LoadWifi(); view.Status(result); lease.Complete("COMPLETED", result); }
            catch (Exception ex) { lease.Complete("FAILED", ex.Message); throw; }
            finally { RefreshManagedTaskHeader(); }
        });
        view.AdditionalRequested = (action, adapterId, settings) => Run(async () =>
        {
            if (action == "error") { view.Status(settings.HostsEntries); return; }
            NetworkAdditionalPolicy.Validate(action, settings);
            bool readOnly = action is "printers" or "wifi-list";
            string? hash = action == "program" ? NetworkAdditionalService.ProgramHash(settings.ProgramPath) : null;
            if (!readOnly && !await ShowConfirmationWindowAsync("Network additional action: " + action,
                "Adapter: " + adapterId + "\n" + JsonSerializer.Serialize(settings, NetworkSettingsJson.Default.NetworkAdditionalSettings) +
                "\n\nOnly the named action will run. These actions are NOT included in IP/DNS snapshot rollback. Network changes can disconnect this PC. Printer/drive settings apply to this execution account and logon context; elevated mappings may be separate from Explorer. Workgroup/name changes need a later manual restart. Hosts updates retain a full backup. SMTP tests only open a TCP connection.\n" +
                (hash is null ? "" : "PROGRAM SHA256: " + hash + "\nThis program/script runs with the utility's privileges and may change or delete files/settings outside this module. No automatic rollback or trust certification is provided. A two-minute timeout terminates the started process tree; use only short tasks, not interactive editors or long installers.\n") + "Continue?", "Run " + action)) return;
            using var lease = await AcquireManagedTaskAsync("NetworkSettings:Additional", "Network: " + action, readOnly || action == "smtp" ? ["NetworkSettings"] : ["SystemMutation", "NetworkSettings"]);
            if (lease is null) return;
            try { view.Status("Running " + action + "..."); string result = await new NetworkAdditionalService().RunAsync(action, adapterId, settings, hash); view.Status(result); lease.Complete("COMPLETED", result); }
            catch (Exception ex) { lease.Complete("FAILED", ex.Message); throw; }
            finally { RefreshManagedTaskHeader(); }
        });
        view.OpenToolRequested = async tool =>
        {
            string? uri = tool switch { "wifi-ssid" => "ms-settings:network-wifisettings", "wifi" => "ms-settings:network-wifi", "printers" => "ms-settings:printers", "proxy" => "ms-settings:network-proxy", "vpn" => "ms-settings:network-vpn", "firewall" => "windowsdefender://network", _ => null };
            if (uri is not null) { if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(uri))) throw new InvalidOperationException("Windows could not open this Settings page."); return; }
            var (file, argument) = tool switch { "adapters" => ("control.exe", "ncpa.cpl"), "computer" => ("control.exe", "sysdm.cpl"), "devices" => ("mmc.exe", "devmgmt.msc"), "drives" => ("explorer.exe", "shell:MyComputerFolder"), _ => throw new ArgumentException("Unknown Windows tool.") };
            Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), file == "explorer.exe" ? "" : "System32", file), argument) { UseShellExecute = true });
        };
        UiDisplaySettings.Apply(view);
        // Read-only initial scan. Opening this page never applies settings.
        _ = view.RefreshRequested();
    }
}
