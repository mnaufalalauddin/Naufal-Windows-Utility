using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Naufal_Windows_Tech_s_Powertoys;

internal static class CatalogVerificationReport
{
    public static string FormatResult(string name, string operation, ToolToggleOperationResult result)
    {
        string status = result.SkippedUnavailable && result.State.IsConfirmedUnavailable ? "UNAVAILABLE" :
            result.Success && result.Verified ? "VERIFIED" : "FAILED / NOT VERIFIED";
        return $"{operation}: {name}\nResult: {status}\n" +
            $"Before: {Describe(result.BeforeState)}\nAfter: {Describe(result.State)}\n\n{result.Message}";
    }

    private static string Describe(ToolToggleState? snapshot)
    {
        if (snapshot is not ToolToggleState state) return "Not captured";
        if (state.IsConfirmedUnavailable) return $"Unavailable on this PC — {state.Error}";
        if (state.HasReadFailure) return $"Unable to read — {state.Error}";
        return $"{(state.IsOn ? "ON" : state.HasAppliedParts ? "PARTIAL" : "OFF")} — {state.ActualValue}";
    }

    public static string Build(string operation, DateTimeOffset started, TimeSpan elapsed, string summary,
        IEnumerable<(string Id, string Name, CatalogItemProgress Progress)> items)
    {
        StringBuilder report = new();
        report.AppendLine(operation);
        report.AppendLine("Started: " + started.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        report.AppendLine($"Elapsed: {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");
        report.AppendLine(summary);
        foreach (var item in items)
        {
            report.AppendLine();
            report.AppendLine($"[{item.Progress.Phase}] {item.Name} ({item.Id})");
            report.AppendLine(item.Progress.Detail);
        }
        return report.ToString();
    }
}
