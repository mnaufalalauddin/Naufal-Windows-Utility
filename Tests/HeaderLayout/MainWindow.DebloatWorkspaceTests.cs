using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Naufal_Windows_Tech_s_Powertoys;

public sealed partial class MainWindow
{
    private async Task CheckDebloatWorkspaceAsync()
    {
        MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>().Single(i => (string)i.Tag == "Advanced");
        var section = new DebloatWorkspaceSection("De-bloat workspace — provisioned apps for new Windows accounts", "Synthetic inventory only. No service, registry or application mutations are connected in this test host.");
        for (int i = 0; i < 25; i++)
        {
            var card = DebloatWorkspaceView.Card(section.Rows, "Test app " + i,
                "Publisher: example vendor. Package identity and lengthy warnings must remain readable at all supported text scales. Restore original settings before changing the selection.");
            card.Children.Add(DebloatWorkspaceView.Button("Remove provisioning for new Windows accounts"));
        }
        AdvancedPage.Children.Insert(0, section.View);
        section.View.IsExpanded = true;
        try
        {
            foreach (int width in new[] { 480, 800, 1280 })
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (int scale in new[] { 100, 200 })
            {
                _case = $"debloat workspace width={width} theme={theme} scale={scale}";
                AppWindow.Resize(new SizeInt32(width, 1000));
                if (UiDisplaySettings.Theme != theme) UiDisplaySettings.ToggleTheme();
                UiDisplaySettings.SetTextScale(scale);
                UiDisplaySettings.Apply(section.View);
                await Task.Delay(80);
                RootLayout.UpdateLayout();
                Check(section.Viewer.ViewportHeight > 0 && section.Viewer.ScrollableHeight > 0, "debloat inventory has bounded vertical scroll");
                Check(section.Viewer.ScrollableWidth < 1, "debloat cards wrap without horizontal scrolling");
                Check(section.Viewer.IsTabStop && section.Viewer.VerticalScrollMode == ScrollMode.Enabled, "debloat inventory is keyboard scrollable");
                section.Viewer.ChangeView(null, section.Viewer.ScrollableHeight, null, true);
                await Task.Delay(50);
                Check(Math.Abs(section.Viewer.VerticalOffset - section.Viewer.ScrollableHeight) < 2, "last debloat card is reachable");
                foreach (var button in VisualDescendants<Button>(section.Rows))
                {
                    Check(button.Content is TextBlock { TextWrapping: TextWrapping.Wrap }, "long debloat action labels wrap");
                    var label = (TextBlock)button.Content;
                    Check(button.ActualHeight >= label.ActualHeight + button.Padding.Top + button.Padding.Bottom,
                        "debloat button accommodates the full label and vertical padding");
                }
                Check(section.Status.Text.StartsWith("Not scanned"), "rendering does not imply live scan or verification");
                section.Viewer.ChangeView(null, 0, null, true);
                if (width == 1280 && scale == 100)
                {
                    await Task.Delay(120);
                    await SavePreviewAsync("debloat-workspace-fixture-" + theme.ToString().ToLowerInvariant() + ".png");
                }
            }
            File.AppendAllText(App.ResultPath, "De-bloat workspace: PASS; 12 native layout combinations, wrapping action labels, keyboard scrolling and last-card reachability. Synthetic inventory; no backend mutations.\n");
        }
        finally { AdvancedPage.Children.Remove(section.View); UiDisplaySettings.SetTextScale(100); }
    }
}
