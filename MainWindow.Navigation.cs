using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace Naufal_Windows_Tech_s_Powertoys;

public sealed partial class MainWindow
{
    private void MainNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // XAML can raise selection while InitializeComponent is still creating content.
        if (HomePage is null || !RootLayout.IsLoaded) return;
        string selected = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "Home";
        HomePage.Visibility = selected == "Home" ? Visibility.Visible : Visibility.Collapsed;
        RepairPage.Visibility = selected == "Repair" ? Visibility.Visible : Visibility.Collapsed;
        InfoPage.Visibility = selected == "Info" ? Visibility.Visible : Visibility.Collapsed;
        SecurityPage.Visibility = selected == "Security" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility = selected == "Advanced" ? Visibility.Visible : Visibility.Collapsed;
        NetworkPage.Visibility = selected == "Network" ? Visibility.Visible : Visibility.Collapsed;
        UiDisplaySettings.Apply(RootLayout);
        if (selected == "Home") OnHomeActivated();
        if (selected == "Network") OnNetworkActivated();
        if (selected == "Advanced") OnAdvancedActivated();
    }

    private void MainNavigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (sender.DisplayMode != NavigationViewDisplayMode.Expanded) sender.IsPaneOpen = false;
    }

    partial void OnHomeActivated();
    partial void OnNetworkActivated();
    partial void OnAdvancedActivated();

    private void HomeGrid_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not Grid grid || grid.ActualWidth <= 0) return;
        double minimum = (grid == StatusCardsGrid || grid == LiveChartsGrid ? 270 : 205) * UiDisplaySettings.GeometryScale;
        int maximum = grid == LiveChartsGrid ? 2 : 3;
        int columns = Math.Clamp((int)((grid.ActualWidth + grid.ColumnSpacing) / (minimum + grid.ColumnSpacing)), 1, maximum);
        int rows = (grid.Children.Count + columns - 1) / columns;
        if (grid.ColumnDefinitions.Count != columns || grid.RowDefinitions.Count != rows)
        {
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < rows; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (int i = 0; i < grid.Children.Count; i++)
        {
            Grid.SetColumn((FrameworkElement)grid.Children[i], i % columns);
            Grid.SetRow((FrameworkElement)grid.Children[i], i / columns);
        }
    }
}
