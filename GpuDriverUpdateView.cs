using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed partial class GpuDriverUpdateView : StackPanel
{
    internal InfoBar Notice { get; } = new() { IsOpen = true, IsClosable = false };
    internal TextBlock Details { get; } = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    internal HyperlinkButton SourceLink { get; } = new() { Content = "View official release details", Visibility = Visibility.Collapsed };

    internal GpuDriverUpdateView()
    {
        Spacing = 8;
        Children.Add(Notice);
        Children.Add(Details);
        Children.Add(SourceLink);
        ShowNotChecked();
    }

    internal void ShowNotChecked()
    {
        Notice.Title = "Online driver release check";
        Notice.Severity = InfoBarSeverity.Informational;
        Details.Text = "Select a GPU to check its official vendor catalog. An Internet connection is required. Checks do not install drivers.";
        SourceLink.NavigateUri = null;
        SourceLink.Visibility = Visibility.Collapsed;
    }

    internal void ShowChecking()
    {
        ShowNotChecked();
        Notice.Title = "Checking for a new GPU driver...";
        Details.Text = "Contacting the official vendor catalog for the selected GPU. No driver package is downloaded or installed. You can switch GPU or close this window to cancel.";
    }

    internal void ShowResult(GpuDriverUpdate result)
    {
        Notice.Title = result.Headline;
        Notice.Severity = result.State switch
        {
            GpuUpdateState.UpdateAvailable => InfoBarSeverity.Success,
            GpuUpdateState.Unavailable or GpuUpdateState.ComparisonUnavailable => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational
        };
        Details.Text = result.Report;
        SourceLink.NavigateUri = result.Release?.Source;
        SourceLink.Visibility = result.Release is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
