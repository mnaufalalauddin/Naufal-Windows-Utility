using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed record BuiltInAppUpdate(string Id, string State, string Detail, double? Percent = null);
internal sealed record BuiltInAppsResult(int Succeeded, int Unavailable, int Failed, int NotStarted, string Report)
{
    internal bool Success => Failed == 0 && NotStarted == 0;
}

internal interface IBuiltInAppsBackend
{
    Task<IReadOnlyList<BuiltInAppPackage>> ReadAsync();
    Task<IReadOnlyList<BuiltInAppPackage>> ReadForTargetsAsync(IReadOnlyList<BuiltInAppTarget> targets) => ReadAsync();
    Task RemoveAsync(BuiltInAppPackage package, IProgress<CatalogProgressUpdate>? progress);
    Task RestoreAppAsync(BuiltInAppTarget target, IProgress<CatalogProgressUpdate>? progress);
    Task SavePlanAsync(bool restore, IReadOnlyList<BuiltInAppTarget> targets, IReadOnlyList<BuiltInAppPackage> packages);
    Task AppendResultAsync(string text);
    Task RecordVerifiedAsync(string id, bool restored) => Task.CompletedTask;
}

// Pure orchestration, injected backend for tests. Every mutation is a member of
// an exact approved family and has a successful preflight inventory/audit record.
internal static class BuiltInAppsRemoval
{
    internal static async Task<BuiltInAppsResult> RunAsync(IBuiltInAppsBackend backend,
        IEnumerable<string> selectedIds, IProgress<BuiltInAppUpdate>? progress, bool restore = false)
    {
        var targets = BuiltInAppsCatalog.ResolveSelection(selectedIds);
        var before = await backend.ReadForTargetsAsync(targets); // failure aborts BEFORE any removal
        var plan = targets.ToDictionary(t => t.Id, t => before.Where(p => BuiltInAppsCatalog.Matches(t, p))
            .DistinctBy(p => p.FullName, StringComparer.OrdinalIgnoreCase).ToArray());
        await backend.SavePlanAsync(restore, targets, plan.Values.SelectMany(p => p).ToArray());
        int removed = 0, unavailable = 0, failed = 0, notStarted = 0;
        bool stop = false;
        List<string> report = new();
        foreach (var target in targets)
        {
            if (stop)
            {
                notStarted++;
                report.Add(target.Name + ": Not started after an unconfirmed deployment or audit failure.");
                continue; // UI finishes untouched WAITING entries as SKIPPED, not UNAVAILABLE.
            }
            string state, detail;
            if (!restore && plan[target.Id].Length == 0)
            {
                unavailable++;
                state = "UNAVAILABLE";
                detail = target.Name + ": Not installed for this Windows account.";
            }
            else
            {
                bool historyFailure = false;
                progress?.Report(new(target.Id, "RUNNING", (restore ? "Restoring: " : "Removing: ") + target.Name));
                try
                {
                    if (plan[target.Id].FirstOrDefault(p => p.InventoryError is not null) is { } unknown)
                        throw new InvalidOperationException(unknown.InventoryError);
                    var reporter = new ForwardProgress(update =>
                        progress?.Report(new(target.Id, "PROGRESS", update.Detail, update.Percent)));
                    if (restore) await backend.RestoreAppAsync(target, reporter);
                    else foreach (var package in plan[target.Id]) await backend.RemoveAsync(package, reporter);
                    progress?.Report(new(target.Id, "VERIFYING", "Verifying: " + target.Name));
                    var after = await backend.ReadForTargetsAsync([target]);
                    if (after.FirstOrDefault(p => BuiltInAppsCatalog.Matches(target, p) && p.InventoryError is not null) is { } uncertain)
                        throw new InvalidOperationException(uncertain.InventoryError);
                    bool present = after.Any(p => BuiltInAppsCatalog.Matches(target, p) && (!restore || p.Healthy));
                    if (present != restore)
                        throw new InvalidOperationException(target.Kind == BuiltInAppKind.OneDriveDesktop
                            ? "OneDrive installation state was not verified. Use the Microsoft website button or Windows Installed apps, then analyze again."
                            : restore
                            ? "A healthy app package could not be verified. Use this app's Microsoft Store button to complete recovery, then analyze again."
                            : "Package is still registered for this Windows account.");
                    try { await backend.RecordVerifiedAsync(target.Id, restore); }
                    catch (Exception exception)
                    {
                        historyFailure = true;
                        throw new InvalidOperationException("App state was verified, but its removal/restore history could not be saved. No further apps will be changed: " + exception.Message, exception);
                    }
                    removed++;
                    state = "COMPLETED";
                    detail = target.Kind == BuiltInAppKind.OneDriveDesktop
                        ? target.Name + (restore ? ": Installation verified. Sign in and choose sync folders again. Personal files were not restored."
                            : ": Uninstall verified. Syncing stopped; no OneDrive file cleanup was performed by this tool.")
                        : target.Name + (restore ? ": Installed and verified for this Windows account. Personal data was not restored."
                        : ": Uninstalled and verified for this Windows account.");
                }
                catch (Exception exception)
                {
                    failed++;
                    state = "FAILED";
                    detail = $"{target.Name}: {exception.Message} (0x{exception.HResult:X8})";
                    // A timeout is NOT proof that Windows stopped a deployment.
                    stop = historyFailure || exception is TimeoutException or PendingDeploymentException;
                }
            }
            report.Add(detail);
            progress?.Report(new(target.Id, state, detail));
            try { await backend.AppendResultAsync(state + ": " + detail); }
            catch (Exception exception)
            {
                // Preserve actual item result; disclose audit failure separately.
                failed++;
                stop = true;
                report.Add("Audit log could not be updated; no further apps will be changed: " + exception.Message);
            }
        }
        string summary = $"{(restore ? "Restored" : "Removed")}={removed}; Unavailable={unavailable}; Failures={failed}; Not started={notStarted}.";
        return new(removed, unavailable, failed, notStarted, summary + Environment.NewLine + string.Join(Environment.NewLine, report));
    }

    private sealed class ForwardProgress(Action<CatalogProgressUpdate> action) : IProgress<CatalogProgressUpdate>
    {
        public void Report(CatalogProgressUpdate value) => action(value);
    }
}
