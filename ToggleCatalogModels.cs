using System.Collections.Generic;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal enum ToolToggleTier
    {
        Unspecified,
        Safe,
        Advanced,
        VeryAggressive
    }

    internal readonly record struct ToolToggleDefinition(
        string Id,
        string Category,
        string Name,
        string Description,
        bool RequiresAdministrator,
        bool RestartRecommended,
        ToolToggleTier SelectionTier = ToolToggleTier.Unspecified,
        string Warning = "",
        bool IsFeatureSwitch = false)
    {
        public string Name { get; init; } = CatalogDisplayNames.Simplify(Name);
    }

    internal readonly record struct ToolToggleState(
        bool IsOn,
        bool IsAvailable,
        string ActualValue,
        string Error = "",
        bool HasAppliedParts = false,
        bool UnavailableOnThisPc = false)
    {
        public string RecoverySource { get; init; } = string.Empty;
        public bool HasSavedOriginal { get; init; }

        // Only an explicit, successful absence probe may set this flag. An
        // unreadable registry, timeout or missing backup is NOT unavailability.
        public bool IsConfirmedUnavailable => !IsAvailable && UnavailableOnThisPc;
        public bool HasReadFailure => !IsAvailable && !IsConfirmedUnavailable;
        public static ToolToggleState Unavailable(string reason) =>
            new(false, false, "Unavailable", reason, UnavailableOnThisPc: true);
    }

    internal readonly record struct ToolToggleOperationResult(
        bool Success,
        bool Verified,
        string Message,
        ToolToggleState State,
        bool DefaultFallbackHandled = false,
        bool SkippedUnavailable = false,
        bool OriginalBackupMissing = false,
        ToolToggleState? BeforeState = null,
        bool AlreadyApplied = false);

    internal static class CatalogRecovery
    {
        internal static string Snapshot(string path, string missing)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path, writable: false);
                return key is null ? missing : "A snapshot record exists for this option. Restore checks its saved values before using them.";
            }
            catch (System.Exception error) when (error is System.UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                return "Could not check the saved settings: " + error.Message;
            }
        }

    }

    internal interface IToolToggleService
    {
        IReadOnlyList<ToolToggleDefinition> GetDefinitions();

        string DescribeRecovery(ToolToggleDefinition definition) => "Recovery source has not been checked for this option.";

        Task<ToolToggleState> ReadStateAsync(ToolToggleDefinition definition);

        Task<ToolToggleOperationResult> SetStateAsync(
            ToolToggleDefinition definition,
            bool targetOn);

        // Restore replays the first-apply snapshot. Windows Default is a
        // separate operation in the reference application and must never be
        // represented by merely forcing the row toggle to OFF.
        Task<ToolToggleOperationResult> RestoreOriginalAsync(
            ToolToggleDefinition definition) =>
            SetStateAsync(definition, targetOn: false);

        Task<ToolToggleOperationResult> RestoreWindowsDefaultAsync(
            ToolToggleDefinition definition) =>
            RestoreOriginalAsync(definition);
    }
}
