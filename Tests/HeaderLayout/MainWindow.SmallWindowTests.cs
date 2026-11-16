using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Foundation;
using Windows.Graphics;

namespace Naufal_Windows_Tech_s_Powertoys;

public sealed partial class MainWindow
{
    private async Task CheckSmallWindowsAsync()
    {
        foreach (var size in new[] { new SizeInt32(1366, 700), new SizeInt32(1280, 640), new SizeInt32(1024, 550), new SizeInt32(800, 480) })
        foreach (int scale in new[] { 100, 150, 200 })
        {
            UiDisplaySettings.SetTextScale(scale);
            _case = $"small tool {size.Width}x{size.Height}, text {scale}%";
            StackPanel content = new() { Spacing = 8 };
            for (int i = 0; i < 30; i++) content.Children.Add(DebloatWorkspaceView.Text("Synthetic inventory card " + i + " — lengthy description and recovery details. No mutations connected."));
            Button last = DebloatWorkspaceView.Button("Analyze / reload — last action in a long tool panel");
            content.Children.Add(last);
            ToolWindow window = new(this, "Advanced Windows Tweaks & De-Bloat — small display", content,
                primaryButtonText: "Apply selected options", secondaryButtonText: "Restore original configuration", closeButtonText: "Close",
                initialWidth: size.Width, initialHeight: size.Height, minimumWidth: 360, minimumHeight: 300);
            _ = window.ShowAsync();
            try
            {
                window.AppWindow.Resize(size);
                await Task.Delay(100);
                FrameworkElement root = (FrameworkElement)window.Content; root.UpdateLayout();
                Check(window.AppWindow.Size.Width <= size.Width && window.AppWindow.Size.Height <= size.Height,
                    "text scale does not force the window above the requested small-screen size");
                var viewer = VisualDescendants<ScrollViewer>(root).Single(v => ReferenceEquals(v.Content, content));
                Check(viewer.ActualHeight > 40 && viewer.ScrollableHeight > 0, "tool content has a usable scrolling viewport");
                Check(viewer.ScrollableWidth < 1, "tool content wraps without sideways loss");
                viewer.ChangeView(null, viewer.ScrollableHeight, null, true);
                await Task.Delay(60);
                Rect lastBounds = last.TransformToVisual(viewer).TransformBounds(new Rect(0, 0, last.ActualWidth, last.ActualHeight));
                Check(lastBounds.Top >= -1 && lastBounds.Bottom <= viewer.ActualHeight + 1, $"last tool action is reachable at low resolution (action={lastBounds}; viewport={viewer.ActualHeight})");
                int clicks = 0;
                foreach (var button in new[] { last, window.PrimaryButton, window.SecondaryButton, window.CloseButton })
                {
                    Rect bounds = button.TransformToVisual(root).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                    Check(bounds.Left >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Top >= -1 && bounds.Bottom <= root.ActualHeight + 1,
                        "action bounds remain entirely in the visible window: " + button.Content);
                    if (button == window.CloseButton) continue;
                    button.Click += (_, _) => clicks++;
                    var peer = new ButtonAutomationPeer(button);
                    ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                }
                await Task.Delay(50);
                Check(clicks == 3, "visible last, Apply and Restore buttons invoke their handlers");
            }
            finally { window.Close(); }
        }
        UiDisplaySettings.SetTextScale(100);
        File.AppendAllText(App.ResultPath, "Small-window tools: PASS; 1366/1280/1024/800 widths, 480–700 heights, 100/150/200% text, reachable body/footer actions and isolated button invocation.\n");
    }
}
