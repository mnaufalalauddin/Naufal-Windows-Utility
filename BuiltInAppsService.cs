using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Management.Deployment;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed class BuiltInAppsService : IToolActionService, IBuiltInAppsBackend
{
    private static readonly BoundedReadProbe<IReadOnlyList<BuiltInAppPackage>> InventoryProbe = new();
    private string? _auditPath;
    private readonly OneDriveAppService _oneDrive = new();
    internal string? AuditPath => _auditPath;

    public Task RecordVerifiedAsync(string id, bool restored)
    {
        DebloatRemovalHistory.Record(id, restored);
        return Task.CompletedTask;
    }

    public IReadOnlyList<ToolActionDefinition> GetActions() => [new(
        "BuiltInWindowsApps", "Apps / HIGH IMPACT", BuiltInAppsCatalog.Title,
        BuiltInAppsCatalog.Description, "Review apps", false, false,
        Risk: ToolActionRisk.Danger, Warning: BuiltInAppsCatalog.Warning)];

    // Only the review window may provide the explicitly confirmed app IDs.
    public Task<ToolActionResult> RunAsync(ToolActionDefinition definition) =>
        Task.FromResult(new ToolActionResult(false, "Review and confirm the selected apps first."));
    public Task<ToolActionResult> RestoreAsync(ToolActionDefinition definition) =>
        Task.FromResult(new ToolActionResult(false, "Review and confirm the selected apps first."));

    public Task<IReadOnlyList<BuiltInAppPackage>> ReadAsync() => InventoryProbe.ReadAsync(() =>
    {
        PackageManager manager = new();
        // Empty SID explicitly means current process user. Never enumerate all
        // users or deprovision the Windows image, even when running elevated.
        var packages = manager.FindPackagesForUser(string.Empty)
            .Select(p => new BuiltInAppPackage(p.Id.Name, p.Id.FamilyName, p.Id.FullName, p.IsFramework, p.IsResourcePackage, p.Status.VerifyIsOK()))
            .Where(p => BuiltInAppsCatalog.Targets.Any(t => BuiltInAppsCatalog.Matches(t, p)))
            .Concat(OneDriveAppService.ReadInstalled()).ToArray();
        // Defer the network-backed Store-product check until this target is selected.
        return (IReadOnlyList<BuiltInAppPackage>)packages.Append(CopilotStorePolicy.Package(CopilotStorePolicy.Deferred)).ToArray();
    }, TimeSpan.FromSeconds(30));

    public async Task<IReadOnlyList<BuiltInAppPackage>> ReadForTargetsAsync(IReadOnlyList<BuiltInAppTarget> targets)
    {
        var packages = await ReadAsync();
        if (!targets.Any(t => t.Id == "Copilot")) return packages;
        var store = await CopilotStoreService.ProbeAsync();
        return packages.Where(p => p.Kind != BuiltInAppKind.CopilotStore)
            .Concat(store is null ? Array.Empty<BuiltInAppPackage>() : new[] { store }).ToArray();
    }

    public async Task RemoveAsync(BuiltInAppPackage package, IProgress<CatalogProgressUpdate>? progress)
    {
        var target = BuiltInAppsCatalog.Targets.SingleOrDefault(t => BuiltInAppsCatalog.Matches(t, package))
            ?? throw new InvalidOperationException("Package is not an approved removal target.");
        var current = await ReadForTargetsAsync([target]);
        if (current.Any(p => BuiltInAppsCatalog.Matches(target, p) && p.InventoryError is not null))
            throw new InvalidOperationException("The selected app's installation could not be verified. No removal was started.");
        if (!current.Any(p => p.FullName == package.FullName && BuiltInAppsCatalog.Matches(target, p)))
            return; // changed/removed since preview; caller still verifies entire family
        if (target.Kind == BuiltInAppKind.OneDriveDesktop)
        {
            await _oneDrive.ChangeAsync(false, progress, AppendResultAsync);
            return;
        }
        if (package.Kind == BuiltInAppKind.CopilotStore)
        {
            await CopilotStoreService.RemoveAsync(progress, AppendResultAsync);
            return;
        }
        PackageManager manager = new();
        using var reporting = CatalogOperationProgress.Begin(progress);
        DeploymentResult result = await DeploymentOperationTimeout.AwaitAsync(
            () => manager.RemovePackageAsync(package.FullName, RemovalOptions.None),
            "Removing: " + target.Name);
        if (result.ExtendedErrorCode is Exception error && error.HResult < 0)
            throw new InvalidOperationException($"Windows refused the uninstall: {result.ErrorText} (0x{error.HResult:X8}). No protection bypass was attempted.", error);
    }

    public async Task RestoreAppAsync(BuiltInAppTarget requested, IProgress<CatalogProgressUpdate>? progress)
    {
        var target = BuiltInAppsCatalog.ResolveSelection([requested.Id]).Single();
        if (target.Kind == BuiltInAppKind.OneDriveDesktop)
        {
            await _oneDrive.ChangeAsync(true, progress, AppendResultAsync);
            return;
        }
        if ((await ReadAsync()).Any(p => BuiltInAppsCatalog.Matches(target, p) && p.Healthy)) return;
        using var reporting = CatalogOperationProgress.Begin(progress);
        List<string> failures = new();
        // Re-register only the approved family from Windows' own staged payload.
        // No guessed paths, unsigned downloads, data-directory deletion or ACL changes.
        // Read staged identities only; register the selected app for THIS user.
        // Other users' registrations and the provisioned Windows image are not modified.
        var staged = new PackageManager().FindPackages()
            .Select(p => new BuiltInAppPackage(p.Id.Name, p.Id.FamilyName, p.Id.FullName, p.IsFramework, p.IsResourcePackage))
            .Where(p => BuiltInAppsCatalog.Matches(target, p)).Select(p => p.Family);
        foreach (string family in target.Families.Concat(staged).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                PackageManager manager = new();
                var result = await DeploymentOperationTimeout.AwaitAsync(
                    () => manager.RegisterPackageByFamilyNameAsync(family, null, DeploymentOptions.None, null, null),
                    "Restoring: " + target.Name);
                if (result.ExtendedErrorCode is Exception error && error.HResult < 0)
                    throw new InvalidOperationException(result.ErrorText, error);
            }
            catch (Exception exception) when (exception is not TimeoutException and not PendingDeploymentException)
            { failures.Add($"Local registration: {exception.Message} (0x{exception.HResult:X8})"); }
            // Read errors must abort; never interpret them as an absent package.
            if ((await ReadAsync()).Any(p => BuiltInAppsCatalog.Matches(target, p) && p.Healthy)) return;
        }
        if (BuiltInAppsCatalog.StoreProductId(target.Id) is null)
            throw new InvalidOperationException("Local package recovery was unsuccessful. Open this app's Microsoft Store button to check availability, compatibility and licensing. " + string.Join(" ", failures));

        // Resolve the current user's official App Installer alias, not PATH/CWD.
        string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (!File.Exists(winget))
            throw new InvalidOperationException("WinGet is unavailable for this account. Open this app's Microsoft Store button to reinstall it.");
        using var userRunner = new SameUserProcessRunner();
        var source = await userRunner.RunAsync(winget,
            ["source", "export", "--name", "msstore", "--disable-interactivity"], TimeSpan.FromSeconds(30));
        if (source.ExitCode != 0 || source.TimedOut || !BuiltInAppsCatalog.IsMicrosoftStoreSource(source.StandardOutput))
            throw new InvalidOperationException("The official Microsoft Store source could not be verified. No download was started. Use the app's Microsoft Store button; sources were not reset or changed.");
        progress?.Report(new("Installing: " + target.Name + " (Microsoft Store)", null));
        var install = await DeploymentOperationTimeout.AwaitExternalAsync(() => userRunner.RunAsync(
            winget, BuiltInAppsCatalog.InstallArguments(target.Id), Timeout.InfiniteTimeSpan), "Installing: " + target.Name);
        await AppendResultAsync("Microsoft Store " + target.Name + ": exit " + install.ExitCode + Environment.NewLine + install.CombinedOutput);
        if (install.ExitCode != 0 || install.TimedOut)
            throw new InvalidOperationException($"Microsoft Store installation did not complete (exit {install.ExitCode}). Use the app's Microsoft Store button to check availability, compatibility and licensing. {install.CombinedOutput}");
        // Success still requires the orchestration's fresh exact-family/health readback.
    }

    public async Task SavePlanAsync(bool restore, IReadOnlyList<BuiltInAppTarget> targets, IReadOnlyList<BuiltInAppPackage> packages)
    {
        string directory = Path.Combine(AppDataPaths.LocalRoot, "Logs", "BuiltInApps");
        Directory.CreateDirectory(directory);
        _auditPath = Path.Combine(directory, (restore ? "restore-" : "uninstall-") + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".txt");
        string text = "BUILT-IN WINDOWS APPS — STORE: CURRENT USER; ONEDRIVE: CONFIRMED INSTALLATION SCOPE" + Environment.NewLine +
            "Started UTC: " + DateTime.UtcNow.ToString("O") + Environment.NewLine +
            "Account: " + Environment.UserDomainName + "\\" + Environment.UserName + Environment.NewLine +
            "This is an inventory/operation log, NOT an app-data backup." + Environment.NewLine +
            "Selected: " + string.Join(", ", targets.Select(t => t.Name)) + Environment.NewLine +
            string.Join(Environment.NewLine, packages.Select(p => p.Family + " | " + p.FullName)) + Environment.NewLine;
        await File.WriteAllTextAsync(_auditPath, text, new UTF8Encoding(false));
        if (targets.Any(t => t.Kind == BuiltInAppKind.OneDriveDesktop))
            await _oneDrive.PrepareAsync(restore, packages);
    }

    public Task AppendResultAsync(string text) => File.AppendAllTextAsync(
        _auditPath ?? throw new InvalidOperationException("Audit plan must be saved before removal."),
        text + Environment.NewLine, new UTF8Encoding(false));
}
