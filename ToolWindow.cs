using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.UI;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal enum ToolWindowResult
    {
        Closed,
        Primary,
        Secondary
    }

    /// <summary>
    /// A reusable WinUI 3 top-level window for every tool opened from the main
    /// dashboard. The native overlapped presenter supplies the normal Windows
    /// resize, minimize and maximize behavior, while the star-sized content row
    /// keeps the hosted tool responsive.
    /// </summary>
    internal sealed class ToolWindow : Window
    {
        // Accessed on the UI thread; ownership includes confirmation/progress
        // grandchildren so closing the dashboard cannot orphan a tool.
        private static readonly HashSet<ToolWindow> OpenTools = new();
        private readonly Window _owner;
        private readonly ContentControl _analysisHost = new()
        {
            Visibility = Visibility.Collapsed,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(18, 0, 18, 8)
        };

        internal InlineAnalysisProgress CreateAnalysisProgress(IReadOnlyList<CatalogProgressItem> items) => new(_analysisHost, items);
        public Func<bool>? IsBusy { get; set; }
        public bool IsClosed { get; private set; }

        public static bool HasBusyOwnedWindows(Window owner) =>
            OpenTools.Any(tool => tool.BelongsTo(owner) && tool.IsBusy?.Invoke() == true);

        private bool BelongsTo(Window ancestor)
        {
            Window current = _owner;
            while (!ReferenceEquals(current, ancestor))
            {
                if (current is not ToolWindow parent) return false;
                current = parent._owner;
            }
            return true;
        }

        private void Owner_Closed(object sender, WindowEventArgs args) => Close();

        private readonly TaskCompletionSource<ToolWindowResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _minimumWidth;
        private readonly int _minimumHeight;
        private readonly Grid _root;
        private string _canonicalTitle;
        private readonly TextBlock _headingText;
        private int _maximumPracticalWidth = int.MaxValue;
        private int _maximumPracticalHeight = int.MaxValue;
        private double _baseWindowWidth;
        private double _baseWindowHeight;
        private double _appliedGeometryScale = 1d;
        private bool _displaySettingsResizeInProgress;
        private bool _closingFromButton;
        private ToolWindowResult _buttonResult = ToolWindowResult.Closed;

        public Button PrimaryButton { get; }
        public Button SecondaryButton { get; }
        public Button CloseButton { get; }

        public bool CloseOnPrimary { get; set; }
        public bool CloseOnSecondary { get; set; }

        public ToolWindow(
            Window owner,
            string title,
            FrameworkElement toolContent,
            string? primaryButtonText = null,
            string? secondaryButtonText = null,
            string closeButtonText = "Close",
            int initialWidth = 980,
            int initialHeight = 720,
            int minimumWidth = 520,
            int minimumHeight = 380)
        {
            _owner = owner;
            _canonicalTitle = title;
            Title = title;
            _minimumWidth = Math.Max(360, minimumWidth);
            _minimumHeight = Math.Max(260, minimumHeight);

            Grid root = new()
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 247, 249, 252))
            };
            _root = root;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star)
            });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // An optional inline scan panel sits above the fixed action footer.
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _headingText = new TextBlock
            {
                Text = title,
                FontSize = 21,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 18, 28, 40)),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            };
            ToolTipService.SetToolTip(_headingText, title);
            Border headingBorder = new()
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 211, 220, 232)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(20, 14, 20, 12),
                Child = _headingText
            };
            root.Children.Add(headingBorder);

            toolContent.HorizontalAlignment = HorizontalAlignment.Stretch;
            toolContent.VerticalAlignment = VerticalAlignment.Stretch;
            toolContent.MinWidth = 0;
            toolContent.MinHeight = 0;

            ContentPresenter presenter = new()
            {
                Content = ResponsiveToolLayout.Body(toolContent),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(18, 14, 18, 14)
            };
            Grid.SetRow(presenter, 1);
            root.Children.Add(presenter);
            Grid.SetRow(_analysisHost, 2);
            root.Children.Add(_analysisHost);

            PrimaryButton = CreateFooterButton(primaryButtonText, isPrimary: true);
            SecondaryButton = CreateFooterButton(secondaryButtonText, isPrimary: false);
            CloseButton = CreateFooterButton(closeButtonText, isPrimary: false);

            PrimaryButton.Click += (_, _) =>
            {
                if (CloseOnPrimary)
                {
                    CloseWithResult(ToolWindowResult.Primary);
                }
            };
            SecondaryButton.Click += (_, _) =>
            {
                if (CloseOnSecondary)
                {
                    CloseWithResult(ToolWindowResult.Secondary);
                }
            };
            CloseButton.Click += (_, _) => CloseWithResult(ToolWindowResult.Closed);

            Grid footerButtons = ResponsiveToolLayout.Footer(SecondaryButton, PrimaryButton, CloseButton);

            Border footerBorder = new()
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 211, 220, 232)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(18, 11, 18, 11),
                Child = footerButtons
            };
            Grid.SetRow(footerBorder, 3);
            root.Children.Add(footerBorder);

            Content = root;
            Closed += ToolWindow_Closed;
            UiDisplaySettings.Changed += UiDisplaySettings_Changed;
            root.Loaded += (_, _) => UiDisplaySettings.Apply(root);
            UiDisplaySettings.Apply(root);

            ConfigureNativeWindow(owner, initialWidth, initialHeight);
            AppWindow.Closing += (_, args) =>
            {
                if (IsBusy?.Invoke() == true || HasBusyOwnedWindows(this)) args.Cancel = true;
            };
            OpenTools.Add(this);
            owner.Closed += Owner_Closed;
        }

        public Task<ToolWindowResult> ShowAsync()
        {
            Activate();
            return _completion.Task;
        }

        public void SetTitle(string title)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            _canonicalTitle = title.Trim();
            string translated = _canonicalTitle;
            Title = translated;
            AppWindow.Title = translated;
            _headingText.Text = _canonicalTitle;
            ToolTipService.SetToolTip(_headingText, _canonicalTitle);
        }

        private void ConfigureNativeWindow(
            Window owner,
            int initialWidth,
            int initialHeight)
        {
            AppWindow.Title = Title;
            AppWindowIcon.Apply(AppWindow);
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = true;
            }

            RectInt32 ownerRect = new(
                owner.AppWindow.Position.X,
                owner.AppWindow.Position.Y,
                owner.AppWindow.Size.Width,
                owner.AppWindow.Size.Height);
            RectInt32 workArea = GetWorkArea(owner, ownerRect);
            _maximumPracticalWidth = Math.Max(360, workArea.Width);
            _maximumPracticalHeight = Math.Max(260, workArea.Height);
            _baseWindowWidth = Math.Max(_minimumWidth, initialWidth);
            _baseWindowHeight = Math.Max(_minimumHeight, initialHeight);
            _appliedGeometryScale = UiDisplaySettings.GeometryScale;
            int width = Math.Min(
                _maximumPracticalWidth,
                Math.Max(
                    GetScaledMinimumWidth(),
                    ScaleWindowDimension(initialWidth)));
            int height = Math.Min(
                _maximumPracticalHeight,
                Math.Max(
                    GetScaledMinimumHeight(),
                    ScaleWindowDimension(initialHeight)));
            int x = Math.Clamp(ownerRect.X + (ownerRect.Width - width) / 2,
                workArea.X, workArea.X + Math.Max(0, workArea.Width - width));
            int y = Math.Clamp(ownerRect.Y + (ownerRect.Height - height) / 2,
                workArea.Y, workArea.Y + Math.Max(0, workArea.Height - height));
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

            AppWindow.Changed += (_, args) =>
            {
                if (IsClosed) return;
                if (args.DidPositionChange)
                {
                    RectInt32 currentArea = GetWorkArea(this, workArea);
                    _maximumPracticalWidth = Math.Max(360, currentArea.Width);
                    _maximumPracticalHeight = Math.Max(260, currentArea.Height);
                }
                if (!args.DidSizeChange)
                {
                    return;
                }

                // Minimized/maximized sizes are controlled by Windows and must
                // never replace the user's restored-window baseline.
                if (AppWindow.Presenter is OverlappedPresenter currentPresenter &&
                    currentPresenter.State != OverlappedPresenterState.Restored) return;

                if (_displaySettingsResizeInProgress)
                {
                    _displaySettingsResizeInProgress = false;
                    return;
                }

                int constrainedWidth = Math.Max(GetScaledMinimumWidth(), AppWindow.Size.Width);
                int constrainedHeight = Math.Max(GetScaledMinimumHeight(), AppWindow.Size.Height);
                if (constrainedWidth != AppWindow.Size.Width ||
                    constrainedHeight != AppWindow.Size.Height)
                {
                    ResizeForDisplaySettings(constrainedWidth, constrainedHeight);
                    return;
                }


                // Preserve the user's chosen size as an unscaled baseline. A
                // later 25-200% change can then resize proportionally without
                // accumulating rounding or losing a size that was screen-capped.
                _baseWindowWidth = AppWindow.Size.Width / _appliedGeometryScale;
                _baseWindowHeight = AppWindow.Size.Height / _appliedGeometryScale;
            };
        }

        private static Button CreateFooterButton(string? text, bool isPrimary)
        {
            Button button = new()
            {
                Content = text ?? string.Empty,
                MinWidth = 0,
                MinHeight = 36,
                Padding = new Thickness(10, 6, 10, 6),
                Visibility = string.IsNullOrWhiteSpace(text)
                    ? Visibility.Collapsed
                    : Visibility.Visible,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(0)
            };

            button.ContentTemplate = FooterLabelTemplate();

            if (isPrimary)
            {
                button.Background = new SolidColorBrush(Color.FromArgb(255, 0, 103, 192));
                button.Foreground = new SolidColorBrush(Colors.White);
                button.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0, 88, 164));
            }

            return button;
        }

        private static DataTemplate FooterLabelTemplate() => (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding}' TextWrapping='Wrap' TextAlignment='Center'/></DataTemplate>");

        private void CloseWithResult(ToolWindowResult result)
        {
            if (IsBusy?.Invoke() == true || HasBusyOwnedWindows(this)) return;
            _closingFromButton = true;
            _buttonResult = result;
            Close();
        }

        private void ToolWindow_Closed(object sender, WindowEventArgs args)
        {
            IsClosed = true;
            OpenTools.Remove(this);
            _owner.Closed -= Owner_Closed;
            UiDisplaySettings.Changed -= UiDisplaySettings_Changed;
            _completion.TrySetResult(
                _closingFromButton ? _buttonResult : ToolWindowResult.Closed);
        }

        private void UiDisplaySettings_Changed(object? sender, EventArgs args)
        {
            if (IsClosed) return;
            Title = _canonicalTitle;
            AppWindow.Title = Title;
            _headingText.Text = _canonicalTitle;
            UiDisplaySettings.Apply(_root);
            double previousGeometryScale = _appliedGeometryScale;
            _appliedGeometryScale = UiDisplaySettings.GeometryScale;
            if (previousGeometryScale == _appliedGeometryScale ||
                AppWindow.Presenter is OverlappedPresenter currentPresenter &&
                currentPresenter.State != OverlappedPresenterState.Restored) return;
            int targetWidth = Math.Min(
                _maximumPracticalWidth,
                Math.Max(
                    GetScaledMinimumWidth(),
                    (int)Math.Round(_baseWindowWidth * _appliedGeometryScale)));
            int targetHeight = Math.Min(
                _maximumPracticalHeight,
                Math.Max(
                    GetScaledMinimumHeight(),
                    (int)Math.Round(_baseWindowHeight * _appliedGeometryScale)));
            ResizeForDisplaySettings(targetWidth, targetHeight);
        }

        private void ResizeForDisplaySettings(int width, int height)
        {
            if (width == AppWindow.Size.Width && height == AppWindow.Size.Height)
            {
                return;
            }

            _displaySettingsResizeInProgress = true;
            AppWindow.Resize(new SizeInt32(width, height));
        }

        private int GetScaledMinimumWidth()
        {
            return Math.Min(
                _maximumPracticalWidth,
                Math.Max(360, _minimumWidth));
        }

        private int GetScaledMinimumHeight()
        {
            return Math.Min(
                _maximumPracticalHeight,
                Math.Max(260, _minimumHeight));
        }

        private static int ScaleWindowDimension(int value)
        {
            return Math.Max(1, (int)Math.Round(value * UiDisplaySettings.GeometryScale));
        }

        private static RectInt32 GetWorkArea(Window window, RectInt32 fallback)
        {
            try
            {
                return DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea
                    ?? fallback;
            }
            catch (Exception exception)
            {
                App.WriteCrashLog(exception, "Reading window work area");
                return fallback;
            }
        }
    }
}
