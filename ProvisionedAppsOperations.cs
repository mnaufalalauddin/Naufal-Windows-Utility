using System;
using System.Linq;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys;

internal interface IProvisionedAppsBackend
{
    Task<ProvisionedAppEntry[]> ReadAsync();
    Task<string> BeginLogAsync(ProvisionedAppEntry selected);
    Task<NativeCommandResult> RemoveAsync(string exactPackageName);
    Task AppendAsync(string log, string text);
}

internal static class ProvisionedAppsOperations
{
    internal static async Task<string> RemoveAsync(IProvisionedAppsBackend backend, ProvisionedAppEntry selected)
    {
        var before = await backend.ReadAsync();
        // Always re-evaluate the catalog identity, not just a caller's TargetId.
        var expected = DebloatWorkspacePolicy.ParseProvisioned("DisplayName : " + selected.DisplayName +
            "\nPackageName : " + selected.PackageName).SingleOrDefault();
        if (expected?.TargetId is null || expected != selected || before.Count(p => p == selected) != 1)
            throw new InvalidOperationException("Package is unavailable or is not an approved deprovisioning target.");
        string log = await backend.BeginLogAsync(selected);
        var result = await backend.RemoveAsync(selected.PackageName);
        await backend.AppendAsync(log, result.CombinedOutput + "\nExit: " + result.ExitCode + "; timeout: " + result.TimedOut);
        if (result.TimedOut || result.ExitCode is not (0 or 3010))
            throw new InvalidOperationException("Deprovisioning did not complete; do not rerun blindly. Review: " + log);
        var after = await backend.ReadAsync();
        if (after.Any(p => p.PackageName == selected.PackageName))
            throw new InvalidOperationException("Package remains provisioned. Review: " + log);
        string message = "Package no longer provisioned for new accounts. Existing user installations were not removed. Recovery requires original licensed packages/dependencies or Windows recovery media. " +
            (result.ExitCode == 3010 ? "Windows requests a restart; no restart was performed. " : "") + "Log: " + log;
        await backend.AppendAsync(log, message);
        return message;
    }
}
