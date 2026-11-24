using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Naufal_Windows_Tech_s_Powertoys
{
    /// <summary>
    /// Shared modeless progress surface for long-running maintenance operations.
    /// Closing the window never cancels the backend; late progress is ignored safely.
    /// </summary>
    internal sealed class MaintenanceProgressWindow
    {
        private sealed class DispatcherProgress<T> : IProgress<T>
        {
            private readonly DispatcherQueue _dispatcher;
            private readonly Action<T> _handler;

            public DispatcherProgress(DispatcherQueue dispatcher, Action<T> handler)
            {
                _dispatcher = dispatcher;
                _handler = handler;
            }

            public void Report(T value)
            {
                if (_dispatcher.HasThreadAccess)
                {
                    _handler(value);
                }
                else
                {
                    _dispatcher.TryEnqueue(() => _handler(value));
                }
            }
        }

        private readonly ToolWindow _window;
        private readonly Grid _contentRoot;
        private readonly TextBlock _currentStageText;
        private readonly TextBlock _stageCountText;
        private readonly TextBlock _elapsedText;
        private readonly TextBlock _percentText;
        private readonly TextBlock _resultText;
        private readonly ProgressBar _progressBar;
        private readonly TextBox _outputBox;
        private readonly IReadOnlyList<TextBlock> _stageStatusTexts;
        private readonly List<string> _stageStatuses = new();
        private readonly List<ProgressBar> _stageBars = new();
        private bool _succeeded;
        private int _warningCount;
        private readonly IReadOnlyList<string> _stages;
        private readonly string _operationTitle;
        private readonly DispatcherTimer _elapsedTimer = new();
        private readonly Stopwatch _stopwatch = new();
        private readonly StringBuilder _liveOutput = new();
        private Task<ToolWindowResult>? _closeTask;
        private bool _closed;
        private bool _completed;
        private int _activeStage;

        public MaintenanceProgressWindow(
            Window owner,
            string title,
            string subtitle,
            IReadOnlyList<string> stages,
            bool offerWindowsUpdateRetry = false)
        {
            _operationTitle = title;
            _stages = stages;
            _contentRoot = new Grid { RowSpacing = 12 };
            _contentRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(190) });
            _contentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _contentRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _contentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _contentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid summary = new() { RowSpacing = 8 };
            summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _currentStageText = new TextBlock
            {
                Text = "Preparing...",
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            summary.Children.Add(_currentStageText);

            StackPanel summaryRight = new()
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 2
            };
            _stageCountText = new TextBlock
            {
                Text = $"0 of {stages.Count}",
                HorizontalAlignment = HorizontalAlignment.Right,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            _elapsedText = new TextBlock
            {
                Text = "Elapsed: 00:00:00",
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 70, 95))
            };
            summaryRight.Children.Add(_stageCountText);
            summaryRight.Children.Add(_elapsedText);
            Grid.SetColumn(summaryRight, 1);
            summary.Children.Add(summaryRight);

            _progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Height = 14,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = GetTrackBrush(),
                Foreground = GetWorkingBrush()
            };
            Grid.SetRow(_progressBar, 1);
            summary.Children.Add(_progressBar);

            _percentText = new TextBlock
            {
                Text = "0%",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(_percentText, 1);
            Grid.SetColumn(_percentText, 1);
            summary.Children.Add(_percentText);

            Border summaryBorder = new()
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 238, 243, 249)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 187, 199, 213)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 12, 14, 12),
                Child = summary
            };
            Grid.SetRow(summaryBorder, 4);
            _contentRoot.Children.Add(summaryBorder);

            StackPanel stageRows = new() { Spacing = 5 };
            List<TextBlock> statusTexts = new();
            for (int index = 0; index < stages.Count; index++)
            {
                Grid row = new();
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                row.Children.Add(new TextBlock
                {
                    Text = $"{index + 1}. {stages[index]}",
                    TextWrapping = TextWrapping.Wrap
                });
                TextBlock status = new()
                {
                    Text = "WAITING",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 70, 95)),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                };
                Grid.SetColumn(status, 1);
                row.Children.Add(status);
                ProgressBar stageBar = new()
                {
                    Minimum = 0, Maximum = 100, Value = 0, Height = 12, MinHeight = 12,
                    Margin = new Thickness(0, 5, 0, 8),
                    Foreground = GetWorkingBrush(), Background = GetTrackBrush()
                };
                Grid.SetRow(stageBar, 1);
                Grid.SetColumnSpan(stageBar, 2);
                row.Children.Add(stageBar);
                _stageBars.Add(stageBar);
                stageRows.Children.Add(row);
                statusTexts.Add(status);
                _stageStatuses.Add("WAITING");
            }
            _stageStatusTexts = statusTexts;

            ScrollViewer stageViewer = new()
            {
                Content = stageRows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Enabled,
                Padding = new Thickness(10)
            };
            Border stageBorder = new()
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 187, 199, 213)),
                BorderThickness = new Thickness(1),
                Child = stageViewer
            };
            Grid.SetRow(stageBorder, 0);
            _contentRoot.Children.Add(stageBorder);

            TextBlock outputHeading = new()
            {
                Text = "LIVE OUTPUT",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            Grid.SetRow(outputHeading, 1);
            _contentRoot.Children.Add(outputHeading);

            _outputBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Color.FromArgb(255, 8, 17, 29)),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 236, 242, 250))
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_outputBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(_outputBox, ScrollBarVisibility.Auto);
            Grid.SetRow(_outputBox, 2);
            _contentRoot.Children.Add(_outputBox);

            _resultText = new TextBlock
            {
                Text = "Operation is starting...",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 70, 95))
            };
            Grid.SetRow(_resultText, 3);
            _contentRoot.Children.Add(_resultText);

            _window = new ToolWindow(
                owner,
                title,
                _contentRoot,
                primaryButtonText: offerWindowsUpdateRetry ? "Open Windows Update to retry" : null,
                secondaryButtonText: "Copy log",
                closeButtonText: "Close",
                initialWidth: 980,
                initialHeight: 760,
                minimumWidth: 700,
                minimumHeight: 560);
            if (offerWindowsUpdateRetry)
            {
                _window.PrimaryButton.IsEnabled = false;
                _window.PrimaryButton.Click += async (_, _) =>
                {
                    try
                    {
                        if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:windowsupdate")))
                            _resultText.Text += "\nOpen Settings → Windows Update to retry the download.";
                    }
                    catch (Exception error) { _resultText.Text += "\nCould not open Windows Update: " + error.Message; }
                };
            }
            _window.CloseButton.IsEnabled = false;
            _window.IsBusy = () => !_completed;
            _window.SecondaryButton.IsEnabled = false;
            _window.SecondaryButton.Click += (_, _) => CopyLog();
            UiDisplaySettings.Changed += UiDisplaySettings_Changed;
            _window.Closed += (_, _) =>
            {
                _closed = true;
                _elapsedTimer.Stop();
                UiDisplaySettings.Changed -= UiDisplaySettings_Changed;
            };

            Progress = new DispatcherProgress<MaintenanceProgressUpdate>(
                _contentRoot.DispatcherQueue,
                ReportCore);
            _elapsedTimer.Interval = TimeSpan.FromSeconds(1);
            _elapsedTimer.Tick += (_, _) => UpdateElapsed();
        }

        public IProgress<MaintenanceProgressUpdate> Progress { get; }

        public void Show()
        {
            if (_closeTask is not null)
            {
                return;
            }

            _stopwatch.Start();
            _elapsedTimer.Start();
            _closeTask = _window.ShowAsync();
        }

        public Task WaitUntilClosedAsync()
        {
            return _closeTask ?? Task.CompletedTask;
        }

        public void Complete(
            bool success,
            int warningCount,
            string message,
            string finalReport,
            bool partialRepair = false)
        {
            if (_closed || _completed)
            {
                return;
            }

            MaintenanceCompletion completion = MaintenanceCompletion.Evaluate(success || partialRepair, warningCount, _stageStatuses);
            if ((success || partialRepair) && !completion.Success)
                message = "The operation reported completion, but one or more stages failed or were not verified. Review the stage results and log.";
            partialRepair = partialRepair && completion.Success;
            success = completion.Success && !partialRepair;
            warningCount = completion.WarningCount;
            _completed = true;
            _succeeded = success || partialRepair;
            _warningCount = warningCount;
            _stopwatch.Stop();
            _elapsedTimer.Stop();
            UpdateElapsed();

            for (int index = 0; index < _stageStatuses.Count; index++)
            {
                SetStageStatus(index, completion.Stages[index]);
            }
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = 100;
            _progressBar.Foreground = success ? GetWorkingBrush() : GetFailureBrush();
            _percentText.Text = partialRepair ? "Limited scope only" : success ? "100%" : "Finished with errors";
            _percentText.Foreground = success ? GetWorkingBrush() : GetFailureBrush();
            _currentStageText.Text = partialRepair ? "Partial repair" : success ? "Completed" : "Failed";
            _stageCountText.Text = success
                ? $"{_stages.Count} of {_stages.Count}"
                : $"{Math.Max(0, _activeStage)} of {_stages.Count}";
            _resultText.Text = warningCount > 0
                ? $"{message} Warnings: {warningCount}."
                : message;
            _resultText.Foreground = GetStatusBrush(
                partialRepair ? "WARNING" : success ? warningCount > 0 ? "WARNING" : "PASS" : "FAILED");
            if (partialRepair)
            {
                _progressBar.Foreground = GetStatusBrush("WARNING");
                _percentText.Foreground = GetStatusBrush("WARNING");
            }
            _window.SetTitle(partialRepair ? $"Partial {_operationTitle}" : success
                ? $"Completed {_operationTitle}"
                : $"Failed {_operationTitle}");

            if (!string.IsNullOrWhiteSpace(finalReport))
            {
                _liveOutput.Clear();
                _liveOutput.Append(finalReport.TrimEnd());
                _outputBox.Text = _liveOutput.ToString();
            }

            _window.CloseButton.IsEnabled = true;
            if (_window.PrimaryButton.Visibility == Visibility.Visible) _window.PrimaryButton.IsEnabled = true;
            _window.SecondaryButton.IsEnabled = _liveOutput.Length > 0;
        }

        private void ReportCore(MaintenanceProgressUpdate update)
        {
            if (_closed || _completed || !MaintenanceCompletion.CanReport(_stageStatuses, _activeStage, update))
            {
                return;
            }
            // A delayed terminal callback can settle an earlier row, but must
            // not move the current stage/title/progress back to that row.
            if (update.StageIndex < _activeStage)
            {
                SetStageStatus(update.StageIndex - 1, update.Status);
                return;
            }

            if (update.Status == "SKIPPED" && update.StageName == "Not reached")
            {
                if (update.StageIndex > 0 && update.StageIndex <= _stageStatusTexts.Count)
                {
                    SetStageStatus(update.StageIndex - 1, "SKIPPED");
                }
                return;
            }
            _activeStage = Math.Clamp(update.StageIndex, 1, update.StageCount);
            _currentStageText.Text = update.StageName;
            _window.SetTitle(update.StageName);
            _stageCountText.Text = $"{_activeStage} of {update.StageCount}";
            double? stagePercent = update.StagePercent is double supplied && double.IsFinite(supplied)
                ? Math.Clamp(supplied, 0, 100) : null;
            double stageFraction = stagePercent.HasValue
                ? stagePercent.Value / 100d
                : 0d;
            double percent = ((_activeStage - 1d + stageFraction) / update.StageCount) * 100d;
            _progressBar.Value = percent;
            _progressBar.Foreground = GetWorkingBrush();
            _percentText.Text = $"{Math.Round(percent):0}%";
            _percentText.Foreground = GetWorkingBrush();

            if (_activeStage <= _stageStatusTexts.Count)
            {
                SetStageStatus(_activeStage - 1, update.Status);
                ProgressBar stageBar = _stageBars[_activeStage - 1];
                if (update.Status == "RUNNING")
                {
                    stageBar.IsIndeterminate = !stagePercent.HasValue;
                    stageBar.Value = stagePercent ?? 0;
                }
            }

            string line = $"[{_stopwatch.Elapsed:hh\\:mm\\:ss}] [{_activeStage}/{update.StageCount}] {update.Detail}";
            if (_liveOutput.Length > 0)
            {
                _liveOutput.AppendLine();
            }
            _liveOutput.Append(line);
            if (_liveOutput.Length > 250000) _liveOutput.Remove(0, _liveOutput.Length - 200000);
            _outputBox.Text = _liveOutput.ToString();
            _outputBox.SelectionStart = _outputBox.Text.Length;
        }

        private void UpdateElapsed()
        {
            _elapsedText.Text = $"Elapsed: {_stopwatch.Elapsed:hh\\:mm\\:ss}";
        }

        private void CopyLog()
        {
            if (_liveOutput.Length == 0)
            {
                return;
            }

            try
            {
                DataPackage package = new();
                package.SetText(_liveOutput.ToString());
                Clipboard.SetContent(package);
                Clipboard.Flush();
                _resultText.Text = "Log copied to Clipboard.";
            }
            catch (Exception exception)
            {
                _resultText.Text = $"Copy failed: {exception.Message}";
            }
        }

        private void UiDisplaySettings_Changed(object? sender, EventArgs args)
        {
            for (int index = 0; index < _stageStatuses.Count; index++) SetStageStatus(index, _stageStatuses[index]);
            if (_completed)
            {
                _resultText.Foreground = GetStatusBrush(
                    _succeeded
                        ? _warningCount > 0
                            ? "WARNING"
                            : "PASS"
                        : "FAILED");
            }
            _progressBar.Background = GetTrackBrush();
            _progressBar.Foreground = _completed && !_succeeded ? GetFailureBrush() : GetWorkingBrush();
        }

        private void SetStageStatus(int index, string status)
        {
            _stageStatuses[index] = status;
            _stageStatusTexts[index].Text = status;
            _stageStatusTexts[index].Foreground = GetStatusBrush(status);
            ProgressBar bar = _stageBars[index];
            bar.Background = GetTrackBrush();
            bar.Foreground = status is "FAILED" or "NOT VERIFIED" ? GetFailureBrush() : GetWorkingBrush();
            if (status != "RUNNING")
            {
                bar.IsIndeterminate = false;
                bar.Value = status is "WAITING" or "SKIPPED" ? 0 : 100;
            }
        }

        private static Brush GetStatusBrush(string status)
        {
            bool dark = UiDisplaySettings.Theme == ElementTheme.Dark;
            Color color = status switch
            {
                "PASS" => dark
                    ? Color.FromArgb(255, 75, 211, 132)
                    : Color.FromArgb(255, 0, 112, 60),
                "WARNING" => dark
                    ? Color.FromArgb(255, 255, 180, 90)
                    : Color.FromArgb(255, 193, 91, 0),
                "FAILED" or "NOT VERIFIED" => dark
                    ? Color.FromArgb(255, 255, 138, 138)
                    : Color.FromArgb(255, 185, 28, 28),
                "RUNNING" => dark
                    ? Color.FromArgb(255, 117, 183, 255)
                    : Color.FromArgb(255, 0, 79, 146),
                _ => dark
                    ? Color.FromArgb(255, 185, 198, 216)
                    : Color.FromArgb(255, 49, 70, 95)
            };
            return new SolidColorBrush(color);
        }

        private static Brush GetWorkingBrush() => new SolidColorBrush(
            UiDisplaySettings.Theme == ElementTheme.Dark
                ? Color.FromArgb(255, 108, 203, 95)
                : Color.FromArgb(255, 16, 124, 16));

        private static Brush GetFailureBrush() => new SolidColorBrush(
            UiDisplaySettings.Theme == ElementTheme.Dark
                ? Color.FromArgb(255, 255, 138, 138)
                : Color.FromArgb(255, 196, 43, 28));

        private static Brush GetTrackBrush() => new SolidColorBrush(
            UiDisplaySettings.Theme == ElementTheme.Dark
                ? Color.FromArgb(255, 58, 65, 75)
                : Color.FromArgb(255, 230, 230, 230));
    }
}
