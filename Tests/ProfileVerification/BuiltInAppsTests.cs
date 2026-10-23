using Naufal_Windows_Tech_s_Powertoys;

internal static class BuiltInAppsTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var targets = BuiltInAppsCatalog.Targets;
        check(targets.Count == 140, "140 merged apps retain original entries and all requested additions");
        check(targets.SelectMany(t => t.Families).Distinct().Count() == 32, "original exact family allowlist retained");
        check(targets.Select(t => t.Id).Distinct().Count() == targets.Count, "stable unique app IDs");
        check(targets.Select(t => t.Name).SequenceEqual(targets.Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase)), "A-Z canonical ordering");
        foreach (var target in targets.Where(t => t.ExactPackageNames.Count > 0))
        foreach (string name in target.ExactPackageNames)
        {
            string family = name + "_8wekyb3d8bbwe";
            var package = Package(target, family);
            check(BuiltInAppsCatalog.Matches(target, package), "exact identity alias " + name);
            check(!BuiltInAppsCatalog.Matches(target, Package(target, "Extra." + family)), "no substring matching " + name);
            check(!BuiltInAppsCatalog.Matches(target, package with { IsFramework = true }), "framework alias excluded " + name);
            check(!BuiltInAppsCatalog.Matches(target, package with { IsResource = true }), "resource alias excluded " + name);
        }
        check(targets.Single(t => t.Id == "News").ExactPackageNames.Contains("Microsoft.News"), "Bing and Microsoft News merged");
        foreach (var target in targets.Where(t => t.ExactPackageNames.Any(n => n.StartsWith("Microsoft.") || n.StartsWith("MicrosoftCorporationII.") || n.StartsWith("MicrosoftWindows."))))
        {
            string name = target.ExactPackageNames.First(n => n.StartsWith("Microsoft"));
            check(!BuiltInAppsCatalog.Matches(target, Package(target, name + "_evilpublisher")), "Microsoft publisher cannot be spoofed " + name);
        }
        check(targets.Single(t => t.Id == "XboxIdentity").Recommendation == AppRemovalRecommendation.NotRecommended, "Xbox dependency red");
        check(targets.Single(t => t.Id == "HPPower").Recommendation == AppRemovalRecommendation.NotRecommended, "OEM power red");
        check(targets.Single(t => t.Id == "CandyCrush").Recommendation == AppRemovalRecommendation.Recommended, "unused promotional game green");
        check(targets.Single(t => t.Id == "Paint").Recommendation == AppRemovalRecommendation.Optional, "user-choice app yellow");
        foreach (var target in targets)
        foreach (string family in target.Families)
        {
            var package = Package(target, family);
            check(BuiltInAppsCatalog.Matches(target, package), "approved " + family);
            check(BuiltInAppsCatalog.Matches(target, package with { Healthy = false }), "damaged app still removable " + family);
            check(!BuiltInAppsCatalog.Matches(target, package with { IsFramework = true }), "framework excluded " + family);
            check(!BuiltInAppsCatalog.Matches(target, package with { IsResource = true }), "resource excluded " + family);
            check(!BuiltInAppsCatalog.Matches(target, package with { Family = family + "evil" }), "publisher identity exact " + family);
            check(!BuiltInAppsCatalog.Matches(target, package with { FullName = "C:\\" + package.FullName }), "no arbitrary path " + family);
            check(!BuiltInAppsCatalog.Matches(target, package with { Name = "Microsoft.WindowsStore" }), "name must match family " + family);
            check(!BuiltInAppsCatalog.Matches(target, package with { FullName = package.FullName + "_wrongpublisher" }), "full name publisher exact " + family);
        }
        foreach (string forbidden in new[] { "NotepadPlusPlus_2247w0b46hfww", "Microsoft.WindowsStore_8wekyb3d8bbwe",
            "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy",
            "MicrosoftWindows.Client.CBS_cw5n1h2txyewy", "Microsoft.HEVCVideoExtensions_8wekyb3d8bbwe" })
            check(!targets.Any(t => BuiltInAppsCatalog.Matches(t, Package(t, forbidden))), "excluded system/paid/third-party " + forbidden);
        check(BuiltInAppsCatalog.ResolveSelection(["Clock", "AV1", "Clock"]).Select(t => t.Id).SequenceEqual(["Clock", "AV1"]), "selection canonical ordering and distinct IDs");
        foreach (string[] invalid in new[] { Array.Empty<string>(), new[] { "Clock", "WindowsStore" }, new[] { "*" }, new[] { "clock" } })
            check(await ThrowsAsync(() => Task.Run(() => BuiltInAppsCatalog.ResolveSelection(invalid))), "reject invalid selection");
        foreach (var target in targets)
        {
            var uri = BuiltInAppsCatalog.StoreUri(target);
            check(target.Kind == BuiltInAppKind.OneDriveDesktop ? uri.AbsoluteUri == OneDriveAppPolicy.RecoveryUrl
                : uri.Scheme == "ms-windows-store" && (uri.Host == "pdp" || uri.Host == "search"), "official recovery link " + target.Id);
            if (BuiltInAppsCatalog.StoreProductId(target.Id) is string product)
            {
                var args = BuiltInAppsCatalog.InstallArguments(target.Id);
                check(product.Length is 12 or 14 && product.All(char.IsAsciiLetterOrDigit), "fixed product ID " + target.Id);
                check(args[Array.IndexOf(args, "--id") + 1] == product && args.Contains("--exact"), "exact restore ID " + target.Id);
                check(args[Array.IndexOf(args, "--scope") + 1] == "user", "current-user Store install " + target.Id);
                check(args[Array.IndexOf(args, "--source") + 1] == "msstore", "Store source only " + target.Id);
                check(!args.Any(a => a is "--force" or "--ignore-security-hash" or "--allow-reboot" or "--override"), "no force bypass " + target.Id);
            }
            else check(await ThrowsAsync(() => Task.Run(() => BuiltInAppsCatalog.InstallArguments(target.Id))), "no guessed ID " + target.Id);
        }
        const string trusted = """{"Name":"msstore","Type":"Microsoft.Rest","Arg":"https://storeedgefd.dsx.mp.microsoft.com/v9.0"}""";
        check(BuiltInAppsCatalog.IsMicrosoftStoreSource(trusted), "official Store source accepted");
        foreach (string invalid in new[] { "", "{}", "[]", "null", "corrupt", trusted.Replace("https:", "http:"),
            trusted.Replace(".com/", ".com.attacker.test/"), trusted.Replace("msstore", "other"),
            trusted.Replace("Microsoft.Rest", "Microsoft.PreIndexed.Package"), trusted.Replace("/v9.0", "/v9.0?redirect=evil") })
            check(!BuiltInAppsCatalog.IsMicrosoftStoreSource(invalid), "untrusted/malformed source rejected");

        Fake backend = new() { Packages = targets.Select(t => Package(t)).ToList() };
        var updates = new Updates();
        var removed = await BuiltInAppsRemoval.RunAsync(backend, targets.Select(t => t.Id), updates);
        check(removed.Success && removed.Succeeded == targets.Count && backend.Packages.Count == 0, "all apps uninstall verified");
        check(backend.Saved && !backend.SavedRestore && backend.Log.Count == targets.Count, "plan before removal and all result logs");
        check(backend.History.Count == targets.Count && backend.History.All(e => !e.Restored), "history records only verified removals");
        check(updates.Items.Count(u => u.State == "COMPLETED") == targets.Count && updates.Items.Any(u => u.State == "PROGRESS"), "per-app success and live progress");
        var restored = await BuiltInAppsRemoval.RunAsync(backend, targets.Select(t => t.Id), updates, restore: true);
        check(restored.Success && restored.Succeeded == targets.Count && backend.Packages.Count == targets.Count, "restore all missing apps verifies each identity");
        check(backend.SavedRestore, "restore saves audit before mutations");
        check(backend.History.Count(e => e.Restored) == targets.Count, "verified restore records an intentional return");
        backend = new(); updates = new();
        removed = await BuiltInAppsRemoval.RunAsync(backend, ["Clock"], updates);
        check(removed.Success && removed.Unavailable == 1 && backend.RemoveCalls == 0 && updates.Items.Single().State == "UNAVAILABLE", "absent uninstall neutral, no mutation");
        check(backend.History.Count == 0, "already-absent app does not invent removal history");
        backend = new() { Packages = [Package(targets.Single(t => t.Id == "Clock"))], RemoveFailure = new UnauthorizedAccessException("denied") };
        removed = await BuiltInAppsRemoval.RunAsync(backend, ["Clock"], new Updates());
        check(!removed.Success && removed.Failed == 1 && removed.Unavailable == 0, "access denied not unavailable");
        backend = new() { ReadFailure = new UnauthorizedAccessException("inventory denied") };
        check(await ThrowsAsync(() => BuiltInAppsRemoval.RunAsync(backend, ["Clock"], null)) && backend.RemoveCalls == 0 && !backend.Saved, "failed inventory cannot uninstall or create empty success plan");
        check(await ThrowsAsync(() => BuiltInAppsRemoval.RunAsync(backend, ["Clock"], null, true)) && backend.RestoreCalls == 0, "failed inventory cannot restore");
        backend = new() { SaveFailure = true, Packages = [Package(targets.Single(t => t.Id == "Clock"))] };
        check(await ThrowsAsync(() => BuiltInAppsRemoval.RunAsync(backend, ["Clock"], null)) && backend.RemoveCalls == 0, "failed audit aborts before uninstall");
        check(await ThrowsAsync(() => BuiltInAppsRemoval.RunAsync(backend, ["Clock"], null, true)) && backend.RestoreCalls == 0, "failed audit aborts before restore");
        backend = new() { Packages = [Package(targets.Single(t => t.Id == "Clock"))], KeepAfterRemove = true };
        removed = await BuiltInAppsRemoval.RunAsync(backend, ["Clock"], null);
        check(!removed.Success && removed.Failed == 1, "uninstall return alone not success");
        check(backend.History.Count == 0, "failed readback cannot arm reinstalled detection");
        backend = new() { Packages = targets.Select(t => Package(t)).ToList(), HistoryFailure = true };
        removed = await BuiltInAppsRemoval.RunAsync(backend, ["AV1", "Clock"], null);
        check(!removed.Success && removed.NotStarted == 1 && backend.RemoveCalls == 1, "history persistence failure stops further removals");
        backend = new() { NoRestore = true };
        restored = await BuiltInAppsRemoval.RunAsync(backend, ["Clock"], null, true);
        check(!restored.Success && restored.Failed == 1 && restored.Unavailable == 0, "restore missing readback not false success/unavailable");
        backend = new() { RestoreUnhealthy = true };
        restored = await BuiltInAppsRemoval.RunAsync(backend, ["Clock"], null, true);
        check(!restored.Success, "damaged registration not verified restore");
        backend = new() { FailReadback = true };
        restored = await BuiltInAppsRemoval.RunAsync(backend, ["Clock"], null, true);
        check(!restored.Success && restored.Unavailable == 0, "readback failure not absent");
        foreach (bool restore in new[] { false, true })
        {
            backend = new() { Packages = [CopilotStorePolicy.Package("Store inventory denied")] };
            var uncertain = await BuiltInAppsRemoval.RunAsync(backend, ["Copilot"], null, restore);
            check(!uncertain.Success && uncertain.Failed == 1 && uncertain.Unavailable == 0 &&
                backend.RemoveCalls == 0 && backend.RestoreCalls == 0, "unknown Store identity blocks mutation, never absent " + restore);
        }
        foreach (Exception failure in new Exception[] { new TimeoutException("Windows still busy"), new PendingDeploymentException("busy") })
        {
            backend = new() { Packages = targets.Select(t => Package(t)).ToList(), RemoveFailure = failure };
            removed = await BuiltInAppsRemoval.RunAsync(backend, ["AV1", "Clock"], null);
            check(!removed.Success && removed.Failed == 1 && removed.NotStarted == 1 && backend.RemoveCalls == 1, "timeout/pending removal stops later apps");
            backend = new() { RestoreFailure = failure };
            restored = await BuiltInAppsRemoval.RunAsync(backend, ["AV1", "Clock"], null, true);
            check(!restored.Success && restored.NotStarted == 1 && backend.RestoreCalls == 1, "timeout/pending restore stops later apps");
        }
        backend = new() { Packages = targets.Select(t => Package(t)).ToList(), AppendFailure = true };
        removed = await BuiltInAppsRemoval.RunAsync(backend, ["AV1", "Clock"], null);
        check(!removed.Success && removed.Succeeded == 1 && removed.NotStarted == 1 && backend.RemoveCalls == 1, "lost audit stops batch without lying about completed app");
        var teams = targets.Single(t => t.Id == "Teams");
        backend = new() { Packages = teams.Families.Select(f => Package(teams, f)).ToList() };
        removed = await BuiltInAppsRemoval.RunAsync(backend, ["Teams"], null);
        check(removed.Success && removed.Succeeded == 1 && backend.RemoveCalls == 2, "both Teams variants removed as one requested app");

        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string service = File.ReadAllText(Path.Combine(root, "BuiltInAppsService.cs"));
        string ui = File.ReadAllText(Path.Combine(root, "MainWindow.BuiltInApps.cs"));
        check(service.Contains("FindPackagesForUser(string.Empty)") && service.Contains("RemovalOptions.None"), "production current-user enumeration/removal");
        check(!service.Contains("RemovePackageForUserAsync") && !service.Contains("DeprovisionPackageForAllUsersAsync") && !service.Contains("RegistryKey"), "no all-user removal / deprovision / ACL bypass");
        check(service.Contains("DeploymentOperationTimeout.AwaitAsync") && service.Contains("Timeout.InfiniteTimeSpan"), "deployment gate and no premature external kill");
        check(service.Contains("IsMicrosoftStoreSource(source.StandardOutput)"), "production verifies Store endpoint");
        check(ui.Contains("primaryButtonText: \"Uninstall selected\"") && ui.Contains("secondaryButtonText: \"Restore selected\""), "both requested buttons wired");
        check(ui.Contains("new CatalogProgressWindow(window, verb") && ui.Contains("progressWindow.UnavailableItem"), "separate per-app progress and neutral absence");
        check(ui.Contains("IsChecked = false") && ui.Contains("CloseOnPrimary = true") && ui.IndexOf("confirmWindow.ShowAsync()") < ui.IndexOf("BuiltInAppsRemoval.RunAsync"), "no preselection / confirm before mutation");
        await CheckOneDriveAsync(check, targets, service, ui, root);
        BuiltInAppsViewTests.Run(check);
    }

    private static BuiltInAppPackage Package(BuiltInAppTarget target, string? family = null)
    {
        if (family is null && target.Kind == BuiltInAppKind.OneDriveDesktop)
            return OneDriveAppPolicy.Installed("user", true);
        family ??= target.Families.FirstOrDefault() ?? target.ExactPackageNames[0] + "_8wekyb3d8bbwe";
        int split = family.LastIndexOf('_');
        string name = family[..split];
        return new(name, family, name + "_1.0.0.0_x64_" + family[split..], false, false);
    }

    private static async Task CheckOneDriveAsync(Action<bool, string> check,
        IReadOnlyList<BuiltInAppTarget> targets, string service, string ui, string root)
    {
        var target = targets.Single(t => t.Id == "OneDrive");
        check(target.Name == "Microsoft OneDrive" && target.Families.Count == 0 &&
            target.Kind == BuiltInAppKind.OneDriveDesktop, "OneDrive is desktop sync client, not invented AppX family");
        check(OneDriveAppPolicy.ReadSavedScope(null) == "user", "new OneDrive defaults to current account");
        check(OneDriveAppPolicy.ResolveScope(true, [], null) == "user", "restore without saved scope uses Microsoft default");
        check(OneDriveAppPolicy.ResolveScope(true, [], "machine") == "machine", "restore preserves previous shared scope");
        check(OneDriveAppPolicy.ResolveScope(true, ["user"], "machine") == "user", "existing scope overrides old journal");
        check(OneDriveAppPolicy.ResolveScope(false, [], "machine") is null, "absent uninstall has no mutation scope");
        foreach (bool restore in new[] { false, true })
        {
            check(await ThrowsAsync(() => Task.Run(() => OneDriveAppPolicy.ResolveScope(restore, ["user", "machine"], null))), "mixed scopes blocked");
            check(await ThrowsAsync(() => Task.Run(() => OneDriveAppPolicy.ResolveScope(restore, ["unknown"], null))), "unknown installed scope blocked");
        }
        foreach (string scope in new[] { "user", "machine" })
        {
            check(OneDriveAppPolicy.ReadSavedScope(scope + "\r\n") == scope, "scope journal round-trip " + scope);
            var installed = OneDriveAppPolicy.Installed(scope, true);
            check(BuiltInAppsCatalog.Matches(target, installed), "desktop scope match " + scope);
            foreach (var invalid in new[] { installed with { Kind = BuiltInAppKind.Appx }, installed with { Scope = "all" },
                installed with { Name = "OneDrive" }, installed with { Family = "Microsoft.OneDrive_8wekyb3d8bbwe" },
                installed with { FullName = "C:\\arbitrary.exe" }, installed with { IsFramework = true }, installed with { IsResource = true } })
                check(!BuiltInAppsCatalog.Matches(target, invalid), "reject mismatched desktop identity");
            foreach (bool restore in new[] { false, true })
            {
                var args = OneDriveAppPolicy.Arguments(restore, scope);
                check(args[0] == (restore ? "install" : "uninstall"), "OneDrive verb");
                check(args[Array.IndexOf(args, "--id") + 1] == "Microsoft.OneDrive" && args.Contains("--exact"), "OneDrive exact ID");
                check(args[Array.IndexOf(args, "--scope") + 1] == scope, "OneDrive explicit scope");
                check(args[Array.IndexOf(args, "--source") + 1] == "winget", "OneDrive not Store viewer");
                check(args.Contains("--accept-package-agreements") == restore, "install-only package agreement flag");
                check(!args.Any(a => a is "--force" or "--all" or "--override" or "--ignore-security-hash" or "--allow-reboot"), "OneDrive no bypass, broad removal or reboot");
            }
            var backend = new Fake { Packages = [installed] };
            var updates = new Updates();
            var result = await BuiltInAppsRemoval.RunAsync(backend, ["OneDrive"], updates);
            check(result.Success && result.Succeeded == 1 && backend.RemoveCalls == 1, "OneDrive removal readback " + scope);
            check(updates.Items.Any(u => u.State == "COMPLETED") && updates.Items.Any(u => u.State == "PROGRESS"), "OneDrive item progress " + scope);
            backend = new Fake { Packages = [installed], KeepAfterRemove = true };
            result = await BuiltInAppsRemoval.RunAsync(backend, ["OneDrive"], null);
            check(!result.Success, "OneDrive still installed never green " + scope);
        }
        foreach (string invalid in new[] { "", "all", "Machine", "user --all", "C:\\file", "user\nmachine" })
        {
            check(await ThrowsAsync(() => Task.Run(() => OneDriveAppPolicy.ReadSavedScope(invalid))), "invalid scope journal fails closed");
            check(await ThrowsAsync(() => Task.Run(() => OneDriveAppPolicy.Arguments(true, invalid))), "invalid command scope rejected");
        }
        const string trusted = """{"Name":"winget","Type":"Microsoft.PreIndexed.Package","Arg":"https://cdn.winget.microsoft.com/cache"}""";
        check(OneDriveAppPolicy.IsOfficialSource(trusted), "OneDrive official source");
        foreach (string invalid in new[] { "", "{}", "[]", "null", "broken", trusted.Replace("https:", "http:"),
            trusted.Replace(".com/", ".com.attacker.test/"), trusted.Replace("\"winget\"", "\"msstore\""),
            trusted.Replace("Microsoft.PreIndexed.Package", "Microsoft.Rest"), trusted.Replace("/cache", "/cache?redirect=evil") })
            check(!OneDriveAppPolicy.IsOfficialSource(invalid), "OneDrive rejects source substitution");
        var absent = await BuiltInAppsRemoval.RunAsync(new Fake(), ["OneDrive"], null);
        check(absent.Success && absent.Unavailable == 1, "OneDrive absent uninstall neutral");
        var restored = await BuiltInAppsRemoval.RunAsync(new Fake(), ["OneDrive"], null, true);
        check(restored.Success && restored.Succeeded == 1, "OneDrive restore verified");
        foreach (var backend in new[] { new Fake { NoRestore = true }, new Fake { RestoreUnhealthy = true },
            new Fake { FailReadback = true }, new Fake { RestoreFailure = new TimeoutException("pending") } })
        {
            restored = await BuiltInAppsRemoval.RunAsync(backend, ["OneDrive"], null, true);
            check(!restored.Success && restored.Unavailable == 0, "OneDrive unverified restore is not absence/success");
        }
        string desktop = File.ReadAllText(Path.Combine(root, "OneDriveAppService.cs"));
        check(service.Contains(".Concat(OneDriveAppService.ReadInstalled())") && service.Contains("_oneDrive.ChangeAsync(false") &&
            service.Contains("_oneDrive.ChangeAsync(true"), "desktop backend wired into both actions and inventory");
        check(desktop.Contains("RegistryHive.CurrentUser : RegistryHive.LocalMachine") && desktop.Contains("RegistryView.Registry32"), "both OneDrive scopes and registry views");
        check(!desktop.Contains("Directory.Delete") && !desktop.Contains("File.Delete") && !desktop.Contains("SetValue") &&
            !desktop.Contains("UninstallString"), "no personal file/registry cleanup or registry command execution");
        check(desktop.Contains("IsOfficialSource(source.StandardOutput)") && desktop.Contains("Timeout.InfiniteTimeSpan"), "verified source and deployment gate");
        check(ui.Contains("OneDriveAppPolicy.Warning") && ui.Contains("OneDriveAppPolicy.SourceConsent") &&
            ui.Contains("OneDriveAppPolicy.RestoreNotice") && ui.Contains("{BuiltInAppsCatalog.Targets.Count}"), "OneDrive warning, consent, restore and dynamic count");
    }
    private static async Task<bool> ThrowsAsync(Func<Task> action)
    { try { await action(); return false; } catch { return true; } }
    private sealed class Updates : IProgress<BuiltInAppUpdate>
    {
        internal List<BuiltInAppUpdate> Items = new();
        public void Report(BuiltInAppUpdate update) => Items.Add(update);
    }
    private sealed class Fake : IBuiltInAppsBackend
    {
        internal List<BuiltInAppPackage> Packages = new();
        internal List<string> Log = new();
        internal List<(string Id, bool Restored)> History = [];
        internal bool HistoryFailure;
        internal Exception? ReadFailure, RemoveFailure, RestoreFailure;
        internal bool SaveFailure, AppendFailure, Saved, SavedRestore, KeepAfterRemove, NoRestore, RestoreUnhealthy, FailReadback;
        internal int RemoveCalls, RestoreCalls, Reads;
        public Task<IReadOnlyList<BuiltInAppPackage>> ReadAsync()
        {
            Reads++;
            if (ReadFailure is not null) throw ReadFailure;
            if (FailReadback && Reads > 1) throw new UnauthorizedAccessException("readback denied");
            return Task.FromResult<IReadOnlyList<BuiltInAppPackage>>(Packages.ToArray());
        }
        public Task SavePlanAsync(bool restore, IReadOnlyList<BuiltInAppTarget> targets, IReadOnlyList<BuiltInAppPackage> packages)
        { if (SaveFailure) throw new IOException("audit denied"); Saved = true; SavedRestore = restore; return Task.CompletedTask; }
        public Task AppendResultAsync(string text)
        { if (AppendFailure) throw new IOException("disk full"); Log.Add(text); return Task.CompletedTask; }
        public Task RecordVerifiedAsync(string id, bool restored)
        { if (HistoryFailure) throw new IOException("history disk full"); History.Add((id, restored)); return Task.CompletedTask; }
        public Task RemoveAsync(BuiltInAppPackage package, IProgress<CatalogProgressUpdate>? progress)
        {
            if (!Saved) throw new InvalidOperationException("mutation before audit");
            RemoveCalls++; if (RemoveFailure is not null) throw RemoveFailure;
            progress?.Report(new("Removing package", 40));
            if (!KeepAfterRemove) Packages.RemoveAll(p => p.FullName == package.FullName);
            return Task.CompletedTask;
        }
        public Task RestoreAppAsync(BuiltInAppTarget target, IProgress<CatalogProgressUpdate>? progress)
        {
            if (!Saved) throw new InvalidOperationException("restore before audit");
            RestoreCalls++; if (RestoreFailure is not null) throw RestoreFailure;
            if (!NoRestore && !Packages.Any(p => BuiltInAppsCatalog.Matches(target, p)))
                Packages.Add(Package(target) with { Healthy = !RestoreUnhealthy });
            return Task.CompletedTask;
        }
    }
}
