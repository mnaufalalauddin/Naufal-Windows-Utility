namespace Naufal_Windows_Tech_s_Powertoys;

internal enum CatalogStateView { All, On, Off, Partial, Unavailable, NotVerified }

internal static class CatalogStateFilter
{
    internal static CatalogStateView Classify(ToolToggleState? state, bool scanCurrent)
    {
        if (!scanCurrent || state is null) return CatalogStateView.NotVerified;
        if (state.Value.IsConfirmedUnavailable) return CatalogStateView.Unavailable;
        if (!state.Value.IsAvailable) return CatalogStateView.NotVerified;
        if (state.Value.IsOn) return CatalogStateView.On;
        return state.Value.HasAppliedParts ? CatalogStateView.Partial : CatalogStateView.Off;
    }

    internal static bool Includes(CatalogStateView view, ToolToggleState? state, bool scanCurrent) =>
        view == CatalogStateView.All || view == Classify(state, scanCurrent);

    internal static string Label(CatalogStateView view) => view switch
    {
        CatalogStateView.All => "All options",
        CatalogStateView.On => "ON",
        CatalogStateView.Off => "OFF",
        CatalogStateView.Partial => "Partially applied",
        CatalogStateView.Unavailable => "Unavailable",
        _ => "Not verified"
    };
}
