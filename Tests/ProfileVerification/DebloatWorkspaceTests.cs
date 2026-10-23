using Microsoft.Win32;
using Naufal_Windows_Tech_s_Powertoys;
using System.Text.Json;

internal static class DebloatWorkspaceTests
{
    internal static void Run(Action<bool, string> check)
    {
        var a = new DebloatTarget(RegistryHive.CurrentUser, RegistryView.Registry64, "TEST ONLY", "A");
        var b = a with { Name = "B" };
        DebloatEdit[] desired = [new(a, DebloatValue.Dword(1)), new(b, DebloatValue.Dword(1))];
        var registry = new FakeRegistry();
        var journal = new FakeJournal();
        registry.Values[a] = DebloatValue.Dword(7);
        DebloatTransaction.Change("test", desired, false, registry, journal);
        check(registry.Writes == 2 && journal.Snapshot!.Original[0].Value!.Data.Single() == "7", "workspace captures baseline before writing");
        check(journal.Snapshot!.Original[1].Value is null, "workspace captures absent value distinctly");
        DebloatTransaction.Change("test", desired, false, registry, journal);
        check(journal.Snapshot!.Original[0].Value!.Data.Single() == "7", "repeated apply preserves first baseline");
        DebloatTransaction.Change("test", desired, true, registry, journal);
        check(registry.Values[a]!.Data.Single() == "7" && registry.Values[b] is null && journal.Snapshot is null, "restore verifies present and absent baseline and clears record");
        Throws(() => DebloatTransaction.Change("test", desired, true, registry, journal), "restore without snapshot never applies guessed defaults");
        DebloatTransaction.Change("test", desired, false, registry, journal);
        int count = registry.Writes;
        registry.Values[b] = DebloatValue.Dword(9);
        Throws(() => DebloatTransaction.Change("test", desired, true, registry, journal), "external drift blocks entire restore");
        check(registry.Writes == count && journal.Snapshot is not null, "drift preserves snapshot without partial restore");
        registry.Values[b] = DebloatValue.Dword(1);
        Throws(() => DebloatTransaction.Change("test", [desired[0] with { Value = DebloatValue.Dword(2) }, desired[1]], false, registry, journal), "different selection requires restore first");
        Throws(() => DebloatTransaction.Change("test", [desired[1], desired[0]], true, registry, journal), "snapshot target ordering validated");
        Throws(() => DebloatTransaction.Change("other", desired, true, registry, journal), "snapshot operation identity validated");
        Throws(() => DebloatTransaction.Change("test", [], false, registry, journal), "empty plan rejected");
        Throws(() => DebloatTransaction.Change("test", [desired[0], desired[0]], false, registry, journal), "duplicate target rejected");
        registry = new FakeRegistry { FailAt = 2 };
        journal = new FakeJournal();
        Throws(() => DebloatTransaction.Change("test", desired, false, registry, journal), "partial failure reported");
        check(journal.Snapshot is not null && registry.Values[a]!.Data[0] == "1", "partial apply preserves recovery record");
        registry.FailAt = 0;
        DebloatTransaction.Change("test", desired, true, registry, journal);
        check(registry.Values[a] is null && registry.Values[b] is null, "partial apply can restore entire original baseline");
        registry = new FakeRegistry(); journal = new FakeJournal { FailSave = true };
        Throws(() => DebloatTransaction.Change("test", desired, false, registry, journal), "journal persistence failure aborts");
        check(registry.Writes == 0, "no mutation before durable journal");
        registry = new FakeRegistry { IgnoreWrites = true }; journal = new FakeJournal();
        Throws(() => DebloatTransaction.Change("test", desired, false, registry, journal), "readback mismatch rejected");
        check(journal.Snapshot is not null, "readback mismatch retains recovery");
        string json = JsonSerializer.Serialize(journal.Snapshot, DebloatWorkspaceJson.Default.DebloatSnapshot);
        check(JsonSerializer.Deserialize(json, DebloatWorkspaceJson.Default.DebloatSnapshot)!.Applied.Length == 2, "AOT JSON snapshot metadata roundtrip");
        foreach (var value in new[] { DebloatValue.Dword(-1), new DebloatValue(RegistryValueKind.String, ["literal %TEMP%"]),
            new DebloatValue(RegistryValueKind.ExpandString, ["%USERPROFILE%\\tool.exe"]),
            new DebloatValue(RegistryValueKind.MultiString, ["A", "B"]), new DebloatValue(RegistryValueKind.Binary, ["AAEC"]) })
            check(DebloatRegistry.Decode(value) is not null, "typed snapshot decodes " + value.Kind);
        check(!DebloatTransaction.Equal(null, DebloatValue.Dword(0)), "absent is not zero");
        check(!DebloatTransaction.Equal(new(RegistryValueKind.String, ["1"]), DebloatValue.Dword(1)), "type comparison is exact");
        check(!DebloatTransaction.Equal(new(RegistryValueKind.MultiString, ["a", "b"]), new(RegistryValueKind.MultiString, ["b", "a"])), "list ordering preserved");
        var history = new[] { new DebloatRemovalEvent("News", false, DateTimeOffset.UtcNow), new DebloatRemovalEvent("Paint", false, DateTimeOffset.UtcNow), new DebloatRemovalEvent("Paint", true, DateTimeOffset.UtcNow) };
        check(DebloatWorkspacePolicy.Reappeared(history, ["News", "Paint"]).SequenceEqual(["News"]), "intentional restore not flagged as reinstall");
        check(DebloatWorkspacePolicy.Reappeared(history, []).Length == 0, "absent apps not flagged");
        check(DebloatWorkspacePolicy.Reappeared([], ["News"]).Length == 0, "no invented historic removals");
        check(DebloatWorkspacePolicy.Reappeared(history.Append(new("Paint", false, DateTimeOffset.UtcNow)), ["Paint"]).Single() == "Paint", "subsequent verified removal re-arms detection");
        check(DebloatWorkspacePolicy.SupportsAi("Recall", 26100, 3915, "Professional"), "Recall supported build boundary");
        check(!DebloatWorkspacePolicy.SupportsAi("Recall", 26100, 3914, "Professional"), "Recall unsupported servicing build");
        check(!DebloatWorkspacePolicy.SupportsAi("ClickToDo", 26300, 1, "Core"), "Home edition not treated as supported policy");
        check(DebloatWorkspacePolicy.SupportsAi("Notepad", 22621, 0, "Professional"), "Notepad Windows 22H2 boundary");
        check(!DebloatWorkspacePolicy.SupportsAi("Paint", 26100, 3359, "Professional"), "Paint policy servicing boundary");
        check(DebloatWorkspacePolicy.AiControls.Select(c => c.Id).Distinct().Count() == 4, "four independent policy controls alongside separate Copilot app workflow");
        check(DebloatWorkspacePolicy.AiControls.All(c => c.Edits.All(e => e.Target.Hive == RegistryHive.LocalMachine)), "explicit machine scope for AI policies");
        var parsed = DebloatWorkspacePolicy.ParseProvisioned("DisplayName : Microsoft.BingWeather\nPackageName : Microsoft.BingWeather_1.0.0.0_neutral_~_8wekyb3d8bbwe\nDisplayName : Microsoft.WindowsStore\nPackageName : Microsoft.WindowsStore_1.0.0.0_neutral_~_8wekyb3d8bbwe");
        check(parsed.Length == 2 && parsed[0].TargetId == "Weather", "provisioned package allowlist uses exact family");
        check(parsed[1].TargetId is null, "Store is not an approved deprovisioning target");
        check(DebloatWorkspacePolicy.ParseProvisioned("DisplayName : Microsoft.BingWeather\nPackageName : Microsoft.BingWeather_1.0.0.0_neutral_~_spoof")[0].TargetId is null, "publisher spoof rejected");
        Throws(() => DebloatWorkspacePolicy.ParseProvisioned("PackageName : invalid"), "malformed provisioning identity blocks removal");
        Throws(() => DebloatWorkspacePolicy.ParseProvisioned("DisplayName : Wrong\nPackageName : Microsoft.BingWeather_1.0.0.0_neutral_~_8wekyb3d8bbwe"), "mismatched display identity rejected");
        check(DebloatJournal.Key("a") != DebloatJournal.Key("../a") && DebloatJournal.Key("../a").All(char.IsAsciiHexDigit), "journal filenames cannot traverse directories");

        void Throws(Action action, string name)
        {
            bool threw = false;
            try { action(); } catch (Exception) { threw = true; }
            check(threw, name);
        }
    }
    private sealed class FakeRegistry : IDebloatRegistry
    {
        internal Dictionary<DebloatTarget, DebloatValue?> Values = [];
        internal int Writes, FailAt;
        internal bool IgnoreWrites;
        public DebloatValue? Read(DebloatTarget target) => Values.GetValueOrDefault(target);
        public void Write(DebloatEdit edit)
        {
            Writes++;
            if (FailAt == Writes) throw new IOException("Injected write failure");
            if (!IgnoreWrites) Values[edit.Target] = edit.Value;
        }
    }
    internal static async Task ProvisioningAsync(Action<bool, string> check)
    {
        var selected = DebloatWorkspacePolicy.ParseProvisioned("DisplayName : Microsoft.BingWeather\nPackageName : Microsoft.BingWeather_1.0.0.0_neutral_~_8wekyb3d8bbwe").Single();
        foreach (int code in new[] { 0, 3010 })
        {
            var backend = new ProvisioningFake(selected) { Code = code };
            string message = await ProvisionedAppsOperations.RemoveAsync(backend, selected);
            check(backend.Removes == 1 && backend.Logged && backend.Reads == 2, "provisioning one exact mutation with before/after evidence " + code);
            check(message.Contains("new accounts") && message.Contains("Existing user installations were not removed"), "provisioning scope disclosed " + code);
            check(message.Contains("requests a restart") == (code == 3010), "restart requirement distinct from automatic reboot " + code);
        }
        foreach (string fault in new[] { "read", "log", "timeout", "exit", "remain", "readback", "append" })
        {
            var backend = new ProvisioningFake(selected) { Fault = fault };
            bool threw = false;
            try { await ProvisionedAppsOperations.RemoveAsync(backend, selected); } catch { threw = true; }
            check(threw, "provisioning fails honestly: " + fault);
            check(backend.Removes <= 1, "provisioning never retries mutation: " + fault);
            if (fault is "read" or "log") check(backend.Removes == 0, "preflight/log failure precedes mutation: " + fault);
        }
        foreach (var invalid in new[] { selected with { TargetId = null }, selected with { TargetId = "Store" },
            selected with { PackageName = selected.PackageName.Replace("8wekyb3d8bbwe", "spoof") } })
        {
            var backend = new ProvisioningFake(invalid);
            bool threw = false;
            try { await ProvisionedAppsOperations.RemoveAsync(backend, invalid); } catch { threw = true; }
            check(threw && backend.Removes == 0, "caller cannot promote a non-allowlisted package to removable");
        }
        bool unreadable = false;
        try { DebloatWorkspacePolicy.ParseProvisioned("unreadable data"); } catch { unreadable = true; }
        check(unreadable, "empty/unrecognized provisioning output not interpreted as successful removal");
    }
    private sealed class ProvisioningFake(ProvisionedAppEntry entry) : IProvisionedAppsBackend
    {
        internal int Reads, Removes, Code;
        internal bool Logged;
        internal string Fault = "";
        public Task<ProvisionedAppEntry[]> ReadAsync()
        {
            Reads++;
            if (Fault == "read" || Fault == "readback" && Reads > 1) throw new IOException("Injected read failure");
            return Task.FromResult(Removes == 0 || Fault == "remain" ? new[] { entry } : Array.Empty<ProvisionedAppEntry>());
        }
        public Task<string> BeginLogAsync(ProvisionedAppEntry selected)
        { if (Fault == "log") throw new IOException("Log unavailable"); Logged = true; return Task.FromResult("test-log"); }
        public Task<NativeCommandResult> RemoveAsync(string exactPackageName)
        {
            if (!Logged || exactPackageName != entry.PackageName) throw new Exception("Unverified mutation");
            Removes++;
            return Task.FromResult(new NativeCommandResult(Fault == "exit" ? 5 : Code, "synthetic", "", Fault == "timeout", TimeSpan.Zero));
        }
        public Task AppendAsync(string log, string text)
        { if (Fault == "append") throw new IOException("Append unavailable"); return Task.CompletedTask; }
    }
    private sealed class FakeJournal : IDebloatJournal
    {
        internal DebloatSnapshot? Snapshot;
        internal bool FailSave;
        public DebloatSnapshot? Load(string id) => Snapshot;
        public void Save(DebloatSnapshot snapshot)
        { if (FailSave) throw new IOException("Injected persistence failure"); Snapshot = snapshot; }
        public void Delete(string id) => Snapshot = null;
    }
}
