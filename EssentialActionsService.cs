using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal sealed class EssentialActionsService : IToolActionService
    {
        private const string IconCacheBackupPath =
            @"Software\Naufal Windows Tech\Powertoys\Backups\EssentialActions\IconCache";
        private const string StoreSearchBackupPath =
            @"Software\Naufal Windows Tech\Powertoys\Backups\EssentialActions\StoreSearch";
        private const string NtfsBackupPath =
            @"Software\Naufal Windows Tech\Powertoys\Backups\EssentialActions\NtfsPerformance";
        private const string StoragePowerBackupPath =
            @"Software\Naufal Windows Tech\Powertoys\Backups\EssentialActions\StoragePowerLatency";
        private const string ExplorerPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer";
        private const string FileSystemPath =
            @"SYSTEM\CurrentControlSet\Control\FileSystem";
        private const string StorageDiskSubgroupGuid =
            "0012ee47-9041-4b5d-9b77-535fba8b1442";
        private const string StorageAhciLpmGuid =
            "0b2d69d7-a2a1-449c-9680-f91c70521c60";
        private const string StorageNvmePrimaryIdleGuid =
            "d639518a-e56d-4345-8af2-b9f32fb26109";
        private const string StorageNvmeSecondaryIdleGuid =
            "d3d55efd-c1ff-424e-9dc3-441be7833010";

        private static readonly string[] NtfsValueNames =
        {
            "NtfsDisableLastAccessUpdate",
            "NtfsDisable8dot3NameCreation",
            "NtfsMftZoneReservation",
            "RefsDisableLastAccessUpdate"
        };

        private readonly NativeCommandRunner _commandRunner = new();

        private static readonly IReadOnlyList<ToolActionDefinition> Actions =
            new[]
            {
                new ToolActionDefinition(
                    "StoreSearch",
                    "Recommendations",
                    "Microsoft Store Recommended Search Results",
                    "Blocks Microsoft Store recommended-search results for the current user. The complete existing Store database security descriptor is captured for Restore.",
                    "Block",
                    false,
                    true),
                new ToolActionDefinition(
                    "IconCache",
                    "Explorer",
                    "Icon Cache Size - 100 MB",
                    "Sets Explorer's MaxCachedIcons value to 102400 KB so a larger icon cache can be retained. The current value and registry type are captured before the first change and Explorer may need to restart before the new limit is used.",
                    "Apply",
                    true,
                    true),
                new ToolActionDefinition(
                    "TempFiles",
                    "Cleanup",
                    "Temporary Files - Remove",
                    "Measures and removes deletable files and empty folders from the current user's Temp directory and Windows Temp. Files locked by running applications are skipped, and the final report shows file, folder, and byte totals rather than claiming every item was removed.",
                    "Run cleanup",
                    true,
                    false),
                new ToolActionDefinition(
                    "NtfsPerformance",
                    "System / Storage",
                    "NTFS Performance Options",
                    "Disables NTFS last-access timestamp updates and new 8.3 short-name creation to reduce metadata work. Legacy applications can depend on short names; the exact FileSystem settings are captured and verified for Restore.",
                    "Apply",
                    true,
                    true,
                    "Disable NTFS last-access updates and 8.3-name creation? Some legacy software can depend on these features.",
                    "Restore the exact NTFS/FileSystem values captured before Apply?",
                    RestartRecommended: true),
                new ToolActionDefinition(
                    "StoragePowerLatency",
                    "System / Storage",
                    "SSD / NVMe Power & Idle Latency Optimization",
                    "Changes the active power plan so AHCI links stay Active on AC/DC and primary/secondary NVMe idle timeouts are 0 ms on AC. This favors storage response latency over idle power, battery life, and temperature; all affected plan values are captured for Restore.",
                    "Apply",
                    true,
                    true,
                    "Apply SSD/NVMe low-latency power settings to the current power plan? This can increase idle power usage and temperature.",
                    "Restore the exact storage power values captured before Apply?"),
                new ToolActionDefinition(
                    "DriverOptionalUpdates",
                    "System / Storage",
                    "Driver Optional Updates",
                    "Opens the Windows Update Optional updates page where Microsoft- or vendor-published driver packages can be reviewed. The application does not select or install a driver automatically, preserving Windows confirmation and rollback workflows.",
                    "Open",
                    false,
                    false),
                new ToolActionDefinition(
                    "VirtualMemorySettings",
                    "System / Storage",
                    "Virtual Memory Settings",
                    "Opens the native Windows Virtual Memory configuration surface for reviewing automatic paging-file management, drive placement, and custom sizes. No pagefile setting is changed automatically by this catalog action.",
                    "Open",
                    false,
                    false),
                new ToolActionDefinition(
                    "WindowsTroubleshoot",
                    "System / Storage",
                    "Windows Troubleshoot",
                    "Opens the native Windows troubleshooting page for available system troubleshooters and diagnostic history. The action only navigates to Windows Settings and does not start or approve a repair automatically.",
                    "Open",
                    false,
                    false),
                new ToolActionDefinition(
                    "DesktopBackground",
                    "System / Storage",
                    "Desktop Background",
                    "Opens the native Windows Background personalization page for selecting a picture, slideshow, solid color, and fit mode. The application does not replace the wallpaper or copy image assets automatically.",
                    "Open",
                    false,
                    false)
            };

        public IReadOnlyList<ToolActionDefinition> GetActions() => Actions;

        private static string SnapshotPath(string id) => id switch
        {
            "IconCache" => IconCacheBackupPath,
            "NtfsPerformance" => NtfsBackupPath,
            "StoragePowerLatency" => StoragePowerBackupPath,
            "StoreSearch" => StoreSearchBackupPath,
            _ => throw new ArgumentOutOfRangeException(nameof(id))
        };

        internal static string DescribeRecovery(string id) =>
            CatalogRecovery.Snapshot(SnapshotPath(id), "No snapshot found. Restoring this option requires its original saved settings.");

        public async Task<ToolToggleState> ReadBulkStateAsync(string id)
        {
            try
            {
                using RegistryKey? snapshot = Registry.CurrentUser.OpenSubKey(SnapshotPath(id), writable: false);
                bool captured = EssentialSnapshotValidation.HasSnapshot(name => snapshot?.GetValue(name));
                if (id == "IconCache")
                {
                    using RegistryKey? key = OpenLocalMachineKey(ExplorerPath, writable: false);
                    string value = Convert.ToString(key?.GetValue("MaxCachedIcons"), CultureInfo.InvariantCulture) ?? "";
                    return new(value == "102400", true, $"MaxCachedIcons={(value.Length == 0 ? "Windows default" : value)}")
                        { HasSavedOriginal = captured };
                }
                if (id == "NtfsPerformance")
                {
                    var ntfs = ReadNtfsPerformanceState();
                    return new(ntfs.IsApplied, true, ntfs.ActualValue,
                        HasAppliedParts: ntfs.LastAccessMatches || ntfs.ShortNamesMatch) { HasSavedOriginal = captured };
                }
                string scheme = await GetActivePowerSchemeGuidAsync();
                StoragePowerState state = await ReadStoragePowerStateAsync(scheme);
                bool readable = state.Ahci.Ac.HasValue && state.Ahci.Dc.HasValue &&
                    state.NvmePrimary.Ac.HasValue && state.NvmeSecondary.Ac.HasValue;
                string actual = $"Scheme={scheme}; AHCI AC/DC={state.Ahci.Ac}/{state.Ahci.Dc}; " +
                    $"NVMe primary AC/DC={state.NvmePrimary.Ac}/{state.NvmePrimary.Dc}; " +
                    $"NVMe secondary AC/DC={state.NvmeSecondary.Ac}/{state.NvmeSecondary.Dc}";
                if (!readable) return new(false, false, actual, "Unable to read power-plan storage settings.");
                bool applied = state.Ahci.Ac == 0 && state.Ahci.Dc == 0 &&
                    state.NvmePrimary.Ac == 0 && state.NvmeSecondary.Ac == 0;
                bool anyApplied = state.Ahci.Ac == 0 || state.Ahci.Dc == 0 || state.NvmePrimary.Ac == 0 || state.NvmeSecondary.Ac == 0;
                return new(applied, true, actual, HasAppliedParts: anyApplied) { HasSavedOriginal = captured };
            }
            catch (Exception exception)
            {
                return new(false, false, "Unable to read", exception.Message);
            }
        }

        public async Task<ToolActionResult> RunAsync(ToolActionDefinition definition)
        {
            if (definition.RequiresAdministrator && !WindowsPrivilegeService.IsAdministrator())
            {
                return new ToolActionResult(
                    false,
                    "Administrator rights are required. Run the app or Visual Studio as Administrator.");
            }

            try
            {
                return definition.Id switch
                {
                    "IconCache" => await ApplyIconCacheAsync(),
                    "StoreSearch" => await ApplyStoreSearchBlockAsync(),
                    "TempFiles" => await CleanTemporaryFilesAsync(),
                    "NtfsPerformance" => await ApplyNtfsPerformanceAsync(),
                    "StoragePowerLatency" => await ApplyStoragePowerLatencyAsync(),
                    "DriverOptionalUpdates" => OpenWindowsSetting(
                        "ms-settings:windowsupdate-optionalupdates",
                        "Driver optional updates",
                        "devmgmt.msc"),
                    "VirtualMemorySettings" => OpenWindowsSetting(
                        "SystemPropertiesPerformance.exe",
                        "Virtual Memory settings"),
                    "WindowsTroubleshoot" => OpenWindowsSetting(
                        "ms-settings:troubleshoot",
                        "Windows Troubleshoot"),
                    "DesktopBackground" => OpenWindowsSetting(
                        "ms-settings:personalization-background",
                        "Desktop Background"),
                    _ => new ToolActionResult(false, $"Unsupported essential action: {definition.Id}")
                };
            }
            catch (Exception exception)
            {
                return new ToolActionResult(false, exception.Message);
            }
        }

        public async Task<ToolActionResult> RestoreAsync(ToolActionDefinition definition)
        {
            if (!definition.SupportsRestore)
            {
                return new ToolActionResult(false, "This action does not have a restore operation.");
            }

            if (definition.RequiresAdministrator && !WindowsPrivilegeService.IsAdministrator())
            {
                return new ToolActionResult(
                    false,
                    "Administrator rights are required. Run the app or Visual Studio as Administrator.");
            }

            try
            {
                return definition.Id switch
                {
                    "IconCache" => await RestoreIconCacheAsync(),
                    "StoreSearch" => await RestoreStoreSearchBlockAsync(),
                    "NtfsPerformance" => await RestoreNtfsPerformanceAsync(),
                    "StoragePowerLatency" => await RestoreStoragePowerLatencyAsync(),
                    _ => new ToolActionResult(false, $"Unsupported restore action: {definition.Id}")
                };
            }
            catch (Exception exception)
            {
                return new ToolActionResult(false, exception.Message);
            }
        }

        public Task<ToolToggleState> ReadStoreSearchStateAsync()
        {
            try
            {
                using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                    StoreSearchBackupPath,
                    writable: false);
                string? capturedPath = Convert.ToString(
                    backup?.GetValue("Path"),
                    CultureInfo.InvariantCulture);
                string path = string.IsNullOrWhiteSpace(capturedPath)
                    ? GetStoreDatabasePath()
                    : Path.GetFullPath(capturedPath);
                if (!CatalogAvailability.FileIsPresent(path))
                {
                    return Task.FromResult(new ToolToggleState(
                        false,
                        false,
                        "Microsoft Store database is absent",
                        "This Store recommendation target is not available on this Windows installation.", UnavailableOnThisPc: true));
                }

                try
                {
                    FileSecurity security = FileSystemAclExtensions.GetAccessControl(
                        new FileInfo(path),
                        AccessControlSections.Access);
                    bool blocked = HasEveryoneFullControlDeny(security);
                    return Task.FromResult(new ToolToggleState(
                        blocked,
                        true,
                        blocked
                            ? "Store recommendation database ACL is blocked"
                            : "Store recommendation database ACL is available"));
                }
                catch (UnauthorizedAccessException) when (backup?.GetValue("Captured") is not null)
                {
                    return Task.FromResult(new ToolToggleState(
                        true,
                        true,
                        "Store recommendation database access is denied by the captured block"));
                }
            }
            catch (FileNotFoundException exception)
            {
                return Task.FromResult(new ToolToggleState(
                    false,
                    false,
                    "Microsoft Store database is absent",
                    exception.Message, UnavailableOnThisPc: true));
            }
            catch (Exception exception)
            {
                return Task.FromResult(new ToolToggleState(
                    false,
                    false,
                    "Unable to read Store recommendation state",
                    exception.Message));
            }
        }

        public async Task<ToolToggleOperationResult> SetStoreSearchStateAsync(bool targetOn)
        {
            try
            {
                ToolActionResult result = targetOn
                    ? await ApplyStoreSearchBlockAsync()
                    : await RestoreStoreSearchBlockAsync();
                ToolToggleState state = await ReadStoreSearchStateAsync();
                bool verified = result.Success && state.IsAvailable && state.IsOn == targetOn;
                return new ToolToggleOperationResult(
                    verified,
                    verified,
                    verified
                        ? result.Message
                        : $"{result.Message} Verification: {state.ActualValue}",
                    state);
            }
            catch (Exception exception)
            {
                ToolToggleState state = await ReadStoreSearchStateAsync();
                return new ToolToggleOperationResult(false, false, exception.Message, state);
            }
        }

        private async Task<ToolActionResult> ApplyIconCacheAsync()
        {
            using RegistryKey? current = OpenLocalMachineKey(ExplorerPath, writable: false);
            object? original = current?.GetValue(
                "MaxCachedIcons",
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);

            using (RegistryKey backup = Registry.CurrentUser.CreateSubKey(IconCacheBackupPath, writable: true))
            {
                if (!EssentialSnapshotValidation.HasSnapshot(name => backup.GetValue(name)))
                {
                    RegistryValueKind kind = original is null ? RegistryValueKind.String : current!.GetValueKind("MaxCachedIcons");
                    string serialized = original is null ? string.Empty : EssentialSnapshotValidation.SerializeIcon(original, kind);
                    backup.SetValue("Existed", original is null ? 0 : 1, RegistryValueKind.DWord);
                    if (original is not null)
                    {
                        backup.SetValue("Kind", kind.ToString(), RegistryValueKind.String);
                        backup.SetValue("Value", serialized, RegistryValueKind.String);
                    }
                    backup.Flush();
                    backup.SetValue("Captured", 1, RegistryValueKind.DWord);
                    backup.Flush();
                }
                _ = EssentialSnapshotValidation.ReadIcon(name => backup.GetValue(name));
            }

            using (RegistryKey explorer = CreateLocalMachineKey(ExplorerPath))
            {
                explorer.SetValue("MaxCachedIcons", "102400", RegistryValueKind.String);
            }

            string? verified;
            using (RegistryKey? verifyKey = OpenLocalMachineKey(ExplorerPath, writable: false))
            {
                verified = Convert.ToString(
                    verifyKey?.GetValue("MaxCachedIcons"),
                    CultureInfo.InvariantCulture);
            }
            if (!string.Equals(verified, "102400", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("MaxCachedIcons could not be verified after Apply.");
            }

            await RestartExplorerAsync();
            return new ToolActionResult(
                true,
                "MaxCachedIcons=102400 was applied and verified. Explorer was restarted.");
        }

        private async Task<ToolActionResult> RestoreIconCacheAsync()
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(IconCacheBackupPath, writable: false);
            if (backup?.GetValue("Captured") is null)
            {
                throw new InvalidOperationException(
                    "The original Icon Cache value has not been captured yet. Run Apply first.");
            }

            EssentialSavedValue saved = EssentialSnapshotValidation.ReadIcon(name => backup.GetValue(name));
            using (RegistryKey explorer = CreateLocalMachineKey(ExplorerPath))
            {
                if (!saved.Exists)
                {
                    explorer.DeleteValue("MaxCachedIcons", throwOnMissingValue: false);
                }
                else
                {
                    explorer.SetValue("MaxCachedIcons", saved.Value!, saved.Kind);
                }
                object? actual = explorer.GetValue("MaxCachedIcons", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (!saved.Matches(actual, actual is null ? null : explorer.GetValueKind("MaxCachedIcons")))
                    throw new InvalidDataException("Icon Cache restore did not match the exact saved value and type. Backup retained.");
            }

            await RestartExplorerAsync();
            Registry.CurrentUser.DeleteSubKeyTree(IconCacheBackupPath, throwOnMissingSubKey: false);
            return new ToolActionResult(
                true,
                "The captured MaxCachedIcons value and type were restored and verified. Explorer was restarted.");
        }

        private static Task<ToolActionResult> ApplyStoreSearchBlockAsync()
        {
            string path = GetStoreDatabasePath();
            FileInfo file = new(path);
            FileSecurity security = FileSystemAclExtensions.GetAccessControl(
                file,
                AccessControlSections.Access);
            string originalSddl = security.GetSecurityDescriptorSddlForm(
                AccessControlSections.Access);
            _ = StoreDaclSnapshot.AccessSddl(originalSddl);

            using (RegistryKey backup = Registry.CurrentUser.CreateSubKey(
                StoreSearchBackupPath,
                writable: true))
            {
                if (backup.GetValue("Captured") is null)
                {
                    backup.SetValue("Path", path, RegistryValueKind.String);
                    backup.SetValue("Sddl", originalSddl, RegistryValueKind.String);
                    backup.Flush();
                    backup.SetValue("Captured", 1, RegistryValueKind.DWord);
                    backup.Flush();
                }
                else if (backup.GetValue("Captured") is not int captured || captured != 1 ||
                    !string.Equals(backup.GetValue("Path") as string, path, StringComparison.OrdinalIgnoreCase) ||
                    backup.GetValue("Sddl") is not string saved || string.IsNullOrWhiteSpace(saved))
                    throw new InvalidDataException("The saved Store permissions are incomplete. Apply was not started.");
                else _ = StoreDaclSnapshot.AccessSddl(saved);
            }

            SecurityIdentifier everyone = new(WellKnownSidType.WorldSid, null);
            FileSystemAccessRule denyRule = new(
                everyone,
                FileSystemRights.FullControl,
                AccessControlType.Deny);
            security.SetAccessRule(denyRule);
            FileSystemAclExtensions.SetAccessControl(file, security);

            FileSecurity verified = FileSystemAclExtensions.GetAccessControl(
                file,
                AccessControlSections.Access);
            bool blocked = HasEveryoneFullControlDeny(verified);
            if (!blocked)
            {
                throw new InvalidOperationException(
                    "The Store database deny ACL could not be verified after Apply.");
            }

            return Task.FromResult(new ToolActionResult(
                true,
                "The Store recommendation database is blocked. Its original access permissions are saved for Restore."));
        }

        private static Task<ToolActionResult> RestoreStoreSearchBlockAsync()
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                StoreSearchBackupPath,
                writable: false);
            string path = Convert.ToString(
                backup?.GetValue("Path"),
                CultureInfo.InvariantCulture) ?? GetStoreDatabasePath();
            string? sddl = Convert.ToString(
                backup?.GetValue("Sddl"),
                CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(sddl) || backup?.GetValue("Captured") is not int captured || captured != 1)
            {
                throw new InvalidOperationException(
                    "The original Store database ACL has not been captured yet. Run Block first.");
            }
            if (!CatalogAvailability.FileIsPresent(path))
            {
                throw new FileNotFoundException(
                    "The Store database saved in the snapshot no longer exists.",
                    path);
            }

            FileSecurity security = new();
            security.SetSecurityDescriptorSddlForm(StoreDaclSnapshot.AccessSddl(sddl), AccessControlSections.Access);
            string? actual = null;
            try
            {
                actual = FileSystemAclExtensions.GetAccessControl(
                    new FileInfo(path),
                    AccessControlSections.Access)
                    .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            }
            catch (UnauthorizedAccessException)
            {
                // A deny rule may block READ_CONTROL while the owner still has
                // WRITE_DAC. Try only the saved DACL; do not take ownership.
            }
            if (actual is null || !StoreDaclSnapshot.Matches(sddl, actual))
            {
                FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
                actual = FileSystemAclExtensions.GetAccessControl(new FileInfo(path), AccessControlSections.Access)
                    .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            }
            if (!StoreDaclSnapshot.Matches(sddl, actual))
            {
                throw new InvalidOperationException(
                    "The Store database ACL restore could not be verified exactly.");
            }

            Registry.CurrentUser.DeleteSubKeyTree(
                StoreSearchBackupPath,
                throwOnMissingSubKey: false);
            return Task.FromResult(new ToolActionResult(
                true,
                "The captured Store access permissions were restored and verified. Ownership and audit rules were preserved."));
        }

        private static string GetStoreDatabasePath()
        {
            string localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            string path = Path.Combine(
                localAppData,
                "Packages",
                "Microsoft.WindowsStore_8wekyb3d8bbwe",
                "LocalState",
                "store.db");
            if (!CatalogAvailability.FileIsPresent(path))
            {
                throw new FileNotFoundException(
                    "Microsoft Store database was not found.",
                    path);
            }
            return Path.GetFullPath(path);
        }

        private static bool HasEveryoneFullControlDeny(FileSecurity security) =>
            security.GetAccessRules(
                    includeExplicit: true,
                    includeInherited: true,
                    targetType: typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Any(rule =>
                    rule.IdentityReference is SecurityIdentifier sid &&
                    sid.IsWellKnown(WellKnownSidType.WorldSid) &&
                    rule.AccessControlType == AccessControlType.Deny &&
                    (rule.FileSystemRights & FileSystemRights.FullControl) != 0);

        private async Task<ToolActionResult> CleanTemporaryFilesAsync()
        {
            string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string[] requestedRoots =
            {
                Path.GetTempPath(),
                Path.Combine(windowsDirectory, "Temp")
            };
            string[] roots = requestedRoots
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .ToArray();

            TempMetrics before = await Task.Run(() => MeasureRoots(roots));
            List<string> errors = new();
            foreach (string root in roots)
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(root))
                {
                    TryDeleteEntry(entry, errors);
                }
            }
            TempMetrics after = await Task.Run(() => MeasureRoots(roots));

            long removedFiles = Math.Max(0, before.Files - after.Files);
            long removedFolders = Math.Max(0, before.Folders - after.Folders);
            long removedBytes = Math.Max(0, before.Bytes - after.Bytes);
            string warning = errors.Count == 0
                ? string.Empty
                : $" {errors.Count} locked/in-use item(s) could not be removed.";
            return new ToolActionResult(
                true,
                $"Removed {removedFiles:N0} file(s), {removedFolders:N0} folder(s), and {FormatBytes(removedBytes)}.{warning}");
        }

        private async Task<ToolActionResult> ApplyNtfsPerformanceAsync()
        {
            CaptureNtfsSnapshot();
            string fsutil = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "fsutil.exe");
            NativeCommandResult lastAccess = await _commandRunner.RunAsync(
                fsutil,
                new[] { "behavior", "set", "disablelastaccess", "1" },
                TimeSpan.FromSeconds(20));
            NativeCommandResult shortNames = await _commandRunner.RunAsync(
                fsutil,
                new[] { "behavior", "set", "disable8dot3", "1" },
                TimeSpan.FromSeconds(20));

            var state = ReadNtfsPerformanceState();
            bool verified = NtfsPerformanceVerification.ApplySucceeded(lastAccess, shortNames, state);
            return new ToolActionResult(
                verified,
                verified
                    ? "NTFS configuration applied and verified. " + state.ActualValue + ". Restart Windows for all changes to take effect."
                    : "NTFS options did not pass read-back verification. The original snapshot is retained for Restore." +
                        Environment.NewLine + state.ActualValue +
                        Environment.NewLine + $"disablelastaccess: exit={lastAccess.ExitCode}; timedOut={lastAccess.TimedOut}. {lastAccess.CombinedOutput}" +
                        Environment.NewLine + $"disable8dot3: exit={shortNames.ExitCode}; timedOut={shortNames.TimedOut}. {shortNames.CombinedOutput}");
        }

        private Task<ToolActionResult> RestoreNtfsPerformanceAsync()
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                NtfsBackupPath,
                writable: false);
            if (backup?.GetValue("Captured") is null)
            {
                throw new InvalidOperationException(
                    "The original NTFS/FileSystem state has not been captured yet. Run Apply first.");
            }

            var savedValues = EssentialSnapshotValidation.ReadNtfs(
                name => backup.GetValue(name), backup.GetValueKind, NtfsValueNames);
            using RegistryKey fileSystem = CreateLocalMachineKey(FileSystemPath);
            foreach ((string name, EssentialSavedValue saved) in savedValues)
            {
                if (!saved.Exists)
                {
                    fileSystem.DeleteValue(name, throwOnMissingValue: false);
                    continue;
                }

                fileSystem.SetValue(name, saved.Value!, saved.Kind);
            }

            bool verified = NtfsValueNames.All(name =>
            {
                object? actual = fileSystem.GetValue(
                    name,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                return savedValues[name].Matches(actual, actual is null ? null : fileSystem.GetValueKind(name));
            });
            if (verified)
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    NtfsBackupPath,
                    throwOnMissingSubKey: false);
            }
            return Task.FromResult(new ToolActionResult(
                verified,
                verified
                    ? "The exact captured NTFS/FileSystem state was restored and verified."
                    : "The NTFS/FileSystem restore did not pass exact read-back verification."));
        }

        private void CaptureNtfsSnapshot()
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey(
                NtfsBackupPath,
                writable: true);
            if (EssentialSnapshotValidation.HasSnapshot(name => backup.GetValue(name)))
            {
                _ = EssentialSnapshotValidation.ReadNtfs(name => backup.GetValue(name), backup.GetValueKind, NtfsValueNames);
                return;
            }

            using RegistryKey? fileSystem = OpenLocalMachineKey(
                FileSystemPath,
                writable: false);
            foreach (string name in NtfsValueNames)
            {
                object? value = fileSystem?.GetValue(
                    name,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                backup.SetValue(
                    name + ".Exists",
                    value is null ? 0 : 1,
                    RegistryValueKind.DWord);
                if (value is not null && fileSystem is not null)
                {
                    if (value is not int || fileSystem.GetValueKind(name) != RegistryValueKind.DWord)
                        throw new InvalidDataException("The original NTFS/FileSystem registry type is unsupported. Apply was not started.");
                    backup.SetValue(
                        name + ".Value",
                        value,
                        fileSystem.GetValueKind(name));
                }
            }
            backup.Flush();
            backup.SetValue("Captured", 1, RegistryValueKind.DWord);
            backup.Flush();
        }

        private async Task<ToolActionResult> ApplyStoragePowerLatencyAsync()
        {
            string scheme = await GetActivePowerSchemeGuidAsync();
            StoragePowerState before = await ReadStoragePowerStateAsync(scheme);
            if (before.Ahci.Ac is null ||
                before.Ahci.Dc is null ||
                before.NvmePrimary.Ac is null ||
                before.NvmeSecondary.Ac is null)
            {
                throw new InvalidOperationException(
                    "The current power-plan storage values could not be captured reliably. No change was made.");
            }
            CaptureStoragePowerSnapshot(before);

            await SetPowerValueAsync(scheme, StorageAhciLpmGuid, acValue: 0, dcValue: 0);
            await SetPowerValueAsync(scheme, StorageNvmePrimaryIdleGuid, acValue: 0, dcValue: null);
            await SetPowerValueAsync(scheme, StorageNvmeSecondaryIdleGuid, acValue: 0, dcValue: null);
            await RunPowerCfgRequiredAsync("/setactive", scheme);

            StoragePowerState after = await ReadStoragePowerStateAsync(scheme);
            bool verified = after.Ahci.Ac == 0 &&
                after.Ahci.Dc == 0 &&
                after.NvmePrimary.Ac == 0 &&
                after.NvmeSecondary.Ac == 0 &&
                after.NvmePrimary.Dc == before.NvmePrimary.Dc &&
                after.NvmeSecondary.Dc == before.NvmeSecondary.Dc;
            return new ToolActionResult(
                verified,
                verified
                    ? "SSD/NVMe power settings applied and verified. AHCI AC/DC=0; primary and secondary NVMe AC timeout=0 ms; DC timeout values were unchanged."
                    : "SSD/NVMe settings did not pass exact read-back verification. The original snapshot is retained for Restore.");
        }

        private async Task<ToolActionResult> RestoreStoragePowerLatencyAsync()
        {
            StoragePowerState expected = ReadStoragePowerSnapshot();
            await SetPowerValueAsync(
                expected.SchemeGuid,
                StorageAhciLpmGuid,
                expected.Ahci.Ac,
                expected.Ahci.Dc);
            await SetPowerValueAsync(
                expected.SchemeGuid,
                StorageNvmePrimaryIdleGuid,
                expected.NvmePrimary.Ac,
                expected.NvmePrimary.Dc);
            await SetPowerValueAsync(
                expected.SchemeGuid,
                StorageNvmeSecondaryIdleGuid,
                expected.NvmeSecondary.Ac,
                expected.NvmeSecondary.Dc);

            string activeScheme = await GetActivePowerSchemeGuidAsync();
            if (string.Equals(activeScheme, expected.SchemeGuid, StringComparison.OrdinalIgnoreCase))
            {
                await RunPowerCfgRequiredAsync("/setactive", expected.SchemeGuid);
            }

            StoragePowerState after = await ReadStoragePowerStateAsync(expected.SchemeGuid);
            bool verified = after.Ahci == expected.Ahci &&
                after.NvmePrimary == expected.NvmePrimary &&
                after.NvmeSecondary == expected.NvmeSecondary;
            if (verified)
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    StoragePowerBackupPath,
                    throwOnMissingSubKey: false);
            }
            return new ToolActionResult(
                verified,
                verified
                    ? "The exact captured SSD/NVMe power settings were restored and verified."
                    : "SSD/NVMe restore did not pass exact read-back verification.");
        }

        private async Task<StoragePowerState> ReadStoragePowerStateAsync(string scheme)
        {
            return new StoragePowerState(
                scheme,
                await ReadPowerPairAsync(scheme, StorageAhciLpmGuid),
                await ReadPowerPairAsync(scheme, StorageNvmePrimaryIdleGuid),
                await ReadPowerPairAsync(scheme, StorageNvmeSecondaryIdleGuid));
        }

        private Task<PowerPair> ReadPowerPairAsync(string scheme, string setting)
        {
            PowerPolicyPair pair = NativePowerPolicyReader.ReadRequiredPairInSubgroup(scheme, StorageDiskSubgroupGuid, setting);
            return Task.FromResult(new PowerPair(pair.Ac, pair.Dc));
        }

        private async Task SetPowerValueAsync(
            string scheme,
            string setting,
            uint? acValue,
            uint? dcValue)
        {
            if (acValue.HasValue)
            {
                await RunPowerCfgRequiredAsync(
                    "/setacvalueindex",
                    scheme,
                    StorageDiskSubgroupGuid,
                    setting,
                    acValue.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (dcValue.HasValue)
            {
                await RunPowerCfgRequiredAsync(
                    "/setdcvalueindex",
                    scheme,
                    StorageDiskSubgroupGuid,
                    setting,
                    dcValue.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        private async Task<string> GetActivePowerSchemeGuidAsync()
        {
            NativeCommandResult result = await RunPowerCfgAsync("/getactivescheme");
            Match match = Regex.Match(
                result.StandardOutput,
                @"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b");
            if (result.ExitCode != 0 || !match.Success)
            {
                throw new InvalidOperationException(
                    "Unable to determine the active Windows power scheme.");
            }
            return match.Value.ToLowerInvariant();
        }

        private async Task RunPowerCfgRequiredAsync(params string[] arguments)
        {
            NativeCommandResult result = await RunPowerCfgAsync(arguments);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.CombinedOutput)
                        ? $"powercfg exited with code {result.ExitCode}."
                        : result.CombinedOutput);
            }
        }

        private Task<NativeCommandResult> RunPowerCfgAsync(params string[] arguments)
        {
            string executable = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "powercfg.exe");
            return _commandRunner.RunAsync(
                executable,
                arguments,
                TimeSpan.FromSeconds(20));
        }

        private static void CaptureStoragePowerSnapshot(StoragePowerState state)
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey(
                StoragePowerBackupPath,
                writable: true);
            if (EssentialSnapshotValidation.HasSnapshot(name => backup.GetValue(name)))
            {
                _ = ReadStoragePowerSnapshot();
                return;
            }
            backup.SetValue("SchemeGuid", state.SchemeGuid, RegistryValueKind.String);
            WritePowerPair(backup, "Ahci", state.Ahci);
            WritePowerPair(backup, "NvmePrimary", state.NvmePrimary);
            WritePowerPair(backup, "NvmeSecondary", state.NvmeSecondary);
            backup.Flush();
            backup.SetValue("Captured", 1, RegistryValueKind.DWord);
            backup.Flush();
        }

        private static StoragePowerState ReadStoragePowerSnapshot()
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                StoragePowerBackupPath,
                writable: false);
            if (!EssentialSnapshotValidation.HasSnapshot(name => backup?.GetValue(name)))
            {
                throw new InvalidOperationException(
                    "The original SSD/NVMe power-setting snapshot does not exist. Run Apply first.");
            }
            string scheme = Convert.ToString(
                backup!.GetValue("SchemeGuid"),
                CultureInfo.InvariantCulture) ?? string.Empty;
            if (!Guid.TryParse(scheme, out _))
            {
                throw new InvalidOperationException("The saved power-scheme snapshot is invalid.");
            }
            return new StoragePowerState(
                scheme,
                ReadPowerPair(backup, "Ahci"),
                ReadPowerPair(backup, "NvmePrimary"),
                ReadPowerPair(backup, "NvmeSecondary"));
        }

        private static void WritePowerPair(RegistryKey key, string name, PowerPair value)
        {
            key.SetValue(name + ".AC", value.Ac.HasValue ? (long)value.Ac.Value : -1L, RegistryValueKind.QWord);
            key.SetValue(name + ".DC", value.Dc.HasValue ? (long)value.Dc.Value : -1L, RegistryValueKind.QWord);
        }

        private static PowerPair ReadPowerPair(RegistryKey key, string name)
        {
            return new PowerPair(
                EssentialSnapshotValidation.ReadPowerIndex(key.GetValue(name + ".AC")),
                EssentialSnapshotValidation.ReadPowerIndex(key.GetValue(name + ".DC")));
        }

        private static uint? ReadLocalMachineUInt32(string path, string name)
        {
            try
            {
                using RegistryKey? key = OpenLocalMachineKey(path, writable: false);
                object? value = key?.GetValue(name);
                return value is null
                    ? null
                    : Convert.ToUInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static NtfsPerformanceState ReadNtfsPerformanceState()
        {
            using RegistryKey? key = OpenLocalMachineKey(FileSystemPath, writable: false);
            object? lastAccess = key?.GetValue("NtfsDisableLastAccessUpdate");
            object? shortNames = key?.GetValue("NtfsDisable8dot3NameCreation");
            return new(lastAccess, lastAccess is null ? null : key!.GetValueKind("NtfsDisableLastAccessUpdate"),
                shortNames, shortNames is null ? null : key!.GetValueKind("NtfsDisable8dot3NameCreation"));
        }

        private static bool RegistryValuesEqual(object? expected, object? actual)
        {
            if (expected is string[] expectedStrings && actual is string[] actualStrings)
            {
                return expectedStrings.SequenceEqual(actualStrings, StringComparer.Ordinal);
            }
            if (expected is byte[] expectedBytes && actual is byte[] actualBytes)
            {
                return expectedBytes.SequenceEqual(actualBytes);
            }
            return Equals(expected, actual) ||
                string.Equals(
                    Convert.ToString(expected, CultureInfo.InvariantCulture),
                    Convert.ToString(actual, CultureInfo.InvariantCulture),
                    StringComparison.Ordinal);
        }

        private static ToolActionResult OpenWindowsSetting(
            string target,
            string displayName,
            string? fallback = null)
        {
            foreach (string candidate in new[] { target, fallback }
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Cast<string>())
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = candidate,
                        UseShellExecute = true
                    });
                    return new ToolActionResult(true, $"Opened: {displayName}");
                }
                catch
                {
                    // Try the original fallback target when one is available.
                }
            }
            return new ToolActionResult(false, $"Unable to open {displayName}.");
        }

        private async Task RestartExplorerAsync()
        {
            await _commandRunner.RunAsync(
                "taskkill.exe",
                new[] { "/F", "/IM", "explorer.exe" },
                TimeSpan.FromSeconds(15));
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            });
        }

        private static TempMetrics MeasureRoots(IEnumerable<string> roots)
        {
            long files = 0;
            long folders = 0;
            long bytes = 0;
            EnumerationOptions options = new()
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false
            };
            foreach (string root in roots)
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(root, "*", options))
                    {
                        files++;
                        try { bytes += new FileInfo(file).Length; } catch { }
                    }
                    folders += Directory.EnumerateDirectories(root, "*", options).LongCount();
                }
                catch
                {
                    // Locked paths are counted as undeletable rather than failing the whole action.
                }
            }
            return new TempMetrics(files, folders, bytes);
        }

        private static void TryDeleteEntry(string path, List<string> errors)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
                bool isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
                if (!isDirectory)
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    return;
                }

                if (!isReparsePoint)
                {
                    foreach (string child in Directory.EnumerateFileSystemEntries(path))
                    {
                        TryDeleteEntry(child, errors);
                    }
                }
                Directory.Delete(path, recursive: false);
            }
            catch (Exception exception)
            {
                if (errors.Count < 50)
                {
                    errors.Add($"{path}: {exception.Message}");
                }
            }
        }

        private static RegistryKey? OpenLocalMachineKey(string path, bool writable)
        {
            RegistryView view = Environment.Is64BitOperatingSystem
                ? RegistryView.Registry64
                : RegistryView.Default;
            RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            RegistryKey? key = baseKey.OpenSubKey(path, writable);
            baseKey.Dispose();
            return key;
        }

        private static RegistryKey CreateLocalMachineKey(string path)
        {
            RegistryView view = Environment.Is64BitOperatingSystem
                ? RegistryView.Registry64
                : RegistryView.Default;
            RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            RegistryKey key = baseKey.CreateSubKey(path, writable: true);
            baseKey.Dispose();
            return key;
        }

        private static string FormatBytes(long value)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = value;
            int unit = 0;
            while (size >= 1024d && unit < units.Length - 1)
            {
                size /= 1024d;
                unit++;
            }
            return $"{size:N2} {units[unit]}";
        }

        private readonly record struct TempMetrics(long Files, long Folders, long Bytes);
        private readonly record struct PowerPair(uint? Ac, uint? Dc);
        private readonly record struct StoragePowerState(
            string SchemeGuid,
            PowerPair Ahci,
            PowerPair NvmePrimary,
            PowerPair NvmeSecondary);
    }
}
