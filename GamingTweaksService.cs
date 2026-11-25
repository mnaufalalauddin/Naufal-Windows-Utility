using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal sealed class GamingTweaksService : IToolToggleService
    {
        private const string BackupRoot =
            @"Software\Naufal Windows Tech\Powertoys\Backups\Gaming";
        private const string MigrationRoot =
            @"Software\Naufal Windows Tech\Powertoys\Migrations\PreviousManualGaming";
        private static readonly string PreviousSnapshotPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WindowsPowerToys",
            "ManualGamingToggleState.json");

        private const string GameModePath = @"Software\Microsoft\GameBar";
        private const string HagsPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        private const string WindowedOptimizationPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
        private const string GameDvrConfigPath = @"System\GameConfigStore";
        private const string GameDvrCapturePath = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
        private const string GameDvrPolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";
        private const string MpoPath = @"SOFTWARE\Microsoft\Windows\Dwm";

        private readonly NativeCommandRunner _commandRunner = new();

        private static readonly IReadOnlyList<ToolToggleDefinition> Definitions =
            new[]
            {
                new ToolToggleDefinition(
                    "DynamicTick",
                    "Timer / Boot",
                    "Dynamic Tick",
                    "Controls the BCD disabledynamictick override. ON removes the override so Windows can suppress idle timer ticks normally; OFF forces continuous periodic ticking for latency testing, which can increase idle power consumption. A restart is required before timing behavior changes.",
                    true,
                    true),
                new ToolToggleDefinition(
                    "HPET",
                    "Timer / Boot",
                    "High Precision Event Timer (HPET) Platform Clock Override",
                    "Controls only the BCD useplatformclock override; it does not disable the HPET hardware device. ON returns clock-source selection to Windows, while OFF writes useplatformclock=no for compatibility/latency testing. Actual benefit is platform-dependent and requires a restart.",
                    true,
                    true),
                new ToolToggleDefinition(
                    "GameDVR",
                    "Capture",
                    "Game DVR (Digital Video Recorder) / Capture",
                    "Controls Windows Game DVR and background capture through user settings and machine policy. OFF disables recording/capture hooks that can consume resources; Xbox Game Bar capture and background recording become unavailable until restored.",
                    true,
                    false,
                    IsFeatureSwitch: true),
                new ToolToggleDefinition(
                    "MPO",
                    "Graphics",
                    "Multiplane Overlay (MPO)",
                    "Controls Desktop Window Manager multiplane overlays. ON removes the override and lets Windows/the display driver use MPO; OFF sets OverlayTestMode=5, which can help diagnose flicker or stutter but may increase composition work. Sign-out or restart is recommended.",
                    true,
                    true),
                new ToolToggleDefinition(
                    "WindowedOptimizations",
                    "Graphics",
                    "Windowed Game Optimizations",
                    "Controls the DirectX swap-effect upgrade used by Optimizations for windowed games. ON returns the feature to Windows defaults for eligible borderless/windowed games; OFF prevents the upgrade and may affect latency, Auto HDR, and variable refresh behavior.",
                    true,
                    false),
                new ToolToggleDefinition(
                    "HAGS",
                    "Graphics",
                    "Hardware-Accelerated GPU Scheduling (HAGS)",
                    "Controls Hardware-Accelerated GPU Scheduling through HwSchMode. ON requests GPU hardware scheduling when supported by the driver; OFF returns scheduling responsibility to Windows. Performance and stability vary by GPU/driver, and a restart is required.",
                    true,
                    true),
                new ToolToggleDefinition(
                    "GameMode",
                    "Gaming",
                    "Game Mode",
                    "Controls Windows Game Mode for the current user. ON allows Windows to prioritize detected games and reduce background activity; OFF disables automatic Game Mode activation without removing Xbox or capture components.",
                    true,
                    false,
                    IsFeatureSwitch: true)
            };

        public string DescribeRecovery(ToolToggleDefinition definition) =>
            CatalogRecovery.Snapshot($@"{BackupRoot}\{definition.Id}", "No current snapshot found. Restore uses the Windows default for the selected gaming setting.");

        public IReadOnlyList<ToolToggleDefinition> GetDefinitions() => Definitions;

        public async Task<ToolToggleState> ReadStateAsync(ToolToggleDefinition definition)
        {
            try
            {
                return definition.Id switch
                {
                    "DynamicTick" => await ReadBcdStateAsync(
                        "disabledynamictick",
                        disabledValue: "Yes"),
                    "HPET" => await ReadBcdStateAsync(
                        "useplatformclock",
                        disabledValue: "No"),
                    "GameDVR" => ReadGameDvrState(),
                    "MPO" => ReadDwordDefaultOnState(
                        RegistryHive.LocalMachine,
                        MpoPath,
                        "OverlayTestMode",
                        offValue: 5),
                    "WindowedOptimizations" => ReadWindowedOptimizationState(),
                    "HAGS" => ReadHagsState(),
                    "GameMode" => ReadDwordDefaultOnState(
                        RegistryHive.CurrentUser,
                        GameModePath,
                        "AutoGameModeEnabled",
                        offValue: 0),
                    _ => new ToolToggleState(
                        false,
                        false,
                        "Unknown definition",
                        $"Unsupported gaming tweak: {definition.Id}")
                };
            }
            catch (Exception exception)
            {
                return new ToolToggleState(
                    false,
                    false,
                    "Unable to read",
                    exception.Message);
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
                if (before.IsAvailable && before.IsOn == targetOn)
                {
                    return new ToolToggleOperationResult(
                        true,
                        true,
                        $"{definition.Name} is already {(targetOn ? "ON" : "OFF")}.",
                        before);
                }

                if (definition.Id == "GameMode")
                {
                    GameModeSetting.Apply(targetOn,
                        () => CaptureRegistryValue(definition.Id, "AutoGameModeEnabled", RegistryHive.CurrentUser, GameModePath, "AutoGameModeEnabled"),
                        value => SetDword(RegistryHive.CurrentUser, GameModePath, "AutoGameModeEnabled", value));
                }
                else if (definition.Id == "GameDVR")
                {
                    IReadOnlyList<PreviousRegistryTarget> targets = GetPreviousRegistryTargets(definition.Id);
                    GameDvrSetting.Apply(targetOn,
                        () =>
                        {
                            TryImportPreviousSnapshot(definition.Id);
                            foreach (PreviousRegistryTarget target in targets)
                                CaptureRegistryValue(definition.Id, target.Tag, target.Hive, target.Path, target.Name);
                        },
                        (name, value) =>
                        {
                            PreviousRegistryTarget target = targets.Single(item => item.Name == name);
                            SetDword(target.Hive, target.Path, target.Name, value);
                        });
                }
                else if (targetOn)
                {
                    await RestoreAsync(definition.Id);
                }
                else
                {
                    await CaptureAndDisableAsync(definition.Id);
                }

                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable && after.IsOn == targetOn;
                if (targetOn && verified && !definition.IsFeatureSwitch) DeleteBackup(definition.Id);
                string restartNote = verified && definition.RestartRecommended
                    ? " Restart Windows before evaluating the result."
                    : string.Empty;
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
                return new ToolToggleOperationResult(
                    false,
                    false,
                    exception.Message,
                    state);
            }
        }

        public async Task<ToolToggleOperationResult> RestoreOriginalAsync(
            ToolToggleDefinition definition)
        {
            if (!WindowsPrivilegeService.IsAdministrator())
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
                TryImportPreviousSnapshot(definition.Id);
                if (!HasBackup(definition.Id))
                {
                    ToolToggleOperationResult fallback = await RestoreWindowsDefaultAsync(definition);
                    return fallback with { Message = "No original backup was found. " + fallback.Message, DefaultFallbackHandled = true };
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
            if (!WindowsPrivilegeService.IsAdministrator())
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
                    case "DynamicTick":
                        await RequireCommandSuccessAsync(
                            "bcdedit.exe",
                            new[] { "/deletevalue", "{current}", "disabledynamictick" },
                            tolerateMissingValue: true);
                        break;
                    case "HPET":
                        await RequireCommandSuccessAsync(
                            "bcdedit.exe",
                            new[] { "/deletevalue", "{current}", "useplatformclock" },
                            tolerateMissingValue: true);
                        break;
                    case "GameDVR":
                        RestoreGameDvrDefaults();
                        break;
                    case "MPO":
                        DeleteRegistryValue(RegistryHive.LocalMachine, MpoPath, "OverlayTestMode");
                        break;
                    case "WindowedOptimizations":
                        RemoveWindowedOptimizationOffToken();
                        break;
                    case "HAGS":
                        DeleteRegistryValue(RegistryHive.LocalMachine, HagsPath, "HwSchMode");
                        break;
                    case "GameMode":
                        DeleteRegistryValue(RegistryHive.CurrentUser, GameModePath, "AutoGameModeEnabled");
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported gaming tweak: {definition.Id}");
                }

                ToolToggleState after = await ReadStateAsync(definition);
                bool verified = after.IsAvailable && after.IsOn;
                if (verified) DeleteBackup(definition.Id);
                return new ToolToggleOperationResult(
                    verified,
                    verified,
                    verified
                        ? $"{definition.Name} was returned to Windows-controlled/default behavior."
                        : $"Windows-default read-back failed. Actual: {after.ActualValue}",
                    after);
            }
            catch (Exception exception)
            {
                ToolToggleState state = await ReadStateAsync(definition);
                return new ToolToggleOperationResult(false, false, exception.Message, state);
            }
        }

        private async Task CaptureAndDisableAsync(string id)
        {
            switch (id)
            {
                case "DynamicTick":
                    await CaptureBcdAsync(id, "disabledynamictick");
                    await RequireCommandSuccessAsync(
                        "bcdedit.exe",
                        new[] { "/set", "{current}", "disabledynamictick", "yes" });
                    break;

                case "HPET":
                    await CaptureBcdAsync(id, "useplatformclock");
                    await RequireCommandSuccessAsync(
                        "bcdedit.exe",
                        new[] { "/set", "{current}", "useplatformclock", "no" });
                    break;

                case "GameDVR":
                    CaptureRegistryValue(id, "GameDVR_Enabled", RegistryHive.CurrentUser, GameDvrConfigPath, "GameDVR_Enabled");
                    CaptureRegistryValue(id, "AppCaptureEnabled", RegistryHive.CurrentUser, GameDvrCapturePath, "AppCaptureEnabled");
                    CaptureRegistryValue(id, "HistoricalCaptureEnabled", RegistryHive.CurrentUser, GameDvrCapturePath, "HistoricalCaptureEnabled");
                    CaptureRegistryValue(id, "AllowGameDVR", RegistryHive.LocalMachine, GameDvrPolicyPath, "AllowGameDVR");
                    SetDword(RegistryHive.CurrentUser, GameDvrConfigPath, "GameDVR_Enabled", 0);
                    SetDword(RegistryHive.CurrentUser, GameDvrCapturePath, "AppCaptureEnabled", 0);
                    SetDword(RegistryHive.CurrentUser, GameDvrCapturePath, "HistoricalCaptureEnabled", 0);
                    SetDword(RegistryHive.LocalMachine, GameDvrPolicyPath, "AllowGameDVR", 0);
                    break;

                case "MPO":
                    CaptureRegistryValue(id, "OverlayTestMode", RegistryHive.LocalMachine, MpoPath, "OverlayTestMode");
                    SetDword(RegistryHive.LocalMachine, MpoPath, "OverlayTestMode", 5);
                    break;

                case "WindowedOptimizations":
                    CaptureRegistryValue(id, "DirectXUserGlobalSettings", RegistryHive.CurrentUser, WindowedOptimizationPath, "DirectXUserGlobalSettings");
                    string current = Convert.ToString(
                        ReadRegistryValue(RegistryHive.CurrentUser, WindowedOptimizationPath, "DirectXUserGlobalSettings"),
                        CultureInfo.InvariantCulture) ?? string.Empty;
                    string cleaned = Regex.Replace(
                        current,
                        @"(?i)(^|;)SwapEffectUpgradeEnable=[01](?=;|$)",
                        "$1");
                    cleaned = Regex.Replace(cleaned, @";;+", ";").Trim(';');
                    string newValue = string.IsNullOrWhiteSpace(cleaned)
                        ? "SwapEffectUpgradeEnable=0"
                        : $"{cleaned};SwapEffectUpgradeEnable=0";
                    SetString(RegistryHive.CurrentUser, WindowedOptimizationPath, "DirectXUserGlobalSettings", newValue);
                    break;

                case "HAGS":
                    CaptureRegistryValue(id, "HwSchMode", RegistryHive.LocalMachine, HagsPath, "HwSchMode");
                    SetDword(RegistryHive.LocalMachine, HagsPath, "HwSchMode", 1);
                    break;

                case "GameMode":
                    CaptureRegistryValue(id, "AutoGameModeEnabled", RegistryHive.CurrentUser, GameModePath, "AutoGameModeEnabled");
                    SetDword(RegistryHive.CurrentUser, GameModePath, "AutoGameModeEnabled", 0);
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported gaming tweak: {id}");
            }
        }

        private async Task RestoreAsync(string id)
        {
            TryImportPreviousSnapshot(id);
            using (RegistryKey? backup = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{id}", writable: false))
            {
                if (backup is not null && id is not ("DynamicTick" or "HPET"))
                    RegistryRestorePlan.RequireTags(key => backup.GetValue(key),
                        GetPreviousRegistryTargets(id).Select(target => target.Tag), DeserializeRegistryValue);
            }
            switch (id)
            {
                case "DynamicTick":
                    await RestoreBcdAsync(id, "disabledynamictick");
                    break;

                case "HPET":
                    await RestoreBcdAsync(id, "useplatformclock");
                    break;

                case "GameDVR":
                    if (HasBackup(id))
                    {
                        RestoreRegistryValue(id, "GameDVR_Enabled", RegistryHive.CurrentUser, GameDvrConfigPath, "GameDVR_Enabled");
                        RestoreRegistryValue(id, "AppCaptureEnabled", RegistryHive.CurrentUser, GameDvrCapturePath, "AppCaptureEnabled");
                        RestoreRegistryValue(id, "HistoricalCaptureEnabled", RegistryHive.CurrentUser, GameDvrCapturePath, "HistoricalCaptureEnabled");
                        RestoreRegistryValue(id, "AllowGameDVR", RegistryHive.LocalMachine, GameDvrPolicyPath, "AllowGameDVR");
                    }
                    else
                    {
                        RestoreGameDvrDefaults();
                    }
                    break;

                case "MPO":
                    RestoreRegistryOrDefault(
                        id,
                        "OverlayTestMode",
                        RegistryHive.LocalMachine,
                        MpoPath,
                        "OverlayTestMode",
                        defaultValue: null);
                    break;

                case "WindowedOptimizations":
                    if (HasBackup(id))
                    {
                        RestoreRegistryValue(id, "DirectXUserGlobalSettings", RegistryHive.CurrentUser, WindowedOptimizationPath, "DirectXUserGlobalSettings");
                    }
                    else
                    {
                        RemoveWindowedOptimizationOffToken();
                    }
                    break;

                case "HAGS":
                    RestoreRegistryOrDefault(
                        id,
                        "HwSchMode",
                        RegistryHive.LocalMachine,
                        HagsPath,
                        "HwSchMode",
                        2);
                    break;

                case "GameMode":
                    RestoreRegistryOrDefault(
                        id,
                        "AutoGameModeEnabled",
                        RegistryHive.CurrentUser,
                        GameModePath,
                        "AutoGameModeEnabled",
                        1);
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported gaming tweak: {id}");
            }
        }

        private async Task<ToolToggleState> ReadBcdStateAsync(
            string option,
            string disabledValue)
        {
            string? value = await GetBcdValueAsync(option);
            bool isOn = !string.Equals(value, disabledValue, StringComparison.OrdinalIgnoreCase);
            return new ToolToggleState(
                isOn,
                true,
                value is null ? "Default / not configured" : $"{option}={value}");
        }

        private ToolToggleState ReadGameDvrState()
        {
            int? config = ReadDword(RegistryHive.CurrentUser, GameDvrConfigPath, "GameDVR_Enabled");
            int? capture = ReadDword(RegistryHive.CurrentUser, GameDvrCapturePath, "AppCaptureEnabled");
            int? historical = ReadDword(RegistryHive.CurrentUser, GameDvrCapturePath, "HistoricalCaptureEnabled");
            int? policy = ReadDword(RegistryHive.LocalMachine, GameDvrPolicyPath, "AllowGameDVR");
            bool policyDisabled = policy == 0;
            bool userDisabled = config == 0 && capture == 0;
            bool historicalDisabledOrDefault = historical is null or 0;
            bool disabled = policyDisabled || (userDisabled && historicalDisabledOrDefault);
            string actual = $"Config={FormatNullable(config)}, Capture={FormatNullable(capture)}, " +
                            $"Historical={FormatNullable(historical)}, Policy={FormatNullable(policy)}";
            return new ToolToggleState(!disabled, true, actual);
        }

        private static void RestoreGameDvrDefaults() => RegistryRestorePlan.Execute(new[]
        {
            new RestoreRegistryValue(new(RegistryHive.CurrentUser, GameDvrConfigPath, "GameDVR_Enabled"), null, null),
            new RestoreRegistryValue(new(RegistryHive.CurrentUser, GameDvrCapturePath, "AppCaptureEnabled"), null, null),
            new RestoreRegistryValue(new(RegistryHive.CurrentUser, GameDvrCapturePath, "HistoricalCaptureEnabled"), null, null),
            new RestoreRegistryValue(new(RegistryHive.LocalMachine, GameDvrPolicyPath, "AllowGameDVR"), null, null)
        });

        private static ToolToggleState ReadDwordDefaultOnState(
            RegistryHive hive,
            string path,
            string name,
            int offValue)
        {
            int? value = ReadDword(hive, path, name);
            return new ToolToggleState(
                value != offValue,
                true,
                value.HasValue ? $"{name}={value.Value}" : $"{name}=<default/absent>");
        }

        private static ToolToggleState ReadHagsState()
        {
            int? value = ReadDword(RegistryHive.LocalMachine, HagsPath, "HwSchMode");
            bool isOn = value is null or 2;
            return new ToolToggleState(
                isOn,
                true,
                value.HasValue ? $"HwSchMode={value.Value}" : "HwSchMode=<default/absent>");
        }

        private static ToolToggleState ReadWindowedOptimizationState()
        {
            string? value = Convert.ToString(
                ReadRegistryValue(
                    RegistryHive.CurrentUser,
                    WindowedOptimizationPath,
                    "DirectXUserGlobalSettings"),
                CultureInfo.InvariantCulture);
            bool isOff = !string.IsNullOrWhiteSpace(value) &&
                         Regex.IsMatch(
                             value,
                             @"(?i)(^|;)SwapEffectUpgradeEnable=0(?:;|$)");
            return new ToolToggleState(
                !isOff,
                true,
                string.IsNullOrWhiteSpace(value)
                    ? "DirectXUserGlobalSettings=<default/absent>"
                    : value);
        }

        private static void TryImportPreviousSnapshot(string id)
        {
            try
            {
                using (RegistryKey? existing = Registry.CurrentUser.OpenSubKey(
                           $@"{BackupRoot}\{id}",
                           writable: false))
                {
                    if (existing is not null)
                    {
                        return;
                    }
                }

                if (!CatalogAvailability.FileIsPresent(PreviousSnapshotPath))
                {
                    return;
                }

                FileInfo source = new(PreviousSnapshotPath);
                if (source.Length > 8 * 1024 * 1024) throw new InvalidOperationException("The previous-app snapshot is too large to validate.");
                string fingerprint = $"{source.Length}:{source.LastWriteTimeUtc.Ticks}";
                using (RegistryKey? migration = Registry.CurrentUser.OpenSubKey(
                           MigrationRoot,
                           writable: false))
                {
                    if (string.Equals(
                            Convert.ToString(migration?.GetValue(id), CultureInfo.InvariantCulture),
                            fingerprint,
                            StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                using JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(PreviousSnapshotPath));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("The previous-app snapshot root is invalid.");
                if (!document.RootElement.TryGetProperty(id, out JsonElement snapshot)) return;
                if (snapshot.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("The previous-app snapshot entry is invalid.");

                string? bcdValue = null;
                bool bcdSnapshot = id is "DynamicTick" or "HPET";
                List<ImportedPreviousRegistryValue> registryImports = new();
                if (bcdSnapshot)
                {
                    if (snapshot.TryGetProperty("Value", out JsonElement valueElement) &&
                        valueElement.ValueKind == JsonValueKind.String)
                    {
                        bcdValue = valueElement.GetString();
                    }
                }
                else
                {
                    IReadOnlyList<PreviousRegistryTarget> targets =
                        GetPreviousRegistryTargets(id);
                    if (targets.Count == 0)
                    {
                        return;
                    }

                    foreach (PreviousRegistryTarget target in targets)
                    {
                        JsonElement state = snapshot;
                        if (!string.IsNullOrEmpty(target.SnapshotProperty) &&
                            (!snapshot.TryGetProperty(target.SnapshotProperty, out state) ||
                             state.ValueKind != JsonValueKind.Object))
                        {
                            throw new InvalidOperationException("The previous-app snapshot is missing a required value.");
                        }

                        if (!TryReadPreviousRegistryState(
                                state,
                                target,
                                out ImportedPreviousRegistryValue import))
                        {
                            throw new InvalidOperationException("The previous-app snapshot value is invalid.");
                        }
                        registryImports.Add(import);
                    }
                }

                using RegistryKey backup = Registry.CurrentUser.CreateSubKey(
                    $@"{BackupRoot}\{id}",
                    writable: true);
                backup.SetValue("Snapshot.Imported", 1, RegistryValueKind.DWord);
                if (bcdSnapshot)
                {
                    backup.SetValue(
                        "BcdExists",
                        string.IsNullOrWhiteSpace(bcdValue) ? 0 : 1,
                        RegistryValueKind.DWord);
                    backup.SetValue("BcdValue", bcdValue ?? string.Empty, RegistryValueKind.String);
                    backup.Flush();
                    backup.SetValue("BcdCaptured", 1, RegistryValueKind.DWord);
                }
                else
                {
                    foreach (ImportedPreviousRegistryValue import in registryImports)
                    {
                        string tag = import.Target.Tag;
                        backup.SetValue($"{tag}.Exists", import.Existed ? 1 : 0, RegistryValueKind.DWord);
                        if (import.Existed)
                        {
                            backup.SetValue($"{tag}.Kind", import.Kind.ToString(), RegistryValueKind.String);
                            backup.SetValue($"{tag}.Value", import.Value, RegistryValueKind.String);
                        }
                        backup.Flush();
                        backup.SetValue($"{tag}.Captured", 1, RegistryValueKind.DWord);
                    }
                }
                backup.SetValue("Snapshot.Complete", 1, RegistryValueKind.DWord);

                using RegistryKey marker = Registry.CurrentUser.CreateSubKey(
                    MigrationRoot,
                    writable: true);
                marker.SetValue(id, fingerprint, RegistryValueKind.String);
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException("The previous-app backup could not be read or validated. Defaults were not substituted.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new InvalidOperationException("The previous-app backup could not be read or validated. Defaults were not substituted.", exception);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("The previous-app backup could not be read or validated. Defaults were not substituted.", exception);
            }
            catch (System.Security.SecurityException exception)
            {
                throw new InvalidOperationException("The previous-app backup could not be read or validated. Defaults were not substituted.", exception);
            }
        }

        private static IReadOnlyList<PreviousRegistryTarget> GetPreviousRegistryTargets(string id) =>
            id switch
            {
                "GameDVR" => new[]
                {
                    new PreviousRegistryTarget("GameDVR_Enabled", "GameDVR_Enabled", RegistryHive.CurrentUser, GameDvrConfigPath, "GameDVR_Enabled"),
                    new PreviousRegistryTarget("AppCaptureEnabled", "AppCaptureEnabled", RegistryHive.CurrentUser, GameDvrCapturePath, "AppCaptureEnabled"),
                    new PreviousRegistryTarget("HistoricalCaptureEnabled", "HistoricalCaptureEnabled", RegistryHive.CurrentUser, GameDvrCapturePath, "HistoricalCaptureEnabled"),
                    new PreviousRegistryTarget("AllowGameDVR", "AllowGameDVR", RegistryHive.LocalMachine, GameDvrPolicyPath, "AllowGameDVR")
                },
                "MPO" => new[]
                {
                    new PreviousRegistryTarget(string.Empty, "OverlayTestMode", RegistryHive.LocalMachine, MpoPath, "OverlayTestMode")
                },
                "WindowedOptimizations" => new[]
                {
                    new PreviousRegistryTarget(string.Empty, "DirectXUserGlobalSettings", RegistryHive.CurrentUser, WindowedOptimizationPath, "DirectXUserGlobalSettings")
                },
                "HAGS" => new[]
                {
                    new PreviousRegistryTarget(string.Empty, "HwSchMode", RegistryHive.LocalMachine, HagsPath, "HwSchMode")
                },
                "GameMode" => new[]
                {
                    new PreviousRegistryTarget(string.Empty, "AutoGameModeEnabled", RegistryHive.CurrentUser, GameModePath, "AutoGameModeEnabled")
                },
                _ => Array.Empty<PreviousRegistryTarget>()
            };

        private static bool TryReadPreviousRegistryState(
            JsonElement state,
            PreviousRegistryTarget target,
            out ImportedPreviousRegistryValue import)
        {
            if (!state.TryGetProperty("Exists", out JsonElement existsElement) ||
                existsElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                import = default;
                return false;
            }
            bool existed = existsElement.ValueKind == JsonValueKind.True;
            RegistryValueKind kind = RegistryValueKind.String;
            string value = string.Empty;
            if (existed)
            {
                if (!state.TryGetProperty("Type", out JsonElement typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String ||
                    !Enum.TryParse(typeElement.GetString(), ignoreCase: true, out kind) ||
                    !state.TryGetProperty("Value", out JsonElement valueElement) ||
                    !TrySerializeJsonValue(valueElement, kind, out value))
                {
                    import = default;
                    return false;
                }
            }

            import = new ImportedPreviousRegistryValue(target, existed, kind, value);
            return true;
        }

        private static bool TrySerializeJsonValue(
            JsonElement value,
            RegistryValueKind kind,
            out string serialized)
        {
            try
            {
                serialized = kind switch
                {
                    RegistryValueKind.Binary when value.ValueKind == JsonValueKind.Array =>
                        Convert.ToBase64String(value.EnumerateArray()
                            .Select(item => item.GetByte())
                            .ToArray()),
                    RegistryValueKind.MultiString when value.ValueKind == JsonValueKind.Array =>
                        string.Join("\u001f", value.EnumerateArray()
                            .Select(item => item.GetString() ?? string.Empty)),
                    RegistryValueKind.DWord => value.GetInt32().ToString(CultureInfo.InvariantCulture),
                    RegistryValueKind.QWord => value.GetInt64().ToString(CultureInfo.InvariantCulture),
                    _ when value.ValueKind == JsonValueKind.String => value.GetString() ?? string.Empty,
                    _ => value.ToString()
                };
                return true;
            }
            catch (Exception exception) when (
                exception is FormatException or InvalidOperationException or OverflowException)
            {
                serialized = string.Empty;
                return false;
            }
        }

        private static void DeleteIncompleteImportedBackup(string id)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    $@"{BackupRoot}\{id}",
                    writable: false);
                bool imported = Convert.ToInt32(
                    key?.GetValue("Snapshot.Imported", 0) ?? 0,
                    CultureInfo.InvariantCulture) == 1;
                bool complete = Convert.ToInt32(
                    key?.GetValue("Snapshot.Complete", 0) ?? 0,
                    CultureInfo.InvariantCulture) == 1;
                if (imported && !complete)
                {
                    DeleteBackup(id);
                }
            }
            catch
            {
                // Restore remains guarded by the required snapshot fields.
            }
        }

        private static void DeleteImportedBackup(string id)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    $@"{BackupRoot}\{id}",
                    writable: false);
                if (Convert.ToInt32(
                        key?.GetValue("Snapshot.Imported", 0) ?? 0,
                        CultureInfo.InvariantCulture) == 1)
                {
                    DeleteBackup(id);
                }
            }
            catch
            {
                // The original JSON remains available for a later retry.
            }
        }

        private async Task CaptureBcdAsync(string id, string option)
        {
            using RegistryKey backup = Registry.CurrentUser.CreateSubKey(
                $@"{BackupRoot}\{id}",
                writable: true);
            if (backup.GetValue("BcdCaptured") is not null)
            {
                return;
            }

            string? value = await GetBcdValueAsync(option);
            backup.SetValue("BcdExists", value is null ? 0 : 1, RegistryValueKind.DWord);
            backup.SetValue("BcdValue", value ?? string.Empty, RegistryValueKind.String);
            backup.Flush();
            backup.SetValue("BcdCaptured", 1, RegistryValueKind.DWord);
        }

        private async Task RestoreBcdAsync(string id, string option)
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(
                $@"{BackupRoot}\{id}",
                writable: false);
            bool captured = backup?.GetValue("BcdCaptured") is not null;
            bool existed = Convert.ToInt32(
                backup?.GetValue("BcdExists", 0) ?? 0,
                CultureInfo.InvariantCulture) == 1;
            string value = Convert.ToString(
                backup?.GetValue("BcdValue", string.Empty),
                CultureInfo.InvariantCulture) ?? string.Empty;
            if (backup is not null && (backup.GetValue("BcdCaptured") is not int committed || committed != 1 ||
                backup.GetValue("BcdExists") is not int present || present is not (0 or 1) ||
                (existed && string.IsNullOrWhiteSpace(value))))
                throw new InvalidOperationException("The original BCD snapshot is incomplete. Backup retained.");

            if (captured && existed && !string.IsNullOrWhiteSpace(value))
            {
                await RequireCommandSuccessAsync(
                    "bcdedit.exe",
                    new[] { "/set", "{current}", option, value });
            }
            else
            {
                await RequireCommandSuccessAsync(
                    "bcdedit.exe",
                    new[] { "/deletevalue", "{current}", option },
                    tolerateMissingValue: true);
            }
            string? actual = await GetBcdValueAsync(option);
            string? expected = captured && existed ? value : null;
            if (!BcdRestoreVerification.Matches(option, expected, actual))
                throw new InvalidOperationException("BCD restore read-back failed. Backup retained.");
        }

        private async Task<string?> GetBcdValueAsync(string option)
        {
            NativeCommandResult result = await _commandRunner.RunAsync(
                "bcdedit.exe",
                new[] { "/enum", "{current}" },
                TimeSpan.FromSeconds(15));
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.CombinedOutput)
                        ? "Unable to read the current boot configuration."
                        : result.CombinedOutput);
            }

            foreach (string line in result.StandardOutput.Split(
                         new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith(option, StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Length <= option.Length || !char.IsWhiteSpace(trimmed[option.Length]))
                {
                    continue;
                }

                string value = trimmed[option.Length..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            return null;
        }

        private async Task RequireCommandSuccessAsync(
            string executable,
            IReadOnlyList<string> arguments,
            bool tolerateMissingValue = false)
        {
            bool bcdWrite = executable.Equals("bcdedit.exe", StringComparison.OrdinalIgnoreCase) &&
                arguments.Count >= 3 && arguments[1] == "{current}" &&
                (arguments[0] == "/deletevalue" || (arguments[0] == "/set" && arguments.Count == 4));
            string? expected = bcdWrite && arguments[0] == "/set" ? arguments[3] : null;
            if (bcdWrite && BcdRestoreVerification.Matches(arguments[2], expected, await GetBcdValueAsync(arguments[2]))) return;
            NativeCommandResult result = await _commandRunner.RunAsync(
                executable,
                arguments,
                TimeSpan.FromSeconds(20));
            if (bcdWrite)
            {
                if (BcdRestoreVerification.Matches(arguments[2], expected, await GetBcdValueAsync(arguments[2]))) return;
                throw new InvalidOperationException($"BCD write/read-back failed for {arguments[2]} (exit {result.ExitCode}). Backup retained. {result.CombinedOutput}");
            }
            if (result.ExitCode == 0)
            {
                return;
            }

            string output = result.CombinedOutput;
            if (tolerateMissingValue &&
                (output.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("cannot find", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(output)
                    ? $"{executable} failed with exit code {result.ExitCode}."
                    : output);
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

        private static void RestoreRegistryOrDefault(
            string id,
            string tag,
            RegistryHive hive,
            string path,
            string name,
            int? defaultValue)
        {
            if (HasBackup(id))
            {
                RestoreRegistryValue(id, tag, hive, path, name);
            }
            else if (defaultValue.HasValue)
            {
                SetDword(hive, path, name, defaultValue.Value);
            }
            else
            {
                DeleteRegistryValue(hive, path, name);
            }
        }

        private static void RemoveWindowedOptimizationOffToken()
        {
            string current = Convert.ToString(
                ReadRegistryValue(
                    RegistryHive.CurrentUser,
                    WindowedOptimizationPath,
                    "DirectXUserGlobalSettings"),
                CultureInfo.InvariantCulture) ?? string.Empty;
            string cleaned = Regex.Replace(
                current,
                @"(?i)(^|;)SwapEffectUpgradeEnable=0(?=;|$)",
                "$1");
            cleaned = Regex.Replace(cleaned, @";;+", ";").Trim(';');
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                DeleteRegistryValue(
                    RegistryHive.CurrentUser,
                    WindowedOptimizationPath,
                    "DirectXUserGlobalSettings");
            }
            else
            {
                SetString(
                    RegistryHive.CurrentUser,
                    WindowedOptimizationPath,
                    "DirectXUserGlobalSettings",
                    cleaned);
            }
        }

        private static bool HasBackup(string id)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                $@"{BackupRoot}\{id}",
                writable: false);
            return key is not null;
        }

        private static void DeleteBackup(string id)
        {
            try
            {
                using RegistryKey? root = Registry.CurrentUser.OpenSubKey(
                    BackupRoot,
                    writable: true);
                root?.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
            }
            catch
            {
                // The system change has already been verified; a stale backup is safer
                // than deleting unrelated state when registry cleanup is denied.
            }
        }

        private static object? ReadRegistryValue(
            RegistryHive hive,
            string path,
            string name)
        {
            using RegistryKey? key = OpenKey(hive, path, writable: false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        private static int? ReadDword(
            RegistryHive hive,
            string path,
            string name)
        {
            object? value = ReadRegistryValue(hive, path, name);
            if (value is null)
            {
                return null;
            }

            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
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
            using RegistryKey? key = OpenKey(hive, path, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
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

        private readonly record struct PreviousRegistryTarget(
            string SnapshotProperty,
            string Tag,
            RegistryHive Hive,
            string Path,
            string Name);

        private readonly record struct ImportedPreviousRegistryValue(
            PreviousRegistryTarget Target,
            bool Existed,
            RegistryValueKind Kind,
            string Value);
    }
}
