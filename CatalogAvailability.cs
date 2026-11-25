using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Naufal_Windows_Tech_s_Powertoys;

internal static class CatalogAvailability
{
    internal const string SummaryTemplate = "{0} out of {1} have been verified, but {2} tweaks can't be applied due to unavailability on this PC.";
    internal static string Summary(int verified, int total, int unavailable) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, SummaryTemplate, verified, total, unavailable);

    // File.Exists also returns false for access errors. Those must stay errors.
    internal static bool FileIsPresent(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException("Expected a file but found a directory: " + path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static ToolToggleState Aggregate(IReadOnlyList<ToolToggleState> states)
    {
        var available = states.Where(state => state.IsAvailable).ToArray();
        int applied = available.Count(state => state.IsOn);
        int unavailable = states.Count(state => state.IsConfirmedUnavailable);
        int failed = states.Count(state => state.HasReadFailure);
        string actual = $"Applied {applied}/{available.Length}" +
            (unavailable > 0 ? $"; Not applicable {unavailable}" : string.Empty) +
            (failed > 0 ? $"; Verification failed {failed}" : string.Empty);
        string detail = string.Join("; ", states.Where(state => !string.IsNullOrWhiteSpace(state.Error))
            .Select(state => state.Error).Distinct(StringComparer.OrdinalIgnoreCase));
        return new(
            failed == 0 && available.Length > 0 && applied == available.Length,
            failed == 0 && available.Length > 0,
            actual, detail,
            HasAppliedParts: states.Any(state => state.IsOn || state.HasAppliedParts),
            UnavailableOnThisPc: states.Count > 0 && unavailable == states.Count)
            { HasSavedOriginal = states.Any(state => state.HasSavedOriginal) };
    }
}
