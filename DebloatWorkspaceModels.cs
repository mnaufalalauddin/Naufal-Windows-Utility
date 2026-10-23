using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed record DebloatValue(RegistryValueKind Kind, string[] Data)
{
    internal static DebloatValue Dword(int value) => new(RegistryValueKind.DWord, [value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
}
internal sealed record DebloatTarget(RegistryHive Hive, RegistryView View, string Path, string Name);
internal sealed record DebloatEdit(DebloatTarget Target, DebloatValue? Value);
internal sealed record DebloatSnapshot(string Id, DateTimeOffset Captured, DebloatEdit[] Original, DebloatEdit[] Applied);
internal sealed record DebloatRemovalEvent(string AppId, bool Restored, DateTimeOffset At);
internal sealed record DesktopAppEntry(string Identity, string Name, string Publisher, string Version, string Location, string Scope,
    string UninstallCommand, bool SystemComponent);
internal sealed record StartupAppEntry(string Id, string Name, string Command, string Publisher, string Source,
    DebloatTarget Target, DebloatValue? Registration, bool DisabledByUtility, string? BlockReason);
internal sealed record BackgroundAppEntry(string Family, string Name, string? BlockReason);
internal sealed record ProvisionedAppEntry(string PackageName, string DisplayName, string? TargetId);
internal sealed record AiControl(string Id, string Name, string Warning, DebloatEdit[] Edits);

internal interface IDebloatRegistry
{
    DebloatValue? Read(DebloatTarget target);
    void Write(DebloatEdit edit);
}
internal interface IDebloatJournal
{
    DebloatSnapshot? Load(string id);
    void Save(DebloatSnapshot snapshot);
    void Delete(string id);
}

// The first baseline survives repeated Apply. All targets and all drift checks
// are validated before any mutation; partial failures retain the recovery record.
internal static class DebloatTransaction
{
    internal static bool Equal(DebloatValue? a, DebloatValue? b) => a is null ? b is null :
        b is not null && a.Kind == b.Kind && a.Data.SequenceEqual(b.Data, StringComparer.Ordinal);

    internal static void Change(string id, DebloatEdit[] desired, bool restore,
        IDebloatRegistry registry, IDebloatJournal journal)
    {
        if (desired.Length == 0 || desired.Select(e => e.Target).Distinct().Count() != desired.Length)
            throw new InvalidOperationException("The change plan is empty or contains duplicate targets.");
        var saved = journal.Load(id);
        var current = desired.Select(e => new DebloatEdit(e.Target, registry.Read(e.Target))).ToArray();
        if (saved is not null)
        {
            if (saved.Id != id || !saved.Original.Select(e => e.Target).SequenceEqual(desired.Select(e => e.Target)) ||
                !saved.Applied.Select(e => e.Target).SequenceEqual(desired.Select(e => e.Target)))
                throw new InvalidOperationException("Recovery record does not match this operation. No changes made.");
            for (int i = 0; i < current.Length; i++)
                if (!Equal(current[i].Value, saved.Applied[i].Value) && !Equal(current[i].Value, saved.Original[i].Value))
                    throw new InvalidOperationException("Settings changed outside this operation. Review before restoring; recovery record retained.");
        }
        if (restore && saved is null) throw new InvalidOperationException("No original snapshot exists. Defaults were not substituted.");
        if (!restore && saved is not null && !desired.Zip(saved.Applied).All(p => Equal(p.First.Value, p.Second.Value)))
            throw new InvalidOperationException("Restore the previous selection first before applying a different selection.");
        DebloatEdit[] plan = restore ? saved!.Original : desired;
        if (!restore)
            journal.Save(new(id, saved?.Captured ?? DateTimeOffset.UtcNow, saved?.Original ?? current, plan));
        foreach (var edit in plan) registry.Write(edit);
        if (plan.Any(e => !Equal(registry.Read(e.Target), e.Value)))
            throw new InvalidOperationException("Configuration read-back failed. Recovery record retained.");
        if (restore) journal.Delete(id);
    }
}

internal static class DebloatWorkspacePolicy
{
    internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string BackgroundPath = @"Software\Policies\Microsoft\Windows\AppPrivacy";
    internal const string BackgroundList = "LetAppsRunInBackground_ForceDenyTheseApps";
    internal const string AiWarning = "Policy configuration only; not proof of runtime behavior. Restart the affected app. Organization policies may override local changes.";
    internal static DebloatEdit Policy(string path, string name, int value) => new(
        new(RegistryHive.LocalMachine, RegistryView.Registry64, path, name), DebloatValue.Dword(value));
    internal static readonly AiControl[] AiControls =
    [
        new("Recall", "Recall snapshots", "Stops future AI snapshot analysis; disabling Recall can delete saved snapshots. " + AiWarning,
            [Policy(@"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1)]),
        new("ClickToDo", "Click to Do", AiWarning,
            [Policy(@"Software\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", 1)]),
        new("Notepad", "Notepad AI", AiWarning,
            [Policy(@"Software\Policies\WindowsNotepad", "DisableAIFeatures", 1)]),
        new("Paint", "Paint AI", AiWarning,
            [Policy(@"Software\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableCocreator", 1),
             Policy(@"Software\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableGenerativeFill", 1),
             Policy(@"Software\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableImageCreator", 1)])
    ];
    internal static bool SupportsAi(string id, int build, int revision, string edition) =>
        (edition.StartsWith("Professional", StringComparison.OrdinalIgnoreCase) ||
         edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) || edition.Contains("Education", StringComparison.OrdinalIgnoreCase)) &&
        (id is "Recall" or "ClickToDo" ? build > 26100 || build == 26100 && revision >= 3915 :
         id == "Paint" ? build > 26100 || build == 26100 && revision >= 3360 || (build is 22621 or 22631) && revision >= 4870 :
         id == "Notepad" && build >= 22621);

    internal static string[] Reappeared(IEnumerable<DebloatRemovalEvent> events, IEnumerable<string> installed) =>
        events.GroupBy(e => e.AppId, StringComparer.Ordinal).Where(g => !g.Last().Restored)
            .Select(g => g.Key).Intersect(installed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    internal static ProvisionedAppEntry[] ParseProvisioned(string output)
    {
        var results = new List<ProvisionedAppEntry>();
        string? display = null;
        foreach (string line in output.Replace("\r", "").Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string field = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
            if (field == "DisplayName") display = value;
            if (field != "PackageName") continue;
            string[] parts = value.Split('_');
            if (display is null || parts.Length != 5 || parts[0] != display || !Version.TryParse(parts[1], out _))
                throw new InvalidOperationException("Unexpected provisioned-package inventory. No removal allowed.");
            string family = parts[0] + "_" + parts[4];
            var target = BuiltInAppsCatalog.Targets.FirstOrDefault(t => t.Kind == BuiltInAppKind.Appx &&
                t.Recommendation != AppRemovalRecommendation.NotRecommended &&
                t.Families.Contains(family, StringComparer.OrdinalIgnoreCase));
            results.Add(new(value, display, target?.Id));
            display = null;
        }
        if (results.Select(p => p.PackageName).Distinct().Count() != results.Count)
            throw new InvalidOperationException("Duplicate provisioned-package identities.");
        if (display is not null || results.Count == 0 && !output.Contains("The operation completed successfully.", StringComparison.Ordinal))
            throw new InvalidOperationException("Provisioned inventory is incomplete or unreadable.");
        return results.ToArray();
    }
}

[JsonSerializable(typeof(DebloatSnapshot))]
[JsonSerializable(typeof(DebloatRemovalEvent[]))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class DebloatWorkspaceJson : JsonSerializerContext { }
