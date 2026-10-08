using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Naufal_Windows_Tech_s_Powertoys;

// One shared, non-scrolling action header. Wrapped labels and equal star columns
// keep all three actions reachable at high text scale without a second footer.
internal sealed partial class GpuDriverHeader : StackPanel
{
    internal ComboBox ChannelPicker { get; } = new() { Header = "Driver type (verified for the selected GPU)",
        PlaceholderText = "Checking compatible driver types...", HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 240, IsEnabled = false };
    internal Button CheckButton { get; } = CreateButton("Check for updates");
    internal Button RepairButton { get; } = CreateButton("Repair driver");
    internal Button InstallButton { get; } = CreateButton("Download & install");
    internal Grid Actions { get; } = new() { ColumnSpacing = 8 };
    internal GpuDriverHeader()
    {
        Spacing = 8;
        Margin = new Thickness(0, 0, 0, 12);
        Children.Add(ChannelPicker);
        Children.Add(Actions);
        Button[] buttons = [CheckButton, RepairButton, InstallButton];
        for (int index = 0; index < buttons.Length; index++)
        {
            Actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(buttons[index], index);
            Actions.Children.Add(buttons[index]);
        }
    }
    private static Button CreateButton(string text) => new()
    {
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, IsHitTestVisible = false },
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(10, 7, 10, 7),
        MinHeight = 34, IsEnabled = false
    };
}
