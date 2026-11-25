using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys;

// Selection chooses the operation's scope, never its ON/OFF direction.
// Apply always targets the applied state; Restore replays the saved snapshot.
internal sealed class CatalogSelectionPlan
{
    public IReadOnlyList<ToolToggleDefinition> Selected { get; }
    public IReadOnlyList<ToolToggleDefinition> ToApply { get; }
    public IReadOnlyList<ToolToggleDefinition> ToRestore { get; }
    public int AlreadyAppliedCount => Selected.Count - ToApply.Count;
    public int AlreadyRestoredCount => Selected.Count - ToRestore.Count;

    private CatalogSelectionPlan(
        List<ToolToggleDefinition> selected,
        List<ToolToggleDefinition> toApply,
        List<ToolToggleDefinition> toRestore)
    {
        Selected = selected.AsReadOnly();
        ToApply = toApply.AsReadOnly();
        ToRestore = toRestore.AsReadOnly();
    }

    public static CatalogSelectionPlan Create(
        IReadOnlyList<ToolToggleDefinition> definitions,
        IReadOnlyDictionary<string, ToolToggleState> states,
        Func<ToolToggleDefinition, bool> isSelected)
    {
        List<ToolToggleDefinition> selected = new();
        List<ToolToggleDefinition> toApply = new();
        List<ToolToggleDefinition> toRestore = new();
        Dictionary<string, ToolToggleDefinition> unique = new(StringComparer.OrdinalIgnoreCase);
        foreach (ToolToggleDefinition definition in definitions)
        {
            if (!isSelected(definition)) continue;
            if (unique.TryGetValue(definition.Id, out var previous))
            {
                if (previous != definition)
                    throw new InvalidOperationException($"Conflicting definitions for action '{definition.Id}'. No actions were executed.");
                continue;
            }
            unique.Add(definition.Id, definition);
            if (
                !states.TryGetValue(definition.Id, out ToolToggleState state) ||
                !state.IsAvailable)
            {
                continue;
            }

            selected.Add(definition);
            if (!state.IsOn)
            {
                toApply.Add(definition);
            }
            if (state.IsOn || state.HasAppliedParts || state.HasSavedOriginal || definition.IsFeatureSwitch)
            {
                toRestore.Add(definition);
            }
        }
        return new CatalogSelectionPlan(selected, toApply, toRestore);
    }
}

internal enum CatalogOperation
{
    Apply,
    RestoreSavedState,
    RestoreWindowsDefaults,
    SetOff
}

internal static class CatalogOperationRunner
{
    public static Task<ToolToggleOperationResult> ExecuteAsync(
        IToolToggleService service,
        ToolToggleDefinition definition,
        CatalogOperation operation,
        IProgress<CatalogProgressUpdate>? progress = null)
        => service is ICatalogPlanSource
            ? ExecuteCoreAsync(service, definition, operation, progress)
            : CatalogExecutionBatch.ExecuteAsync(service, definition, operation, () => ExecuteCoreAsync(service, definition, operation, progress));

    private static async Task<ToolToggleOperationResult> ExecuteCoreAsync(
        IToolToggleService service, ToolToggleDefinition definition, CatalogOperation operation,
        IProgress<CatalogProgressUpdate>? progress)
    {
        using var progressScope = CatalogOperationProgress.Begin(progress);
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        // Re-check after queue admission: hardware/packages may have disappeared
        // since Analyze. Never turn a failed WRITE into a neutral absence result.
        ToolToggleState before = await CatalogStateReader.ReadAsync(service, definition, TimeSpan.FromSeconds(30));
        // Task-returning backends can still perform registry, ACL, snapshot or
        // hardware work synchronously before their first await. Keep all of it
        // off the XAML dispatcher, including exact/default fallback Restore.
        ToolToggleOperationResult result = await Task.Run(() =>
            ExecuteWithStateAsync(service, definition, operation, before));
        return result with { BeforeState = before };
    }

