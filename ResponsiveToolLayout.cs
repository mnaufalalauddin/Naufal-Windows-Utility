using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;

namespace Naufal_Windows_Tech_s_Powertoys;

internal static class ResponsiveToolLayout
{
    // Fixed footer actions stay outside the scrolling content. Multi-row tool
    // bodies gain an overflow escape on short windows instead of clipping their
    // Auto rows against a minimum-height inventory. Already scrollable dialogs
    // keep their single bounded viewport.
    internal static FrameworkElement Body(FrameworkElement content)
    {
        if (content is ScrollViewer) return content;
        ScrollViewer viewport = new()
        {
            Content = content, IsTabStop = true,
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        viewport.SizeChanged += (_, _) =>
        {
            bool shortWindow = viewport.ActualHeight < 600 * UiDisplaySettings.GeometryScale;
            viewport.VerticalScrollBarVisibility = shortWindow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        };
        return viewport;
    }

    internal static Grid Footer(params Button[] buttons)
    {
        Grid grid = new() { ColumnSpacing = 8, RowSpacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var button in buttons)
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            grid.Children.Add(button);
            button.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Arrange());
        }
        grid.SizeChanged += (_, _) => Arrange();
        void Arrange()
        {
            Button[] visible = buttons.Where(b => b.Visibility == Visibility.Visible).ToArray();
            if (visible.Length == 0 || grid.ActualWidth <= 0) return;
            int columns = Math.Max(1, Math.Min(visible.Length, (int)((grid.ActualWidth + 8) / (120 * UiDisplaySettings.GeometryScale + 8))));
            int rows = (visible.Length + columns - 1) / columns;
            if (grid.ColumnDefinitions.Count != columns || grid.RowDefinitions.Count != rows)
            {
                grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
                for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                for (int i = 0; i < rows; i++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            }
            for (int i = 0; i < visible.Length; i++) { Grid.SetRow(visible[i], i / columns); Grid.SetColumn(visible[i], i % columns); }
        }
        return grid;
    }
}
