using System;
using System.Collections.Generic;
using System.Linq;

namespace Naufal_Windows_Tech_s_Powertoys;

internal enum BuiltInAppInstallState { Installed, NeedsRepair, NotInstalled, NotVerified }
internal enum BuiltInAppFilter { All, Installed, NotInstalled, NotVerified }

internal static class BuiltInAppsView
{
    internal static BuiltInAppInstallState Classify(IEnumerable<BuiltInAppPackage> matches)
    {
        var packages = matches.ToArray();
        // Deferred Store checks and failed inventories are not proof of absence.
        if (packages.Any(p => p.InventoryError is not null)) return BuiltInAppInstallState.NotVerified;
        if (packages.Length == 0) return BuiltInAppInstallState.NotInstalled;
        return packages.Any(p => p.Healthy) ? BuiltInAppInstallState.Installed : BuiltInAppInstallState.NeedsRepair;
    }

    internal static bool Includes(BuiltInAppFilter filter, BuiltInAppInstallState state) => filter switch
    {
        BuiltInAppFilter.All => true,
        BuiltInAppFilter.Installed => state is BuiltInAppInstallState.Installed or BuiltInAppInstallState.NeedsRepair,
        BuiltInAppFilter.NotInstalled => state == BuiltInAppInstallState.NotInstalled,
        BuiltInAppFilter.NotVerified => state == BuiltInAppInstallState.NotVerified,
        _ => false
    };

    internal static string Label(BuiltInAppFilter filter) => filter switch
    {
        BuiltInAppFilter.All => "All apps",
        BuiltInAppFilter.Installed => "Installed",
        BuiltInAppFilter.NotInstalled => "Not installed",
        BuiltInAppFilter.NotVerified => "Not verified",
        _ => throw new ArgumentOutOfRangeException(nameof(filter))
    };

    internal static string[] VisibleIds(IEnumerable<BuiltInAppTarget> targets,
        IReadOnlyDictionary<string, BuiltInAppInstallState> states, BuiltInAppFilter filter) => targets
        .Where(t => Includes(filter, states.TryGetValue(t.Id, out var state) ? state : BuiltInAppInstallState.NotVerified))
        .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Id, StringComparer.Ordinal)
        .Select(t => t.Id).ToArray();

    internal static string[] SelectedVisibleIds(IEnumerable<BuiltInAppTarget> targets,
        IReadOnlyDictionary<string, BuiltInAppInstallState> states, BuiltInAppFilter filter, IEnumerable<string> selected)
    {
        var ids = new HashSet<string>(selected, StringComparer.Ordinal);
        return VisibleIds(targets, states, filter).Where(ids.Contains).ToArray();
    }
}
