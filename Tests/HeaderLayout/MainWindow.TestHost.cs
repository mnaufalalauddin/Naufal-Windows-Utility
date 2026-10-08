using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Foundation;
using Windows.Graphics;

namespace Naufal_Windows_Tech_s_Powertoys;

public sealed partial class MainWindow : Window
{
    private int _checks;
    private int _cases;
    private string _case = "initialization";
    private double _minimumHeaderContrast = double.MaxValue;
    private double _minimumButtonContrast = double.MaxValue;
    private readonly Border _catalogTestRoot = new()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.White),
        Child = new StackPanel { Spacing = 8 }
    };
    public MainWindow()
    {
        InitializeComponent();
        Title = "Isolated header layout tests — no Windows changes";
        UiDisplaySettings.KeepRecoveryControlUsable(ThemeButton);
        UiDisplaySettings.KeepRecoveryControlUsable(TextScaleButton);
        UiDisplaySettings.KeepRecoveryControlUsable(TextScaleGlyph);
        UiDisplaySettings.Changed += DisplayChanged;
        UiDisplaySettings.Apply(RootLayout);
        RootLayout.Loaded += Run;
        Closed += (_, _) => { UiDisplaySettings.Changed -= DisplayChanged; };
    }

    private void DisplayChanged(object? sender, EventArgs e)
    {
        UiDisplaySettings.Apply(RootLayout);
        UpdateHeaderLayout();
    }

    private async void Run(object sender, RoutedEventArgs e)
    {
        RootLayout.Loaded -= Run;
        try
        {
            _case = $"saved startup scale={UiDisplaySettings.TextScalePercent}";
            await Task.Delay(50);
            RootLayout.UpdateLayout();
            UpdateHeaderLayout();
            RootLayout.UpdateLayout();
            CheckHeader();
            int[] sequence = [25, 50, 75, 100, 125, 150, 175, 200, 175, 150, 125, 100, 75, 50, 25, 100];
            const string language = "en";
            foreach (int width in new[] { 1920, 1280, 800, 480 })
            foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                AppWindow.Resize(new SizeInt32(width, 800));
                if (UiDisplaySettings.Theme != theme) UiDisplaySettings.ToggleTheme();
                foreach (int percent in sequence)
                {
                    _case = $"width={width}, theme={theme}, language={language}, scale={percent}";
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    UiDisplaySettings.SetTextScale(percent);
                    await Task.Delay(30);
                    RootLayout.UpdateLayout();
                    CheckHeader();
                    _cases++;
                }
            }
            foreach (int percent in sequence.Take(8))
            {
                _case = $"flyout scale={percent}";
                UiDisplaySettings.SetTextScale(percent);
                await Task.Delay(30);
                RootLayout.UpdateLayout();
                TextScaleButton_Click(TextScaleButton, new RoutedEventArgs());
                await Task.Delay(50);
                var popup = (MenuFlyout)FlyoutBase.GetAttachedFlyout(TextScaleButton);
                Check(popup.IsOpen, "scaling flyout opens");
                Check(popup.Items.Count == 10, "all eight options, separator and reset available");
                Check(popup.Items.OfType<ToggleMenuFlyoutItem>().All(item => item.FontSize >= 14), "readable flyout");
                popup.Hide();
            }
            await CheckNavigationAsync();
            await CheckNavigationPaneAsync();
            await CheckDynamicButtonsAsync();
            await SaveThemePreviewsAsync();
            await CheckDiskDashboardAsync();
            await CheckDialogScrollingAsync();
            await CheckGpuUpdatesAsync();
            File.AppendAllText(App.ResultPath, $"PASS: {_checks} native WinUI assertions, {_cases} layout cases, 8 flyouts. Minimum header contrast: {_minimumHeaderContrast:F2}:1; button contrast: {_minimumButtonContrast:F2}:1. No Windows settings changed.\n");
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            File.AppendAllText(App.ResultPath, "FAIL: " + _case + "\n" + exception + "\n");
            Environment.ExitCode = 1;
        }
        finally { Close(); }
    }

    private async Task CheckNavigationAsync()
    {
        var pages = new FrameworkElement[] { HomePage, RepairPage, InfoPage, SecurityPage, AdvancedPage };
        Check(MainNavigation.MenuItems.Count == 5, "exactly five navigation pages");
        foreach (int width in new[] { 1920, 1280, 800, 480 })
        foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        foreach (int percent in new[] { 25, 100, 200 })
        {
            AppWindow.Resize(new SizeInt32(width, 1000));
            if (UiDisplaySettings.Theme != theme) UiDisplaySettings.ToggleTheme();
            UiDisplaySettings.SetTextScale(percent);
            // Let the automatic sidebar transition finish before comparing pages.
            await Task.Delay(400);
            Rect? homeToolbar = null;
            for (int index = 0; index < pages.Length; index++)
            {
                _case = $"navigation width={width}, theme={theme}, scale={percent}, page={pages[index].Name}";
                MainNavigation.SelectedItem = MainNavigation.MenuItems[index];
                await Task.Delay(40);
                if (MainNavigation.DisplayMode != NavigationViewDisplayMode.Expanded) MainNavigation.IsPaneOpen = false;
                RootLayout.UpdateLayout();
                Check(pages[index].Visibility == Visibility.Visible, "selected page visible");
                Check(pages.Count(p => p.Visibility == Visibility.Visible) == 1, "only one page visible");
                CheckButtons();
                CheckUtilityButtonLayout();
                CheckUtilityViewport();
                Rect toolbar = UtilityButtonsGrid.TransformToVisual(RootLayout).TransformBounds(
                    new Rect(0, 0, UtilityButtonsGrid.ActualWidth, UtilityButtonsGrid.ActualHeight));
                Point pageOrigin = pages[index].TransformToVisual(RootLayout).TransformPoint(new Point());
                Check(Math.Abs(pageOrigin.X - toolbar.Left) <= 1, "page and shared toolbar have the same left edge");
                homeToolbar ??= toolbar;
                Check(Math.Abs(toolbar.Left - homeToolbar.Value.Left) <= 1 && Math.Abs(toolbar.Width - homeToolbar.Value.Width) <= 1,
                    $"toolbar position/width stable across pages: Home={homeToolbar}, current={toolbar}");
                _cases++;
            }
        }
        MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
        await Task.Delay(60);
    }

    private async Task CheckNavigationPaneAsync()
    {
        AppWindow.Resize(new SizeInt32(1280, 1000));
        UiDisplaySettings.SetTextScale(100);
        MainNavigation.SelectedItem = MainNavigation.MenuItems[2];
        var inventory = InfoPage.Children.OfType<Expander>().Single();
        string originalText = CatalogInventoryText.Text;
        try
        {
            CatalogInventoryText.Text = new string('W', 800) + "\n" + string.Join("\n", Enumerable.Repeat("Synthetic inventory row", 30));
            inventory.IsExpanded = true;
            foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (bool paneOpen in new[] { false, true })
            {
                _case = $"expanded inventory, theme={theme}, sidebar open={paneOpen}";
                if (UiDisplaySettings.Theme != theme) UiDisplaySettings.ToggleTheme();
                MainNavigation.IsPaneOpen = paneOpen;
                await Task.Delay(400);
                RootLayout.UpdateLayout();
                CheckUtilityButtonLayout();
                CheckUtilityViewport();
                _cases++;
            }
        }
        finally
        {
            CatalogInventoryText.Text = originalText;
            inventory.IsExpanded = false;
            MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
        }
    }

    private void CheckUtilityViewport()
    {
        var viewport = (ScrollViewer)MainNavigation.Content;
        Rect viewportBounds = viewport.TransformToVisual(RootLayout).TransformBounds(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
        foreach (Button button in new[] { AboutButton, TaskStatusButton, ExitButton })
        {
            Rect bounds = button.TransformToVisual(RootLayout).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            Check(bounds.Left >= Math.Max(0, viewportBounds.Left) && bounds.Right <= Math.Min(RootLayout.ActualWidth, viewportBounds.Right) + 1,
                $"{button.Name} is inside the visible navigation viewport: button={bounds}, viewport={viewportBounds}");
            Rect hostBounds = button.TransformToVisual(null).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            Point center = new(hostBounds.X + hostBounds.Width / 2, hostBounds.Y + hostBounds.Height / 2);
            Check(VisualTreeHelper.FindElementsInHostCoordinates(center, RootLayout).Contains(button), button.Name + " center is hit-testable on the active page");
        }
    }

    private void CheckHeader()
    {
        Check(RootLayout.Language == "en-US" && RootLayout.FlowDirection == FlowDirection.LeftToRight, "English-only presentation");
        var homeHeading = HomePage.Children.OfType<TextBlock>().First();
        Check(Math.Abs(homeHeading.FontSize - Math.Max(4, 28 * UiDisplaySettings.TextScalePercent / 100d)) < 0.01, "unnamed Home heading scales from original size after GC");
        CheckThemePalette();
        CheckButtons();
        CheckUtilityButtonLayout();
        CheckUtilityViewport();
        foreach (FrameworkElement control in new FrameworkElement[] { TextScaleButton, ThemeButton })
        {
            Rect bounds = control.TransformToVisual(RootLayout).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
            Check(bounds.Left >= -0.5 && bounds.Right <= RootLayout.ActualWidth + 0.5,
                $"{control.Name} inside width {RootLayout.ActualWidth}: {bounds}");
            Check(bounds.Top >= 0 && bounds.Bottom <= RootLayout.ActualHeight, $"{control.Name} inside height");
            Check(control.ActualWidth > 0 && control.ActualHeight > 0, $"{control.Name} is not collapsed");
        }
        Check(TextScaleButton.ActualWidth >= 32 && TextScaleButton.ActualHeight >= 32, "recovery hit target at least 32 DIP");
        // Hit-test coordinates belong to the XamlRoot host, not the root Grid's
        // logical coordinate space (which is mirrored in Arabic/Urdu).
        Rect button = TextScaleButton.TransformToVisual(null).TransformBounds(new Rect(0, 0, TextScaleButton.ActualWidth, TextScaleButton.ActualHeight));
        Point center = new(button.X + button.Width / 2, button.Y + button.Height / 2);
        Check(VisualTreeHelper.FindElementsInHostCoordinates(center, RootLayout).Contains(TextScaleButton), "scaling button center is hit-testable");
    }

    private void CheckUtilityButtonLayout()
    {
        Button[] buttons = [AboutButton, TaskStatusButton, ExitButton];
        var first = buttons[0];
        foreach (Button button in buttons)
        {
            Check(double.IsNaN(button.Width), button.Name + " has no fixed width");
            Check(Math.Abs(button.ActualWidth - first.ActualWidth) <= 1, button.Name + " fills an equal responsive column");
            Check(Math.Abs(button.ActualHeight - first.ActualHeight) <= 1, button.Name + " matches adjacent heights");
            Check(button.Padding.Equals(first.Padding) && button.Margin.Equals(first.Margin), button.Name + " shares padding and spacing");
            Check(button.CornerRadius.Equals(first.CornerRadius) && button.FontSize == first.FontSize, button.Name + " shares radius and font size");
            Check(button.HorizontalAlignment == HorizontalAlignment.Stretch && button.VerticalAlignment == VerticalAlignment.Center,
                button.Name + " uses shared alignment");
            Rect bounds = button.TransformToVisual(UtilityButtonsGrid).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            Check(bounds.Left >= -1 && bounds.Right <= UtilityButtonsGrid.ActualWidth + 1, button.Name + " fits the available row");
            TextBlock measuredLabel = new()
            {
                Text = button.Content.ToString(), FontSize = button.FontSize,
                FontFamily = button.FontFamily, FontWeight = button.FontWeight
            };
            measuredLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Check(measuredLabel.DesiredSize.Width <= button.ActualWidth - button.Padding.Left - button.Padding.Right - 2 + 1,
                button.Name + " has enough width for the full label");
            Check(measuredLabel.DesiredSize.Height <= button.ActualHeight - button.Padding.Top - button.Padding.Bottom - 2 + 1,
                button.Name + " has enough height for the full label");
        }
        for (int index = 1; index < buttons.Length; index++)
        {
            Button previous = buttons[index - 1], next = buttons[index];
            Rect before = previous.TransformToVisual(UtilityButtonsGrid).TransformBounds(new Rect(0, 0, previous.ActualWidth, previous.ActualHeight));
            Rect after = next.TransformToVisual(UtilityButtonsGrid).TransformBounds(new Rect(0, 0, next.ActualWidth, next.ActualHeight));
            if (Grid.GetRow(previous) == Grid.GetRow(next))
                Check(Math.Abs(after.Left - before.Right - UtilityButtonsGrid.ColumnSpacing) <= 1, "consistent horizontal action spacing");
            else
                Check(after.Top >= before.Bottom + UtilityButtonsGrid.RowSpacing - 1, "wrapped actions do not overlap");
        }
        Check(RootLayout.FindName("RebootButton") is null, "standalone Reboot button removed");
    }

    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(_case + ": " + message);
        _checks++;
    }

    private void CheckThemePalette()
    {
        Check(RootLayout.Children[0] is Border, "header Border projection: " + RootLayout.Children[0].GetType().FullName);
        var header = (Border)RootLayout.Children[0];
        Check(header.Background is SolidColorBrush, "header brush projection: " + header.Background?.GetType().FullName);
        var background = ((SolidColorBrush)header.Background!).Color;
        foreach (var pair in new[]
        {
            ("Clock", ClockText.Foreground), ("Date", DateText.Foreground), ("Session", SessionText.Foreground)
        })
        {
            Check(pair.Item2 is SolidColorBrush, pair.Item1 + " has a solid text brush");
            var foreground = ((SolidColorBrush)pair.Item2).Color;
            double ratio = Contrast(foreground, background);
            _minimumHeaderContrast = Math.Min(_minimumHeaderContrast, ratio);
            Check(ratio >= 4.5, $"{pair.Item1} contrast >= 4.5:1, actual {ratio:F2}:1 in {UiDisplaySettings.Theme}");
        }
        Check(((SolidColorBrush)FullRepairButton.Foreground).Color == Microsoft.UI.Colors.White,
            "explicit white text on primary colored buttons preserved");
        Check(ClockText.ReadLocalValue(TextBlock.ForegroundProperty) == DependencyProperty.UnsetValue,
            "theme-owned header foregrounds are never frozen as local values");
        if (UiDisplaySettings.Theme == ElementTheme.Light)
            Check(((SolidColorBrush)DateText.Foreground).Color == Windows.UI.Color.FromArgb(255, 52, 69, 92),
                "explicit muted light text restored exactly");
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (T nested in VisualDescendants<T>(child)) yield return nested;
        }
    }

    private void CheckButtons()
    {
        Button[] buttons = AuthoredButtons(RootLayout).Where(b => b.IsEnabled).ToArray();
        Check(buttons.Length == 33, "all 33 enabled authored buttons are covered across all five pages");
        Check(!buttons.Any(b => b.Content?.ToString()?.Contains("Resource Analyzer") == true), "retired analyzer absent");
        Check(buttons.Contains(AboutButton), "About participates in dashboard contrast checks");
        Check(AboutButton.Content?.ToString() == "About",
            "About caption remains English");
        foreach (Button button in buttons)
            CheckButtonContrast(button);
    }

    private void CheckButtonContrast(Button button)
    {
        string name = string.IsNullOrEmpty(button.Name) ? button.Content?.ToString() ?? "button" : button.Name;
        Check(button.Foreground is SolidColorBrush && button.Background is SolidColorBrush, name + " uses solid brushes");
        var background = ((SolidColorBrush)button.Background).Color;
        double ratio = Contrast(((SolidColorBrush)button.Foreground).Color, background);
        _minimumButtonContrast = Math.Min(_minimumButtonContrast, ratio);
        Check(ratio >= 4.5, $"{name} button contrast >= 4.5:1, actual {ratio:F2}:1 in {UiDisplaySettings.Theme}");
        foreach (TextBlock label in VisualDescendants<TextBlock>(button).Where(t => !string.IsNullOrEmpty(t.Text)))
        {
            Check(label.Foreground is SolidColorBrush, name + " rendered label has a solid brush");
            double textRatio = Contrast(((SolidColorBrush)label.Foreground).Color, background);
            Check(textRatio >= 4.5, $"{name} rendered label contrast >= 4.5:1, actual {textRatio:F2}:1");
        }
    }

    private static IEnumerable<Button> AuthoredButtons(DependencyObject parent)
    {
        if (parent is Button button) yield return button;
        IEnumerable<DependencyObject> children = parent switch
        {
            Panel panel => panel.Children.Cast<DependencyObject>(),
            Border border when border.Child is not null => new[] { border.Child },
            ContentControl control when control.Content is DependencyObject content => new[] { content },
            _ => Array.Empty<DependencyObject>()
        };
        foreach (DependencyObject child in children)
            foreach (Button nested in AuthoredButtons(child)) yield return nested;
    }

    private async Task CheckDynamicButtonsAsync()
    {
        var panel = (StackPanel)_catalogTestRoot.Child;
        // Catalogs use their own windows in production; do not detach/reparent
        // the dashboard while testing a separate catalog's theme inheritance.
        Window catalogWindow = new() { Content = _catalogTestRoot };
        catalogWindow.Activate();
        try
        {
        foreach (ElementTheme startingTheme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            if (UiDisplaySettings.Theme != startingTheme) UiDisplaySettings.ToggleTheme();
            UiDisplaySettings.Apply(_catalogTestRoot);
            Button implicitButton = new() { Content = "Dynamic implicit button" };
            Button styledButton = new() { Content = "Dynamic styled button", Style = QuickRepairButton.Style };
            Button localButton = new()
            {
                Content = "Dynamic local colors",
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 11, 21, 32)),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 238, 243, 249))
            };
            panel.Children.Add(implicitButton);
            panel.Children.Add(styledButton);
            panel.Children.Add(localButton);
            foreach (ElementTheme theme in new[] { startingTheme, ElementTheme.Light, ElementTheme.Dark, startingTheme })
            {
                _case = $"dynamic buttons created in {startingTheme}, current {theme}";
                if (UiDisplaySettings.Theme != theme) UiDisplaySettings.ToggleTheme();
                UiDisplaySettings.Apply(_catalogTestRoot);
                await Task.Delay(40);
                _catalogTestRoot.UpdateLayout();
                foreach (Button button in new[] { implicitButton, styledButton, localButton })
                {
                    CheckButtonContrast(button);
                    button.IsEnabled = false;
                    button.IsEnabled = true;
                    CheckButtonContrast(button);
                }
                foreach (Button button in new[] { implicitButton, styledButton })
                {
                    Check(button.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue &&
                        button.ReadLocalValue(Control.BackgroundProperty) == DependencyProperty.UnsetValue &&
                        button.ReadLocalValue(Control.BorderBrushProperty) == DependencyProperty.UnsetValue,
                        "style-owned button colors are not frozen locally");
                    button.Style = FullRepairButton.Style;
                    UiDisplaySettings.Apply(_catalogTestRoot);
                    await Task.Delay(20);
                    Check(((SolidColorBrush)button.Foreground).Color == Microsoft.UI.Colors.White,
                        "switch to primary style retains white text");
                    Check(((SolidColorBrush)button.Background).Color == ((SolidColorBrush)FullRepairButton.Background).Color,
                        "switch to primary style retains primary background");
                    button.Style = QuickRepairButton.Style;
                    UiDisplaySettings.Apply(_catalogTestRoot);
                    await Task.Delay(20);
                    CheckButtonContrast(button);
                }
            }
            panel.Children.Clear();
        }
        }
        finally
        {
            catalogWindow.Close();
        }
    }

    private async Task SaveThemePreviewsAsync()
    {
        UiDisplaySettings.SetTextScale(100);
        AppWindow.Resize(new SizeInt32(1920, 1080));
        MainNavigation.IsPaneOpen = true;
        foreach (ElementTheme theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            _case = "render preview " + theme;
            if (UiDisplaySettings.Theme != theme) UiDisplaySettings.ToggleTheme();
            UiDisplaySettings.Apply(RootLayout);
            await Task.Delay(700);
            MainNavigation.IsPaneOpen = true;
            await Task.Delay(700);
            RootLayout.UpdateLayout();
            Check(MainNavigation.DisplayMode == NavigationViewDisplayMode.Expanded && MainNavigation.IsPaneOpen, "wide sidebar shows labels");
            File.AppendAllText(App.ResultPath, $"Preview navigation: width={MainNavigation.ActualWidth}; mode={MainNavigation.DisplayMode}; open={MainNavigation.IsPaneOpen}\n");
            CheckHeader();
            await SavePreviewAsync($"theme-preview-{theme.ToString().ToLowerInvariant()}.png");
            for (int index = 1; index < MainNavigation.MenuItems.Count; index++)
            {
                var item = (NavigationViewItem)MainNavigation.MenuItems[index];
                _case = $"render navigation preview {item.Tag}, {theme}";
                MainNavigation.SelectedItem = item;
                await Task.Delay(300);
                RootLayout.UpdateLayout();
                CheckUtilityViewport();
                await SavePreviewAsync($"navigation-preview-{item.Tag}-{theme.ToString().ToLowerInvariant()}.png");
            }
            MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
        }
    }

    private async Task SavePreviewAsync(string name)
    {
        RenderTargetBitmap bitmap = new();
        await bitmap.RenderAsync(RootLayout);
        byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
        Check(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "preview has nonzero dimensions");
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        using var stream = file.AsRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    private static double Contrast(Windows.UI.Color a, Windows.UI.Color b)
    {
        if (a.A < 255)
        {
            double opacity = a.A / 255d;
            byte Blend(byte front, byte back) => (byte)Math.Round(front * opacity + back * (1 - opacity));
            a = Windows.UI.Color.FromArgb(255, Blend(a.R, b.R), Blend(a.G, b.G), Blend(a.B, b.B));
        }
        static double Luminance(Windows.UI.Color c)
        {
            static double Channel(byte v)
            {
                double s = v / 255d;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private void ThemeButton_Click(object s, RoutedEventArgs e) => UiDisplaySettings.ToggleTheme();
    private void HomeQuickRepair_Click(object s, RoutedEventArgs e) { }
    private void HomeSystemReport_Click(object s, RoutedEventArgs e) { }
    private void HomeShaderCache_Click(object s, RoutedEventArgs e) { }
    // MainWindow.xaml is linked unchanged; all system-action handlers are inert
    // in this test-only host, so accidental input cannot launch a repair/tweak.
    private void TaskStatusButton_Click(object s, RoutedEventArgs e) { }
    private void AboutButton_Click(object s, RoutedEventArgs e) { }
    private void ExitButton_Click(object s, RoutedEventArgs e) { }
    private void FullRepairButton_Click(object s, RoutedEventArgs e) { }
    private void QuickRepairButton_Click(object s, RoutedEventArgs e) { }
    private void WindowsUpdateFixButton_Click(object s, RoutedEventArgs e) { }
    private void MicrosoftStoreFixButton_Click(object s, RoutedEventArgs e) { }
    private void ExplorerFixButton_Click(object s, RoutedEventArgs e) { }
    private void DiskInfoButton_Click(object s, RoutedEventArgs e) { }
    private void SystemReportButton_Click(object s, RoutedEventArgs e) { }
    private void CatalogInventory_Click(object s, RoutedEventArgs e) { }
    private void SecurityMitigations_Click(object s, RoutedEventArgs e) { }
    private void WindowsActivationButton_Click(object s, RoutedEventArgs e) { }
    private void OfficeActivationButton_Click(object s, RoutedEventArgs e) { }
    private void DisableDefenderButton_Click(object s, RoutedEventArgs e) { }
    private void RestoreDefenderButton_Click(object s, RoutedEventArgs e) { }
    private void BitLockerManagerButton_Click(object s, RoutedEventArgs e) { }
    private void SmartAppControlButton_Click(object s, RoutedEventArgs e) { }
    private void EssentialTweaksButton_Click(object s, RoutedEventArgs e) { }
    private void GamingTweaksButton_Click(object s, RoutedEventArgs e) { }
    private void RuntimeCompatibilityButton_Click(object s, RoutedEventArgs e) { }
    private void GpuDriverManagerButton_Click(object s, RoutedEventArgs e) { }
    private void DebloatButton_Click(object s, RoutedEventArgs e) { }
    private void MsiModeUtilityButton_Click(object s, RoutedEventArgs e) { }
    private void LegacyWindowsPanelsButton_Click(object s, RoutedEventArgs e) { }
    private void ProfileSelectionButton_Click(object s, RoutedEventArgs e) { }
    private void ApplyProfileButton_Click(object s, RoutedEventArgs e) { }
}
