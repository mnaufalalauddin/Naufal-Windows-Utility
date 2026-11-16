using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Foundation;
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
            foreach (var size in new[] { new SizeInt32(480, 640), new SizeInt32(800, 480), new SizeInt32(1024, 550), new SizeInt32(1280, 640), new SizeInt32(1366, 700) })
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (int scale in new[] { 100, 200 })
            {
                _case = $"debloat workspace size={size.Width}x{size.Height} theme={theme} scale={scale}";
                AppWindow.Resize(size);
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
                var last = VisualDescendants<Button>(section.Rows).Last();
                last.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
                await Task.Delay(100);
                var bounds = last.TransformToVisual(RootLayout).TransformBounds(new Rect(0, 0, last.ActualWidth, last.ActualHeight));
                Check(bounds.Top >= 0 && bounds.Bottom <= RootLayout.ActualHeight + 1 && bounds.Left >= 0 && bounds.Right <= RootLayout.ActualWidth + 1,
                    $"last workspace action is visible through both scroll containers: {bounds}");
                bool invoked = false;
                RoutedEventHandler handler = (_, _) => invoked = true;
                last.Click += handler;
                ((IInvokeProvider)new ButtonAutomationPeer(last).GetPattern(PatternInterface.Invoke)).Invoke();
                await Task.Delay(30);
                last.Click -= handler;
                Check(invoked, "last workspace action handler is invokable at low resolution");
                foreach (var button in VisualDescendants<Button>(section.Rows))
                {
                    Check(button.Content is TextBlock { TextWrapping: TextWrapping.Wrap }, "long debloat action labels wrap");
                    var label = (TextBlock)button.Content;
                    Check(button.ActualHeight >= label.ActualHeight + button.Padding.Top + button.Padding.Bottom,
                        "debloat button accommodates the full label and vertical padding");
                }
                Check(section.Status.Text.StartsWith("Not scanned"), "rendering does not imply live scan or verification");
                section.Viewer.ChangeView(null, 0, null, true);
                if (size.Width == 1366 && scale == 100)
                {
                    await Task.Delay(120);
                    await SavePreviewAsync("debloat-workspace-fixture-" + theme.ToString().ToLowerInvariant() + ".png");
                }
            }
            File.AppendAllText(App.ResultPath, "De-bloat workspace: PASS; 20 native small-window layout combinations, wrapping action labels, keyboard scrolling, last-card visibility and isolated invocation. Synthetic inventory; no backend mutations.\n");
        }
        finally { AdvancedPage.Children.Remove(section.View); UiDisplaySettings.SetTextScale(100); }
    }
}
