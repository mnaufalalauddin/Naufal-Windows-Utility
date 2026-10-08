using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;

namespace Naufal_Windows_Tech_s_Powertoys;

public sealed partial class MainWindow
{
    private async Task CheckGpuUpdatesAsync()
    {
        var release = new GpuDriverRelease("617.42", "Tue Oct 06, 2026",
            new Uri("https://www.nvidia.com/en-us/drivers/details/280103/"),
            "NVIDIA Game Ready / WHQL (selected model, Windows 11 x64)", ["617.42"]);
        var result = GpuDriverUpdates.Evaluate("NVIDIA", "32.0.16.1714", true, release,
            new DateTimeOffset(2026, 10, 7, 14, 20, 0, TimeSpan.FromHours(7)));
        foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        foreach (int scale in new[] { 25, 100, 200 })
        {
            _case = $"GPU release panel theme={theme}, scale={scale}";
            if (UiDisplaySettings.Theme != theme) UiDisplaySettings.ToggleTheme();
            UiDisplaySettings.SetTextScale(scale);
            var view = new GpuDriverUpdateView();
            var header = new GpuDriverHeader();
            foreach (var channel in new[] { GpuDriverChannel.GameReady, GpuDriverChannel.Studio })
                header.ChannelPicker.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = GpuDriverChannels.Name(channel), TextWrapping = TextWrapping.Wrap }, Tag = channel });
            header.ChannelPicker.SelectedIndex = 0;
            header.ChannelPicker.IsEnabled = true;
            foreach (var action in new[] { header.CheckButton, header.RepairButton, header.InstallButton }) action.IsEnabled = true;
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = "TEST FIXTURE — NVIDIA GeForce RTX 4070 Ti SUPER", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(view);
            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(header);
            Grid.SetRow(scroll, 1);
            body.Children.Add(scroll);
            var dialog = new ToolWindow(this, "GPU Driver Manager — isolated preview", body,
                initialWidth: 880, initialHeight: 640, minimumWidth: 600, minimumHeight: 400);
            _ = dialog.ShowAsync();
            try
            {
                view.ShowResult(result);
                var root = (FrameworkElement)dialog.Content;
                UiDisplaySettings.Apply(root);
                foreach (var size in new[] { new SizeInt32(880, 640), new SizeInt32(620, 430) })
                {
                    dialog.AppWindow.Resize(size);
                    await Task.Delay(60);
                    root.UpdateLayout();
                    Check(view.Notice.Title == "New GPU driver available", "update notification is visible");
                    Check(view.Details.Text.Contains("Release time: Not published by vendor"), "missing release hour is explained");
                    Check(view.Details.Text.Contains("2026-10-06") && view.Details.Text.Contains("617.42"), "version and release date are present");
                    Check(view.Details.IsTextSelectionEnabled && view.Details.TextWrapping == TextWrapping.Wrap, "metadata selectable and wrapped");
                    Check(scroll.ScrollableWidth < 1 && scroll.ViewportHeight > 0, "release panel has bounded vertical viewport without horizontal overflow");
                    scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
                    await Task.Delay(40);
                    Rect viewport = scroll.TransformToVisual(root).TransformBounds(new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight));
                    Rect? first = null;
                    foreach (Button action in new[] { header.CheckButton, header.RepairButton, header.InstallButton })
                    {
                        Rect button = action.TransformToVisual(root).TransformBounds(new Rect(0, 0, action.ActualWidth, action.ActualHeight));
                        Check(button.Bottom <= viewport.Top + 2 && button.Top >= 0, "all GPU actions remain in the same non-scrolling header");
                        Check(button.Left >= -1 && button.Right <= root.ActualWidth + 1, "GPU header action fits window");
                        if (first is { } anchor) Check(Math.Abs(button.Top - anchor.Top) < 1 && Math.Abs(button.Height - anchor.Height) < 1, "three action buttons have equal alignment and height");
                        first ??= button;
                        Check(action.Content is TextBlock { TextWrapping: TextWrapping.Wrap }, "header button labels wrap at high text scaling");
                    }
                    Check(dialog.PrimaryButton.Visibility == Visibility.Collapsed && dialog.SecondaryButton.Visibility == Visibility.Collapsed, "no duplicate install/repair buttons in footer");
                    Check(GpuDriverUpdates.DisplayInstalledVersion("NVIDIA", "32.0.16.1714") == "617.14", "native view uses public NVIDIA version");
                    Check(view.SourceLink.Visibility == Visibility.Visible && view.SourceLink.NavigateUri == release.Source, "official source link exposed");
                    scroll.ChangeView(null, 0, null, disableAnimation: true);
                    await Task.Delay(40);
                    if (scale == 100 && size.Width == 880)
                        await SaveDiskPreviewAsync(root, "gpu-update-" + theme.ToString().ToLowerInvariant() + ".png");
                }
                view.ShowChecking();
                Check(view.SourceLink.Visibility == Visibility.Collapsed, "pending request clears prior result and link");
                Check(!view.Details.Text.Contains("617.42"), "switching GPU cannot show stale version while checking");
                view.ShowResult(new GpuDriverUpdate(GpuUpdateState.Unavailable, null, result.CheckedAt, "Network unavailable; retry later."));
                Check(view.Notice.Severity == InfoBarSeverity.Warning && view.SourceLink.Visibility == Visibility.Collapsed, "failed lookup is warning, not up to date");
                Check(view.Details.Text.Contains("Release time: Not verified"), "failed lookup does not invent release metadata");
                view.ShowResult(result with { State = GpuUpdateState.UpToDate });
                Check(view.Notice.Title == "Driver is up to date for this catalog", "current driver status distinct from update notification");
                header.ChannelPicker.SelectedIndex = 1;
                Check((GpuDriverChannel)((ComboBoxItem)header.ChannelPicker.SelectedItem).Tag == GpuDriverChannel.Studio, "Studio can be selected independently of version");
                int invoked = 0;
                foreach (Button action in new[] { header.CheckButton, header.RepairButton, header.InstallButton })
                {
                    action.Click += (_, _) => invoked++;
                    var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(action);
                    ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
                    await Task.Delay(15);
                }
                Check(invoked == 3, "all three header buttons invoke their own handler; test handlers do not change Windows");
            }
            finally { dialog.Close(); }
        }
        File.AppendAllText(App.ResultPath, "GPU update panel: PASS; 12 native layout combinations and pending/error/current states, synthetic metadata only.\n");
    }
}
