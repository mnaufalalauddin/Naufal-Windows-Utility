using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal sealed class EssentialTweaksService : IToolToggleService, ICatalogEffectSource
    {
        private const string BackupRoot =
            @"Software\Naufal Windows Tech\Powertoys\Backups\Essential";

        private const string EndTaskPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings";
        private const string ClassicContextPath =
            @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32";
        private const string ClassicContextParentPath =
            @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
        private const string ConsumerFeaturesPath =
            @"SOFTWARE\Policies\Microsoft\Windows\CloudContent";
        private const string DeliveryOptimizationPath =
            @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";
        private const string PowerPath =
            @"System\CurrentControlSet\Control\Session Manager\Power";
        private const string HibernateMenuPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FlyoutMenuSettings";
        private const string LocationConsentPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location";
        private const string LocationSensorPath =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Sensor\Overrides\{BFA794E4-F964-4FDB-90F6-51056BFE4B44}";
        private const string MapsPath = @"SYSTEM\Maps";
        private const string ExplorerAdvancedPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string ExplorerHomePath =
            @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";
        private const string ExplorerGalleryPath =
            @"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}";
        private const string MachineEnvironmentPath =
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
        private const string ServiceControlPath =
            @"SYSTEM\CurrentControlSet\Control";
        private static PhotoViewerEntry[] PhotoViewerPlan() =>
            PhotoViewerRegistration.CreatePlan(PhotoViewerRegistration.ViewerDll, Environment.SystemDirectory);

        private static readonly RegistryTarget[] TelemetryTargets =
        {
            new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),
            new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
            new(RegistryHive.CurrentUser, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0),
            new(RegistryHive.CurrentUser, @"Software\Microsoft\Input\TIPC", "Enabled", 0),
            new(RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1),
            new(RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1),
            new(RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0),
            new(RegistryHive.CurrentUser, @"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0),
            new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0),
            new(RegistryHive.CurrentUser, ExplorerAdvancedPath, "Start_TrackProgs", 0),
            new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
            new(RegistryHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0)
        };

        private static readonly ServiceTarget[] ManualServiceTargets =
        {
            new("CscService", 4, "disabled"),
            new("DiagTrack", 4, "disabled"),
            new("MapsBroker", 3, "demand"),
            new("StorSvc", 3, "demand"),
            new("SharedAccess", 4, "disabled")
        };

        private readonly NativeCommandRunner _commandRunner = new();

        private static readonly IReadOnlyList<ToolToggleDefinition> Definitions =
            new[]
            {
                new ToolToggleDefinition(
                    "SysMain",
                    "Services",
                    "SysMain",
                    "Controls the SysMain service that learns application usage and maintains prefetch data. ON configures Automatic startup and starts the service; OFF disables and stops it, which can increase application or boot load times on some systems.",
                    true,
                    false),
                new ToolToggleDefinition(
                    "EndTask",
                    "Explorer",
                    "Show End Task on Taskbar",
                    "Adds End task to the taskbar application's right-click jump list on supported Windows builds. It provides a direct process-termination command and does not change Task Manager or automatically close applications.",
                    false,
                    true),
                new ToolToggleDefinition(
                    "ClassicContext",
                    "Explorer",
                    "Use Classic Context Menu",
                    "Makes File Explorer open the full classic context menu directly instead of the compact Windows 11 menu. The underlying shell extensions remain installed and Explorer may need to restart before the visual change appears.",
                    false,
                    true),
                new ToolToggleDefinition(
                    "ConsumerFeatures",
                    "Recommendations",
                    "Disable Consumer Features",
                    "Disables promoted app installs and reduces Microsoft Store content suggestions.",
                    true,
                    true),
                new ToolToggleDefinition(
                    "DeliveryOptimization",
                    "Windows Update",
                    "Disable Delivery Optimization P2P Sharing",
                    "Sets DODownloadMode=0 (HTTP only) while keeping the DoSvc service available.",
                    true,
                    false),
                new ToolToggleDefinition(
                    "Hibernation",
                    "Power",
                    "Disable Hibernation",
                    "Runs the Windows power configuration needed to disable hibernation, remove Hibernate from the power menu, and release hiberfil.sys storage. Fast Startup also depends on hibernation and will be unavailable while this option is active.",
                    true,
                    true),
                new ToolToggleDefinition(
                    "Location",
                    "Privacy",
                    "Disable Location Tracking",
                    "Disables Windows location access, sensor permission, map updates, and the location service.",
                    true,
                    true),
                new ToolToggleDefinition(
                    "Telemetry",
                    "Privacy / HIGH IMPACT",
                    "Disable Microsoft Telemetry",
                    "Applies the telemetry registry, service, and PowerShell telemetry baseline.",
                    true,
                    true),
                new ToolToggleDefinition(
                    "ServicesManual",
                    "Services",
                    "Services - Set to Manual",
                    "Applies the defined startup policy to Offline Files, telemetry, downloaded maps, Storage Service, and Internet Connection Sharing, then sets the service-host split threshold from installed memory. Existing service configuration is captured and every target is read back.",
                    true,
                    true),
                new ToolToggleDefinition(
                    "PhotoViewer",
                    "Apps",
                    "Legacy Windows Photo Viewer",
                    "Registers the classic Windows Photo Viewer commands and capabilities for 13 common image extensions, then opens Default Apps for user confirmation. Windows protects per-extension UserChoice values, so this tool does not force an association silently.",
                    true,
                    false),
                new ToolToggleDefinition(
                    "Widgets",
                    "Apps",
                    "Widgets - Remove",
                    "Removes Windows Web Experience/Widgets packages. OFF reinstalls from Microsoft Store through WinGet and verifies package registration.",
                    true,
                    true),
                new ToolToggleDefinition(
                    "ExplorerHomeGallery",
                    "Explorer",
                    "Disable File Explorer Home and Gallery",
                    "Sets This PC as the default location and hides Home and Gallery from the navigation tree.",
                    false,
                    true)
            };

        public string DescribeRecovery(ToolToggleDefinition definition) =>
            definition.Id == "Widgets" ? "WinGet reinstalls Windows Web Experience Pack from Microsoft Store and checks package registration. Removed app data is outside this recovery." :
            CatalogRecovery.Snapshot($@"{BackupRoot}\{definition.Id}", definition.Id is "SysMain" or "DeliveryOptimization" or "ConsumerFeatures"
                    ? "No current snapshot found. A documented Windows default is available."
                    : definition.Id == "Telemetry" ? "Restore checks the shared privacy snapshots for the original values."
                    : "No current snapshot found. This option needs the original backup.");

        public IReadOnlyList<ToolToggleDefinition> GetDefinitions() => Definitions;

        public IReadOnlyList<CatalogEffect> GetEffects(ToolToggleDefinition definition) => definition.Id switch
        {
            "Telemetry" => TelemetryTargets.Select(t => SharedPrivacySnapshot.Describe("Telemetry",
                CatalogEffect.Registry(t.Hive.ToString(), t.Path, t.Name, "DWord", t.AppliedValue))).ToArray(),
            "ServicesManual" => ManualServiceTargets.Select(t => CatalogEffect.Registry("HKLM", @"SYSTEM\CurrentControlSet\Services\" + t.Name, "Start", "DWord", t.StartValue))
                .Append(CatalogEffect.Registry("HKLM", ServiceControlPath, "SvcHostSplitThresholdInKB", "DynamicDWord", "installed physical memory in KiB")).ToArray(),
            "Hibernation" => new[] { CatalogEffect.Registry("HKLM", PowerPath, "HibernateEnabled", "DWord", 0), new CatalogEffect("Power/Hibernation", "Disabled") },
            "EndTask" => new[] { CatalogEffect.Registry("HKCU", EndTaskPath, "TaskbarEndTask", "DWord", 1) },
            "ConsumerFeatures" => new[] { CatalogEffect.Registry("HKLM", ConsumerFeaturesPath, "DisableWindowsConsumerFeatures", "DWord", 1) },
            "DeliveryOptimization" => new[] { CatalogEffect.Registry("HKLM", DeliveryOptimizationPath, "DODownloadMode", "DWord", 0) },
            _ => Array.Empty<CatalogEffect>()
        };

        public async Task<ToolToggleState> ReadStateAsync(ToolToggleDefinition definition)
        {
            try
            {
                return definition.Id switch
                {
                    "SysMain" => await ReadSysMainStateAsync(),
                    "EndTask" => ReadDwordAppliedState(
                        RegistryHive.CurrentUser,
                        EndTaskPath,
                        "TaskbarEndTask",
                        1),
                    "ClassicContext" => ReadClassicContextState(),
                    "ConsumerFeatures" => ReadDwordAppliedState(
                        RegistryHive.LocalMachine,
                        ConsumerFeaturesPath,
                        "DisableWindowsConsumerFeatures",
                        1),
                    "DeliveryOptimization" => ReadDwordAppliedState(
                        RegistryHive.LocalMachine,
                        DeliveryOptimizationPath,
                        "DODownloadMode",
                        0),
                    "Hibernation" => ReadDwordAppliedState(
                        RegistryHive.LocalMachine,
                        PowerPath,
                        "HibernateEnabled",
                        0),
                    "Location" => ReadLocationState(),
                    "Telemetry" => await ReadTelemetryStateAsync(),
                    "ServicesManual" => ReadServicesManualState(),
                    "PhotoViewer" => ReadPhotoViewerState(),
                    "Widgets" => await ReadWidgetsStateAsync(),
                    "ExplorerHomeGallery" => ReadExplorerHomeGalleryState(),
                    _ => new ToolToggleState(
                        false,
                        false,
                        "Unknown definition",
                        $"Unsupported essential tweak: {definition.Id}")
                };
            }
            catch (Exception exception)
            {
                return new ToolToggleState(false, false, "Unable to read", exception.Message);
            }
        }

        public async Task<ToolToggleOperationResult> SetStateAsync(
            ToolToggleDefinition definition,
            bool targetOn)
        {
            if (definition.RequiresAdministrator &&
                !WindowsPrivilegeService.IsAdministrator())
            {
                ToolToggleState denied = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(
                    false,
                    false,
                    "Administrator rights are required. Run the app or Visual Studio as Administrator.",
                    denied);
            }

            try
            {
                ToolToggleState before = await ReadStateAsync(definition);
                if (!before.IsAvailable)
                    return new ToolToggleOperationResult(false, false,
                        $"Cannot apply or restore {definition.Name}: current state is unavailable. {before.Error}", before);
                if (before.IsAvailable && before.IsOn == targetOn)
                {
                    if (definition.Id == "PhotoViewer" && targetOn) OpenPhotoViewerDefaultApps();
                    return new ToolToggleOperationResult(
                        true,
                        true,
                        $"{definition.Name} is already {(targetOn ? "ON" : "OFF")}." +
                        (definition.Id == "PhotoViewer" && targetOn ? Environment.NewLine + before.ActualValue : ""),
                        before);
                }

                if (targetOn)
                {
                    await ApplyAsync(definition.Id);
                }
                else
                {
                    await RestoreAsync(definition.Id);
                }

                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable && after.IsOn == targetOn;
                if (!targetOn && verified) DeleteBackup(definition.Id);
                if (verified && targetOn && definition.Id == "PhotoViewer")
                {
                    OpenPhotoViewerDefaultApps();
                }
                string restartNote = verified && definition.RestartRecommended
                    ? Environment.NewLine + "Restart Explorer, sign out, or reboot to make the UI change visible."
                    : string.Empty;
                if (verified && targetOn && definition.Id == "PhotoViewer")
                    restartNote += Environment.NewLine + after.ActualValue;
                return new ToolToggleOperationResult(
                    verified,
                    verified,
                    verified
                        ? $"{definition.Name} is now {(targetOn ? "ON" : "OFF")}.{restartNote}"
                        : $"Verification did not match the requested state. Actual: {after.ActualValue}",
                    after);
            }
            catch (Exception exception)
            {
                ToolToggleState state = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(false, false, exception.Message, state);
            }
        }

        public async Task<ToolToggleOperationResult> RestoreOriginalAsync(
            ToolToggleDefinition definition)
        {
            if (definition.RequiresAdministrator &&
                !WindowsPrivilegeService.IsAdministrator())
            {
                ToolToggleState denied = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(
                    false,
                    false,
                    "Administrator rights are required.",
                    denied);
            }

            try
            {
                if (definition.Id != "Widgets")
                {
                    if (definition.Id == "Telemetry")
                        DebloatService.PrepareSharedSnapshots(definition.Id, SharedSnapshotPreparation.Restore);
                    using RegistryKey? snapshot = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{definition.Id}", writable: false);
                    if (snapshot is null)
                    {
                        if (definition.Id is not ("SysMain" or "ConsumerFeatures" or "DeliveryOptimization"))
                            throw new InvalidOperationException("No original backup is available, and no verified Windows default is registered for this option. No values were changed.");
                        ToolToggleOperationResult fallback = await RestoreWindowsDefaultAsync(definition);
                        return fallback with { Message = "No original backup was found. " + fallback.Message, DefaultFallbackHandled = true };
                    }
                    EnsureBackupExists(definition.Id);
                }
                await RestoreAsync(definition.Id);
                ToolToggleState after = await ReadStateAsync(definition);
                if (after.IsAvailable) DeleteBackup(definition.Id);
                return new ToolToggleOperationResult(
                    after.IsAvailable,
                    after.IsAvailable,
                    after.IsAvailable
                        ? $"{definition.Name} original state was restored. Actual: {after.ActualValue}"
                        : after.Error,
                    after);
            }
            catch (Exception exception)
            {
                ToolToggleState state = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(false, false, exception.Message, state);
            }
        }

        public async Task<ToolToggleOperationResult> RestoreWindowsDefaultAsync(
            ToolToggleDefinition definition)
        {
            if (definition.RequiresAdministrator &&
                !WindowsPrivilegeService.IsAdministrator())
            {
                ToolToggleState denied = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(
                    false,
                    false,
                    "Administrator rights are required.",
                    denied);
            }

            try
            {
                switch (definition.Id)
                {
                    case "SysMain":
                        await ConfigureServiceAsync("SysMain", "auto");
                        await RunServiceCommandAsync("start", "SysMain", tolerateAlreadyState: true);
                        break;
                    case "EndTask":
                        DeleteRegistryValue(
                            RegistryHive.CurrentUser,
                            EndTaskPath,
                            "TaskbarEndTask");
                        break;
                    case "ClassicContext":
                        DeleteRegistryValue(RegistryHive.CurrentUser, ClassicContextPath, string.Empty);
                        RemoveEmptyClassicContextKey();
                        break;
                    case "ConsumerFeatures":
                        DeleteRegistryValue(
                            RegistryHive.LocalMachine,
                            ConsumerFeaturesPath,
                            "DisableWindowsConsumerFeatures");
                        break;
                    case "DeliveryOptimization":
                        DeleteRegistryValue(
                            RegistryHive.LocalMachine,
                            DeliveryOptimizationPath,
                            "DODownloadMode");
                        break;
                    case "Hibernation":
                        await RequireCommandSuccessAsync(
                            "powercfg.exe",
                            new[] { "/hibernate", "on" });
                        SetDword(RegistryHive.LocalMachine, PowerPath, "HibernateEnabled", 1);
                        SetDword(RegistryHive.LocalMachine, HibernateMenuPath, "ShowHibernateOption", 1);
                        break;
                    case "Location":
                        await ConfigureServiceAsync("lfsvc", "demand");
                        SetString(RegistryHive.LocalMachine, LocationConsentPath, "Value", "Allow");
                        SetDword(RegistryHive.LocalMachine, LocationSensorPath, "SensorPermissionState", 1);
                        SetDword(RegistryHive.LocalMachine, MapsPath, "AutoUpdateEnabled", 1);
                        break;
                    case "Telemetry":
                        DebloatService.PrepareSharedSnapshots("Telemetry", SharedSnapshotPreparation.Validate);
                        foreach (RegistryTarget target in TelemetryTargets)
                        {
                            DeleteRegistryValue(target.Hive, target.Path, target.Name);
                        }
                        DeleteRegistryValue(
                            RegistryHive.CurrentUser,
                            @"Software\Microsoft\Siuf\Rules",
                            "PeriodInNanoSeconds");
                        DeleteRegistryValue(
                            RegistryHive.LocalMachine,
                            MachineEnvironmentPath,
                            "POWERSHELL_TELEMETRY_OPTOUT");
                        await ConfigureServiceAsync("DiagTrack", "auto");
                        await ConfigureServiceAsync("WerSvc", "demand");
                        break;
                    case "ServicesManual":
                        foreach ((string name, string mode) in new[]
                                 {
                                     ("CscService", "demand"),
                                     ("DiagTrack", "auto"),
                                     ("MapsBroker", "auto"),
                                     ("StorSvc", "auto"),
                                     ("SharedAccess", "auto")
                                 })
                        {
                            await ConfigureServiceAsync(name, mode);
                        }
                        DeleteRegistryValue(
                            RegistryHive.LocalMachine,
                            ServiceControlPath,
                            "SvcHostSplitThresholdInKB");
                        break;
                    case "PhotoViewer":
                        // Windows registrations vary by installation. Restore
                        // the saved registration; never manufacture a default
                        // or clear a marker while leaving all changes applied.
                        return await RestoreOriginalAsync(definition);
                    case "Widgets":
                        await ReinstallWidgetsAsync();
                        break;
                    case "ExplorerHomeGallery":
                        DeleteRegistryValue(RegistryHive.CurrentUser, ExplorerAdvancedPath, "LaunchTo");
                        DeleteRegistryValue(RegistryHive.CurrentUser, ExplorerHomePath, "System.IsPinnedToNameSpaceTree");
                        DeleteRegistryValue(RegistryHive.CurrentUser, ExplorerGalleryPath, "System.IsPinnedToNameSpaceTree");
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported essential tweak: {definition.Id}");
                }

                ToolToggleState after = await ReadStateAsync(definition);
                bool expectedOn = definition.Id == "SysMain";
                bool verified = after.IsAvailable && after.IsOn == expectedOn;
                if (verified)
                {
                    DeleteBackup(definition.Id);
                }
                return new ToolToggleOperationResult(
                    verified,
                    verified,
                    verified
                        ? $"{definition.Name} was returned to the Windows default."
                        : $"Windows-default read-back failed. Actual: {after.ActualValue}",
                    after);
            }
            catch (Exception exception)
            {
                ToolToggleState state = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(false, false, exception.Message, state);
            }
        }

        private async Task ApplyAsync(string id)
        {
            switch (id)
            {
                case "SysMain":
                    CaptureRegistryValue(id, "Start", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\SysMain", "Start");
                    CaptureBoolean(id, "WasRunning", await IsServiceRunningAsync("SysMain"));
                    await ConfigureServiceAsync("SysMain", "auto");
                    await RunServiceCommandAsync("start", "SysMain", tolerateAlreadyState: true);
                    break;

                case "EndTask":
                    CaptureRegistryValue(id, "TaskbarEndTask", RegistryHive.CurrentUser, EndTaskPath, "TaskbarEndTask");
                    SetDword(RegistryHive.CurrentUser, EndTaskPath, "TaskbarEndTask", 1);
                    break;

                case "ClassicContext":
                    CaptureRegistryValue(id, "Default", RegistryHive.CurrentUser, ClassicContextPath, string.Empty);
                    SetString(RegistryHive.CurrentUser, ClassicContextPath, string.Empty, string.Empty);
                    break;

                case "ConsumerFeatures":
                    CaptureRegistryValue(id, "DisableWindowsConsumerFeatures", RegistryHive.LocalMachine, ConsumerFeaturesPath, "DisableWindowsConsumerFeatures");
                    SetDword(RegistryHive.LocalMachine, ConsumerFeaturesPath, "DisableWindowsConsumerFeatures", 1);
                    break;

                case "DeliveryOptimization":
                    CaptureRegistryValue(id, "DODownloadMode", RegistryHive.LocalMachine, DeliveryOptimizationPath, "DODownloadMode");
                    SetDword(RegistryHive.LocalMachine, DeliveryOptimizationPath, "DODownloadMode", 0);
                    break;

                case "Hibernation":
                    CaptureRegistryValue(id, "HibernateEnabled", RegistryHive.LocalMachine, PowerPath, "HibernateEnabled");
                    CaptureRegistryValue(id, "ShowHibernateOption", RegistryHive.LocalMachine, HibernateMenuPath, "ShowHibernateOption");
                    await RequireCommandSuccessAsync("powercfg.exe", new[] { "/hibernate", "off" });
                    SetDword(RegistryHive.LocalMachine, PowerPath, "HibernateEnabled", 0);
                    SetDword(RegistryHive.LocalMachine, HibernateMenuPath, "ShowHibernateOption", 0);
                    break;

                case "Location":
                    CaptureRegistryValue(id, "ServiceStart", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\lfsvc", "Start");
                    CaptureBoolean(id, "ServiceWasRunning", await IsServiceRunningAsync("lfsvc"));
                    CaptureRegistryValue(id, "Consent", RegistryHive.LocalMachine, LocationConsentPath, "Value");
                    CaptureRegistryValue(id, "Sensor", RegistryHive.LocalMachine, LocationSensorPath, "SensorPermissionState");
                    CaptureRegistryValue(id, "Maps", RegistryHive.LocalMachine, MapsPath, "AutoUpdateEnabled");
                    await RunServiceCommandAsync("stop", "lfsvc", tolerateAlreadyState: true);
                    await ConfigureServiceAsync("lfsvc", "disabled");
                    SetString(RegistryHive.LocalMachine, LocationConsentPath, "Value", "Deny");
                    SetDword(RegistryHive.LocalMachine, LocationSensorPath, "SensorPermissionState", 0);
                    SetDword(RegistryHive.LocalMachine, MapsPath, "AutoUpdateEnabled", 0);
                    break;

                case "Telemetry":
                    DebloatService.PrepareSharedSnapshots(id, SharedSnapshotPreparation.Capture);
                    foreach (RegistryTarget target in TelemetryTargets)
                    {
                        CaptureRegistryValue(id, RegistryTargetTag(target), target.Hive, target.Path, target.Name);
                        SetDword(target.Hive, target.Path, target.Name, target.AppliedValue);
                    }
                    CaptureRegistryValue(id, "PeriodInNanoSeconds", RegistryHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "PeriodInNanoSeconds");
                    DeleteRegistryValue(RegistryHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "PeriodInNanoSeconds");
                    CaptureRegistryValue(id, "PowerShellTelemetry", RegistryHive.LocalMachine, MachineEnvironmentPath, "POWERSHELL_TELEMETRY_OPTOUT");
                    SetString(RegistryHive.LocalMachine, MachineEnvironmentPath, "POWERSHELL_TELEMETRY_OPTOUT", "1");
                    await CaptureAndDisableServiceAsync(id, "DiagTrack");
                    await CaptureAndDisableServiceAsync(id, "WerSvc");
                    break;

                case "ServicesManual":
                    foreach (ServiceTarget target in ManualServiceTargets)
                    {
                        string path = $@"SYSTEM\CurrentControlSet\Services\{target.Name}";
                        CaptureRegistryValue(id, $"{target.Name}.Start", RegistryHive.LocalMachine, path, "Start");
                        CaptureRegistryValue(id, $"{target.Name}.Delayed", RegistryHive.LocalMachine, path, "DelayedAutoStart");
                        CaptureBoolean(id, $"{target.Name}.Running", await IsServiceRunningAsync(target.Name));
                        await ConfigureServiceAsync(target.Name, target.ScStartMode);
                    }
                    CaptureRegistryValue(id, "SvcHostSplitThresholdInKB", RegistryHive.LocalMachine, ServiceControlPath, "SvcHostSplitThresholdInKB");
                    SetDword(RegistryHive.LocalMachine, ServiceControlPath, "SvcHostSplitThresholdInKB", GetPhysicalMemoryKilobytesDword());
                    break;

                case "PhotoViewer":
                    string viewerDll = PhotoViewerRegistration.ViewerDll;
                    if (!File.Exists(viewerDll))
                    {
                        throw new FileNotFoundException(
                            "Windows Photo Viewer DLL was not found.",
                            viewerDll);
                    }

                    PhotoViewerRegistration.Apply(PhotoViewerPlan(),
                        entry => CaptureRegistryValue(id, entry.Tag, RegistryHive.LocalMachine, entry.Path, entry.Name),
                        () =>
                        {
                            using RegistryKey snapshot = Registry.CurrentUser.CreateSubKey($@"{BackupRoot}\{id}", writable: true);
                            snapshot.SetValue(PhotoViewerRegistration.SchemaKey, PhotoViewerRegistration.CurrentSchema, RegistryValueKind.DWord);
                            snapshot.Flush();
                        },
                        entry => SetString(RegistryHive.LocalMachine, entry.Path, entry.Name, entry.Value),
                        PhotoViewerRegistration.NotifyShell);
                    using (RegistryKey backup = Registry.CurrentUser.CreateSubKey($@"{BackupRoot}\{id}", writable: true))
                    {
                        // Retained for compatibility, never used as proof of ON.
                        backup.SetValue("Applied", 1, RegistryValueKind.DWord);
                    }
                    break;

                case "Widgets":
                    await RemoveWidgetsAsync();
                    break;

                case "ExplorerHomeGallery":
                    CaptureRegistryValue(id, "LaunchTo", RegistryHive.CurrentUser, ExplorerAdvancedPath, "LaunchTo");
                    CaptureRegistryValue(id, "HomePinned", RegistryHive.CurrentUser, ExplorerHomePath, "System.IsPinnedToNameSpaceTree");
                    CaptureRegistryValue(id, "GalleryPinned", RegistryHive.CurrentUser, ExplorerGalleryPath, "System.IsPinnedToNameSpaceTree");
                    SetDword(RegistryHive.CurrentUser, ExplorerAdvancedPath, "LaunchTo", 1);
                    SetDword(RegistryHive.CurrentUser, ExplorerHomePath, "System.IsPinnedToNameSpaceTree", 0);
                    SetDword(RegistryHive.CurrentUser, ExplorerGalleryPath, "System.IsPinnedToNameSpaceTree", 0);
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported essential tweak: {id}");
            }
        }

        private async Task RestoreAsync(string id)
        {
            if (id != "Widgets") EnsureBackupExists(id);
            switch (id)
            {
                case "SysMain":
                    await ConfigureSavedServiceAsync(id, "SysMain", "Start");
                    RestoreRegistryValue(id, "Start", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\SysMain", "Start");
                    if (ReadBoolean(id, "WasRunning"))
                    {
                        await RunServiceCommandAsync("start", "SysMain", tolerateAlreadyState: true);
                    }
                    else
                    {
                        await RunServiceCommandAsync("stop", "SysMain", tolerateAlreadyState: true);
                    }
                    break;

                case "EndTask":
                    RestoreRegistryValue(id, "TaskbarEndTask", RegistryHive.CurrentUser, EndTaskPath, "TaskbarEndTask");
                    break;

                case "ClassicContext":
                    RestoreRegistryValue(id, "Default", RegistryHive.CurrentUser, ClassicContextPath, string.Empty);
                    if (!BackupValueExisted(id, "Default")) RemoveEmptyClassicContextKey();
                    break;

                case "ConsumerFeatures":
                    RestoreRegistryValue(id, "DisableWindowsConsumerFeatures", RegistryHive.LocalMachine, ConsumerFeaturesPath, "DisableWindowsConsumerFeatures");
                    break;

                case "DeliveryOptimization":
                    RestoreRegistryValue(id, "DODownloadMode", RegistryHive.LocalMachine, DeliveryOptimizationPath, "DODownloadMode");
                    break;

                case "Hibernation":
                    bool wasEnabled = ReadBackedUpDword(id, "HibernateEnabled") is not 0;
                    await RequireCommandSuccessAsync(
                        "powercfg.exe",
                        new[] { "/hibernate", wasEnabled ? "on" : "off" });
                    RestoreRegistryValue(id, "HibernateEnabled", RegistryHive.LocalMachine, PowerPath, "HibernateEnabled");
                    RestoreRegistryValue(id, "ShowHibernateOption", RegistryHive.LocalMachine, HibernateMenuPath, "ShowHibernateOption");
                    break;

                case "Location":
                    await ConfigureSavedServiceAsync(id, "lfsvc", "ServiceStart");
                    RestoreRegistryValue(id, "ServiceStart", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\lfsvc", "Start");
                    RestoreRegistryValue(id, "Consent", RegistryHive.LocalMachine, LocationConsentPath, "Value");
                    RestoreRegistryValue(id, "Sensor", RegistryHive.LocalMachine, LocationSensorPath, "SensorPermissionState");
                    RestoreRegistryValue(id, "Maps", RegistryHive.LocalMachine, MapsPath, "AutoUpdateEnabled");
                    if (ReadBoolean(id, "ServiceWasRunning"))
                    {
                        await RunServiceCommandAsync("start", "lfsvc", tolerateAlreadyState: true);
                    }
                    else
                    {
                        await RunServiceCommandAsync("stop", "lfsvc", tolerateAlreadyState: true);
                    }
                    break;

                case "Telemetry":
                    DebloatService.PrepareSharedSnapshots(id, SharedSnapshotPreparation.Restore);
                    EnsureBackupExists(id);
                    foreach (RegistryTarget target in TelemetryTargets)
                    {
                        RestoreRegistryValue(id, RegistryTargetTag(target), target.Hive, target.Path, target.Name);
                    }
                    RestoreRegistryValue(id, "PeriodInNanoSeconds", RegistryHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "PeriodInNanoSeconds");
                    RestoreRegistryValue(id, "PowerShellTelemetry", RegistryHive.LocalMachine, MachineEnvironmentPath, "POWERSHELL_TELEMETRY_OPTOUT");
                    await RestoreCapturedServiceAsync(id, "DiagTrack");
                    await RestoreCapturedServiceAsync(id, "WerSvc");
                    break;

                case "ServicesManual":
                    EnsureBackupExists(id);
                    foreach (ServiceTarget target in ManualServiceTargets)
                    {
                        string path = $@"SYSTEM\CurrentControlSet\Services\{target.Name}";
                        if (!HasCapturedService(id, target.Name) && ReadRegistryValue(RegistryHive.LocalMachine, path, "Start") is null) continue;
                        await ConfigureSavedServiceAsync(id, target.Name, $"{target.Name}.Start", $"{target.Name}.Delayed");
                        RestoreRegistryValue(id, $"{target.Name}.Start", RegistryHive.LocalMachine, path, "Start");
                        RestoreRegistryValue(id, $"{target.Name}.Delayed", RegistryHive.LocalMachine, path, "DelayedAutoStart");
                        if (ReadBoolean(id, $"{target.Name}.Running"))
                        {
                            await RunServiceCommandAsync("start", target.Name, tolerateAlreadyState: true);
                        }
                        else
                        {
                            await RunServiceCommandAsync("stop", target.Name, tolerateAlreadyState: true);
                        }
                    }
                    RestoreRegistryValue(id, "SvcHostSplitThresholdInKB", RegistryHive.LocalMachine, ServiceControlPath, "SvcHostSplitThresholdInKB");
                    break;

                case "PhotoViewer":
                    EnsureBackupExists(id);
                    using (RegistryKey snapshot = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{id}", writable: false)!)
                    {
                        try
                        {
                            foreach (var entry in PhotoViewerRegistration.RestoreEntries(PhotoViewerPlan(), key => snapshot.GetValue(key)))
                                RestoreRegistryValue(id, entry.Tag, RegistryHive.LocalMachine, entry.Path, entry.Name);
                        }
                        finally { PhotoViewerRegistration.NotifyShell(); }
                    }
                    break;

                case "Widgets":
                    await ReinstallWidgetsAsync();
                    break;

                case "ExplorerHomeGallery":
                    RestoreRegistryValue(id, "LaunchTo", RegistryHive.CurrentUser, ExplorerAdvancedPath, "LaunchTo");
                    RestoreRegistryValue(id, "HomePinned", RegistryHive.CurrentUser, ExplorerHomePath, "System.IsPinnedToNameSpaceTree");
                    RestoreRegistryValue(id, "GalleryPinned", RegistryHive.CurrentUser, ExplorerGalleryPath, "System.IsPinnedToNameSpaceTree");
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported essential tweak: {id}");
            }
        }

        private async Task<ToolToggleState> ReadSysMainStateAsync()
        {
            using RegistryKey? key = OpenKey(RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Services\SysMain", writable: false);
            if (key is null) return ToolToggleState.Unavailable("SysMain is not installed.");
            int start = Convert.ToInt32(key.GetValue("Start") ??
                throw new InvalidOperationException("SysMain exists but its startup configuration could not be read."), CultureInfo.InvariantCulture);
            bool running = await IsServiceRunningAsync("SysMain");
            bool isOn = start == 2 && running;
            return new ToolToggleState(
                isOn,
                true,
                $"Start={start}, Running={running}");
        }

        private static ToolToggleState ReadClassicContextState()
        {
            using RegistryKey? key = OpenKey(
                RegistryHive.CurrentUser,
                ClassicContextPath,
                writable: false);
            return new ToolToggleState(
                key is not null,
                true,
                key is null ? "Registry key absent" : "Classic context registry key present");
        }

        private static ToolToggleState ReadLocationState()
        {
            int? start = ReadDword(
                RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Services\lfsvc",
                "Start");
            string? consent = Convert.ToString(
                ReadRegistryValue(RegistryHive.LocalMachine, LocationConsentPath, "Value"),
                CultureInfo.InvariantCulture);
            int? sensor = ReadDword(RegistryHive.LocalMachine, LocationSensorPath, "SensorPermissionState");
            int? maps = ReadDword(RegistryHive.LocalMachine, MapsPath, "AutoUpdateEnabled");
            bool applied = (start is null or 4) &&
                           string.Equals(consent, "Deny", StringComparison.OrdinalIgnoreCase) &&
                           sensor == 0 && maps == 0;
            return new ToolToggleState(
                applied,
                true,
                $"ServiceStart={FormatNullable(start)}, Consent={consent ?? "<absent>"}, " +
                $"Sensor={FormatNullable(sensor)}, Maps={FormatNullable(maps)}");
        }

        private static ToolToggleState ReadExplorerHomeGalleryState()
        {
            int? launch = ReadDword(RegistryHive.CurrentUser, ExplorerAdvancedPath, "LaunchTo");
            int? home = ReadDword(RegistryHive.CurrentUser, ExplorerHomePath, "System.IsPinnedToNameSpaceTree");
            int? gallery = ReadDword(RegistryHive.CurrentUser, ExplorerGalleryPath, "System.IsPinnedToNameSpaceTree");
            bool applied = launch == 1 && home == 0 && gallery == 0;
            return new ToolToggleState(
                applied,
                true,
                $"LaunchTo={FormatNullable(launch)}, Home={FormatNullable(home)}, Gallery={FormatNullable(gallery)}");
        }

        private async Task<ToolToggleState> ReadTelemetryStateAsync()
        {
            List<string> mismatches = new();
            foreach (RegistryTarget target in TelemetryTargets)
            {
                int? value = ReadDword(target.Hive, target.Path, target.Name);
                if (value != target.AppliedValue)
                {
                    mismatches.Add($"{target.Name}={FormatNullable(value)}");
                }
            }

            string? optOut = Convert.ToString(
                ReadRegistryValue(RegistryHive.LocalMachine, MachineEnvironmentPath, "POWERSHELL_TELEMETRY_OPTOUT"),
                CultureInfo.InvariantCulture);
            if (!string.Equals(optOut, "1", StringComparison.Ordinal))
            {
                mismatches.Add($"POWERSHELL_TELEMETRY_OPTOUT={optOut ?? "<absent>"}");
            }

            foreach (string serviceName in new[] { "DiagTrack", "WerSvc" })
            {
                int? start = ReadDword(
                    RegistryHive.LocalMachine,
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}",
                    "Start");
                if (start.HasValue && start != 4)
                {
                    mismatches.Add($"{serviceName}.Start={start}");
                }
            }

            bool applied = mismatches.Count == 0;
            return new ToolToggleState(
                applied,
                true,
                applied
                    ? "12 registry policies, DiagTrack/WerSvc, and PowerShell telemetry opt-out match"
                    : string.Join(", ", mismatches));
        }

        private static ToolToggleState ReadServicesManualState()
        {
            List<string> mismatches = new();
            foreach (ServiceTarget target in ManualServiceTargets)
            {
                int? start = ReadDword(
                    RegistryHive.LocalMachine,
                    $@"SYSTEM\CurrentControlSet\Services\{target.Name}",
                    "Start");
                if (start != target.StartValue)
                {
                    mismatches.Add($"{target.Name}={FormatNullable(start)}");
                }
            }

            object? threshold = ReadRegistryValue(
                RegistryHive.LocalMachine,
                ServiceControlPath,
                "SvcHostSplitThresholdInKB");
            if (threshold is null)
            {
                mismatches.Add("SvcHostSplitThresholdInKB=<absent>");
            }

            bool applied = mismatches.Count == 0;
            return new ToolToggleState(
                applied,
                true,
                applied
                    ? "CscService/DiagTrack disabled; MapsBroker/StorSvc manual; SharedAccess disabled; split threshold present"
                    : string.Join(", ", mismatches));
        }

        private static ToolToggleState ReadPhotoViewerState()
        {
            string viewerDll = PhotoViewerRegistration.ViewerDll;
            if (!CatalogAvailability.FileIsPresent(viewerDll))
            {
                return new ToolToggleState(
                    false,
                    false,
                    "PhotoViewer.dll is missing",
                    "This Windows installation does not contain the legacy Photo Viewer DLL.", UnavailableOnThisPc: true);
            }

            PhotoViewerStatus status = PhotoViewerRegistration.Inspect(PhotoViewerPlan(), PhotoViewerRegistration.ReadNative);
            return new ToolToggleState(status.Ready, true, status.Detail);
        }

        private static void OpenPhotoViewerDefaultApps()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                        ? "ms-settings:defaultapps?registeredAppMachine=Windows%20Photo%20Viewer"
                        : "ms-settings:defaultapps",
                    UseShellExecute = true
                });
            }
            catch
            {
                // Readback still reports registration only, not a new default.
                // The result explains how to choose defaults manually.
            }
        }

        private static readonly BoundedReadProbe<IReadOnlyList<Package>> WidgetInventory = new();

        private static Task<IReadOnlyList<Package>> FindWidgetPackagesAsync() =>
            WidgetInventory.ReadAsync(FindWidgetPackages, TimeSpan.FromSeconds(30));

        private static async Task<ToolToggleState> ReadWidgetsStateAsync()
        {
            IReadOnlyList<Package> packages = await FindWidgetPackagesAsync();
            return new ToolToggleState(
                packages.Count == 0,
                true,
                packages.Count == 0
                    ? "Windows Web Experience/Widgets packages are not registered for this user"
                    : string.Join(", ", packages.Select(package => package.Id.Name)));
        }

        private static IReadOnlyList<Package> FindWidgetPackages()
        {
            PackageManager manager = new();
            return manager.FindPackagesForUser(string.Empty)
                .Where(package =>
                    package.Id.Name.Contains("WebExperience", StringComparison.OrdinalIgnoreCase) ||
                    package.Id.Name.Contains("WidgetsPlatformRuntime", StringComparison.OrdinalIgnoreCase))
                .GroupBy(package => package.Id.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private static async Task RemoveWidgetsAsync()
        {
            PackageManager manager = new();
            IReadOnlyList<Package> packages = await FindWidgetPackagesAsync();
            foreach (Package package in packages)
            {
                DeploymentResult result = await DeploymentOperationTimeout.AwaitAsync(
                    () => manager.RemovePackageAsync(
                        package.Id.FullName,
                        RemovalOptions.RemoveForAllUsers),
                    $"Removing {package.Id.Name}");
                if (result.ExtendedErrorCode is not null &&
                    result.ExtendedErrorCode.HResult < 0)
                {
                    throw new InvalidOperationException(
                        $"Unable to remove {package.Id.Name}: {result.ErrorText} " +
                        $"(0x{result.ExtendedErrorCode.HResult:X8})");
                }
            }
        }

        private async Task ReinstallWidgetsAsync()
        {
            if ((await FindWidgetPackagesAsync()).Count > 0)
            {
                return;
            }

            List<string> failures = new();
            foreach ((string name, string productId) in new[]
                     {
                         ("Windows Web Experience Pack", "9MSSGKG348SP"),
                         ("Widgets Platform Runtime", "9N3RK8ZV2ZR8")
                     })
            {
                NativeCommandResult result = await _commandRunner.RunAsync(
                    "winget.exe",
                    new[]
                    {
                        "install",
                        "--id", productId,
                        "--source", "msstore",
                        "--exact",
                        "--silent",
                        "--accept-source-agreements",
                        "--accept-package-agreements",
                        "--disable-interactivity"
                    },
                    TimeSpan.FromMinutes(3));
                if (result.ExitCode != 0)
                {
                    failures.Add($"{name}: {result.CombinedOutput}");
                }

                for (int attempt = 0; attempt < 15; attempt++)
                {
                    if ((await FindWidgetPackagesAsync()).Count > 0)
                    {
                        return;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-windows-store://pdp/?ProductId=9MSSGKG348SP",
                    UseShellExecute = true
                });
            }
            catch
            {
                // Keep the WinGet details as the authoritative failure report.
            }

            throw new InvalidOperationException(
                "Widgets could not be verified after reinstall. Microsoft Store was opened for manual installation. " +
                string.Join(" | ", failures));
        }

        private static ToolToggleState ReadDwordAppliedState(
            RegistryHive hive,
            string path,
            string name,
            int appliedValue)
        {
            int? value = ReadDword(hive, path, name);
            return new ToolToggleState(
                value == appliedValue,
                true,
                value.HasValue ? $"{name}={value.Value}" : $"{name}=<absent>");
        }

        private async Task<bool> IsServiceRunningAsync(string serviceName)
        {
            if (!GamingLiveStatusService.TryReadServiceState(serviceName, out string state) || state is not ("Running" or "Stopped"))
                throw new InvalidOperationException($"Cannot read a stable state for {serviceName}: {state}.");
            return state == "Running";
        }

        private async Task ConfigureServiceAsync(string serviceName, string startMode)
        {
            await RequireCommandSuccessAsync(
                "sc.exe",
                new[] { "config", serviceName, "start=", startMode });
        }

        private async Task CaptureAndDisableServiceAsync(string id, string serviceName)
        {
            string path = $@"SYSTEM\CurrentControlSet\Services\{serviceName}";
            if (ReadRegistryValue(RegistryHive.LocalMachine, path, "Start") is null)
            {
                return;
            }

            CaptureRegistryValue(id, $"{serviceName}.Start", RegistryHive.LocalMachine, path, "Start");
            CaptureRegistryValue(id, $"{serviceName}.Delayed", RegistryHive.LocalMachine, path, "DelayedAutoStart");
            CaptureBoolean(id, $"{serviceName}.Running", await IsServiceRunningAsync(serviceName));
            await RunServiceCommandAsync("stop", serviceName, tolerateAlreadyState: true);
            await ConfigureServiceAsync(serviceName, "disabled");
        }

        private async Task RestoreCapturedServiceAsync(string id, string serviceName)
        {
            string path = $@"SYSTEM\CurrentControlSet\Services\{serviceName}";
            if (!HasCapturedService(id, serviceName) && ReadRegistryValue(RegistryHive.LocalMachine, path, "Start") is null) return;
            await ConfigureSavedServiceAsync(id, serviceName, $"{serviceName}.Start", $"{serviceName}.Delayed");
            RestoreRegistryValue(id, $"{serviceName}.Start", RegistryHive.LocalMachine, path, "Start");
            RestoreRegistryValue(id, $"{serviceName}.Delayed", RegistryHive.LocalMachine, path, "DelayedAutoStart");
            if (ReadBoolean(id, $"{serviceName}.Running"))
            {
                await RunServiceCommandAsync("start", serviceName, tolerateAlreadyState: true);
            }
            else
            {
                await RunServiceCommandAsync("stop", serviceName, tolerateAlreadyState: true);
            }
        }

        private Task RunServiceCommandAsync(string action, string serviceName, bool tolerateAlreadyState) =>
            ServiceRestoreRuntime.EnsureAsync(serviceName, action == "start", _commandRunner);

        private async Task RequireCommandSuccessAsync(
            string executable,
            IReadOnlyList<string> arguments)
        {
            NativeCommandResult result = await _commandRunner.RunAsync(
                executable,
                arguments,
                TimeSpan.FromSeconds(30));
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.CombinedOutput)
                        ? $"{executable} failed with exit code {result.ExitCode}."
                        : result.CombinedOutput);
            }
        }

        private static void CaptureRegistryValue(
            string id,
            string tag,
            RegistryHive hive,
            string path,
            string name)
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey(
                $@"{BackupRoot}\{id}",
                writable: true);
            if (RegistrySnapshotCommit.IsCaptured(key => backup.GetValue(key), tag))
            {
                return;
            }

            using RegistryKey? source = OpenKey(hive, path, writable: false);
            RegistrySnapshotCommit.Capture(backup, tag, source, name, SerializeRegistryValue);
        }

        private static void RestoreRegistryValue(string id, string tag, RegistryHive hive, string path, string name)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{id}", writable: false);
            if (backup is null) throw new InvalidOperationException("The original snapshot is missing. Backup retained.");
            RegistryRestorePlan.RestoreTagged(key => backup.GetValue(key), tag, new(hive, path, name), DeserializeRegistryValue);
        }

        private static bool BackupValueExisted(string id, string tag)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                $@"{BackupRoot}\{id}",
                writable: false);
            if (backup is null || !RegistrySnapshotCommit.IsCaptured(key => backup.GetValue(key), tag))
                throw new InvalidOperationException("The original snapshot is missing for " + tag + ".");
            return Convert.ToInt32(
                backup?.GetValue($"{tag}.Exists", 0) ?? 0,
                CultureInfo.InvariantCulture) == 1;
        }

        private static int? ReadBackedUpDword(string id, string tag)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                $@"{BackupRoot}\{id}",
                writable: false);
            if (backup is null || !RegistrySnapshotCommit.IsCaptured(key => backup.GetValue(key), tag))
                throw new InvalidOperationException("The original snapshot is missing for " + tag + ".");
            if (
                Convert.ToInt32(
                    backup.GetValue($"{tag}.Exists", 0),
                    CultureInfo.InvariantCulture) != 1)
            {
                return null;
            }

            string text = Convert.ToString(
                backup.GetValue($"{tag}.Value", string.Empty),
                CultureInfo.InvariantCulture) ?? string.Empty;
            return int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value)
                ? value
                : null;
        }

        private static void CaptureBoolean(string id, string name, bool value)
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey(
                $@"{BackupRoot}\{id}",
                writable: true);
            if (backup.GetValue(name) is null)
            {
                backup.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        private static bool ReadBoolean(string id, string name)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                $@"{BackupRoot}\{id}",
                writable: false);
            return ServiceRestoreSnapshot.ReadRunningState(backup?.GetValue(name), name);
        }

        private static bool HasCapturedService(string id, string serviceName)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{id}");
            return backup is not null && RegistrySnapshotCommit.IsCaptured(key => backup.GetValue(key), $"{serviceName}.Start");
        }

        private static void DeleteBackup(string id)
        {
            try
            {
                using RegistryKey? root = Registry.CurrentUser.OpenSubKey(
                    BackupRoot,
                    writable: true);
                root?.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
                root?.Flush();
                SharedPrivacySnapshot.RetireUnused(id);
            }
            catch
            {
                // A stale backup is safer than deleting unrelated state.
            }
        }

        private static void EnsureBackupExists(string id)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{id}", writable: false);
            if (backup is null) throw new InvalidOperationException("The exact original-state snapshot is unavailable.");
            var tags = new List<string>(id switch
            {
                "SysMain" => new[] { "Start" },
                "EndTask" => new[] { "TaskbarEndTask" },
                "ClassicContext" => new[] { "Default" },
                "ConsumerFeatures" => new[] { "DisableWindowsConsumerFeatures" },
                "DeliveryOptimization" => new[] { "DODownloadMode" },
                "Hibernation" => new[] { "HibernateEnabled", "ShowHibernateOption" },
                "Location" => new[] { "ServiceStart", "Consent", "Sensor", "Maps" },
                "Telemetry" => TelemetryTargets.Select(RegistryTargetTag).Concat(new[] { "PeriodInNanoSeconds", "PowerShellTelemetry" }).ToArray(),
                "ServicesManual" => new[] { "SvcHostSplitThresholdInKB" },
                "PhotoViewer" => PhotoViewerRegistration.RestoreEntries(PhotoViewerPlan(), key => backup.GetValue(key)).Select(entry => entry.Tag).ToArray(),
                "ExplorerHomeGallery" => new[] { "LaunchTo", "HomePinned", "GalleryPinned" },
                _ => throw new InvalidOperationException("Unsupported original-state snapshot.")
            });
            string[] services = id == "Telemetry" ? new[] { "DiagTrack", "WerSvc" } :
                id == "ServicesManual" ? ManualServiceTargets.Select(s => s.Name).ToArray() : Array.Empty<string>();
            foreach (string service in services)
            {
                bool captured = backup.GetValue($"{service}.Start.Captured") is not null;
                if (!captured && ReadRegistryValue(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{service}", "Start") is null) continue;
                tags.Add($"{service}.Start");
                tags.Add($"{service}.Delayed");
                ServiceRestoreSnapshot.ReadRunningState(backup.GetValue($"{service}.Running"), service);
            }
            if (id == "SysMain") ServiceRestoreSnapshot.ReadRunningState(backup.GetValue("WasRunning"), "SysMain");
            if (id == "Location") ServiceRestoreSnapshot.ReadRunningState(backup.GetValue("ServiceWasRunning"), "lfsvc");
            RegistryRestorePlan.RequireTags(key => backup.GetValue(key), tags, DeserializeRegistryValue);
            foreach (string startTag in tags.Where(t => t == "Start" || t == "ServiceStart" || t.EndsWith(".Start", StringComparison.Ordinal)))
                _ = ServiceRestoreSnapshot.ToScStartMode(ReadBackedUpDword(id, startTag) ??
                    throw new InvalidOperationException("The saved service startup configuration is absent. Backup retained."), null);
        }

        private async Task ConfigureSavedServiceAsync(string id, string service, string startTag, string? delayedTag = null)
        {
            int start = ReadBackedUpDword(id, startTag) ??
                throw new InvalidOperationException($"The original startup configuration for {service} is missing. Backup retained.");
            int? delayed = delayedTag is null ? ReadDword(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{service}", "DelayedAutoStart") : ReadBackedUpDword(id, delayedTag);
            string mode = ServiceRestoreSnapshot.ToScStartMode(start, delayed);
            int? current = ReadDword(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{service}", "Start");
            int? currentDelayed = ReadDword(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{service}", "DelayedAutoStart");
            if (current != start || (start == 2 && (currentDelayed == 1) != (delayed == 1)))
                await ConfigureServiceAsync(service, mode);
        }

        private static void RemoveEmptyClassicContextKey()
        {
            using (RegistryKey? key = OpenKey(RegistryHive.CurrentUser, ClassicContextPath, writable: false))
            {
                if (key is null || key.ValueCount != 0 || key.SubKeyCount != 0) return;
            }
            // Nonrecursive: do not erase another program's values or sibling registrations.
            Registry.CurrentUser.DeleteSubKey(ClassicContextPath, throwOnMissingSubKey: false);
        }

        private static int GetPhysicalMemoryKilobytesDword()
        {
            MemoryStatusEx status = new()
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
            };
            if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0)
            {
                throw new InvalidOperationException("Unable to determine physical memory for SvcHostSplitThresholdInKB.");
            }

            uint kilobytes = (uint)Math.Min(status.TotalPhysical / 1024UL, uint.MaxValue);
            return unchecked((int)kilobytes);
        }

        private static string RegistryTargetTag(RegistryTarget target)
        {
            uint hash = 2166136261;
            foreach (char character in $"{target.Hive}|{target.Path}|{target.Name}")
            {
                hash ^= character;
                hash *= 16777619;
            }
            return $"Telemetry.{hash:X8}";
        }

        private static object? ReadRegistryValue(
            RegistryHive hive,
            string path,
            string name)
        {
            using RegistryKey? key = OpenKey(hive, path, writable: false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        private static int? ReadDword(RegistryHive hive, string path, string name)
        {
            object? value = ReadRegistryValue(hive, path, name);
            return value is null
                ? null
                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static void SetDword(
            RegistryHive hive,
            string path,
            string name,
            int value)
        {
            using RegistryKey key = CreateKey(hive, path);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }

        private static void SetString(
            RegistryHive hive,
            string path,
            string name,
            string value)
        {
            using RegistryKey key = CreateKey(hive, path);
            key.SetValue(name, value, RegistryValueKind.String);
        }

        private static void DeleteRegistryValue(
            RegistryHive hive,
            string path,
            string name)
        {
            RegistryRestorePlan.Execute(new[] { new RestoreRegistryValue(new(hive, path, name), null, null) });
        }

        private static void DeleteCurrentUserTree(string path)
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }

        private static RegistryKey? OpenKey(
            RegistryHive hive,
            string path,
            bool writable)
        {
            RegistryView view = Environment.Is64BitOperatingSystem
                ? RegistryView.Registry64
                : RegistryView.Default;
            RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            RegistryKey? key = baseKey.OpenSubKey(path, writable);
            baseKey.Dispose();
            return key;
        }

        private static RegistryKey CreateKey(RegistryHive hive, string path)
        {
            RegistryView view = Environment.Is64BitOperatingSystem
                ? RegistryView.Registry64
                : RegistryView.Default;
            RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            RegistryKey key = baseKey.CreateSubKey(path, writable: true);
            baseKey.Dispose();
            return key;
        }

        private static string SerializeRegistryValue(
            object value,
            RegistryValueKind kind)
        {
            return kind switch
            {
                RegistryValueKind.Binary => Convert.ToBase64String((byte[])value),
                RegistryValueKind.MultiString => string.Join("\u001f", (string[])value),
                RegistryValueKind.DWord => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                RegistryValueKind.QWord => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            };
        }

        private static object DeserializeRegistryValue(
            string value,
            RegistryValueKind kind)
        {
            return kind switch
            {
                RegistryValueKind.Binary => Convert.FromBase64String(value),
                RegistryValueKind.MultiString => value.Split('\u001f'),
                RegistryValueKind.DWord => int.Parse(value, CultureInfo.InvariantCulture),
                RegistryValueKind.QWord => long.Parse(value, CultureInfo.InvariantCulture),
                _ => value
            };
        }

        private static string FormatNullable(int? value) =>
            value.HasValue
                ? value.Value.ToString(CultureInfo.InvariantCulture)
                : "<absent>";

        private readonly record struct RegistryTarget(
            RegistryHive Hive,
            string Path,
            string Name,
            int AppliedValue);

        private readonly record struct ServiceTarget(
            string Name,
            int StartValue,
            string ScStartMode);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    }
}
