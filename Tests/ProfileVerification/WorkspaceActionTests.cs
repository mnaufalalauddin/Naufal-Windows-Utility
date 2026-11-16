using Microsoft.Win32;
using Naufal_Windows_Tech_s_Powertoys;

internal static class WorkspaceActionTests
{
    internal static void Run(Action<bool, string> check)
    {
        var previousContext = SynchronizationContext.Current;
        var queuedContext = new QueuedContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(queuedContext);
            string detail = "Initial";
            using var progress = new ScopedProgress<string>(text => detail = text);
            progress.Report("Working"); queuedContext.Drain();
            check(detail == "Working", "active progress reaches UI context");
            progress.Report("Stale progress"); progress.Dispose();
            detail = "Final result"; queuedContext.Drain();
            progress.Report("Late producer"); queuedContext.Drain();
            check(detail == "Final result", "queued and late progress cannot overwrite final result");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        check(new BuiltInAppsResult(0, 1, 0, 0, "absent").TaskState == "UNAVAILABLE", "absent app is not a successful removal");
        check(new BuiltInAppsResult(1, 0, 0, 0, "verified").TaskState == "COMPLETED", "verified removal completes");
        check(new BuiltInAppsResult(1, 0, 1, 0, "partial").TaskState == "FAILED", "partial failures never reported completed");
        const string exe = @"C:\Program Files\Example App\uninstall.exe";
        var plan = RegisteredAppCommand.Parse(exe + " /remove \"two words\"", p => p == exe);
        check(plan.Executable == exe && plan.Arguments.SequenceEqual(new[] { "/remove", "two words" }), "unquoted vendor path resolved without executable search");
        var quoted = RegisteredAppCommand.Parse('"' + exe + "\" /remove \"two words\"", p => p == exe);
        check(quoted.Executable == plan.Executable && quoted.Arguments.SequenceEqual(plan.Arguments), "quoted and unquoted command preserve arguments");
        var start = plan.StartInfo();
        check(start.FileName == exe && start.UseShellExecute && start.WorkingDirectory == Path.GetDirectoryName(exe), "explicit executable and vendor working directory");
        check(start.ArgumentList.SequenceEqual(plan.Arguments) && start.Arguments == "", "no shell concatenation, quiet or restart arguments injected");
        var msi = RegisteredAppCommand.Parse("MsiExec.exe /X{00000000-0000-0000-0000-000000000000}", _ => true);
        check(msi.Executable == Path.Combine(Environment.SystemDirectory, "MsiExec.exe") && msi.Arguments.Length == 1, "MSI resolves only Windows system executable");
        foreach (string invalid in new[] { "", @"\\server\share\remove.exe", @"relative\remove.exe", @"C:\missing.exe", "\"C:\\broken.exe", @"C:\good.exe:payload.exe" })
            Reject(() => RegisteredAppCommand.Parse(invalid, _ => false), "invalid/missing registration rejected: " + invalid);
        Reject(() => RegisteredAppCommand.Parse(@"C:\App.exe folder\remove.exe /x", _ => true), "multiple matching executable boundaries rejected");
        var dll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "Installer2", "InstallerCore", "NVI2.DLL");
        var host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "RunDll32.EXE");
        DesktopAppEntry app = new("LocalMachine|Registry64|{EXAMPLE}_Display.PhysX", "NVIDIA PhysX", "NVIDIA Corporation", "test", "test", "All users",
            $"\"{host}\" \"{dll}\",UninstallPackage Display.PhysX", false);
        var nvidia = RegisteredAppCommand.Uninstall(app, _ => true);
        check(nvidia.NvidiaModule == dll && nvidia.Executable == host && nvidia.Arguments.SequenceEqual(new[] { dll + ",UninstallPackage", "Display.PhysX" }), "NVIDIA registered DLL/export/package preserved; signature gate required");
        Reject(() => RegisteredAppCommand.Uninstall(app with { Identity = "CurrentUser|Registry64|{EXAMPLE}_Display.PhysX" }, _ => true), "user registered NVIDIA DLL host is not trusted");
        Reject(() => RegisteredAppCommand.Uninstall(app with { UninstallCommand = app.UninstallCommand.Replace("UninstallPackage", "OtherExport") }, _ => true), "arbitrary DLL export blocked");
        Reject(() => RegisteredAppCommand.Uninstall(app with { Identity = "LocalMachine|Registry64|{EXAMPLE}_Different.Package" }, _ => true), "NVIDIA selected registration package must match");
        Reject(() => RegisteredAppCommand.Uninstall(app with { UninstallCommand = app.UninstallCommand.Replace(dll, @"C:\Temp\NVI2.DLL") }, _ => true), "DLL outside NVIDIA InstallerCore blocked");
        Reject(() => RegisteredAppCommand.Uninstall(app with { UninstallCommand = app.UninstallCommand.Replace(host, @"C:\Temp\rundll32.exe") }, _ => true), "non-Windows rundll32 blocked");
        Reject(() => RegisteredAppCommand.Uninstall(app with { UninstallCommand = app.UninstallCommand + " extra" }, _ => true), "extra NVIDIA arguments blocked");
        Reject(() => RegisteredAppCommand.Uninstall(app with { SystemComponent = true }, _ => true), "system component guard retained");
        foreach (string shell in new[] { "cmd", "powershell", "pwsh", "wscript", "cscript", "regsvr32", "mshta" })
            Reject(() => RegisteredAppCommand.Uninstall(app with { UninstallCommand = @"C:\Windows\System32\" + shell + ".exe /c anything" }, _ => true), "script host remains blocked: " + shell);

        var ai = WorkspaceAvailability.Ai(true, false, true, true, null, null);
        check(ai.Configured && ai.ApplyLabel == "Already disabled by policy" && ai.ApplyBlock is not null, "configured AI is not offered as fresh disable");
        check(ai.RestoreBlock!.Contains("Windows AI / Paint AI"), "combined snapshot owner identified before click");
        check(WorkspaceAvailability.Ai(false, false, false, false, null, null).ApplyBlock is null, "new supported unmanaged AI remains actionable");
        check(WorkspaceAvailability.Ai(false, true, false, true, null, null).RestoreBlock is null, "owned AI snapshot allows restore");
        check(WorkspaceAvailability.Ai(false, true, false, true, null, "Managed").RestoreBlock == "Managed", "managed-device safety not bypassed");
        check(WorkspaceAvailability.Ai(false, false, false, true, null, null).ApplyBlock is not null, "existing unowned AI policy still protected");
        check(WorkspaceAvailability.Ai(false, false, false, false, "Unsupported", null).ApplyBlock == "Unsupported", "unsupported AI explanation shown before click");
        check(WorkspaceAvailability.StartupApprovalBlock(null) is null, "missing approval allows current-user registration control");
        var approval = new byte[12]; approval[0] = 2;
        check(WorkspaceAvailability.StartupApprovalBlock(approval) is null, "enabled approval allows startup disable");
        foreach (byte value in new byte[] { 3, 7 }) { approval[0] = value; check(WorkspaceAvailability.StartupApprovalBlock(approval)!.StartsWith("Already disabled"), "Windows-disabled startup explained: " + value); }
        foreach (object unknown in new object[] { new byte[1], new byte[12], "bad", 2 })
            check(WorkspaceAvailability.StartupApprovalBlock(unknown) is not null, "unknown approval never treated as enabled");
        void Reject(Action action, string name)
        {
            bool rejected = false; try { action(); } catch { rejected = true; }
            check(rejected, name);
        }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<Action> _pending = new();
        public override void Post(SendOrPostCallback callback, object? state) => _pending.Enqueue(() => callback(state));
        internal void Drain() { while (_pending.TryDequeue(out var action)) action(); }
    }

    internal static void ReadOnlyProbe()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        int found = 0;
        foreach (string id in uninstall!.GetSubKeyNames())
        {
            using var key = uninstall.OpenSubKey(id);
            string name = key?.GetValue("DisplayName") as string ?? "";
            if (!name.Contains("PhysX", StringComparison.OrdinalIgnoreCase) && name != "Steam" && !name.Contains("Display Driver Uninstaller", StringComparison.OrdinalIgnoreCase)) continue;
            var entry = new DesktopAppEntry("LocalMachine|Registry64|" + id, name, "", "", "", "All users", key?.GetValue("UninstallString") as string ?? "", false);
            var plan = RegisteredAppCommand.Uninstall(entry);
            Console.WriteLine($"READ-ONLY {name}: command resolved; explicit EXE={Path.GetFileName(plan.Executable)}; args={plan.Arguments.Length}; NVIDIA signature gate={plan.NvidiaModule is not null}.");
            found++;
        }
        if (found == 0) throw new InvalidOperationException("No scoped registrations found; no evidence collected.");
        Console.WriteLine("No process was launched, no application removed and no registry written. This is command-resolution evidence only.");
    }
}