    private static async Task<ToolToggleOperationResult> ExecuteWithStateAsync(
        IToolToggleService service, ToolToggleDefinition definition, CatalogOperation operation, ToolToggleState before)
    {
        if (before.IsConfirmedUnavailable)
            return new(false, false, before.Error, before, SkippedUnavailable: true);
        if (before.HasReadFailure)
            return new(false, false, before.Error, before);
        if (operation == CatalogOperation.Apply && definition.Id == "FastStartupEnable")
        {
            var hibernation = service.GetDefinitions().FirstOrDefault(d => d.Id == "Hibernation");
            if (!string.IsNullOrEmpty(hibernation.Id))
            {
                ToolToggleState dependency = await CatalogStateReader.ReadAsync(service, hibernation, TimeSpan.FromSeconds(30));
                if (!dependency.IsAvailable || dependency.IsOn)
                    return new(false, false, "Fast Startup requires hibernation. Hibernation is disabled or could not be verified; restore/enable hibernation and analyze again. No Fast Startup writes performed.", before);
            }
        }
        // Admission may happen after another task applied the same setting.
        // Do not overwrite its original snapshot or repeat its system writes.
        if (operation == CatalogOperation.Apply && before.IsOn)
            return new(true, true,
                "Already applied — current configuration verified; no writes performed. This does not verify post-reboot effectiveness.",
                before, AlreadyApplied: true);
        if (service is ICatalogPlanSource)
        {
            // Composite wrappers are an ordered set of canonical references,
            // not a second executor with its own independent child snapshots.
            var groups = CatalogEffectPlan.Expand(service, definition)
                .GroupBy(action => action.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            if (groups.Any(group => group.Any(action => action.Definition != group.First().Definition)))
                return new(false, false, "Incompatible canonical definitions; no child actions executed.", before);
            var leaves = groups.Select(group => group.First()).ToArray();
            if (leaves.Length == 0) return new(false, false, "No canonical actions are registered for this option.", before);
            var results = new List<ToolToggleOperationResult>();
            foreach (var leaf in leaves)
                results.Add(await ExecuteAsync(leaf.Service, leaf.Definition, operation, CatalogOperationProgress.Current));
            ToolToggleState after = await CatalogStateReader.ReadAsync(service, definition, TimeSpan.FromSeconds(30));
            var applicable = results.Where(r => !(r.SkippedUnavailable && r.State.IsConfirmedUnavailable)).ToArray();
            bool verified = applicable.Length > 0 && applicable.All(r => r.Success && r.Verified) && after.IsAvailable;
            if (operation == CatalogOperation.Apply) verified &= after.IsOn;
            if (operation == CatalogOperation.SetOff) verified &= !after.IsOn;
            string detail = string.Join(Environment.NewLine, results.Select(r => r.Message));
            return new(verified, verified,
                $"{definition.Name}: {leaves.Length} canonical action reference(s); " +
                (verified ? "configuration verified." : "one or more results remain unverified.") + Environment.NewLine + detail,
                after, DefaultFallbackHandled: operation is CatalogOperation.RestoreSavedState or CatalogOperation.RestoreWindowsDefaults,
                SkippedUnavailable: applicable.Length == 0 && after.IsConfirmedUnavailable);
        }
        if (operation == CatalogOperation.RestoreSavedState)
        {
            ToolToggleOperationResult exact = await service.RestoreOriginalAsync(definition);
            // Only the backend can establish actual backup absence. An English
            // error containing "snapshot unavailable" may mean denial/corruption.
            if (exact.Success || exact.DefaultFallbackHandled || !exact.OriginalBackupMissing)
            {
                return exact;
            }

            ToolToggleOperationResult fallback =
                await service.RestoreWindowsDefaultAsync(definition);
            string explanation = fallback.Success
                ? "No saved pre-change state was available, so the documented Windows default was restored."
                : "No saved pre-change state was available, and the documented Windows-default fallback also failed.";
            return fallback with
            {
                Message = explanation + " " + fallback.Message
            };
        }

        return operation switch
        {
            CatalogOperation.Apply =>
                await service.SetStateAsync(definition, targetOn: true),
            CatalogOperation.SetOff =>
                await service.SetStateAsync(definition, targetOn: false),
            CatalogOperation.RestoreWindowsDefaults =>
                await service.RestoreWindowsDefaultAsync(definition),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

}
