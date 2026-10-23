using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed class DebloatWorkspaceSection
{
    internal StackPanel Panel { get; } = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
    internal StackPanel Rows { get; } = new() { Spacing = 10 };
    internal TextBlock Status { get; } = DebloatWorkspaceView.Text("Not scanned. Choose Analyze / reload.");
    internal Button Reload { get; } = DebloatWorkspaceView.Button("Analyze / reload");
    internal ScrollViewer Viewer { get; }
    internal Expander View { get; }
    internal DebloatWorkspaceSection(string title, string description)
    {
        Viewer = new ScrollViewer { Content = Rows, MaxHeight = 520, IsTabStop = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Panel.Children.Add(DebloatWorkspaceView.Text(description));
        Panel.Children.Add(Reload); Panel.Children.Add(Status); Panel.Children.Add(Viewer);
        View = new Expander { Header = DebloatWorkspaceView.Text(title), Content = Panel,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }
}

internal static class DebloatWorkspaceView
{
    internal static TextBlock Text(string text) => new()
    { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    internal static Button Button(string text)
    {
        var label = Text(text); label.IsTextSelectionEnabled = false;
        label.TextAlignment = TextAlignment.Center;
        // Explicit geometry is captured before attachment by the scale manager;
        // do not depend on an implicit style that is resolved later in the tree.
        return new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 36, Padding = new Thickness(12, 8, 12, 8) };
    }
    internal static StackPanel Card(StackPanel rows, string name, string details)
    {
        StackPanel card = new() { Spacing = 8, Padding = new Thickness(12) };
        card.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        card.Children.Add(Text(details));
        rows.Children.Add(new Border { Child = card, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray) });
        return card;
    }
}
