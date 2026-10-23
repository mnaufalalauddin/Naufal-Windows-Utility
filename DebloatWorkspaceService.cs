using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Windows.Management.Deployment;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed class DebloatWorkspaceService : IProvisionedAppsBackend
{
    private readonly DebloatRegistry _registry = new();
    private readonly DebloatJournal _journal = new();
    private readonly NativeCommandRunner _commands = new();
    internal bool HasSnapshot(string id) => _journal.Load(id) is not null;

    internal static void RequireLocalPolicyOwner()
    {
        if (!WindowsPrivilegeService.IsAdministrator()) throw new InvalidOperationException("Administrator rights required.");
        if (new SecurityMitigationNative().Read().Managed != false)
            throw new InvalidOperationException("Organization management is present or unresolved. Use the policy owner's controls; nothing was changed.");
    }

    internal string AiStatus(AiControl control)
    {
        bool disabled = control.Edits.All(e => DebloatTransaction.Equal(_registry.Read(e.Target), e.Value));
        string state = disabled ? "Disable policy configured" : "Disable policy not fully configured";
        return state + (HasSnapshot("ai-" + control.Id) ? " · Original snapshot available" : " · No utility snapshot");
    }
    internal static string? AiUnavailable(AiControl control)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string edition = key?.GetValue("EditionID") as string ?? "";
        int build = int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out int b) ? b : 0;
        int ubr = key?.GetValue("UBR") is int r ? r : 0;
        if (!DebloatWorkspacePolicy.SupportsAi(control.Id, build, ubr, edition)) return "Unsupported Windows edition or servicing build.";
        if (control.Id is "Notepad" or "Paint")
        {
            string name = control.Id == "Notepad" ? "Microsoft.WindowsNotepad" : "Microsoft.Paint";
            var package = new PackageManager().FindPackagesForUser("").FirstOrDefault(p => p.Id.Name == name);
            if (package is null) return "The related app is not installed for this account.";
            var v = package.Id.Version;
            if (control.Id == "Notepad" && new Version(v.Major, v.Minor, v.Build, v.Revision) < new Version(11, 2503, 16, 0))
                return "Notepad 11.2503.16.0 or newer is required.";
        }
        return null;
    }
    internal void ChangeAi(AiControl requested, bool restore)
    {
        var control = DebloatWorkspacePolicy.AiControls.Single(c => c.Id == requested.Id);
        RequireLocalPolicyOwner();
        using var combined = Registry.CurrentUser.OpenSubKey(@"Software\Naufal Windows Tech\Powertoys\Backups\WindowsAI");
        using var paint = Registry.CurrentUser.OpenSubKey(@"Software\Naufal Windows Tech\Powertoys\Backups\DebloatRegistryLab\PaintAI");
        if (combined is not null || control.Id == "Paint" && paint is not null)
            throw new InvalidOperationException("Restore the combined Windows AI / Paint AI snapshot before using individual controls.");
        if (!restore && AiUnavailable(control) is string reason) throw new InvalidOperationException(reason);
        // Refuse to adopt an existing policy as a utility-owned setting.
        if (!restore && !HasSnapshot("ai-" + control.Id) && control.Edits.Any(e => _registry.Read(e.Target) is not null))
            throw new InvalidOperationException("An existing AI policy is present. Review its owner first; no policy was overwritten.");
        DebloatTransaction.Change("ai-" + control.Id, control.Edits, restore, _registry, _journal);
    }

    internal static DesktopAppEntry[] ReadDesktop()
    {
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        List<DesktopAppEntry> entries = [];
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(path);
            if (uninstall is null) continue;
            foreach (string id in uninstall.GetSubKeyNames())
            {
                using var item = uninstall.OpenSubKey(id);
                if (item?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                entries.Add(new($"{hive}|{view}|{id}", name, item.GetValue("Publisher") as string ?? "Not reported",
                    item.GetValue("DisplayVersion") as string ?? "Not reported", item.GetValue("InstallLocation") as string ?? "Not reported",
                    hive == RegistryHive.CurrentUser ? "This account" : "All users", item.GetValue("UninstallString") as string ?? "",
                    item.GetValue("SystemComponent") is int system && system != 0 || item.GetValue("ParentKeyName") is not null));
            }
        }
        return entries.DistinctBy(e => (e.Scope, e.Name, e.UninstallCommand)).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    internal static string[] ParseCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return [];
        nint argv = CommandLineToArgvW(command, out int count);
        if (argv == 0) throw new InvalidOperationException("Unable to parse registered command.");
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "").ToArray(); }
        finally { LocalFree(argv); }
    }
    private static string LocalExecutable(string command)
    {
        string path = Environment.ExpandEnvironmentVariables(ParseCommand(command).FirstOrDefault() ?? "");
        if (string.Equals(path, "msiexec.exe", StringComparison.OrdinalIgnoreCase) || string.Equals(path, "msiexec", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidOperationException("No unambiguous local executable is registered. Use Windows Installed apps.");
        return path;
    }
    internal static void LaunchUninstaller(DesktopAppEntry selected)
    {
        var fresh = ReadDesktop().SingleOrDefault(e => e.Identity == selected.Identity);
        if (fresh != selected) throw new InvalidOperationException("App registration changed. Reload before uninstalling.");
        if (fresh.SystemComponent) throw new InvalidOperationException("System component: use Windows or the vendor's servicing tools.");
        string executable = LocalExecutable(fresh.UninstallCommand);
        if (new[] { "cmd", "powershell", "pwsh", "wscript", "cscript", "rundll32", "regsvr32", "mshta" }
            .Contains(Path.GetFileNameWithoutExtension(executable), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Script-host uninstallers are not launched here. Use Windows Installed apps.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = true };
        foreach (string argument in ParseCommand(fresh.UninstallCommand).Skip(1)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not launch the uninstaller.");
        // Do not kill vendor installers, add silent switches, or claim uninstall success.
    }

    internal StartupAppEntry[] ReadStartup()
    {
        List<StartupAppEntry> result = [];
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            // HKCU Run is shared across WOW64 views; do not create two owners.
            if (hive == RegistryHive.CurrentUser && view == RegistryView.Registry32) continue;
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var run = root.OpenSubKey(DebloatWorkspacePolicy.RunPath);
            if (run is null) continue;
            foreach (string name in run.GetValueNames())
            {
                var target = new DebloatTarget(hive, view, DebloatWorkspacePolicy.RunPath, name);
                var value = _registry.Read(target)!;
                string command = value.Data.FirstOrDefault() ?? "";
                string? blocked = hive != RegistryHive.CurrentUser ? "Machine-wide startup: manage in Windows Settings." : null;
                if (name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) || name.StartsWith("MicrosoftEdgeAutoLaunch", StringComparison.OrdinalIgnoreCase))
                    blocked = "Use the existing OneDrive / Edge controls in Advanced Windows Tweaks to preserve their snapshot ownership.";
                if (value.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) blocked = "Unsupported registration type.";
                using var approved = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" +
                    (view == RegistryView.Registry32 ? "Run32" : "Run"));
                object? approval = approved?.GetValue(name);
                if (approval is not null && (approval is not byte[] bytes || bytes.Length != 12 || bytes[0] != 2))
                    blocked = "Windows startup approval is disabled or unknown. Manage in Windows Settings.";
                string publisher = "Not verified";
                try { publisher = FileVersionInfo.GetVersionInfo(LocalExecutable(command)).CompanyName ?? "Not reported"; } catch { }
                string id = "startup-" + DebloatJournal.Key($"{hive}|{view}|{name}");
                result.Add(new(id, name, command, publisher, $"{hive} / {view} / Run", target, value, false, blocked));
            }
        }
        foreach (var snapshot in _journal.List().Where(s => s.Id.StartsWith("startup-", StringComparison.Ordinal)))
        {
            var edit = snapshot.Original.Single();
            ValidateStartupTarget(snapshot.Id, edit.Target);
            if (result.Any(r => r.Id == snapshot.Id)) continue;
            result.Add(new(snapshot.Id, edit.Target.Name, edit.Value?.Data.Single() ?? "", "Saved registration; publisher not verified",
                "This account / Run / utility snapshot", edit.Target, null, true, null));
        }
        return result.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    private static void ValidateStartupTarget(string id, DebloatTarget target)
    {
        if (target.Hive != RegistryHive.CurrentUser || target.Path != DebloatWorkspacePolicy.RunPath ||
            target.View != RegistryView.Registry64 ||
            id != "startup-" + DebloatJournal.Key($"{target.Hive}|{target.View}|{target.Name}"))
            throw new InvalidDataException("Invalid startup recovery target.");
    }
    internal void ChangeStartup(StartupAppEntry selected, bool restore)
    {
        ValidateStartupTarget(selected.Id, selected.Target);
        var fresh = ReadStartup().SingleOrDefault(e => e.Id == selected.Id);
        if (fresh is null || fresh.BlockReason is not null || fresh.Command != selected.Command ||
            !DebloatTransaction.Equal(fresh.Registration, selected.Registration))
            throw new InvalidOperationException("Startup registration changed or is unavailable. Reload before changing it.");
        if (restore) _ = LocalExecutable(fresh.Command); // Do not recreate a registration for a removed/unresolvable executable.
        DebloatTransaction.Change(selected.Id, [new(selected.Target, null)], restore, _registry, _journal);
    }

    internal static BackgroundAppEntry[] ReadBackground()
    {
        var result = new List<BackgroundAppEntry>();
        foreach (var p in new PackageManager().FindPackagesForUser("").Where(p => !p.IsFramework && !p.IsResourcePackage))
        {
            string? reason = null;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 };
                using var reader = XmlReader.Create(Path.Combine(p.InstalledLocation.Path, "AppxManifest.xml"), settings);
                var manifest = XDocument.Load(reader);
                if (manifest.Descendants().Any(e => e.Attribute("EntryPoint")?.Value == "Windows.FullTrustApplication" ||
                    e.Attribute("Name")?.Value == "runFullTrust")) reason = "Desktop/full-trust app: use the app's own background settings.";
                else if (!manifest.Descendants().Any(e => e.Attribute("Category")?.Value == "windows.backgroundTasks"))
                    reason = "No supported background-task declaration found.";
            }
            catch { reason = "App capabilities could not be verified."; }
            result.Add(new(p.Id.FamilyName, p.Id.Name, reason));
        }
        return result.DistinctBy(p => p.Family).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    internal string[] BackgroundDeniedFamilies()
    {
        var value = _registry.Read(new(RegistryHive.LocalMachine, RegistryView.Registry64,
            DebloatWorkspacePolicy.BackgroundPath, DebloatWorkspacePolicy.BackgroundList));
        if (value is null) return [];
        if (value.Kind != RegistryValueKind.MultiString) throw new InvalidOperationException("Background deny-list type is unknown; no state inferred.");
        return value.Data;
    }
    internal void ChangeBackground(string[] families, bool restore)
    {
        RequireLocalPolicyOwner();
        var target = new DebloatTarget(RegistryHive.LocalMachine, RegistryView.Registry64,
            DebloatWorkspacePolicy.BackgroundPath, DebloatWorkspacePolicy.BackgroundList);
        if (restore)
        {
            DebloatTransaction.Change("background", [new(target, null)], true, _registry, _journal);
            return;
        }
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var version = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string edition = version?.GetValue("EditionID") as string ?? "";
        if (!PrivacyPolicyCatalog.EditionSupports("Pro", Environment.OSVersion.Version.Build, edition))
            throw new InvalidOperationException("This Windows edition does not support the background-app policy.");
        if (families.Length == 0) throw new InvalidOperationException("Select at least one supported app.");
        var current = ReadBackground();
        foreach (string family in families)
            if (!current.Any(p => p.Family == family && p.BlockReason is null)) throw new InvalidOperationException("App support changed. Reload the inventory.");
        foreach (string name in new[] { "LetAppsRunInBackground", "LetAppsRunInBackground_ForceAllowTheseApps", "LetAppsRunInBackground_UserInControlOfTheseApps" })
            if (_registry.Read(target with { Name = name }) is not null)
                throw new InvalidOperationException("An existing background policy is present. No policy was overridden.");
        if (!HasSnapshot("background") && _registry.Read(target) is not null)
            throw new InvalidOperationException("A background deny list already exists. Review its owner first.");
        DebloatTransaction.Change("background", [new(target, new(RegistryValueKind.MultiString, families.Distinct().Order(StringComparer.Ordinal).ToArray()))], false, _registry, _journal);
    }

    private static string Dism => Path.Combine(Environment.SystemDirectory, "dism.exe");
    internal async Task<ProvisionedAppEntry[]> ReadProvisionedAsync()
    {
        var result = await _commands.RunAsync(Dism, ["/English", "/Online", "/Get-ProvisionedAppxPackages"], TimeSpan.FromMinutes(2));
        if (result.TimedOut || result.ExitCode != 0 || !result.StandardOutput.Contains("The operation completed successfully.", StringComparison.Ordinal))
            throw new InvalidOperationException("Provisioned inventory failed or is incomplete: " + result.CombinedOutput);
        return DebloatWorkspacePolicy.ParseProvisioned(result.StandardOutput);
    }
    internal async Task<string> RemoveProvisionedAsync(ProvisionedAppEntry selected)
    {
        RequireLocalPolicyOwner();
        return await ProvisionedAppsOperations.RemoveAsync(this, selected);
    }
    Task<ProvisionedAppEntry[]> IProvisionedAppsBackend.ReadAsync() => ReadProvisionedAsync();
    async Task<string> IProvisionedAppsBackend.BeginLogAsync(ProvisionedAppEntry selected)
    {
        string directory = Path.Combine(AppDataPaths.LocalRoot, "Logs", "ProvisionedApps");
        Directory.CreateDirectory(directory);
        string log = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(log, "NEW ACCOUNTS ONLY. Not an app-data or package-payload backup.\nSelected: " + selected.PackageName + "\n");
        return log;
    }
    Task<NativeCommandResult> IProvisionedAppsBackend.RemoveAsync(string exactPackageName) => _commands.RunAsync(Dism,
        ["/English", "/Online", "/Remove-ProvisionedAppxPackage", "/PackageName:" + exactPackageName, "/NoRestart"], TimeSpan.FromMinutes(8));
    Task IProvisionedAppsBackend.AppendAsync(string log, string text) => File.AppendAllTextAsync(log, "\n" + text);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
