using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Naufal_Windows_Tech_s_Powertoys
{
    public sealed partial class MainWindow : Window
    {
        private readonly DispatcherTimer _clockTimer = new();
        private readonly DispatcherTimer _monitorTimer = new();
        private readonly DispatcherTimer _gamingStatusTimer = new();
        private readonly Stopwatch _sessionTimer = Stopwatch.StartNew();
        private readonly SystemMonitorService _systemMonitor = new();
        private readonly GamingLiveStatusService _gamingLiveStatusService = new();
        private readonly PerformanceProfileService _performanceProfileService = new();
        private readonly SystemInformationService _systemInformationService = new();
        private readonly SystemReportService _systemReportService = new();
        private readonly LicensingInformationService _licensingInformationService = new();
        private readonly LegacyWindowsPanelsService _legacyWindowsPanelsService = new();
        private readonly SecurityInformationService _securityInformationService = new();
        private readonly WindowsRepairService _windowsRepairService = new();
        private readonly WindowsUpdateRepairService _windowsUpdateRepairService = new();
        private readonly MicrosoftStoreRepairService _microsoftStoreRepairService = new();
        private readonly ExplorerRepairService _explorerRepairService = new();
        private readonly GamingRuntimeCompatibilityService _gamingRuntimeCompatibilityService = new();
        private readonly GamingRuntimeInstallerService _gamingRuntimeInstallerService = new();
        private readonly GamingTweaksService _gamingTweaksService = new();
        private readonly GamingActionsService _gamingActionsService = new();
        private readonly EssentialTweaksService _essentialTweaksService = new();
        private readonly EssentialActionsService _essentialActionsService = new();
        private readonly WindowsAiService _windowsAiService = new();
        private readonly DefenderPolicyService _defenderPolicyService = new();
        private readonly MsiModeService _msiModeService = new();
        private readonly GpuDriverService _gpuDriverService = new();
        private readonly DebloatService _debloatService = new();
        private readonly DebloatServiceGroupsService _debloatServiceGroupsService = new();
        private readonly DebloatRegistryLabService _debloatRegistryLabService = new();
        private readonly DebloatNetworkStorageService _debloatNetworkStorageService = new();
        private readonly XboxComponentsService _xboxComponentsService = new();
        private readonly FirstRunPrerequisiteService _firstRunPrerequisiteService = new();
        private readonly NativeCommandRunner _commandRunner = new();
        private readonly TaskActivityService _taskActivityService = new();
        private IToolToggleService? _gamingCatalog;
        private IToolToggleService? _essentialCatalog;
        private IToolToggleService? _advancedCatalog;
        private SystemSnapshot? _latestSystemSnapshot;
        private GamingLiveStatusSnapshot? _latestGamingSnapshot;
        private PerformanceProfileKind? _selectedProfile;
        private string _systemStatusError = string.Empty;
        private string _gamingStatusError = string.Empty;
        private bool _gamingRefreshInProgress;
        private bool _profileApplyInProgress;
        private bool _utilityTaskInProgress;
        private bool _closeWarningInProgress;
        private bool _firstRunWizardStarted;
        private bool _isClosed;
        private readonly OperationGate _profileGate = new();
        private ToolWindow? _taskManagerWindow;
        private readonly Style? _normalToolButtonStyle;
        private readonly Style? _primaryToolButtonStyle;

        public MainWindow()
        {
            InitializeComponent();
            UiDisplaySettings.KeepRecoveryControlUsable(ThemeButton);
            UiDisplaySettings.KeepRecoveryControlUsable(TextScaleButton);
            UiDisplaySettings.KeepRecoveryControlUsable(TextScaleGlyph);
            InitializeLiveCharts();
            AppWindowIcon.Apply(AppWindow);

            // Keep direct references to resolved XAML styles. Dynamic lookups through
            // ResourceDictionary indexers are fragile after Native AOT trimming.
            _normalToolButtonStyle = CompetitiveProfileButton.Style;
            _primaryToolButtonStyle = FullRepairButton.Style;

            UiDisplaySettings.Changed += UiDisplaySettings_Changed;
            RootLayout.Loaded += (_, _) =>
            {
                UiDisplaySettings.Apply(RootLayout);
                UpdateHeaderLayout();
            };
            UiDisplaySettings.Apply(RootLayout);
            UpdateDisplaySettingButtons();

            Title = AppIdentity.Product;

            // Match the original dashboard layout by opening maximized.
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Maximize();
                }
            }
            catch
            {
                // The UI can still run normally if maximizing is unavailable.
            }

            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += ClockTimer_Tick;
            _clockTimer.Start();

            _monitorTimer.Interval = TimeSpan.FromSeconds(1);
            _monitorTimer.Tick += MonitorTimer_Tick;
            _monitorTimer.Start();

            // Configuration verification is expensive; live CPU/RAM graphs still sample every second.
            _gamingStatusTimer.Interval = TimeSpan.FromSeconds(15);
            _gamingStatusTimer.Tick += GamingStatusTimer_Tick;
            _gamingStatusTimer.Start();

            Closed += MainWindow_Closed;
            AppWindow.Closing += MainWindow_Closing;
            RootLayout.Loaded += MainWindow_Loaded;

            UpdateClock();
            UpdateLiveStatus();
            _ = UpdateGamingStatusAsync();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (Environment.GetCommandLineArgs().Contains("--capture-startup-check"))
            {
                if (!_startupCheckStarted) await CaptureStartupCheckAsync();
                return;
            }
            if (_firstRunWizardStarted || !_firstRunPrerequisiteService.ShouldShow())
            {
                return;
            }

            _firstRunWizardStarted = true;
            try { await ShowFirstRunPrerequisiteWizardAsync(); }
            catch (Exception exception)
            {
                App.WriteCrashLog(exception, "First-run wizard");
                // Startup/event-handler exceptions must not escape async void.
                if (!_isClosed)
                {
                    try { await ShowMessageDialogAsync("First-Run Setup failed", exception.Message); }
                    catch (Exception displayError) { App.WriteCrashLog(displayError, "First-run error window"); }
                }
            }
        }

        private async Task ShowFirstRunPrerequisiteWizardAsync()
        {
            FirstRunPrerequisiteStatus status;
            try
            {
                status = await _firstRunPrerequisiteService.ReadStatusAsync();
            }
            catch (Exception exception)
            {
                status = new FirstRunPrerequisiteStatus(
                    $"Status check failed: {exception.Message}",
                    "Status check failed",
                    "Will be verified automatically");
            }

            // The dashboard may have been closed while read-only detection ran.
            if (_isClosed) return;

            TextBlock heading = new()
            {
                Text = "Prepare Windows before using all features",
                FontSize = 22,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            TextBlock subtitle = new()
            {
                Text = "Choose the preparation tasks to run. Nothing changes until RUN SELECTED is pressed.",
                TextWrapping = TextWrapping.Wrap
            };
            TextBlock internetNote = new()
            {
                Text = "WinGet setup requires Internet access. WMI verification works offline.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 169, 92, 0))
            };

            CheckBox restorePoint = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
            CheckBox winGet = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
            CheckBox wmi = new() { IsChecked = true, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
            CheckBox infrastructure = new()
            {
                IsChecked = true,
                IsEnabled = false,
                VerticalAlignment = VerticalAlignment.Center
            };

            StackPanel options = new() { Spacing = 9 };
            options.Children.Add(CreateFirstRunPrerequisiteRow(
                restorePoint,
                "Create System Restore Point",
                "RECOMMENDED",
                "Creates a Windows safety checkpoint before repairs, de-bloat actions, and performance tweaks are applied.",
                "A new checkpoint will be requested"));
            options.Children.Add(CreateFirstRunPrerequisiteRow(
                winGet,
                "Install / Update WinGet + App Installer",
                "FEATURE PREREQUISITE",
                "Used by Windows AI/Copilot cleanup and selected AppX/Widgets component reinstall paths.",
                status.WinGet));
            options.Children.Add(CreateFirstRunPrerequisiteRow(
                wmi,
                "Verify WMI system and memory access",
                "AUTOMATIC CHECK",
                "Reads operating-system and RAM data directly through WMI on Windows 10 and 11. WMIC is not required and will not be installed or removed.",
                status.Wmi));
            options.Children.Add(CreateFirstRunPrerequisiteRow(
                infrastructure,
                "Verify Windows Servicing / WMI / AppX",
                "AUTOMATIC CHECK",
                "Checks DISM, WMI/CIM, Windows Modules Installer, AppX Deployment, and State Repository without changing service startup settings.",
                status.Infrastructure));

            StackPanel content = new() { Spacing = 12 };
            content.Children.Add(heading);
            content.Children.Add(subtitle);
            content.Children.Add(internetNote);
            content.Children.Add(options);

            ToolWindow wizard = new(
                this,
                "First-Run Setup",
                new ScrollViewer
                {
                    Content = content,
                    VerticalScrollMode = ScrollMode.Enabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                },
                primaryButtonText: "RUN SELECTED",
                secondaryButtonText: "DONT SHOW AGAIN",
                closeButtonText: "SKIP FOR NOW",
                initialWidth: 900,
                initialHeight: 690,
                minimumWidth: 720,
                minimumHeight: 560)
            {
                CloseOnPrimary = true,
                CloseOnSecondary = true
            };

            TaskActivityService.TaskActivityLease? setupLease = null;
            MaintenanceProgressWindow? setupProgress = null;
            try
            {
                ToolWindowResult result = await wizard.ShowAsync();
                if (result == ToolWindowResult.Secondary)
                {
                    _firstRunPrerequisiteService.Suppress();
                    return;
                }
                if (result != ToolWindowResult.Primary)
                {
                    _firstRunPrerequisiteService.Defer();
                    return;
                }

                if (_isClosed) return;
                setupLease = await AcquireManagedTaskAsync(
                    "FirstRun:Prerequisites", "First-Run Setup",
                    new[] { "SystemMutation", "AppxDeployment", "WindowsServicing" });
                if (setupLease is null) return;
                setupLease.UpdateDetail("Preparing Windows prerequisites.");

                string[] stages =
                {
                    "Administrator / environment preflight",
                    "Create System Restore Point",
                    "Verify System Restore",
                    "Install / Update WinGet + App Installer",
                    "Verify WinGet",
                    WmiPrerequisiteProbe.SystemStage,
                    WmiPrerequisiteProbe.MemoryStage,
                    "Verify Windows Servicing / WMI / AppX",
                    "Save first-run setup state"
                };
                MaintenanceProgressWindow progressWindow = new(
                    this,
                    "First-Run Setup",
                    "Preparing Windows prerequisites with staged read-back verification.",
                    stages);
                setupProgress = progressWindow;
                progressWindow.Show();
                TaskStatusMessage = "TASKS: FIRST-RUN SETUP";
                FirstRunPrerequisiteResult operation =
                    await _firstRunPrerequisiteService.RunAsync(
                        restorePoint.IsChecked == true,
                        winGet.IsChecked == true,
                        progressWindow.Progress);
                TaskStatusMessage = operation.Success
                    ? operation.WarningCount > 0 ? "TASKS: WARNING" : "TASKS: COMPLETE"
                    : "TASKS: FAILED";
                progressWindow.Complete(
                    operation.Success,
                    operation.WarningCount,
                    operation.Success
                        ? "First-run prerequisite setup completed."
                        : "One or more selected prerequisites failed verification.",
                    operation.Report);
                setupLease.Complete(operation.Success
                    ? operation.WarningCount > 0 ? "WARNING" : "COMPLETED" : "FAILED", operation.Report);
            }
            catch (Exception exception)
            {
                setupProgress?.Complete(false, 0, "First-run prerequisite setup failed.", exception.ToString());
                setupLease?.Complete("FAILED", exception.Message);
                if (setupProgress is null) throw;
                App.WriteCrashLog(exception, "First-run prerequisites");
            }
            finally
            {
                setupLease?.Dispose();
                if (!_isClosed)
                {
                    RefreshManagedTaskHeader();
                    UpdateProfileSelectionUi();
                }
            }
        }

        private static Border CreateFirstRunPrerequisiteRow(
            CheckBox selector,
            string title,
            string type,
            string description,
            string status)
        {
            Grid grid = new() { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(selector);

            StackPanel text = new() { Spacing = 3 };
            text.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            text.Children.Add(new TextBlock
            {
                Text = type,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 30, 92, 171))
            });
            text.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap
            });
            text.Children.Add(new TextBlock
            {
                Text = $"Status: {status}",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 70, 95))
            });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            return new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 187, 199, 213)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(13, 10, 13, 10),
                Child = grid
            };
        }

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            UiDisplaySettings.ToggleTheme();
        }

        private async void TaskStatusButton_Click(object sender, RoutedEventArgs e)
        {
            if (_taskManagerWindow is not null)
            {
                _taskManagerWindow.Activate();
                return;
            }

            Grid contentGrid = new() { RowSpacing = 10 };
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid header = CreateTaskActivityGrid();
            header.Background = new SolidColorBrush(Color.FromArgb(255, 176, 196, 222));
            header.Padding = new Thickness(8, 6, 8, 6);
            AddTaskActivityText(header, "TASK", 0, true);
            AddTaskActivityText(header, "STATE", 1, true);
            AddTaskActivityText(header, "RESOURCES", 2, true);
            AddTaskActivityText(header, "ELAPSED", 3, true);
            AddTaskActivityText(header, "DETAIL", 4, true);
            Grid.SetRow(header, 0);
            contentGrid.Children.Add(header);

            ListView list = new()
            {
                SelectionMode = ListViewSelectionMode.Single,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 187, 199, 213)),
                BorderThickness = new Thickness(1, 0, 1, 1)
            };
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            Grid.SetRow(list, 1);
            contentGrid.Children.Add(list);

            TextBlock summary = new()
            {
                Text = "No tasks are currently running.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 70, 95))
            };
            Grid.SetRow(summary, 2);
            contentGrid.Children.Add(summary);

            ToolWindow window = new(
                this,
                "Task Monitoring",
                contentGrid,
                closeButtonText: "Close",
                initialWidth: 1080,
                initialHeight: 560,
                minimumWidth: 760,
                minimumHeight: 380);
            _taskManagerWindow = window;

            void RefreshTaskList()
            {
                IReadOnlyList<TaskActivityEntry> entries = _taskActivityService.RunningSnapshot();
                list.Items.Clear();
                DateTimeOffset now = DateTimeOffset.Now;
                foreach (TaskActivityEntry entry in entries)
                {
                    Grid row = CreateTaskActivityGrid();
                    row.Padding = new Thickness(8, 7, 8, 7);
                    AddTaskActivityText(row, entry.Title, 0, false);
                    TextBlock state = AddTaskActivityText(row, entry.State, 1, true);
                    state.Foreground = GetTaskStateBrush(entry.State);
                    AddTaskActivityText(row, entry.Resources, 2, false);
                    AddTaskActivityText(
                        row,
                        entry.Elapsed(now).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                        3,
                        false);
                    AddTaskActivityText(row, entry.Detail, 4, false);
                    list.Items.Add(new ListViewItem
                    {
                        Content = row,
                        Tag = entry,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(0),
                        Margin = new Thickness(0)
                    });
                }

                summary.Text = entries.Count == 0
                    ? "No tasks are currently running."
                    : $"{entries.Count} running task(s).";
                UiDisplaySettings.Apply(list);
            }

            DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
            refreshTimer.Tick += (_, _) => RefreshTaskList();
            window.Closed += (_, _) =>
            {
                refreshTimer.Stop();
                if (ReferenceEquals(_taskManagerWindow, window))
                {
                    _taskManagerWindow = null;
                }
            };
            RefreshTaskList();
            refreshTimer.Start();
            await window.ShowAsync();
        }

        private static Grid CreateTaskActivityGrid()
        {
            Grid grid = new() { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(115) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.8, GridUnitType.Star) });
            return grid;
        }

        private static TextBlock AddTaskActivityText(
            Grid grid,
            string text,
            int column,
            bool bold)
        {
            TextBlock label = new()
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = bold
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal
            };
            Grid.SetColumn(label, column);
            grid.Children.Add(label);
            return label;
        }

        private static Brush GetTaskStateBrush(string state)
        {
            bool dark = UiDisplaySettings.Theme == ElementTheme.Dark;
            Color color = state switch
            {
                "RUNNING" => dark
                    ? Color.FromArgb(255, 117, 183, 255)
                    : Color.FromArgb(255, 0, 79, 146),
                "QUEUED" => dark
                    ? Color.FromArgb(255, 255, 196, 107)
                    : Color.FromArgb(255, 168, 91, 0),
                "WARNING" => dark
                    ? Color.FromArgb(255, 255, 180, 90)
                    : Color.FromArgb(255, 193, 91, 0),
                "FAILED" => dark
                    ? Color.FromArgb(255, 255, 138, 138)
                    : Color.FromArgb(255, 185, 28, 28),
                _ => dark
                    ? Color.FromArgb(255, 75, 211, 132)
                    : Color.FromArgb(255, 0, 112, 60)
            };
            return new SolidColorBrush(color);
        }

        private void UiDisplaySettings_Changed(object? sender, EventArgs e)
        {
            UiDisplaySettings.Apply(RootLayout);
            RenderLiveCharts();
            UpdateHeaderLayout();
            UpdateDisplaySettingButtons();
            ApplyLiveStatusColors();
            UpdateClock();
        }

        private void UpdateDisplaySettingButtons()
        {
            bool dark = UiDisplaySettings.Theme == ElementTheme.Dark;
            ThemeButton.Content = dark ? "☾" : "☀";
            AutomationProperties.SetName(
                ThemeButton,
                dark ? "Switch to light mode" : "Switch to dark mode");
            AutomationProperties.SetName(
                TextScaleButton,
                $"Text scaling: {UiDisplaySettings.TextScalePercent}%");
            ToolTipService.SetToolTip(
                ThemeButton,
                dark ? "Switch to light mode" : "Switch to dark mode");
            ToolTipService.SetToolTip(
                TextScaleButton,
                $"Text scaling: {UiDisplaySettings.TextScalePercent}%");
        }

        private void ClockTimer_Tick(object? sender, object e)
        {
            UpdateClock();
            _ = OfferPendingRestartAsync();
        }

        private void MonitorTimer_Tick(object? sender, object e)
        {
            UpdateLiveStatus();
            RefreshManagedTaskHeader();
        }

        private void RefreshManagedTaskHeader()
        {
            TaskStatusMessage = _taskActivityService.HasActiveTask
                ? _taskActivityService.HeaderStatus
                : "TASKS: IDLE";
        }

        private async Task<TaskActivityService.TaskActivityLease?> AcquireManagedTaskAsync(
            string id,
            string title,
            IEnumerable<string>? resources)
        {
            Task<TaskActivityService.TaskActivityLease?> leaseRequest =
                _taskActivityService.AcquireAsync(id, title, resources);
            ToolWindow? queuedNotice = null;
            TaskActivityService.TaskActivityLease? lease = await TaskAdmission.WaitAsync(leaseRequest,
                () =>
                {
                    RefreshManagedTaskHeader();
                    queuedNotice = CreateMessageWindow("Task queued",
                        $"{title} was queued because another task holds a required resource. " +
                        "It will start automatically when the conflicting task finishes.");
                    _ = queuedNotice.ShowAsync();
                },
                () => { if (queuedNotice is { IsClosed: false }) queuedNotice.Close(); });
            if (lease is null)
            {
                await ShowMessageDialogAsync(
                    "Task already active",
                    $"{title} is already running or waiting in the task queue.");
                return null;
            }

            RefreshManagedTaskHeader();
            return lease;
        }

        private async void GamingStatusTimer_Tick(object? sender, object e)
        {
            await UpdateGamingStatusAsync();
        }

        private void UpdateClock()
        {
            DateTime now = DateTime.Now;
            TimeSpan elapsed = _sessionTimer.Elapsed;

            ClockText.Text = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            DateText.Text = now.ToString(
                "dddd, dd MMMM yyyy",
                CultureInfo.GetCultureInfo("en-US"));
            SessionText.Text = $"Time spent {elapsed:hh\\:mm\\:ss}";
            UpdatedText.Text = $"Updated {now:HH:mm:ss}";
        }

        private void UpdateLiveStatus()
        {
            try
            {
                SystemSnapshot snapshot = _systemMonitor.ReadSnapshot();
                _latestSystemSnapshot = snapshot;
                _systemStatusError = string.Empty;

                CpuValueText.Text = double.IsFinite(snapshot.CpuPercent) ? $"{snapshot.CpuPercent:0}%" : "Unavailable";
                GpuValueText.Text = snapshot.GpuPercent.HasValue
                    ? $"{snapshot.GpuPercent.Value:0}%"
                    : "Unavailable";
                RamValueText.Text = double.IsFinite(snapshot.MemoryPercent)
                    ? $"{snapshot.MemoryPercent:0}% | {snapshot.MemoryUsedGigabytes:0.0} GB" : "Unavailable";
                NetworkValueText.Text = double.IsFinite(snapshot.ReceiveMegabitsPerSecond) && double.IsFinite(snapshot.SendMegabitsPerSecond)
                    ? $"{snapshot.ReceiveMegabitsPerSecond:0.00} / {snapshot.SendMegabitsPerSecond:0.00} Mbps" : "Unavailable";
                ProcessValueText.Text = snapshot.ProcessCount.ToString(CultureInfo.InvariantCulture);
                UptimeValueText.Text = FormatUptime(snapshot.Uptime);
                AppendLiveCharts(snapshot);

                ApplyLiveStatusColors();
                RenderDetailText();
            }
            catch (Exception exception)
            {
                _systemStatusError = exception.Message;
                _latestSystemSnapshot = null;
                CpuValueText.Text = RamValueText.Text = GpuValueText.Text = NetworkValueText.Text = "Unavailable";
                CpuValueText.Foreground = RamValueText.Foreground = GpuValueText.Foreground = NetworkValueText.Foreground = GetLiveStatusBrush("muted");
                AppendLiveCharts(null);
                RenderDetailText();
            }
        }

        private async Task UpdateGamingStatusAsync()
        {
            if (_isClosed || _gamingRefreshInProgress)
            {
                return;
            }

            _gamingRefreshInProgress = true;

            try
            {
                GamingLiveStatusSnapshot snapshot =
                    await _gamingLiveStatusService.ReadSnapshotAsync();
                if (_isClosed) return;

                _latestGamingSnapshot = snapshot;
                _gamingStatusError = string.Empty;

                PowerPlanValueText.Text = snapshot.PowerPlanName;
                MmcssValueText.Text = snapshot.Mmcss.Profile;
                GameModeValueText.Text = snapshot.GameMode;
                HagsValueText.Text = snapshot.Hags;
                WindowedValueText.Text = snapshot.WindowedOptimization;
                MpoValueText.Text = snapshot.Mpo;
                SysMainValueText.Text = snapshot.SysMain;

                CurrentProfileValueText.Text = snapshot.PerformanceProfile?.DisplayText ?? "Profile verification unavailable";

                UpdateProfileSelectionUi();
                ApplyLiveStatusColors();
                RenderDetailText();
            }
            catch (Exception exception)
            {
                _gamingStatusError = exception.Message;
                if (!_isClosed) RenderDetailText();
            }
            finally
            {
                _gamingRefreshInProgress = false;
            }
        }

        private void ApplyLiveStatusColors()
        {
            if (_latestSystemSnapshot.HasValue)
            {
                SystemSnapshot system = _latestSystemSnapshot.Value;
                CpuValueText.Foreground = !double.IsFinite(system.CpuPercent) ? GetLiveStatusBrush("muted") : system.CpuPercent >= 90
                    ? GetLiveStatusBrush("danger")
                    : system.CpuPercent >= 70
                        ? GetLiveStatusBrush("warning")
                        : GetLiveStatusBrush("success");
                RamValueText.Foreground = !double.IsFinite(system.MemoryPercent) ? GetLiveStatusBrush("muted") : system.MemoryPercent >= 90
                    ? GetLiveStatusBrush("danger")
                    : system.MemoryPercent >= 80
                        ? GetLiveStatusBrush("warning")
                        : GetLiveStatusBrush("success");
                GpuValueText.Foreground = !system.GpuPercent.HasValue
                    ? GetLiveStatusBrush("muted")
                    : system.GpuPercent.Value >= 95
                        ? GetLiveStatusBrush("warning")
                        : GetLiveStatusBrush("success");
                NetworkValueText.Foreground = GetLiveStatusBrush(
                    double.IsFinite(system.ReceiveMegabitsPerSecond) && double.IsFinite(system.SendMegabitsPerSecond) ? "success" : "muted");
                ProcessValueText.Foreground = GetLiveStatusBrush("text");
                UptimeValueText.Foreground = GetLiveStatusBrush("text");
            }

            if (_latestGamingSnapshot.HasValue)
            {
                GamingLiveStatusSnapshot gaming = _latestGamingSnapshot.Value;
                PowerPlanValueText.Foreground = GetLiveStatusBrush("accent");
                MmcssValueText.Foreground = gaming.Mmcss.Profile switch
                {
                    "Competitive Gaming" => GetLiveStatusBrush("success"),
                    "Optimized Gaming" => GetLiveStatusBrush("warning"),
                    "Balanced" => GetLiveStatusBrush("text"),
                    _ => GetLiveStatusBrush("muted")
                };
                GameModeValueText.Foreground = GetOnOffStatusBrush(gaming.GameMode);
                HagsValueText.Foreground = GetOnOffStatusBrush(gaming.Hags);
                CurrentProfileValueText.Foreground = GetLiveStatusBrush(
                    gaming.PerformanceProfile?.Verified == true
                        ? gaming.PerformanceProfile.Profile switch
                        {
                            "Competitive Gaming" => "success",
                            "Optimized Gaming" => "warning",
                            _ => "text"
                        }
                        : "accent");
            }
        }

        private static Brush GetOnOffStatusBrush(string value)
        {
            return value switch
            {
                "ON" => GetLiveStatusBrush("success"),
                "OFF" => GetLiveStatusBrush("warning"),
                _ => GetLiveStatusBrush("muted")
            };
        }

        private static Brush GetLiveStatusBrush(string role)
        {
            bool dark = UiDisplaySettings.Theme == ElementTheme.Dark;
            Color color = (dark, role) switch
            {
                (true, "success") => Color.FromArgb(255, 75, 211, 132),
                (true, "warning") => Color.FromArgb(255, 255, 180, 90),
                (true, "danger") => Color.FromArgb(255, 255, 138, 138),
                (true, "accent") => Color.FromArgb(255, 117, 183, 255),
                (true, "muted") => Color.FromArgb(255, 170, 193, 220),
                (true, _) => Color.FromArgb(255, 245, 247, 250),
                (false, "success") => Color.FromArgb(255, 0, 138, 60),
                (false, "warning") => Color.FromArgb(255, 193, 91, 0),
                (false, "danger") => Color.FromArgb(255, 185, 28, 28),
                (false, "accent") => Color.FromArgb(255, 0, 95, 184),
                (false, "muted") => Color.FromArgb(255, 49, 84, 134),
                _ => Color.FromArgb(255, 11, 21, 32)
            };
            return new SolidColorBrush(color);
        }

        private void ProfileSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_profileApplyInProgress)
            {
                return;
            }

            if (sender is not Button button || button.Tag is not string profileName)
            {
                return;
            }

            _selectedProfile = profileName switch
            {
                "Competitive Gaming" => PerformanceProfileKind.CompetitiveGaming,
                "Optimized Gaming" => PerformanceProfileKind.OptimizedGaming,
                "Balanced" => PerformanceProfileKind.Balanced,
                _ => null
            };

            UpdateProfileSelectionUi();
        }

        private async void ApplyProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_selectedProfile.HasValue || _profileApplyInProgress)
            {
                return;
            }

            using IDisposable? operationLease = _profileGate.TryEnter();
            if (operationLease is null) return;
            try
            {
                UpdateProfileSelectionUi();
                PerformanceProfileKind selectedProfile = _selectedProfile.Value;
                string profileName = PerformanceProfileService.GetDisplayName(selectedProfile);
    
                if (!PerformanceProfileService.IsAdministrator())
                {
                    await ShowMessageDialogAsync(
                        "Administrator rights required",
                        "This profile changes HKLM registry values and the active Windows power plan. " +
                        "Close the app, run Visual Studio as Administrator, and then start the app again.");
                    return;
                }
    
                bool confirmed = await ShowConfirmationWindowAsync(
                    $"Apply {profileName}?",
                    PerformanceProfileService.GetConfirmationDescription(selectedProfile),
                    "Apply profile");
                if (!confirmed)
                {
                    return;
                }
    
                using TaskActivityService.TaskActivityLease? taskLease =
                    await AcquireManagedTaskAsync(
                        "ApplyPerformanceProfile",
                        $"Apply {profileName}",
                        new[] { "SystemMutation", "PerformancePolicy", "BCD", "PowerPolicy" });
                if (taskLease is null)
                {
                    return;
                }

                CatalogProgressWindow progressWindow = new(
                    this,
                    "Applying",
                    new[] { new CatalogProgressItem("PerformanceProfile", profileName) });
                progressWindow.Show();
    
                _profileApplyInProgress = true;
                _gamingStatusTimer.Stop();
                TaskStatusMessage = "TASKS: APPLYING";
                UpdateProfileSelectionUi();
    
                try
                {
                    taskLease.UpdateDetail("Applying gaming performance profile.");
                    progressWindow.BeginItem(
                        "PerformanceProfile",
                        $"Applying: {profileName}");
                    PerformanceProfileApplyResult result =
                        await _performanceProfileService.ApplyAsync(selectedProfile);
                    if (result.Success && result.RestartRequired) _pendingRestarts.Add(profileName);
                    progressWindow.VerifyItem(
                        "PerformanceProfile",
                        $"Verifying: {profileName}");
    
                    await UpdateGamingStatusAsync();
                    progressWindow.CompleteItem(
                        "PerformanceProfile",
                        result.Success,
                        result.Message);
                    progressWindow.UpdateOverall(1, result.Message);
                    progressWindow.Complete(result.Success, result.Message);
    
                    TaskStatusMessage = result.Success
                        ? "TASKS: COMPLETE"
                        : result.RolledBack
                            ? "TASKS: ROLLED BACK"
                            : "TASKS: FAILED";
    
                    taskLease.Complete(
                        result.Success
                            ? "COMPLETED"
                            : result.RolledBack ? "WARNING" : "FAILED",
                        result.Message);
                }
                catch (Exception exception)
                {
                    TaskStatusMessage = "TASKS: FAILED";
                    progressWindow.CompleteItem(
                        "PerformanceProfile",
                        success: false,
                        exception.Message);
                    progressWindow.Complete(success: false, exception.Message);
                    taskLease.Complete("FAILED", exception.Message);
                }
                finally
                {
                    taskLease.Dispose();
                    _profileApplyInProgress = false;
                    _gamingStatusTimer.Start();
                    RefreshManagedTaskHeader();
                    UpdateProfileSelectionUi();
                }
            }
            catch (Exception exception)
            {
                await ShowMessageDialogAsync("Profile operation failed", exception.Message);
            }
            finally
            {
                operationLease.Dispose();
                _profileApplyInProgress = false;
                if (!_isClosed) UpdateProfileSelectionUi();
            }
        }

        private void UpdateProfileSelectionUi()
        {
            // The reference keeps the dashboard usable while a cooperative task
            // runs. Per-operation locks decide whether a requested task starts
            // now or enters the FIFO queue.
            if (_isClosed) return;
            bool taskInProgress = _profileApplyInProgress || _profileGate.IsBusy;

            if (_normalToolButtonStyle is not null)
            {
                CompetitiveProfileButton.Style = _normalToolButtonStyle;
                OptimizedProfileButton.Style = _normalToolButtonStyle;
                BalancedProfileButton.Style = _normalToolButtonStyle;
            }

            CompetitiveProfileButton.IsEnabled = !taskInProgress;
            OptimizedProfileButton.IsEnabled = !taskInProgress;
            BalancedProfileButton.IsEnabled = !taskInProgress;
            DiskInfoButton.IsEnabled = true;
            SystemReportButton.IsEnabled = true;
            WindowsActivationButton.IsEnabled = true;
            OfficeActivationButton.IsEnabled = true;
            LegacyWindowsPanelsButton.IsEnabled = true;
            BitLockerManagerButton.IsEnabled = true;
            SmartAppControlButton.IsEnabled = true;
            FullRepairButton.IsEnabled = true;
            QuickRepairButton.IsEnabled = true;
            WindowsUpdateFixButton.IsEnabled = true;
            MicrosoftStoreFixButton.IsEnabled = true;
            ExplorerFixButton.IsEnabled = true;
            RuntimeCompatibilityButton.IsEnabled = true;
            GamingTweaksButton.IsEnabled = true;
            EssentialTweaksButton.IsEnabled = true;
            DisableDefenderButton.IsEnabled = true;
            RestoreDefenderButton.IsEnabled = true;
            MsiModeUtilityButton.IsEnabled = true;
            GpuDriverManagerButton.IsEnabled = true;
            DebloatButton.IsEnabled = true;

            if (!_selectedProfile.HasValue)
            {
                ApplyProfileButton.Content = "SELECT A PROFILE";
                if (_normalToolButtonStyle is not null)
                {
                    ApplyProfileButton.Style = _normalToolButtonStyle;
                }
                ApplyProfileButton.IsEnabled = false;
                return;
            }

            Button selectedButton = _selectedProfile.Value switch
            {
                PerformanceProfileKind.CompetitiveGaming => CompetitiveProfileButton,
                PerformanceProfileKind.OptimizedGaming => OptimizedProfileButton,
                PerformanceProfileKind.Balanced => BalancedProfileButton,
                _ => throw new ArgumentOutOfRangeException()
            };

            if (_primaryToolButtonStyle is not null)
            {
                selectedButton.Style = _primaryToolButtonStyle;
            }

            string selectedName =
                PerformanceProfileService.GetDisplayName(_selectedProfile.Value);
            PerformanceProfileState? current = _latestGamingSnapshot?.PerformanceProfile;
            string currentName = current?.Profile ?? string.Empty;
            bool alreadyCurrent = current?.Verified == true && string.Equals(
                selectedName,
                currentName,
                StringComparison.Ordinal);

            ApplyProfileButton.Content = alreadyCurrent
                ? "CURRENT PROFILE"
                : $"APPLY {selectedName.ToUpperInvariant()}";
            Style? applyStyle = alreadyCurrent
                ? _normalToolButtonStyle
                : _primaryToolButtonStyle;
            if (applyStyle is not null)
            {
                ApplyProfileButton.Style = applyStyle;
            }
            ApplyProfileButton.IsEnabled = !taskInProgress && !alreadyCurrent;
        }

        private async Task ShowMessageDialogAsync(string title, string message)
        {
            await CreateMessageWindow(title, message).ShowAsync();
        }

        private ToolWindow CreateMessageWindow(string title, string message)
        {
            ToolWindow window = new(
                this,
                title,
                DialogMessageContent.Create(message),
                closeButtonText: "OK",
                initialWidth: 660,
                initialHeight: 390,
                minimumWidth: 440,
                minimumHeight: 300);
            return window;
        }

        private async Task<bool> ShowConfirmationWindowAsync(
            string title,
            string message,
            string primaryButtonText,
            string closeButtonText = "Cancel")
        {
            ToolWindow window = new(
                this,
                title,
                DialogMessageContent.Create(message),
                primaryButtonText: primaryButtonText,
                closeButtonText: closeButtonText,
                initialWidth: 680,
                initialHeight: 400,
                minimumWidth: 460,
                minimumHeight: 300)
            {
                CloseOnPrimary = true
            };
            return await window.ShowAsync() == ToolWindowResult.Primary;
        }

        private async void DiskInfoButton_Click(object sender, RoutedEventArgs e)
        {
            await RunTableReportTaskAsync(
                "TASKS: READING DISKS",
                "Disk Information",
                "StorageOverview",
                _systemReportService.CollectDiskInformationAsync);
        }

        private async void FullRepairButton_Click(object sender, RoutedEventArgs e)
        {
            await RunWindowsRepairAsync(WindowsRepairMode.Full);
        }

        private async void QuickRepairButton_Click(object sender, RoutedEventArgs e)
        {
            await RunWindowsRepairAsync(WindowsRepairMode.Quick);
        }

        private async Task RunWindowsRepairAsync(WindowsRepairMode mode)
        {
            string title = mode == WindowsRepairMode.Full ? "Full Repair" : "Quick Repair";
            string description = mode == WindowsRepairMode.Full
                ? "Run DISM /RestoreHealth, followed by SFC /scannow? This can take a long time and requires Administrator rights."
                : "Run SFC /scannow? This can take a long time and requires Administrator rights.";

            if (!await ShowConfirmationWindowAsync(
                    title,
                    description,
                    "Run Repair"))
            {
                return;
            }

            string taskId = mode == WindowsRepairMode.Full
                ? "FullRepair"
                : "QuickRepair";
            TaskActivityService.TaskActivityLease? taskLease =
                await AcquireManagedTaskAsync(
                    taskId,
                    title,
                    new[] { "SystemMutation", "WindowsServicing", "SystemFiles" });
            if (taskLease is null)
            {
                return;
            }

            _utilityTaskInProgress = true;
            TaskStatusMessage = mode == WindowsRepairMode.Full
                ? "TASKS: FULL REPAIR"
                : "TASKS: QUICK REPAIR";
            UpdateProfileSelectionUi();

            string[] stages = mode == WindowsRepairMode.Full
                ? new[]
                {
                    "DISM - Restore Windows Image",
                    "SFC - Verify Protected System Files"
                }
                : new[] { "SFC - Verify Protected System Files" };
            MaintenanceProgressWindow progressWindow = new(
                this,
                title,
                mode == WindowsRepairMode.Full
                    ? "Windows image and protected system files will be repaired and verified."
                    : "Protected Windows system files will be scanned and repaired.",
                stages);
            progressWindow.Show();

            try
            {
                taskLease.UpdateDetail("Running repair stages.");
                WindowsRepairResult result = await _windowsRepairService.RunAsync(
                    mode,
                    progressWindow.Progress);
                TaskStatusMessage = result.Success
                    ? result.HasWarnings ? "TASKS: WARNING" : "TASKS: COMPLETE"
                    : "TASKS: FAILED";
                progressWindow.Complete(
                    result.Success,
                    result.HasWarnings ? 1 : 0,
                    result.Success
                        ? result.HasWarnings
                            ? "Repair completed with warnings."
                            : "Repair completed successfully."
                        : "Repair failed.",
                    result.Report);
                taskLease.Complete(
                    result.Success
                        ? result.HasWarnings ? "WARNING" : "COMPLETED"
                        : "FAILED",
                    result.Success
                        ? result.HasWarnings
                            ? "Repair completed with warnings."
                            : "Repair completed successfully."
                        : "Repair failed.");
            }
            catch (Exception exception)
            {
                TaskStatusMessage = "TASKS: FAILED";
                progressWindow.Complete(
                    false,
                    0,
                    $"{title} failed: {exception.Message}",
                    exception.ToString());
                taskLease.Complete("FAILED", exception.Message);
            }
            finally
            {
                taskLease.Dispose();
                _utilityTaskInProgress = false;
                RefreshManagedTaskHeader();
                UpdateProfileSelectionUi();
            }
        }

        private async void WindowsUpdateFixButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await ShowConfirmationWindowAsync(
                    "Windows Update Fix",
                    "Repair Windows Update service startup settings and reset the SoftwareDistribution and catroot2 caches? Administrator rights are required.",
                    "Run Repair"))
            {
                return;
            }

            TaskActivityService.TaskActivityLease? taskLease =
                await AcquireManagedTaskAsync(
                    "WindowsUpdateFix",
                    "Windows Update Fix",
                    new[] { "SystemMutation", "WindowsUpdate", "WindowsServices" });
            if (taskLease is null)
            {
                return;
            }

            _utilityTaskInProgress = true;
            TaskStatusMessage = "TASKS: UPDATE FIX";
            UpdateProfileSelectionUi();

            MaintenanceProgressWindow progressWindow = new(
                this,
                "Windows Update Fix",
                "Windows Update services, persistent locks and update caches will be repaired and verified.",
                new[]
                {
                    "Repair update-service prerequisites",
                    "Stop Windows Update services",
                    "Reset SoftwareDistribution cache",
                    "Reset catroot2 cache",
                    "Restart Windows Update services",
                    "Verify Windows Update repair"
                });
            progressWindow.Show();

            try
            {
                taskLease.UpdateDetail("Repairing Windows Update services and caches.");
                MaintenanceOperationResult result = await _windowsUpdateRepairService.RunAsync(
                    progressWindow.Progress);
                TaskStatusMessage = result.Success
                    ? result.WarningCount > 0 ? "TASKS: WARNING" : "TASKS: COMPLETE"
                    : "TASKS: FAILED";
                progressWindow.Complete(
                    result.Success,
                    result.WarningCount,
                    result.Success
                        ? "Windows Update repair completed."
                        : "Windows Update repair failed.",
                    result.Report);
                taskLease.Complete(
                    result.Success
                        ? result.WarningCount > 0 ? "WARNING" : "COMPLETED"
                        : "FAILED",
                    result.Success
                        ? result.WarningCount > 0
                            ? "Windows Update repair completed with warnings."
                            : "Windows Update repair completed."
                        : "Windows Update repair failed.");
            }
            catch (Exception exception)
            {
                TaskStatusMessage = "TASKS: FAILED";
                progressWindow.Complete(
                    false,
                    0,
                    $"Windows Update Fix failed: {exception.Message}",
                    exception.ToString());
                taskLease.Complete("FAILED", exception.Message);
            }
            finally
            {
                taskLease.Dispose();
                _utilityTaskInProgress = false;
                RefreshManagedTaskHeader();
                UpdateProfileSelectionUi();
            }
        }

        private async void MicrosoftStoreFixButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await ShowConfirmationWindowAsync(
                    "Microsoft Store Fix",
                    "Close Microsoft Store, reset its app data and preferences for the current user, clear its disposable cache, re-register its installed packages, check services, synchronize time and run wsreset.exe? Store preferences and sign-in data may be reset; this does not uninstall purchased apps.",
                    "Run Repair"))
            {
                return;
            }

            TaskActivityService.TaskActivityLease? taskLease =
                await AcquireManagedTaskAsync(
                    "MicrosoftStoreFix",
                    "Microsoft Store Fix",
                    new[] { "SystemMutation", "AppX", "MicrosoftStore", "WindowsServices" });
            if (taskLease is null)
            {
                return;
            }

            _utilityTaskInProgress = true;
            TaskStatusMessage = "TASKS: STORE FIX";
            UpdateProfileSelectionUi();

            MaintenanceProgressWindow progressWindow = new(
                this,
                "Microsoft Store Fix",
                "Microsoft Store packages, cache, services and registration will be repaired and verified.",
                new[]
                {
                    "Close Microsoft Store processes",
                    "Detect Microsoft Store package",
                    "Reset Microsoft Store app data",
                    "Clear Microsoft Store cache",
                    "Re-register Microsoft Store packages",
                    "Check Microsoft Store services",
                    "Synchronize Windows time",
                    "Reset Store cache with wsreset.exe",
                    "Verify Microsoft Store package"
                });
            progressWindow.Show();

            try
            {
                taskLease.UpdateDetail("Repairing Microsoft Store packages and services.");
                MaintenanceOperationResult result = await _microsoftStoreRepairService.RunAsync(
                    progressWindow.Progress);
                TaskStatusMessage = result.Success
                    ? result.WarningCount > 0 ? "TASKS: WARNING" : "TASKS: COMPLETE"
                    : "TASKS: FAILED";
                progressWindow.Complete(
                    result.Success,
                    result.WarningCount,
                    result.Success
                        ? "Microsoft Store repair completed."
                        : "Microsoft Store repair failed.",
                    result.Report);
                taskLease.Complete(
                    result.Success
                        ? result.WarningCount > 0 ? "WARNING" : "COMPLETED"
                        : "FAILED",
                    result.Success
                        ? result.WarningCount > 0
                            ? "Microsoft Store repair completed with warnings."
                            : "Microsoft Store repair completed."
                        : "Microsoft Store repair failed.");
            }
            catch (Exception exception)
            {
                TaskStatusMessage = "TASKS: FAILED";
                progressWindow.Complete(
                    false,
                    0,
                    $"Microsoft Store Fix failed: {exception.Message}",
                    exception.ToString());
                taskLease.Complete("FAILED", exception.Message);
            }
            finally
            {
                taskLease.Dispose();
                _utilityTaskInProgress = false;
                RefreshManagedTaskHeader();
                UpdateProfileSelectionUi();
            }
        }

        private async void ExplorerFixButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await ShowConfirmationWindowAsync(
                    "Explorer Fix",
                    "Restart Windows Explorer once using the logged-on user's normal shell token, rebuild the icon and thumbnail cache, and verify that the taskbar is restored?",
                    "Run Repair"))
            {
                return;
            }

            TaskActivityService.TaskActivityLease? taskLease =
                await AcquireManagedTaskAsync(
                    "ExplorerFix",
                    "Explorer Fix",
                    new[] { "SystemMutation", "ExplorerShell" });
            if (taskLease is null)
            {
                return;
            }

            _utilityTaskInProgress = true;
            TaskStatusMessage = "TASKS: EXPLORER FIX";
            UpdateProfileSelectionUi();

            MaintenanceProgressWindow progressWindow = new(
                this,
                "Explorer Fix",
                "Windows Explorer, icon cache and taskbar will be restarted and verified.",
                new[]
                {
                    "Prepare Explorer recovery launcher",
                    "Stop Windows Explorer",
                    "Rebuild Explorer icon and thumbnail cache",
                    "Start Windows Explorer",
                    "Verify Windows taskbar",
                    "Repair Windows shell registration",
                    "Final taskbar verification"
                });
            progressWindow.Show();

            try
            {
                taskLease.UpdateDetail("Repairing Windows Explorer and taskbar.");
                MaintenanceOperationResult result = await _explorerRepairService.RunAsync(
                    progressWindow.Progress);
                TaskStatusMessage = result.Success
                    ? result.WarningCount > 0 ? "TASKS: WARNING" : "TASKS: COMPLETE"
                    : "TASKS: FAILED";
                progressWindow.Complete(
                    result.Success,
                    result.WarningCount,
                    result.Success
                        ? "Explorer repair completed."
                        : "Explorer repair failed.",
                    result.Report);
                taskLease.Complete(
                    result.Success
                        ? result.WarningCount > 0 ? "WARNING" : "COMPLETED"
                        : "FAILED",
                    result.Success
                        ? result.WarningCount > 0
                            ? "Explorer repair completed with warnings."
                            : "Explorer repair completed."
                        : "Explorer repair failed.");
            }
            catch (Exception exception)
            {
                TaskStatusMessage = "TASKS: FAILED";
                progressWindow.Complete(
                    false,
                    0,
                    $"Explorer Fix failed: {exception.Message}",
                    exception.ToString());
                taskLease.Complete("FAILED", exception.Message);
            }
            finally
            {
                taskLease.Dispose();
                _utilityTaskInProgress = false;
                RefreshManagedTaskHeader();
                UpdateProfileSelectionUi();
            }
        }

        private async void RuntimeCompatibilityButton_Click(object sender, RoutedEventArgs e)
        {
            TaskStatusMessage = "TASKS: RUNTIME CHECK";
            try
            {
                await ShowGamingRuntimeCompatibilityWindowAsync();
            }
            catch (Exception exception)
            {
                TaskStatusMessage = "TASKS: FAILED";
                await ShowMessageDialogAsync(
                    "Games Runtime & Compatibility Check failed",
                    exception.Message);
            }
            finally
            {
                RefreshManagedTaskHeader();
            }
        }

        private async Task ShowGamingRuntimeCompatibilityWindowAsync()
        {
            Grid contentGrid = new();
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid header = CreateGamingRuntimeGrid();
            header.Background = new SolidColorBrush(Color.FromArgb(255, 176, 196, 222));
            header.Padding = new Thickness(8, 6, 8, 6);
            AddGamingRuntimeGridText(header, "COMPONENT", 0, true);
            AddGamingRuntimeGridText(header, "CATEGORY", 1, true);
            AddGamingRuntimeGridText(header, "STATUS", 2, true);
            AddGamingRuntimeGridText(header, "DETAILS", 3, true);
            Grid.SetRow(header, 0);
            contentGrid.Children.Add(header);

            ListView list = new()
            {
                SelectionMode = ListViewSelectionMode.Single,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 187, 199, 213)),
                BorderThickness = new Thickness(1, 0, 1, 1)
            };
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            Grid.SetRow(list, 1);
            contentGrid.Children.Add(list);

            Grid actionBar = new() { ColumnSpacing = 8, Margin = new Thickness(4, 10, 4, 0) };
            actionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock statusText = new()
            {
                Text = "Ready to analyze.",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 84, 134))
            };
            actionBar.Children.Add(statusText);
            Button officialSourceButton = new()
            {
                Content = "Official source",
                MinWidth = 140,
                Height = 34,
                IsEnabled = false,
                CornerRadius = new CornerRadius(0)
            };
            Grid.SetColumn(officialSourceButton, 1);
            actionBar.Children.Add(officialSourceButton);
            Button analyzeButton = new()
            {
                Content = "Analyze",
                MinWidth = 120,
                Height = 34,
                CornerRadius = new CornerRadius(0)
            };
            Grid.SetColumn(analyzeButton, 2);
            actionBar.Children.Add(analyzeButton);
            Button repairSelectedButton = new()
            {
                Content = UiTextKeys.RepairSelected,
                MinWidth = 145,
                Height = 34,
                IsEnabled = false,
                CornerRadius = new CornerRadius(0)
            };
            ApplyActionRiskStyle(repairSelectedButton, ToolActionRisk.Success);
            Grid.SetColumn(repairSelectedButton, 3);
            actionBar.Children.Add(repairSelectedButton);
            Grid.SetRow(actionBar, 2);
            contentGrid.Children.Add(actionBar);

            ToolWindow window = new(
                this,
                "Games Runtime & Compatibility Check",
                contentGrid,
                primaryButtonText: UiTextKeys.DownloadInstall,
                secondaryButtonText: UiTextKeys.EnableSelected,
                closeButtonText: "Close",
                initialWidth: 1240,
                initialHeight: 800,
                minimumWidth: 760,
                minimumHeight: 500);
            window.PrimaryButton.IsEnabled = false;
            window.SecondaryButton.IsEnabled = false;

            bool loadInProgress = false;
            OperationGate runtimeGate = new();
            window.IsBusy = () => loadInProgress || runtimeGate.IsBusy;
            GamingRuntimeEntry? selectedEntry = null;

            void UpdateSelection()
            {
                selectedEntry = list.SelectedItem is ListViewItem item
                    ? item.Tag as GamingRuntimeEntry
                    : null;
                bool installable = selectedEntry is not null &&
                    GamingRuntimeInstallerService.CanInstall(selectedEntry.Id);
                bool repairable = selectedEntry is not null &&
                    selectedEntry.Health == GamingRuntimeHealth.Ready &&
                    GamingRuntimeInstallerService.CanRepair(selectedEntry.Id);
                bool busy = loadInProgress || runtimeGate.IsBusy;
                list.IsEnabled = !busy;
                analyzeButton.IsEnabled = !busy;
                RuntimeActionAvailability available = RuntimeActionAvailability.Resolve(busy,
                    installable, repairable, selectedEntry?.Enableable == true);
                window.PrimaryButton.IsEnabled = available.Install;
                window.SecondaryButton.IsEnabled = available.Enable;
                repairSelectedButton.IsEnabled = available.Repair;
                officialSourceButton.IsEnabled = !busy &&
                    !string.IsNullOrWhiteSpace(selectedEntry?.OfficialSource);
                if (selectedEntry is not null)
                {
                    statusText.Text = selectedEntry.Details;
                }
            }

            async Task AnalyzeAsync(string? preserveId = null)
            {
                if (loadInProgress)
                {
                    return;
                }
                loadInProgress = true;
                analyzeButton.IsEnabled = false;
                window.PrimaryButton.IsEnabled = false;
                window.SecondaryButton.IsEnabled = false;
                repairSelectedButton.IsEnabled = false;
                officialSourceButton.IsEnabled = false;
                list.Items.Clear();
                statusText.Text = "Analyzing installed runtimes and Windows prerequisites...";
                InlineAnalysisProgress progressWindow = window.CreateAnalysisProgress(
                    new[]
                    {
                        new CatalogProgressItem(
                            "RuntimeCompatibility",
                            "Games Runtime & Compatibility")
                    });
                progressWindow.Show();
                progressWindow.BeginItem(
                    "RuntimeCompatibility",
                    "Analyzing: installed runtimes and Windows prerequisites");
                try
                {
                    IReadOnlyList<GamingRuntimeEntry> entries =
                        await _gamingRuntimeCompatibilityService.AnalyzeDetailedAsync();
                    progressWindow.VerifyItem(
                        "RuntimeCompatibility",
                        "Verifying: runtime compatibility results");
                    int ready = 0;
                    int attention = 0;
                    int optional = 0;
                    ListViewItem? preservedItem = null;
                    foreach (GamingRuntimeEntry entry in entries)
                    {
                        Grid row = CreateGamingRuntimeGrid();
                        row.Padding = new Thickness(8, 7, 8, 7);
                        AddGamingRuntimeGridText(row, entry.Component, 0, false);
                        AddGamingRuntimeGridText(row, entry.Category, 1, false);
                        TextBlock health = AddGamingRuntimeGridText(row, entry.Status, 2, true);
                        AddGamingRuntimeGridText(row, entry.Details, 3, false);
                        health.Foreground = new SolidColorBrush(entry.Health switch
                        {
                            GamingRuntimeHealth.Ready => Color.FromArgb(255, 0, 112, 60),
                            GamingRuntimeHealth.Attention => Color.FromArgb(255, 185, 28, 28),
                            _ => Color.FromArgb(255, 99, 105, 115)
                        });
                        switch (entry.Health)
                        {
                            case GamingRuntimeHealth.Ready: ready++; break;
                            case GamingRuntimeHealth.Attention: attention++; break;
                            default: optional++; break;
                        }
                        ListViewItem item = new()
                        {
                            Content = row,
                            Tag = entry,
                            HorizontalContentAlignment = HorizontalAlignment.Stretch,
                            Padding = new Thickness(0),
                            Margin = new Thickness(0)
                        };
                        list.Items.Add(item);
                        if (string.Equals(entry.Id, preserveId, StringComparison.OrdinalIgnoreCase))
                        {
                            preservedItem = item;
                        }
                    }
                    statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 84, 134));
                    statusText.Text =
                        $"Analyzed {entries.Count} component(s) | Ready {ready} | Attention {attention} | Optional {optional}. Select a row for available actions.";
                    if (preservedItem is not null)
                    {
                        list.SelectedItem = preservedItem;
                    }
                    else if (list.Items.Count > 0)
                    {
                        list.SelectedIndex = 0;
                    }
                    TaskStatusMessage = "TASKS: COMPLETE";
                    progressWindow.CompleteItem(
                        "RuntimeCompatibility",
                        success: true,
                        statusText.Text);
                    progressWindow.UpdateOverall(1, statusText.Text);
                    progressWindow.Complete(success: true, statusText.Text);
                }
                catch (Exception exception)
                {
                    progressWindow.CompleteItem(
                        "RuntimeCompatibility",
                        success: false,
                        exception.Message);
                    progressWindow.Complete(success: false, exception.Message);
                    statusText.Text = $"Analysis failed: {exception.Message}";
                    statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28));
                    TaskStatusMessage = "TASKS: FAILED";
                }
                finally
                {
                    loadInProgress = false;
                    analyzeButton.IsEnabled = true;
                    UpdateSelection();
                }
            }

            async Task RunPackageOperationAsync(GamingRuntimeEntry entry, bool repairMode)
            {
                if (loadInProgress || window.IsClosed) return;
                using IDisposable? operationLease = runtimeGate.TryEnter();
                if (operationLease is null) return;
                try
                {
                    UpdateSelection();

                string action = repairMode ? "Repair / re-install" : UiTextKeys.DownloadInstall;
                string confirmation = (repairMode
                    ? UiTextKeys.RuntimeRepairConfirmation
                    : UiTextKeys.RuntimeInstallConfirmation) + "\n\n" + entry.Component;
                if (!await ShowConfirmationWindowAsync(
                        $"{action} gaming runtime",
                        confirmation + "\n\nAdministrator rights and an Internet connection are required.",
                        action))
                {
                    return;
                }

                using TaskActivityService.TaskActivityLease? taskLease =
                    await AcquireManagedTaskAsync(
                        $"GamingRuntime:{entry.Id}",
                        $"{action}: {entry.Component}",
                        new[] { "SystemMutation", "RuntimePackages", "WindowsServicing" });
                if (taskLease is null)
                {
                    return;
                }

                loadInProgress = true;
                analyzeButton.IsEnabled = false;
                repairSelectedButton.IsEnabled = false;
                UpdateSelection();
                TaskStatusMessage = repairMode ? "TASKS: RUNTIME REPAIR" : "TASKS: RUNTIME INSTALL";
                MaintenanceProgressWindow progressWindow = new(
                    window,
                    $"{action}: {entry.Component}",
                    "Packages are accepted only from the mapped official HTTPS source and must pass integrity and publisher verification before execution.",
                    new[]
                    {
                        "Preflight and package plan",
                        "Download official package(s)",
                        "Verify package integrity and publisher",
                        repairMode ? "Repair or re-install runtime" : "Install runtime",
                        "Post-install read-back verification"
                    });
                progressWindow.Show();
                try
                {
                    taskLease.UpdateDetail($"Running verified package workflow for {entry.Component}.");
                    GamingRuntimeInstallResult result = await _gamingRuntimeInstallerService.RunAsync(
                        entry,
                        repairMode,
                        _gamingRuntimeCompatibilityService,
                        progressWindow.Progress);
                    if (result.Success && result.Verified && result.RestartRequired)
                        _pendingRestarts.Add(entry.Component);
                    progressWindow.Complete(
                        result.Success && result.Verified,
                        result.WarningCount,
                        result.Success && result.Verified
                            ? $"{entry.Component} completed and its installed state was verified." +
                              (result.RestartRequired ? " Restart Windows to finish pending changes." : string.Empty)
                            : $"{entry.Component} did not pass the final verification.",
                        result.Report);
                    TaskStatusMessage = result.Success && result.Verified
                        ? "TASKS: COMPLETE"
                        : "TASKS: FAILED";
                    taskLease.Complete(
                        result.Success && result.Verified ? "COMPLETED" : "FAILED",
                        result.Success && result.Verified
                            ? $"{entry.Component} completed and was verified."
                            : $"{entry.Component} failed final verification.");
                }
                catch (Exception exception)
                {
                    string report = $"{action.ToUpperInvariant()} FAILED{Environment.NewLine}{exception}";
                    progressWindow.Complete(false, 0, exception.Message, report);
                    TaskStatusMessage = "TASKS: FAILED";
                    taskLease.Complete("FAILED", exception.Message);
                }
                finally
                {
                    taskLease.Dispose();
                    RefreshManagedTaskHeader();
                    loadInProgress = false;
                    analyzeButton.IsEnabled = true;
                    await AnalyzeAsync(entry.Id);
                }
                }
                catch (Exception exception)
                {
                    statusText.Text = exception.Message;
                }
                finally
                {
                    loadInProgress = false;
                    operationLease.Dispose();
                    if (!window.IsClosed) UpdateSelection();
                }
            }

            async Task RunWindowsEnableOperationAsync(GamingRuntimeEntry entry)
            {
                if (loadInProgress || window.IsClosed) return;
                using IDisposable? operationLease = runtimeGate.TryEnter();
                if (operationLease is null) return;
                try
                {
                    UpdateSelection();

                if (!await ShowConfirmationWindowAsync(
                        "Repair / enable Windows prerequisite",
                        UiTextKeys.RuntimeEnableConfirmation + "\n\n" + entry.Component,
                        "Repair / enable"))
                {
                    return;
                }

                using TaskActivityService.TaskActivityLease? taskLease =
                    await AcquireManagedTaskAsync(
                        $"GamingRuntime:{entry.Id}",
                        $"Repair / enable: {entry.Component}",
                        new[] { "SystemMutation", "RuntimePackages", "WindowsServicing" });
                if (taskLease is null)
                {
                    return;
                }

                loadInProgress = true;
                analyzeButton.IsEnabled = false;
                UpdateSelection();
                TaskStatusMessage = "TASKS: RUNTIME REPAIR";
                MaintenanceProgressWindow progressWindow = new(
                    window,
                    $"Repair / enable: {entry.Component}",
                    "The Windows prerequisite is changed through Windows servicing or Service Control, then analyzed again for read-back verification.",
                    new[]
                    {
                        "Preflight and current-state check",
                        "Enable or restore Windows prerequisite",
                        "Read-back verification"
                    });
                progressWindow.Show();
                int stageWarnings = 0;
                MaintenanceStageTracker stages = new(progressWindow.Progress, () => stageWarnings);
                try
                {
                    taskLease.UpdateDetail($"Repairing Windows prerequisite {entry.Component}.");
                    MaintenanceProgress.StartStage(
                        stages,
                        1,
                        3,
                        "Preflight and current-state check",
                        $"Checking Administrator rights and current state for {entry.Component}...");
                    if (!WindowsPrivilegeService.IsAdministrator())
                    {
                        throw new InvalidOperationException("Administrator rights are required.");
                    }

                    MaintenanceProgress.StartStage(
                        stages,
                        2,
                        3,
                        "Enable or restore Windows prerequisite",
                        $"Applying the supported Windows operation for {entry.Component}...");
                    GamingRuntimeOperationResult result =
                        await _gamingRuntimeCompatibilityService.EnableAsync(entry);

                    stageWarnings = result.RestartRecommended ? 1 : 0;
                    stages.FinishStage(result.Success);
                    MaintenanceProgress.StartStage(
                        stages,
                        3,
                        3,
                        "Read-back verification",
                        $"Re-analyzing {entry.Component} after the operation...");
                    IReadOnlyList<GamingRuntimeEntry> verification =
                        await _gamingRuntimeCompatibilityService.AnalyzeDetailedAsync();
                    GamingRuntimeEntry? verifiedEntry = verification.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
                    stageWarnings = result.RestartRecommended ? 1 : 0;
                    bool verified = result.Success && verifiedEntry?.Health == GamingRuntimeHealth.Ready;
                    bool awaitingRestart = RuntimePrerequisiteVerification.AwaitingRestart(
                        result.Success, result.RestartRecommended, verifiedEntry?.Status);
                    bool accepted = verified || awaitingRestart;
                    if (accepted && result.RestartRecommended) _pendingRestarts.Add(entry.Component);
                    if (awaitingRestart) stageWarnings++;
                    string completionDetail = awaitingRestart
                        ? $"{entry.Component} is waiting for a Windows restart. Readiness is not yet verified; restart and analyze again."
                        : verified ? $"{entry.Component} is enabled and verified." +
                            (result.RestartRecommended ? " Restart Windows is recommended." : string.Empty)
                        : $"{entry.Component} did not pass final verification.";
                    string report =
                        $"WINDOWS PREREQUISITE REPAIR{Environment.NewLine}" +
                        $"Component: {entry.Component}{Environment.NewLine}" +
                        $"Operation: {result.Message}{Environment.NewLine}" +
                        $"Read-back: {verifiedEntry?.Details ?? "Component was not returned by the analyzer."}{Environment.NewLine}" +
                        $"Verified: {(verified ? "YES" : awaitingRestart ? "PENDING RESTART" : "NO")}";
                    stages.Complete(accepted);
                    progressWindow.Complete(
                        accepted,
                        result.RestartRecommended ? 1 : 0,
                        completionDetail,
                        report);
                    TaskStatusMessage = awaitingRestart ? "TASKS: WARNING" : verified ? "TASKS: COMPLETE" : "TASKS: FAILED";
                    taskLease.Complete(
                        awaitingRestart ? "WARNING" : verified ? "COMPLETED" : "FAILED",
                        completionDetail);
                }
                catch (Exception exception)
                {
                    stages.Complete(false);
                    progressWindow.Complete(
                        false,
                        0,
                        exception.Message,
                        $"WINDOWS PREREQUISITE REPAIR FAILED{Environment.NewLine}{exception}");
                    TaskStatusMessage = "TASKS: FAILED";
                    taskLease.Complete("FAILED", exception.Message);
                }
                finally
                {
                    taskLease.Dispose();
                    RefreshManagedTaskHeader();
                    loadInProgress = false;
                    analyzeButton.IsEnabled = true;
                    await AnalyzeAsync(entry.Id);
                }
                }
                catch (Exception exception)
                {
                    statusText.Text = exception.Message;
                }
                finally
                {
                    loadInProgress = false;
                    operationLease.Dispose();
                    if (!window.IsClosed) UpdateSelection();
                }
            }

            list.SelectionChanged += (_, _) => UpdateSelection();
            analyzeButton.Click += async (_, _) => await AnalyzeAsync();
            officialSourceButton.Click += (_, _) =>
            {
                if (selectedEntry is null)
                {
                    return;
                }
                GamingRuntimeOperationResult result =
                    _gamingRuntimeCompatibilityService.OpenOfficialSource(selectedEntry);
                statusText.Text = result.Message;
                statusText.Foreground = new SolidColorBrush(result.Success
                    ? Color.FromArgb(255, 0, 112, 60)
                    : Color.FromArgb(255, 185, 28, 28));
            };
            window.PrimaryButton.Click += async (_, _) =>
            {
                GamingRuntimeEntry? entry = selectedEntry;
                if (entry is null || loadInProgress ||
                    !GamingRuntimeInstallerService.CanInstall(entry.Id))
                {
                    return;
                }
                await RunPackageOperationAsync(entry, repairMode: false);
            };
            repairSelectedButton.Click += async (_, _) =>
            {
                GamingRuntimeEntry? entry = selectedEntry;
                if (entry is null || loadInProgress ||
                    entry.Health != GamingRuntimeHealth.Ready ||
                    !GamingRuntimeInstallerService.CanRepair(entry.Id))
                {
                    return;
                }
                await RunPackageOperationAsync(entry, repairMode: true);
            };
            window.SecondaryButton.Click += async (_, _) =>
            {
                GamingRuntimeEntry? entry = selectedEntry;
                if (entry is null || loadInProgress)
                {
                    return;
                }
                if (entry.Enableable)
                {
                    await RunWindowsEnableOperationAsync(entry);
                }
            };

            Task<ToolWindowResult> windowTask = window.ShowAsync();
            await AnalyzeAsync();
            await windowTask;
        }

        private static Grid CreateGamingRuntimeGrid()
        {
            Grid grid = new() { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            return grid;
        }

        private static TextBlock AddGamingRuntimeGridText(
            Grid grid,
            string text,
            int column,
            bool bold)
        {
            TextBlock label = new()
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontWeight = bold
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal
            };
            Grid.SetColumn(label, column);
            grid.Children.Add(label);
            return label;
        }

        private async void GamingTweaksButton_Click(object sender, RoutedEventArgs e)
        {
            TaskStatusMessage = "TASKS: GAMING TWEAKS";

            try
            {
                await ShowToggleCatalogDialogAsync(
                    "Gaming Tweaks",
                    GamingCatalog(),
                    _gamingActionsService,
                    GamingCatalogOwnership.Notice + "\n" + GamingCatalogOwnership.IndependentNotice);
            }
            catch (Exception exception)
            {
                TaskStatusMessage = "TASKS: FAILED";
                await ShowMessageDialogAsync("Gaming Tweaks failed", exception.Message);
            }
            finally
            {
                RefreshManagedTaskHeader();
                await UpdateGamingStatusAsync();
            }
        }

        private async void EssentialTweaksButton_Click(object sender, RoutedEventArgs e)
        {
            TaskStatusMessage = "TASKS: ESSENTIAL TWEAKS";

            try
            {
                await ShowToggleCatalogDialogAsync(
                    "Essential Windows Tweaks",
                    EssentialCatalog(),
                    new FilteredToolActionService(
                        _essentialActionsService,
                        "IconCache",
                        "TempFiles",
                        "NtfsPerformance",
                        "StoragePowerLatency",
                        "DriverOptionalUpdates",
                        "VirtualMemorySettings",
                        "WindowsTroubleshoot",
                        "DesktopBackground"));
            }
            catch (Exception exception)
            {
                TaskStatusMessage = "TASKS: FAILED";
                await ShowMessageDialogAsync("Essential Windows Tweaks failed", exception.Message);
            }
            finally
            {
                RefreshManagedTaskHeader();
            }
        }

        private async void DebloatButton_Click(object sender, RoutedEventArgs e)
        {
            TaskStatusMessage = "TASKS: DE-BLOAT PREVIEW";
            try
            {
                await ShowToggleCatalogDialogAsync(
                    "Advanced Windows Tweaks & De-Bloat",
                    AdvancedCatalog(),
                    new BuiltInAppsService());
            }
            catch (Exception exception)
            {
                TaskStatusMessage = "TASKS: FAILED";
                await ShowMessageDialogAsync("Windows De-bloat failed", exception.Message);
            }
            finally
            {
                RefreshManagedTaskHeader();
            }
        }

        private async Task ShowToggleCatalogDialogAsync(
            string title,
            IToolToggleService service,
            IToolActionService? actionService = null,
            string? ownershipNotice = null)
        {
            IReadOnlyList<ToolToggleDefinition> definitions = service.GetDefinitions();
            Dictionary<string, ToolToggleState> currentStates = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, CheckBox> selectors = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ToggleSwitch> toggles = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, TextBlock> stateTexts = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Grid> toggleRows = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Func<Task>> bulkActionRefresh = new(StringComparer.OrdinalIgnoreCase);
            Action? refreshBulkSummary = null;
            bool synchronizingControls = false;
            OperationGate individualActionGate = new();
            List<(Button Button, bool RequiresAdministrator)> individualButtons = new();
            Action<bool>? setCatalogInteraction = null;
            Func<bool>? canStartIndividualAction = null;
            Action<Exception>? reportIndividualFailure = null;

            async Task RunIndividualActionAsync(Func<Task> run)
            {
                if (canStartIndividualAction?.Invoke() != true) return;
                using IDisposable? actionLease = individualActionGate.TryEnter();
                if (actionLease is null) return;
                try
                {
                    // Guard before confirmation/admission, not after the worker
                    // starts. Rejected repeat clicks cannot re-enable live work.
                    setCatalogInteraction?.Invoke(false);
                    await run();
                }
                catch (Exception exception)
                {
                    // Includes confirmation-window failures in async Click events.
                    reportIndividualFailure?.Invoke(exception);
                }
                finally
                {
                    actionLease.Dispose();
                    setCatalogInteraction?.Invoke(true);
                }
            }

            Grid contentGrid = new();
            // Match the reference layout: the catalog rows scroll in the
            // remaining space while the bulk toolbar stays anchored below the
            // list and above the progress/status footer.
            contentGrid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star),
                MinHeight = 160
            });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            StackPanel rows = new() { Spacing = 8 };
            if (!string.IsNullOrWhiteSpace(ownershipNotice))
            {
                rows.Children.Add(new TextBlock
                {
                    Text = ownershipNotice,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 0, 4, 8)
                });
            }
            foreach (ToolToggleDefinition definition in definitions)
            {
                ToolToggleState state = new(
                    false,
                    false,
                    "Checking current state...",
                    "Checking current state...");
                currentStates[definition.Id] = state;

                Border card = new()
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb(255, 187, 199, 213)),
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(Color.FromArgb(255, 238, 243, 249)),
                    Padding = new Thickness(12, 9, 12, 9),
                    CornerRadius = new CornerRadius(0)
                };
                Grid row = new();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                // WinUI's default templates need roughly 120 DIPs for a
                // CheckBox and 154 DIPs for a ToggleSwitch. The previous
                // 105/130 columns constrained those templates and visibly cut
                // their square/circle, especially after text scaling.
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });

                StackPanel details = new() { Spacing = 3 };
                details.Children.Add(new TextBlock
                {
                    Text = definition.Name,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                details.Children.Add(CreateCatalogCategoryBadge(
                    definition.Category,
                    definition.SelectionTier));
                details.Children.Add(new TextBlock
                {
                    Text = definition.Description,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 70, 95))
                });
                if (!string.IsNullOrWhiteSpace(definition.Warning))
                {
                    details.Children.Add(new TextBlock
                    {
                        Text = definition.Warning,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28))
                    });
                }
                TextBlock actual = new()
                {
                    Text = "Checking current state...",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 79, 146))
                };
                details.Children.Add(actual);
                row.Children.Add(details);

                CheckBox selector = new()
                {
                    Content = "Select",
                    IsChecked = false,
                    IsEnabled = false,
                    MinWidth = 120,
                    MinHeight = 32,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(selector, 1);
                row.Children.Add(selector);

                ToggleSwitch toggle = new()
                {
                    IsOn = false,
                    IsEnabled = false,
                    OnContent = "ON",
                    OffContent = "OFF",
                    MinWidth = 154,
                    MinHeight = 32,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(toggle, 2);
                row.Children.Add(toggle);

                selectors[definition.Id] = selector;
                toggles[definition.Id] = toggle;
                stateTexts[definition.Id] = actual;
                toggleRows[definition.Id] = row;
                card.Child = row;
                rows.Children.Add(card);
            }

            if (actionService is not null)
            {
                foreach (ToolActionDefinition action in actionService.GetActions())
                {
                    Border card = new()
                    {
                        BorderBrush = new SolidColorBrush(Color.FromArgb(255, 187, 199, 213)),
                        BorderThickness = new Thickness(1),
                        Background = new SolidColorBrush(Color.FromArgb(255, 248, 242, 226)),
                        Padding = new Thickness(12, 9, 12, 9),
                        CornerRadius = new CornerRadius(0)
                    };
                    Grid row = new();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    // Size from the translated button labels, not an English-only
                    // 250-DIP reservation that clips actions at larger text scales.
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    StackPanel details = new() { Spacing = 3 };
                    details.Children.Add(new TextBlock
                    {
                        Text = action.Name,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    });
                    details.Children.Add(CreateCatalogCategoryBadge(
                        action.Category,
                        action.Risk));
                    details.Children.Add(new TextBlock
                    {
                        Text = action.Description,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 70, 95))
                    });
                    TextBlock actionResult = new()
                    {
                        Text = "Ready. No action has run in this dialog session.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 79, 146))
                    };
                    details.Children.Add(actionResult);
                    if (!string.IsNullOrWhiteSpace(action.Warning))
                        details.Children.Add(new TextBlock
                        {
                            Text = action.Warning, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                            Foreground = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28))
                        });
                    row.Children.Add(details);

                    StackPanel actionButtons = new()
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Button runButton = new()
                    {
                        Content = action.RunLabel,
                        MinWidth = 112,
                        IsEnabled = !action.RequiresAdministrator || WindowsPrivilegeService.IsAdministrator()
                    };
                    Button? restoreButton = action.SupportsRestore
                        ? new Button
                        {
                            Content = action.RestoreLabel,
                            MinWidth = 100,
                            IsEnabled = !action.RequiresAdministrator || WindowsPrivilegeService.IsAdministrator()
                        }
                        : null;

                    ApplyActionRiskStyle(runButton, action.Risk);
                    individualButtons.Add((runButton, action.RequiresAdministrator));
                    if (restoreButton is not null)
                        individualButtons.Add((restoreButton, action.RequiresAdministrator));

                    runButton.Click += async (_, _) => await RunIndividualActionAsync(async () =>
                    {
                        if (actionService is BuiltInAppsService builtInApps && action.Id == "BuiltInWindowsApps")
                        {
                            await ShowBuiltInAppsAsync(builtInApps);
                            return;
                        }
                        if (!string.IsNullOrWhiteSpace(action.Confirmation) &&
                            !await ShowConfirmationWindowAsync(
                                action.Name,
                                action.Confirmation,
                                action.RunLabel))
                        {
                            return;
                        }

                        if (action.Id.Equals("ProcessManager", StringComparison.OrdinalIgnoreCase) &&
                            actionService is GamingActionsService gamingActions)
                        {
                            runButton.IsEnabled = false;
                            try
                            {
                                await ShowGamingProcessManagerWindowAsync(gamingActions);
                                actionResult.Text = "Process Manager closed.";
                            }
                            catch (Exception exception)
                            {
                                actionResult.Text = $"Unable to open Process Manager: {exception.Message}";
                            }
                            finally
                            {
                                runButton.IsEnabled = !action.RequiresAdministrator ||
                                                      WindowsPrivilegeService.IsAdministrator();
                            }
                            return;
                        }

                        TaskActivityService.TaskActivityLease? taskLease = null;
                        CatalogProgressWindow? progressWindow = null;
                        try
                        {
                            taskLease = await AcquireManagedTaskAsync(
                                $"CatalogAction:{title}:{action.Id}",
                                $"{title} - {action.Name}",
                                new[] { "SystemMutation", $"Catalog:{title}" });
                            if (taskLease is null)
                            {
                                return;
                            }

                            progressWindow = new CatalogProgressWindow(
                                this,
                                "Running",
                                new[] { new CatalogProgressItem(action.Id, action.Name) });
                            progressWindow.Show();
                            runButton.IsEnabled = false;
                            if (restoreButton is not null)
                            {
                                restoreButton.IsEnabled = false;
                            }
                            actionResult.Text = $"Running: {action.Name}...";
                            taskLease?.UpdateDetail($"Running: {action.Name}.");
                            progressWindow.BeginItem(action.Id, $"Running: {action.Name}");
                            ToolActionResult result = await CatalogActionRunner.ExecuteAsync(
                                actionService, action, restore: false, progressWindow.CreateReporter(action.Id));
                            if (RestartPromptPolicy.ForAction(action, result)) _pendingRestarts.Add(action.Name);
                            if (bulkActionRefresh.TryGetValue(action.Id, out var refresh)) await refresh();
                            bool completed = result.Success || result.SkippedUnavailable;
                            actionResult.Text = result.SkippedUnavailable ? result.Message : result.Success
                                ? "Completed. See the progress window for details."
                                : "Failed. See the progress window for details.";
                            actionResult.Foreground = new SolidColorBrush(result.SkippedUnavailable
                                ? Color.FromArgb(255, 107, 114, 128)
                                : result.Success ? Color.FromArgb(255, 0, 112, 60) : Color.FromArgb(255, 185, 28, 28));
                            if (result.SkippedUnavailable) progressWindow.UnavailableItem(action.Id, result.Message);
                            else progressWindow.CompleteItem(action.Id, result.Success, result.Message);
                            progressWindow.UpdateOverall(1, result.Message);
                            progressWindow.Complete(completed, result.Message);
                            taskLease?.Complete(result.SkippedUnavailable ? "UNAVAILABLE" : result.Success ? "COMPLETED" : "FAILED", result.Message);
                        }
                        catch (Exception exception)
                        {
                            actionResult.Text = "Failed. See the progress window for details.";
                            actionResult.Foreground = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28));
                            progressWindow?.CompleteItem(action.Id, success: false, exception.Message);
                            progressWindow?.Complete(success: false, exception.Message);
                            taskLease?.Complete("FAILED", exception.Message);
                        }
                        finally
                        {
                            taskLease?.Dispose();
                            RefreshManagedTaskHeader();
                            runButton.IsEnabled = !action.RequiresAdministrator || WindowsPrivilegeService.IsAdministrator();
                            if (restoreButton is not null)
                            {
                                restoreButton.IsEnabled = runButton.IsEnabled;
                            }
                        }
                    });
                    actionButtons.Children.Add(runButton);

                    if (restoreButton is not null)
                    {
                        restoreButton.Click += async (_, _) => await RunIndividualActionAsync(async () =>
                        {
                            if (!string.IsNullOrWhiteSpace(action.RestoreConfirmation) &&
                                !await ShowConfirmationWindowAsync(
                                    $"Restore {action.Name}",
                                    action.RestoreConfirmation,
                                    "Restore"))
                            {
                                return;
                            }
                            TaskActivityService.TaskActivityLease? taskLease = null;
                            CatalogProgressWindow? progressWindow = null;
                            try
                            {
                                taskLease = await AcquireManagedTaskAsync(
                                    $"CatalogAction:{title}:{action.Id}",
                                    $"{title} - Restore {action.Name}",
                                    new[] { "SystemMutation", $"Catalog:{title}" });
                                if (taskLease is null)
                                {
                                    return;
                                }

                                progressWindow = new CatalogProgressWindow(
                                    this,
                                    "Restoring",
                                    new[] { new CatalogProgressItem(action.Id, action.Name) });
                                progressWindow.Show();
                                runButton.IsEnabled = false;
                                restoreButton.IsEnabled = false;
                                actionResult.Text = $"Restoring: {action.Name}...";
                                taskLease.UpdateDetail($"Restoring: {action.Name}.");
                                progressWindow.BeginItem(action.Id, $"Restoring: {action.Name}");
                                ToolActionResult result = await CatalogActionRunner.ExecuteAsync(
                                    actionService, action, restore: true, progressWindow.CreateReporter(action.Id));
                                if (RestartPromptPolicy.ForAction(action, result)) _pendingRestarts.Add(action.Name);
                                if (bulkActionRefresh.TryGetValue(action.Id, out var refresh)) await refresh();
                                bool completed = result.Success || result.SkippedUnavailable;
                                actionResult.Text = result.SkippedUnavailable ? result.Message : result.Success
                                    ? "Completed. See the progress window for details."
                                    : "Failed. See the progress window for details.";
                                actionResult.Foreground = new SolidColorBrush(result.SkippedUnavailable
                                    ? Color.FromArgb(255, 107, 114, 128)
                                    : result.Success ? Color.FromArgb(255, 0, 112, 60) : Color.FromArgb(255, 185, 28, 28));
                                if (result.SkippedUnavailable) progressWindow.UnavailableItem(action.Id, result.Message);
                                else progressWindow.CompleteItem(action.Id, result.Success, result.Message);
                                progressWindow.UpdateOverall(1, result.Message);
                                progressWindow.Complete(completed, result.Message);
                                taskLease.Complete(result.SkippedUnavailable ? "UNAVAILABLE" : result.Success ? "COMPLETED" : "FAILED", result.Message);
                            }
                            catch (Exception exception)
                            {
                                actionResult.Text = "Failed. See the progress window for details.";
                                actionResult.Foreground = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28));
                                progressWindow?.CompleteItem(action.Id, success: false, exception.Message);
                                progressWindow?.Complete(success: false, exception.Message);
                                taskLease?.Complete("FAILED", exception.Message);
                            }
                            finally
                            {
                                taskLease?.Dispose();
                                RefreshManagedTaskHeader();
                                runButton.IsEnabled = !action.RequiresAdministrator || WindowsPrivilegeService.IsAdministrator();
                                restoreButton.IsEnabled = runButton.IsEnabled;
                            }
                        });
                        actionButtons.Children.Add(restoreButton);
                    }

                    if (toggleRows.TryGetValue(action.Id, out Grid? existingRow))
                    {
                        // Preserve individual Apply/Restore while giving the same
                        // row Select + state controls for the reference bulk actions.
                        existingRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        existingRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        StackPanel actionsWithResult = new() { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
                        details.Children.Remove(actionResult);
                        actionsWithResult.Children.Add(actionButtons);
                        actionsWithResult.Children.Add(actionResult);
                        Grid.SetRow(actionsWithResult, 1);
                        Grid.SetColumnSpan(actionsWithResult, 3);
                        existingRow.Children.Add(actionsWithResult);
                        async Task RefreshBulkActionStateAsync()
                        {
                            ToolToggleDefinition definition = definitions.Single(item => item.Id == action.Id);
                            ToolToggleState state = await CatalogStateReader.ReadAsync(service, definition, TimeSpan.FromSeconds(30));
                            currentStates[action.Id] = state;
                            synchronizingControls = true;
                            try { toggles[action.Id].IsOn = state.IsOn; }
                            finally { synchronizingControls = false; }
                            UpdateCatalogStateText(stateTexts[action.Id], state);
                            refreshBulkSummary?.Invoke();
                        }
                        // Run handlers already await their action; these completion
                        // callbacks are attached below through a shared delegate.
                        bulkActionRefresh[action.Id] = RefreshBulkActionStateAsync;
                    }
                    else
                    {
                        Grid.SetColumn(actionButtons, 1);
                        row.Children.Add(actionButtons);
                        card.Child = row;
                        rows.Children.Add(card);
                    }
                }
            }

            ScrollViewer catalogRowsViewer = new()
            {
                Content = rows,
                MinHeight = 160,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled
            };
            Grid.SetRow(catalogRowsViewer, 0);
            contentGrid.Children.Add(catalogRowsViewer);

            TextBlock selectionSummary = new()
            {
                Text = "Catalog controls become available after analysis finishes.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 84, 134)),
                Margin = new Thickness(2, 5, 2, 0)
            };
            Grid toolbar = new()
            {
                ColumnSpacing = 6,
                RowSpacing = 6,
                Margin = new Thickness(4, 10, 4, 0)
            };
            for (int column = 0; column < 4; column++)
            {
                toolbar.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
            }
            toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Button selectAllButton = CreateCatalogToolbarButton(
                "Select all",
                Color.FromArgb(255, 37, 99, 235));
            Button selectSafeButton = CreateCatalogToolbarButton(
                UiTextKeys.SelectSafe,
                Color.FromArgb(255, 21, 128, 61));
            Button selectAdvancedButton = CreateCatalogToolbarButton(
                "Select advanced",
                Color.FromArgb(255, 180, 83, 9));
            Button clearButton = CreateCatalogToolbarButton(
                "De-select all",
                Color.FromArgb(255, 90, 101, 114));
            Button restoreSafeButton = CreateCatalogToolbarButton(
                "Restore safe",
                Color.FromArgb(255, 21, 128, 61));
            Button restoreAdvancedButton = CreateCatalogToolbarButton(
                "Restore advanced",
                Color.FromArgb(255, 180, 83, 9));
            Button restoreAllButton = CreateCatalogToolbarButton(
                "Restore all defaults",
                Color.FromArgb(255, 21, 128, 61));
            Button analyzeButton = CreateCatalogToolbarButton(
                UiTextKeys.AnalyzeReload,
                Color.FromArgb(255, 37, 99, 235));
            Button[] toolbarButtons =
            {
                selectAllButton,
                selectSafeButton,
                selectAdvancedButton,
                clearButton,
                restoreSafeButton,
                restoreAdvancedButton,
                restoreAllButton,
                analyzeButton
            };
            for (int index = 0; index < toolbarButtons.Length; index++)
            {
                Button button = toolbarButtons[index];
                button.IsEnabled = false;
                Grid.SetRow(button, index / 4);
                Grid.SetColumn(button, index % 4);
                toolbar.Children.Add(button);
            }
            Grid.SetRow(selectionSummary, 2);
            Grid.SetColumnSpan(selectionSummary, 4);
            toolbar.Children.Add(selectionSummary);
            Grid.SetRow(toolbar, 1);
            contentGrid.Children.Add(toolbar);

            TextBlock statusText = new()
            {
                Text = WindowsPrivilegeService.IsAdministrator()
                    ? "Select rows, then Apply selected or Restore selected. Toggle switches operate on one item after confirmation."
                    : "Read-only mode: run Visual Studio or the app as Administrator to apply changes.",
                Margin = new Thickness(4, 10, 4, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 84, 134))
            };
            CheckBox highImpactAcknowledgement = new()
            {
                Content = "I understand that selected HIGH IMPACT / HIGH RISK items can disable Windows or hardware workflows.",
                Margin = new Thickness(4, 7, 4, 0),
                Visibility = definitions.Any(IsHighImpact)
                    ? Visibility.Visible
                    : Visibility.Collapsed
            };
            StackPanel footer = new() { Spacing = 2 };
            CatalogAvailabilityBadge availabilityBadge = new();
            footer.Children.Add(availabilityBadge.View);
            footer.Children.Add(statusText);
            footer.Children.Add(highImpactAcknowledgement);
            Grid.SetRow(footer, 2);
            contentGrid.Children.Add(footer);

            ToolWindow window = new(
                this,
                title,
                contentGrid,
                primaryButtonText: UiTextKeys.ApplySelected,
                secondaryButtonText: "Restore selected",
                closeButtonText: "Close",
                initialWidth: 1120,
                initialHeight: 820,
                minimumWidth: 680,
                minimumHeight: 480);
            window.PrimaryButton.IsEnabled = false;
            window.SecondaryButton.IsEnabled = false;

            bool statesLoaded = false;
            bool applyInProgress = false;
            bool stateLoadInProgress = false;
            long loadedAtEpoch = -1;
            void InvalidateCatalogView()
            {
                if (window.IsClosed || loadedAtEpoch == CatalogStateEpoch.Version || applyInProgress || stateLoadInProgress) return;
                statesLoaded = false;
                SetCatalogInteraction(false);
                analyzeButton.IsEnabled = !individualActionGate.IsBusy;
                availabilityBadge.View.Visibility = Visibility.Collapsed;
                selectionSummary.Text = "Configuration changed since this scan. Analyze / reload before another operation.";
                foreach (var text in stateTexts.Values) text.Text = "Stale snapshot — Analyze / reload";
            }
            void OnCatalogStateChanged() => DispatcherQueue.TryEnqueue(InvalidateCatalogView);
            window.IsBusy = () => applyInProgress || stateLoadInProgress || individualActionGate.IsBusy;
            canStartIndividualAction = () => !window.IsClosed && statesLoaded &&
                !applyInProgress && !stateLoadInProgress && !individualActionGate.IsBusy;
            setCatalogInteraction = SetCatalogInteraction;
            reportIndividualFailure = exception =>
            {
                if (window.IsClosed) return;
                statusText.Text = exception.Message;
                statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28));
            };

            CatalogSelectionPlan SelectedPlan() => CatalogSelectionPlan.Create(
                definitions,
                currentStates,
                definition => selectors[definition.Id].IsChecked == true);

            void SetCatalogInteraction(bool enabled)
            {
                if (window.IsClosed) return;
                bool busy = applyInProgress || stateLoadInProgress || individualActionGate.IsBusy;
                window.CloseButton.IsEnabled = !busy;
                enabled &= statesLoaded && !busy;
                window.PrimaryButton.IsEnabled = enabled;
                window.SecondaryButton.IsEnabled = enabled;
                highImpactAcknowledgement.IsEnabled = enabled;
                foreach (var item in individualButtons)
                    item.Button.IsEnabled = enabled && (!item.RequiresAdministrator || WindowsPrivilegeService.IsAdministrator());
                foreach (ToolToggleDefinition definition in definitions)
                {
                    bool available = enabled && currentStates[definition.Id].IsAvailable;
                    selectors[definition.Id].IsEnabled = available;
                    toggles[definition.Id].IsEnabled = available;
                }
                foreach (Button button in toolbarButtons)
                {
                    button.IsEnabled = enabled;
                }
            }

            void UpdateSelectionSummary()
            {
                if (!statesLoaded)
                {
                    availabilityBadge.View.Visibility = Visibility.Collapsed;
                    selectionSummary.Text = stateLoadInProgress || loadedAtEpoch < 0 ? "Analyzing the current Windows state..." : "Configuration changed since this scan. Analyze / reload before another operation.";
                    return;
                }

                int available = definitions.Count(definition =>
                    currentStates[definition.Id].IsAvailable);
                int optimized = definitions.Count(definition =>
                    currentStates[definition.Id].IsAvailable &&
                    currentStates[definition.Id].IsOn);
                availabilityBadge.Update(available, definitions.Count,
                    definitions.Count(definition => currentStates[definition.Id].IsConfirmedUnavailable));
                CatalogSelectionPlan plan = SelectedPlan();
                int selected = plan.Selected.Count;
                int selectedChanges = plan.ToApply.Count;
                selectionSummary.Text =
                    $"Analyzed {available}/{definitions.Count} | Optimized {optimized} | Selected {selected} | Pending changes {selectedChanges}";
            }

            void SelectCatalogScope(Func<ToolToggleDefinition, bool> predicate)
            {
                foreach (ToolToggleDefinition definition in definitions)
                {
                    selectors[definition.Id].IsChecked =
                        currentStates[definition.Id].IsAvailable && predicate(definition);
                }
                UpdateSelectionSummary();
            }

            void QueueCatalogRestart(ToolToggleDefinition definition, ToolToggleOperationResult result)
            {
                if (RestartPromptPolicy.ForToggle(definition, result))
                    _pendingRestarts.Add(definition.Name);
            }

            async Task RunRestoreItemsAsync(
                IReadOnlyList<ToolToggleDefinition> items,
                bool windowsDefault,
                string operationLabel)
            {
                if (window.IsClosed || applyInProgress || stateLoadInProgress || individualActionGate.IsBusy)
                {
                    return;
                }
                if (!statesLoaded)
                {
                    statusText.Text = "Wait until the current setting checks have finished.";
                    return;
                }

                CatalogSelectionPlan restorePlan = CatalogSelectionPlan.Create(
                    items,
                    currentStates,
                    _ => true);
                ToolToggleDefinition[] availableItems = (windowsDefault
                        ? restorePlan.Selected
                        : restorePlan.ToRestore)
                    .ToArray();
                if (availableItems.Length == 0)
                {
                    statusText.Text = restorePlan.Selected.Count == 0
                        ? "No available item exists in this restore scope."
                        : "All available items in this restore scope are already restored (OFF).";
                    statusText.Foreground = new SolidColorBrush(
                        Color.FromArgb(255, 0, 112, 60));
                    return;
                }
                if (availableItems.Any(item => item.RequiresAdministrator) &&
                    !WindowsPrivilegeService.IsAdministrator())
                {
                    statusText.Text = "Administrator rights are required.";
                    statusText.Foreground = new SolidColorBrush(
                        Color.FromArgb(255, 185, 28, 28));
                    return;
                }

                applyInProgress = true;
                TaskActivityService.TaskActivityLease? taskLease = null;
                CatalogProgressWindow? progressWindow = null;
                try
                {
                    // Lock before awaiting the prompt: rapid clicks must not
                    // open concurrent operations against the same snapshot.
                    SetCatalogInteraction(false);
                    var restoreConflicts = CatalogEffectPlan.Create(service, availableItems).Conflicts;
                    if (restoreConflicts.Count > 0)
                    {
                        statusText.Text = "Restore blocked: overlapping actions have independent or incompatible owners. Select one owner and review its original snapshot.\n" + string.Join(Environment.NewLine, restoreConflicts);
                        await ShowMessageDialogAsync("Conflicting restore owners", statusText.Text);
                        return;
                    }
                    string targetDescription = windowsDefault
                        ? "Windows-controlled/default behavior. This intentionally discards saved pre-tweak snapshots for these items."
                        : "their captured pre-tweak state; when no snapshot exists, a documented Windows default is used instead.";
                    if (!await ShowConfirmationWindowAsync(
                            $"{title} - {operationLabel}",
                            $"Restore {availableItems.Length} applied item(s) to {targetDescription}" +
                            (windowsDefault || restorePlan.AlreadyRestoredCount == 0
                                ? string.Empty
                                : Environment.NewLine + $"{restorePlan.AlreadyRestoredCount} already-restored item(s) will be skipped.") +
                            Environment.NewLine + Environment.NewLine +
                            string.Join(Environment.NewLine, availableItems.Select(item => "• " + item.Name)),
                            "Restore"))
                    {
                        return;
                    }

                    taskLease = await AcquireManagedTaskAsync(
                        $"Catalog:{title}",
                        $"{title} - {operationLabel}",
                        new[] { "SystemMutation", $"Catalog:{title}" });
                    if (taskLease is null)
                    {
                        return;
                    }

                    if (loadedAtEpoch != CatalogStateEpoch.Version)
                    {
                        statusText.Text = "Configuration changed while waiting. Analyze / reload and review Restore again; nothing was applied by this request.";
                        taskLease.Complete("WARNING", statusText.Text);
                        return;
                    }

                    window.PrimaryButton.IsEnabled = false;
                    window.SecondaryButton.IsEnabled = false;
                    foreach (ToggleSwitch toggle in toggles.Values)
                    {
                        toggle.IsEnabled = false;
                    }
                    foreach (CheckBox selector in selectors.Values)
                    {
                        selector.IsEnabled = false;
                    }
                    foreach (Button button in toolbarButtons)
                    {
                        button.IsEnabled = false;
                    }

                    progressWindow = new CatalogProgressWindow(
                        this,
                        "Restoring",
                        availableItems
                            .Select(item => new CatalogProgressItem(item.Id, item.Name))
                            .ToArray());
                    progressWindow.Show();
                    using var executionBatch = CatalogExecutionBatch.Begin();
                    int succeeded = 0;
                    int completed = 0;
                    List<string> failures = new();

                    foreach (ToolToggleDefinition definition in availableItems)
                    {
                        statusText.Text = $"Restoring: {definition.Name}...";
                        taskLease.UpdateDetail($"Restoring: {definition.Name}");
                        progressWindow.BeginItem(definition.Id);
                        ToolToggleOperationResult result;
                        try
                        {
                            result = await CatalogOperationJournal.RunAsync(CatalogOperationJournal.DirectoryPath, definition,
                                windowsDefault ? CatalogOperation.RestoreWindowsDefaults : CatalogOperation.RestoreSavedState,
                                () => CatalogOperationRunner.ExecuteAsync(
                                service,
                                definition,
                                windowsDefault
                                    ? CatalogOperation.RestoreWindowsDefaults
                                    : CatalogOperation.RestoreSavedState,
                                progressWindow.CreateReporter(definition.Id)));
                            progressWindow.VerifyItem(definition.Id);
                        }
                        catch (Exception exception)
                        {
                            progressWindow.CompleteItem(
                                definition.Id,
                                success: false,
                                exception.Message);
                            throw;
                        }
                        currentStates[definition.Id] = result.State;
                        synchronizingControls = true;
                        try
                        {
                            toggles[definition.Id].IsOn = result.State.IsOn;
                            if (result.Success && result.Verified)
                            {
                                selectors[definition.Id].IsChecked = false;
                            }
                        }
                        finally
                        {
                            synchronizingControls = false;
                        }
                        UpdateCatalogStateText(stateTexts[definition.Id], result.State);
                        if (result.Success && result.Verified)
                        {
                            succeeded++;
                            QueueCatalogRestart(definition, result);
                        }
                        else if (!result.SkippedUnavailable || !result.State.IsConfirmedUnavailable)
                        {
                            failures.Add($"{definition.Name}:\n{result.Message}");
                        }
                        progressWindow.CompleteItem(definition.Id, result);
                        completed++;
                        progressWindow.UpdateOverall(
                            completed,
                            $"Processed {completed}/{availableItems.Length}: {definition.Name}");
                    }

                    string operationReport = failures.Count == 0
                        ? $"Restored and verified {succeeded} item(s)."
                        : $"Restored {succeeded}/{availableItems.Length}. {string.Join(" | ", failures)}";
                    statusText.Text = failures.Count == 0
                        ? $"Restore completed for {succeeded} item(s). See the progress window for details."
                        : $"Restore completed with {failures.Count} failure(s). See the progress window for details.";
                    statusText.Foreground = new SolidColorBrush(failures.Count == 0
                        ? Color.FromArgb(255, 0, 112, 60)
                        : Color.FromArgb(255, 185, 28, 28));
                    progressWindow.Complete(failures.Count == 0, operationReport);
                    taskLease.Complete(
                        failures.Count == 0 ? "COMPLETED" : "WARNING",
                        operationReport);
                    UpdateSelectionSummary();

                }
                catch (Exception exception)
                {
                    progressWindow?.Complete(success: false, exception.Message);
                    statusText.Text = exception.Message;
                    statusText.Foreground = new SolidColorBrush(
                        Color.FromArgb(255, 185, 28, 28));
                    taskLease?.Complete("FAILED", exception.Message);
                }
                finally
                {
                    taskLease?.Dispose();
                    RefreshManagedTaskHeader();
                    applyInProgress = false;
                    SetCatalogInteraction(true);
                    UpdateSelectionSummary();
                    analyzeButton.IsEnabled = true;
                    InvalidateCatalogView();
                }
            }

            selectAllButton.Click += (_, _) =>
                SelectCatalogScope(_ => true);
            selectSafeButton.Click += (_, _) =>
                SelectCatalogScope(definition => !IsAdvancedImpact(definition));
            selectAdvancedButton.Click += (_, _) =>
                SelectCatalogScope(IsAdvancedImpact);
            clearButton.Click += (_, _) =>
            {
                foreach (CheckBox selector in selectors.Values)
                {
                    selector.IsChecked = false;
                }
                UpdateSelectionSummary();
            };
            restoreSafeButton.Click += async (_, _) =>
                await RunRestoreItemsAsync(
                    definitions.Where(definition => !IsAdvancedImpact(definition)).ToArray(),
                    windowsDefault: false,
                    "Restore safe");
            restoreAdvancedButton.Click += async (_, _) =>
                await RunRestoreItemsAsync(
                    definitions.Where(IsAdvancedImpact).ToArray(),
                    windowsDefault: false,
                    "Restore advanced");
            restoreAllButton.Click += async (_, _) =>
                await RunRestoreItemsAsync(
                    definitions,
                    windowsDefault: true,
                    "Restore Windows defaults");

            window.SecondaryButton.Click += async (_, _) =>
            {
                IReadOnlyList<ToolToggleDefinition> selected = SelectedPlan().Selected;
                if (selected.Count == 0)
                {
                    statusText.Text = "Select one or more available items first.";
                    return;
                }
                await RunRestoreItemsAsync(
                    selected,
                    windowsDefault: false,
                    "Restore selected");
            };

            foreach (ToolToggleDefinition definition in definitions)
            {
                CheckBox selector = selectors[definition.Id];
                ToggleSwitch toggle = toggles[definition.Id];
                selector.Checked += (_, _) => UpdateSelectionSummary();
                selector.Unchecked += (_, _) => UpdateSelectionSummary();
                toggle.Toggled += async (_, _) =>
                {
                    if (synchronizingControls)
                    {
                        return;
                    }
                    bool requestedOn = toggle.IsOn;
                    ToolToggleState actual = currentStates[definition.Id];
                    // Keep the switch truthful during confirmation, cancellation,
                    // and failure. Only the service's read-back updates it.
                    synchronizingControls = true;
                    try
                    {
                        toggle.IsOn = actual.IsOn;
                    }
                    finally
                    {
                        synchronizingControls = false;
                    }
                    if (!statesLoaded || stateLoadInProgress || applyInProgress ||
                        !actual.IsAvailable || requestedOn == actual.IsOn)
                    {
                        return;
                    }
                    if (CatalogTogglePolicy.FromSwitch(definition, requestedOn) != CatalogOperation.RestoreSavedState)
                    {
                        await RunApplyItemsAsync(new[] { definition }, requestedOn);
                    }
                    else
                    {
                        await RunRestoreItemsAsync(
                            new[] { definition },
                            windowsDefault: false,
                            "Restore " + definition.Name);
                    }
                    UpdateSelectionSummary();
                };
            }
            window.PrimaryButton.Click += async (_, _) =>
                await RunApplyItemsAsync(SelectedPlan().Selected);

            async Task RunApplyItemsAsync(IReadOnlyList<ToolToggleDefinition> items, bool targetOn = true)
            {
                if (window.IsClosed || applyInProgress || stateLoadInProgress || individualActionGate.IsBusy)
                {
                    return;
                }

                applyInProgress = true;
                TaskActivityService.TaskActivityLease? taskLease = null;
                CatalogProgressWindow? progressWindow = null;
                try
                {
                    if (!statesLoaded)
                    {
                        statusText.Text = "Wait until the current setting checks have finished.";
                        return;
                    }

                    CatalogSelectionPlan plan = CatalogSelectionPlan.Create(
                        items, currentStates, _ => true);
                    IReadOnlyList<ToolToggleDefinition> changes = targetOn ? plan.ToApply
                        : plan.Selected.Where(item => item.IsFeatureSwitch && currentStates[item.Id].IsOn).ToArray();

                    var conflicts = targetOn ? CatalogPlanSafety.ApplyConflicts(plan.Selected) : Array.Empty<string>();
                    conflicts = conflicts.Concat(CatalogEffectPlan.Create(service, plan.Selected).Conflicts).ToArray();
                    if (conflicts.Count > 0)
                    {
                        statusText.Text = string.Join(Environment.NewLine, conflicts);
                        await ShowMessageDialogAsync("Conflicting actions", statusText.Text);
                        return;
                    }

                    if (changes.Count == 0)
                    {
                        statusText.Text = plan.Selected.Count == 0
                            ? "Select at least one available item."
                            : "All selected tweaks are already applied (ON). Use Restore selected to return to the saved state or documented Windows service default.";
                        return;
                    }

                    if (changes.Any(IsHighImpact) &&
                        highImpactAcknowledgement.IsChecked != true)
                    {
                        statusText.Text = "Tick the HIGH IMPACT / HIGH RISK acknowledgement before applying the selected risky item(s).";
                        statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28));
                        return;
                    }

                    if (changes.Any(item => item.RequiresAdministrator) &&
                        !WindowsPrivilegeService.IsAdministrator())
                    {
                        statusText.Text = "Administrator rights are required. Close the app and run Visual Studio or the app as Administrator.";
                        statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28));
                        return;
                    }

                    SetCatalogInteraction(false);
                    if (!await ShowConfirmationWindowAsync(
                            $"{title} - Apply selected",
                            $"Apply {changes.Count} selected tweak(s)? {plan.Selected.Count - changes.Count} already-applied item(s) will be skipped." +
                            Environment.NewLine + Environment.NewLine +
                            string.Join(Environment.NewLine + Environment.NewLine, changes.Select(item => "• " + item.Name + (item.IsFeatureSwitch ? (targetOn ? ": ON" : ": OFF") : "") +
                                Environment.NewLine + item.Description +
                                (item.RestartRecommended ? Environment.NewLine + "Restart-sensitive: saved configuration is not proof of post-reboot effectiveness." : "") +
                                (string.IsNullOrWhiteSpace(item.Warning) ? "" : Environment.NewLine + item.Warning))) +
                            Environment.NewLine + Environment.NewLine + "Restore uses the saved state when available; default fallback and app recovery limitations are described by each option. Review these before applying." +
                            Environment.NewLine + Environment.NewLine +
                            (changes.All(item => item.IsFeatureSwitch) ? string.Empty :
                            "ON means the tweak is applied, not that the affected Windows service or feature is enabled."),
                            UiTextKeys.ApplySelected))
                    {
                        return;
                    }

                    bool needsStoreConsent = targetOn && changes.Any(item => item.Id == "WindowsAI");
                    if (needsStoreConsent && !await ConfirmCopilotSourceAsync(window)) return;
                    using var storeConsent = needsStoreConsent ? CopilotSourceConsent.BeginConfirmedOperation() : null;

                    taskLease = await AcquireManagedTaskAsync(
                        $"Catalog:{title}",
                        $"{title} - Apply selected",
                        new[] { "SystemMutation", $"Catalog:{title}" });
                    if (taskLease is null)
                    {
                        return;
                    }

                    if (loadedAtEpoch != CatalogStateEpoch.Version)
                    {
                        statusText.Text = "Configuration changed while waiting. Analyze / reload and review Apply again; nothing was applied by this request.";
                        taskLease.Complete("WARNING", statusText.Text);
                        return;
                    }

                    window.PrimaryButton.IsEnabled = false;
                    window.SecondaryButton.IsEnabled = false;
                    foreach (ToggleSwitch toggle in toggles.Values)
                    {
                        toggle.IsEnabled = false;
                    }
                    foreach (CheckBox selector in selectors.Values)
                    {
                        selector.IsEnabled = false;
                    }
                    foreach (Button button in toolbarButtons)
                    {
                        button.IsEnabled = false;
                    }

                    progressWindow = new CatalogProgressWindow(
                        this,
                        "Applying",
                        changes
                            .Select(item => new CatalogProgressItem(item.Id, item.Name))
                            .ToArray());
                    progressWindow.Show();
                    using var executionBatch = CatalogExecutionBatch.Begin();
                    int succeeded = 0;
                    int completed = 0;
                    List<string> failures = new();

                    foreach (ToolToggleDefinition definition in changes)
                    {
                        statusText.Text = $"Applying: {definition.Name}...";
                        taskLease.UpdateDetail($"Applying: {definition.Name}");
                        progressWindow.BeginItem(definition.Id);
                        ToolToggleOperationResult result;
                        try
                        {
                            result = await CatalogOperationJournal.RunAsync(CatalogOperationJournal.DirectoryPath, definition,
                                targetOn ? CatalogOperation.Apply : CatalogOperation.SetOff,
                                () => CatalogOperationRunner.ExecuteAsync(
                                service,
                                definition,
                                targetOn ? CatalogOperation.Apply : CatalogOperation.SetOff,
                                progressWindow.CreateReporter(definition.Id)));
                            progressWindow.VerifyItem(definition.Id);
                        }
                        catch (Exception exception)
                        {
                            progressWindow.CompleteItem(
                                definition.Id,
                                success: false,
                                exception.Message);
                            throw;
                        }
                        currentStates[definition.Id] = result.State;
                        synchronizingControls = true;
                        try
                        {
                            toggles[definition.Id].IsOn = result.State.IsOn;
                        }
                        finally
                        {
                            synchronizingControls = false;
                        }
                        UpdateCatalogStateText(stateTexts[definition.Id], result.State);
                        if (result.Success && result.Verified)
                        {
                            succeeded++;
                            selectors[definition.Id].IsChecked = false;
                            QueueCatalogRestart(definition, result);
                        }
                        else if (!result.SkippedUnavailable || !result.State.IsConfirmedUnavailable)
                        {
                            failures.Add($"{definition.Name}:\n{result.Message}");
                        }
                        progressWindow.CompleteItem(definition.Id, result);
                        completed++;
                        progressWindow.UpdateOverall(
                            completed,
                            $"Processed {completed}/{changes.Count}: {definition.Name}");
                    }

                    string operationReport = failures.Count == 0
                        ? $"Applied and verified {succeeded} change(s). Restart Windows for settings marked as reboot-sensitive."
                        : $"Applied {succeeded}/{changes.Count}. {string.Join(" | ", failures)}";
                    statusText.Text = failures.Count == 0
                        ? $"Apply completed for {succeeded} item(s). See the progress window for details."
                        : $"Apply completed with {failures.Count} failure(s). See the progress window for details.";
                    statusText.Foreground = new SolidColorBrush(failures.Count == 0
                        ? Color.FromArgb(255, 0, 112, 60)
                        : Color.FromArgb(255, 185, 28, 28));
                    progressWindow.Complete(failures.Count == 0, operationReport);
                    taskLease.Complete(
                        failures.Count == 0 ? "COMPLETED" : "WARNING",
                        operationReport);
                    UpdateSelectionSummary();

                }
                catch (Exception exception)
                {
                    progressWindow?.Complete(success: false, exception.Message);
                    statusText.Text = exception.Message;
                    statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28));
                    taskLease?.Complete("FAILED", exception.Message);
                }
                finally
                {
                    taskLease?.Dispose();
                    RefreshManagedTaskHeader();
                    applyInProgress = false;
                    SetCatalogInteraction(true);
                    UpdateSelectionSummary();
                    analyzeButton.IsEnabled = true;
                    InvalidateCatalogView();
                }
            }

            async Task LoadCatalogStatesAsync()
            {
                if (window.IsClosed || stateLoadInProgress || applyInProgress || individualActionGate.IsBusy)
                {
                    return;
                }

                stateLoadInProgress = true;
                long scanEpoch = CatalogStateEpoch.Version;
                statesLoaded = false;
                SetCatalogInteraction(false);
                window.PrimaryButton.IsEnabled = false;
                window.SecondaryButton.IsEnabled = false;
                foreach (Button button in toolbarButtons)
                {
                    button.IsEnabled = false;
                }
                foreach (ToggleSwitch toggle in toggles.Values)
                {
                    toggle.IsEnabled = false;
                }
                foreach (CheckBox selector in selectors.Values)
                {
                    selector.IsEnabled = false;
                }
                UpdateSelectionSummary();
                statusText.Text = $"Checking {definitions.Count} current setting(s)... You can review and resize this window while the checks finish.";
                InlineAnalysisProgress progressWindow = window.CreateAnalysisProgress(
                    definitions
                        .Select(item => new CatalogProgressItem(item.Id, item.Name))
                        .ToArray());
                progressWindow.Show();
                foreach (ToolToggleDefinition definition in definitions)
                {
                    progressWindow.BeginItem(
                        definition.Id,
                        $"Analyzing: {definition.Name}");
                }

                try
                {
                    int analyzedCount = 0;
                    async Task<ToolToggleState> ReadStateWithProgressAsync(
                        ToolToggleDefinition definition)
                    {
                        ToolToggleState state = await ReadToggleStateWithTimeoutAsync(
                            service,
                            definition);
                        UpdateCatalogStateText(stateTexts[definition.Id], state);
                        progressWindow.VerifyItem(definition.Id);
                        if (state.IsConfirmedUnavailable)
                            progressWindow.UnavailableItem(definition.Id, state.Error);
                        else
                            progressWindow.CompleteItem(definition.Id, state.IsAvailable,
                                state.IsAvailable ? state.ActualValue : state.Error);
                        analyzedCount++;
                        progressWindow.UpdateOverall(
                            analyzedCount,
                            $"Analyzed {analyzedCount}/{definitions.Count}: {definition.Name}");
                        return state;
                    }

                    Task<ToolToggleState>[] stateTasks = definitions
                        .Select(ReadStateWithProgressAsync)
                        .ToArray();
                    ToolToggleState[] loadedStates = await Task.WhenAll(stateTasks);

                    synchronizingControls = true;
                    try
                    {
                        for (int index = 0; index < definitions.Count; index++)
                        {
                            ToolToggleDefinition definition = definitions[index];
                            ToolToggleState state = loadedStates[index];
                            currentStates[definition.Id] = state;
                            CheckBox selector = selectors[definition.Id];
                            selector.IsChecked = false;
                            selector.IsEnabled = state.IsAvailable;
                            ToggleSwitch toggle = toggles[definition.Id];
                            toggle.IsOn = state.IsOn;
                            toggle.IsEnabled = state.IsAvailable;

                            UpdateCatalogStateText(stateTexts[definition.Id], state);
                        }
                    }
                    finally
                    {
                        synchronizingControls = false;
                    }

                    loadedAtEpoch = scanEpoch;
                    statesLoaded = loadedAtEpoch == CatalogStateEpoch.Version;
                    window.PrimaryButton.IsEnabled = true;
                    window.SecondaryButton.IsEnabled = true;
                    foreach (Button button in toolbarButtons)
                    {
                        button.IsEnabled = true;
                    }
                    int unavailable = loadedStates.Count(state => state.IsConfirmedUnavailable);
                    int failedChecks = loadedStates.Count(state => state.HasReadFailure);
                    statusText.Text = unavailable == 0
                        ? "Select rows, then Apply selected to apply their tweaks or Restore selected to replay saved state/documented Windows service defaults. Toggle switches change one item after confirmation."
                        : $"Current states loaded; {unavailable} item(s) are unavailable on this PC and their Select and ON/OFF controls remain disabled.";
                    if (failedChecks > 0)
                        statusText.Text = $"Verification failed for {failedChecks} item(s). See Analysis details below or the affected rows.";
                    statusText.Foreground = new SolidColorBrush(failedChecks > 0
                        ? Color.FromArgb(255, 185, 28, 28)
                        : unavailable > 0 ? Color.FromArgb(255, 107, 114, 128) : Color.FromArgb(255, 0, 112, 60));
                    progressWindow.Complete(success: failedChecks == 0, statusText.Text);
                    UpdateSelectionSummary();
                }
                catch (Exception exception)
                {
                    foreach (ToolToggleDefinition definition in definitions)
                    {
                        progressWindow.CompleteItem(
                            definition.Id,
                            success: false,
                            exception.Message);
                    }
                    progressWindow.Complete(success: false, exception.Message);
                    statusText.Text = $"Unable to load the catalog state: {exception.Message}";
                    statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28));
                }
                finally
                {
                    stateLoadInProgress = false;
                    SetCatalogInteraction(true);
                    analyzeButton.IsEnabled = true;
                    InvalidateCatalogView();
                }
            }

            analyzeButton.Click += async (_, _) => await LoadCatalogStatesAsync();
            refreshBulkSummary = UpdateSelectionSummary;

            CatalogStateEpoch.Changed += OnCatalogStateChanged;
            try
            {
                Task<ToolWindowResult> windowTask = window.ShowAsync();
                await LoadCatalogStatesAsync();
                await windowTask;
            }
            finally { CatalogStateEpoch.Changed -= OnCatalogStateChanged; }
        }

        private static void UpdateCatalogStateText(TextBlock text, ToolToggleState state)
        {
            text.Text = state.IsAvailable ? $"Actual: {state.ActualValue}"
                : state.IsConfirmedUnavailable ? $"Unavailable: {state.Error}"
                : $"Verification failed: {state.Error}";
            text.Foreground = new SolidColorBrush(state.IsAvailable
                ? Color.FromArgb(255, 0, 79, 146)
                : state.IsConfirmedUnavailable ? Color.FromArgb(255, 107, 114, 128)
                : Color.FromArgb(255, 185, 28, 28));
        }

        private async Task ShowGamingProcessManagerWindowAsync(
            GamingActionsService service)
        {
            Grid contentGrid = new();
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock note = new()
            {
                Text = "Only optional updater, sync, and overlay processes are listed. Defender, Windows Security, GPU-driver core processes, the audio engine, and critical Windows processes are protected.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 2, 4, 10)
            };
            contentGrid.Children.Add(note);

            StackPanel processRows = new() { Spacing = 6 };
            ScrollViewer viewer = new()
            {
                Content = processRows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Enabled
            };
            Grid.SetRow(viewer, 1);
            contentGrid.Children.Add(viewer);

            TextBlock statusText = new()
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 9, 4, 0),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 84, 134))
            };
            Grid.SetRow(statusText, 2);
            contentGrid.Children.Add(statusText);

            ToolWindow window = new(
                this,
                "Process Manager",
                contentGrid,
                primaryButtonText: "End Checked",
                secondaryButtonText: "Refresh",
                closeButtonText: "Close",
                initialWidth: 760,
                initialHeight: 680,
                minimumWidth: 560,
                minimumHeight: 440);
            ApplyActionRiskStyle(window.PrimaryButton, ToolActionRisk.Warning);

            bool operationInProgress = false;
            window.IsBusy = () => operationInProgress;
            Dictionary<string, CheckBox> selectors = new(StringComparer.OrdinalIgnoreCase);
            void Reload()
            {
                processRows.Children.Clear();
                selectors.Clear();
                IReadOnlyList<OptionalProcessState> states = service.ReadOptionalProcesses();
                foreach (OptionalProcessState state in states)
                {
                    CheckBox selector = new()
                    {
                        Content = state.RunningCount > 0
                            ? $"{state.Name}  [RUNNING x{state.RunningCount}]"
                            : $"{state.Name}  [not running]",
                        IsChecked = state.RunningCount > 0,
                        IsEnabled = state.RunningCount > 0,
                        MinHeight = 34,
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    selectors[state.Name] = selector;
                    processRows.Children.Add(selector);
                }
                int running = states.Count(state => state.RunningCount > 0);
                statusText.Text = $"{running} optional process group(s) currently running.";
            }

            window.SecondaryButton.Click += (_, _) => Reload();
            window.PrimaryButton.Click += async (_, _) =>
            {
                if (operationInProgress) return;
                string[] selected = selectors
                    .Where(pair => pair.Value.IsChecked == true)
                    .Select(pair => pair.Key)
                    .ToArray();
                if (selected.Length == 0)
                {
                    statusText.Text = "No running optional process is checked.";
                    return;
                }

                CatalogProgressWindow progressWindow = new(
                    this,
                    "Ending",
                    selected.Select(name => new CatalogProgressItem(name, name)).ToArray());
                progressWindow.Show();

                window.PrimaryButton.IsEnabled = false;
                window.SecondaryButton.IsEnabled = false;
                operationInProgress = true;
                foreach (CheckBox selector in selectors.Values) selector.IsEnabled = false;
                TaskActivityService.TaskActivityLease? taskLease = null;
                int completed = 0;
                List<string> failures = new();
                try
                {
                    taskLease = await AcquireManagedTaskAsync("Gaming:ProcessManager", "Ending optional processes",
                        new[] { "SystemMutation", "Catalog:Gaming Tweaks" });
                    if (taskLease is null)
                    {
                        progressWindow.Complete(false, "Process cleanup is already running or queued.");
                        return;
                    }
                    foreach (string processName in selected)
                    {
                        taskLease.UpdateDetail($"Ending: {processName}");
                        progressWindow.BeginItem(processName, $"Ending {processName} processes...");
                        ToolActionResult result = await Task.Run(
                            () => service.EndOptionalProcesses(new[] { processName }));
                        progressWindow.VerifyItem(processName, $"Verifying {processName} is no longer running...");
                        bool stopped = !service.ReadOptionalProcesses()
                            .Any(state => string.Equals(
                                              state.Name,
                                              processName,
                                              StringComparison.OrdinalIgnoreCase) &&
                                          state.RunningCount > 0);
                        bool success = result.Success && stopped;
                        string detail = success
                            ? $"Ended {processName}."
                            : $"{processName}: {result.Message}";
                        progressWindow.CompleteItem(processName, success, detail);
                        if (!success)
                        {
                            failures.Add(detail);
                        }

                        completed++;
                        progressWindow.UpdateOverall(
                            completed,
                            $"Processed {completed}/{selected.Length} process group(s).");
                    }

                    bool overallSuccess = failures.Count == 0;
                    string summary = overallSuccess
                        ? $"Ended {selected.Length} optional process group(s)."
                        : $"Completed with {failures.Count} failure(s): {string.Join(" | ", failures)}";
                    progressWindow.Complete(overallSuccess, summary);
                    taskLease.Complete(overallSuccess ? "COMPLETED" : "FAILED", summary);
                    Reload();
                    statusText.Text = overallSuccess
                        ? "Process cleanup completed. See the progress window for details."
                        : "Process cleanup completed with failures. See the progress window for details.";
                    statusText.Foreground = new SolidColorBrush(overallSuccess
                        ? Color.FromArgb(255, 0, 112, 60)
                        : Color.FromArgb(255, 185, 28, 28));
                }
                catch (Exception exception)
                {
                    progressWindow.Complete(false, exception.Message);
                    taskLease?.Complete("FAILED", exception.Message);
                    statusText.Text = "Process cleanup failed. See the progress window for details.";
                    statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28));
                }
                finally
                {
                    taskLease?.Dispose();
                    operationInProgress = false;
                    RefreshManagedTaskHeader();
                    window.PrimaryButton.IsEnabled = true;
                    window.SecondaryButton.IsEnabled = true;
                    Reload();
                }
            };

            Reload();
            await window.ShowAsync();
        }

        private static void ApplyActionRiskStyle(Button button, ToolActionRisk risk)
        {
            Color? background = risk switch
            {
                ToolActionRisk.Primary => Color.FromArgb(255, 37, 99, 235),
                ToolActionRisk.Success => Color.FromArgb(255, 21, 128, 61),
                ToolActionRisk.Warning => Color.FromArgb(255, 180, 83, 9),
                ToolActionRisk.Danger => Color.FromArgb(255, 185, 28, 28),
                _ => null
            };
            if (background is Color color)
            {
                button.Background = new SolidColorBrush(color);
                button.BorderBrush = new SolidColorBrush(color);
                button.Foreground = new SolidColorBrush(Colors.White);
            }
        }

        private static Border CreateCatalogCategoryBadge(
            string category,
            ToolToggleTier tier)
        {
            string label = string.IsNullOrWhiteSpace(category)
                ? "GENERAL"
                : category.Trim().ToUpperInvariant();
            Color background = GetCatalogBadgeColor(label, tier);
            return CreateCatalogCategoryBadge(label, background);
        }

        private static Border CreateCatalogCategoryBadge(
            string category,
            ToolActionRisk risk)
        {
            string label = string.IsNullOrWhiteSpace(category)
                ? "GENERAL"
                : category.Trim().ToUpperInvariant();
            Color background = risk switch
            {
                ToolActionRisk.Danger => Color.FromArgb(255, 185, 28, 28),
                ToolActionRisk.Warning => Color.FromArgb(255, 180, 83, 9),
                ToolActionRisk.Success => Color.FromArgb(255, 21, 128, 61),
                ToolActionRisk.Primary => Color.FromArgb(255, 37, 99, 235),
                _ => GetCatalogBadgeColor(label, ToolToggleTier.Unspecified)
            };
            return CreateCatalogCategoryBadge(label, background);
        }

        private static Border CreateCatalogCategoryBadge(string label, Color background)
        {
            return new Border
            {
                Background = new SolidColorBrush(background),
                BorderBrush = new SolidColorBrush(background),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 2, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }

        private static Color GetCatalogBadgeColor(
            string category,
            ToolToggleTier tier)
        {
            if (category.Contains("HIGH RISK", StringComparison.OrdinalIgnoreCase))
            {
                return Color.FromArgb(255, 185, 28, 28);
            }
            if (category.Contains("HIGH IMPACT", StringComparison.OrdinalIgnoreCase) ||
                category.Contains("ADVANCED", StringComparison.OrdinalIgnoreCase) ||
                category.Contains("EXPERIMENTAL", StringComparison.OrdinalIgnoreCase))
            {
                return Color.FromArgb(255, 180, 83, 9);
            }
            if (category.Contains("SAFE", StringComparison.OrdinalIgnoreCase))
            {
                return Color.FromArgb(255, 21, 128, 61);
            }
            if (category.Contains("LEGACY", StringComparison.OrdinalIgnoreCase))
            {
                return Color.FromArgb(255, 107, 70, 193);
            }
            if (tier == ToolToggleTier.VeryAggressive)
            {
                return Color.FromArgb(255, 185, 28, 28);
            }
            if (tier == ToolToggleTier.Advanced)
            {
                return Color.FromArgb(255, 180, 83, 9);
            }
            if (tier == ToolToggleTier.Safe)
            {
                return Color.FromArgb(255, 21, 128, 61);
            }
            return Color.FromArgb(255, 37, 99, 235);
        }

        private static async Task<ToolToggleState> ReadToggleStateWithTimeoutAsync(
            IToolToggleService service,
            ToolToggleDefinition definition)
        {
            try
            {
                return await CatalogStateReader.ReadAsync(service, definition, TimeSpan.FromSeconds(65));
            }
            catch (TimeoutException)
            {
                return new ToolToggleState(
                    false,
                    false,
                    "Status check timed out",
                    "The Windows status provider did not respond within 65 seconds. Other catalog items remain usable.");
            }
            catch (Exception exception)
            {
                return new ToolToggleState(
                    false,
                    false,
                    "Unable to read",
                    exception.Message);
            }
        }

        private static bool IsHighImpact(ToolToggleDefinition definition)
        {
            if (definition.SelectionTier == ToolToggleTier.VeryAggressive)
            {
                return true;
            }
            return definition.Category.Contains("HIGH", StringComparison.OrdinalIgnoreCase) ||
                   definition.Category.Contains("LEGACY", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatElapsed(TimeSpan elapsed) =>
            $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        private static bool IsAdvancedImpact(ToolToggleDefinition definition)
        {
            if (definition.SelectionTier != ToolToggleTier.Unspecified)
            {
                return definition.SelectionTier is
                    ToolToggleTier.Advanced or ToolToggleTier.VeryAggressive;
            }
            return IsHighImpact(definition) ||
                   definition.Category.Contains("ADVANCED", StringComparison.OrdinalIgnoreCase) ||
                   definition.Category.Contains("EXPERIMENTAL", StringComparison.OrdinalIgnoreCase);
        }

        private static Button CreateCatalogToolbarButton(string text, Color background)
        {
            return new Button
            {
                Content = text,
                Background = new SolidColorBrush(background),
                BorderBrush = new SolidColorBrush(background),
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                MinHeight = 34,
                CornerRadius = new CornerRadius(0)
            };
        }

        private async void MsiModeUtilityButton_Click(object sender, RoutedEventArgs e)
        {
            TaskStatusMessage = "TASKS: READING PCI";

            try
            {
                await ShowMsiModeUtilityDialogAsync();
            }
            catch (Exception exception)
            {
                TaskStatusMessage = "TASKS: FAILED";
                await ShowMessageDialogAsync("MSI Mode Utility failed", exception.Message);
            }
            finally
            {
                RefreshManagedTaskHeader();
            }
        }

        private async Task ShowMsiModeUtilityDialogAsync()
        {
            Dictionary<string, MsiDeviceConfiguration> originals = new(
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ToggleSwitch> msiToggles = new(
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, TextBox> limitBoxes = new(
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ComboBox> priorityBoxes = new(
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, TextBlock> resultTexts = new(
                StringComparer.OrdinalIgnoreCase);

            Grid contentGrid = new() { RowSpacing = 9 };
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Button openRegistryButton = new()
            {
                Content = "Open registry",
                MinWidth = 140,
                Height = 34,
                IsEnabled = false
            };
            TextBlock toolbarTitle = new()
            {
                Text = "PCI INTERRUPT CONFIGURATION",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            StackPanel toolbar = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10
            };
            toolbar.Children.Add(openRegistryButton);
            toolbar.Children.Add(toolbarTitle);
            Grid.SetRow(toolbar, 0);
            contentGrid.Children.Add(toolbar);

            Grid tableGrid = new() { MinWidth = 1180 };
            tableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tableGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid header = CreateMsiEditorGrid();
            header.Background = new SolidColorBrush(Color.FromArgb(255, 176, 196, 222));
            header.Padding = new Thickness(8, 6, 8, 6);
            AddMsiGridText(header, "NAME", 0, true);
            AddMsiGridText(header, "IRQ", 1, true);
            AddMsiGridText(header, "MSI", 2, true);
            AddMsiGridText(header, "LIMIT", 3, true);
            AddMsiGridText(header, "MAX LIMIT", 4, true);
            AddMsiGridText(header, "SUPPORTED MODES", 5, true);
            AddMsiGridText(header, "INTERRUPT PRIORITY", 6, true);
            tableGrid.Children.Add(header);

            ListView deviceList = new()
            {
                SelectionMode = ListViewSelectionMode.Single,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 187, 199, 213)),
                BorderThickness = new Thickness(1, 0, 1, 1)
            };
            Grid.SetRow(deviceList, 1);
            tableGrid.Children.Add(deviceList);

            ScrollViewer tableViewer = new()
            {
                Content = tableGrid,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Disabled
            };

            TextBox detailsBox = new()
            {
                Text = "Select a PCI device to inspect its PNP, PCI, IRQ, and driver-INF properties.",
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Color.FromArgb(255, 8, 17, 29)),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 236, 242, 250))
            };
            ScrollViewer.SetVerticalScrollBarVisibility(detailsBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(detailsBox, ScrollBarVisibility.Auto);

            Grid body = new() { RowSpacing = 8 };
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 150 });
            body.Children.Add(tableViewer);
            Grid.SetRow(detailsBox, 1);
            body.Children.Add(detailsBox);
            Grid.SetRow(body, 1);
            contentGrid.Children.Add(body);

            TextBlock statusText = new()
            {
                Text = "Parsing active PCI registry keys and IRQ resources...",
                Margin = new Thickness(4, 2, 4, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 84, 134))
            };
            Grid.SetRow(statusText, 2);
            contentGrid.Children.Add(statusText);

            ToolWindow window = new(
                this,
                "MSI Mode Utility",
                contentGrid,
                primaryButtonText: "Apply changes",
                secondaryButtonText: "Refresh devices",
                closeButtonText: "Close",
                initialWidth: 1500,
                initialHeight: 880,
                minimumWidth: 900,
                minimumHeight: 620);
            window.PrimaryButton.IsEnabled = false;
            bool applyInProgress = false;
            bool loading = false;
            window.IsBusy = () => loading || applyInProgress;

            MsiDeviceConfiguration? GetSelectedDevice()
            {
                return deviceList.SelectedItem is ListViewItem item
                    ? item.Tag as MsiDeviceConfiguration
                    : null;
            }

            bool TryReadRequestedValues(
                MsiDeviceConfiguration original,
                out MsiDeviceApplyRequest request,
                out string error)
            {
                request = default;
                error = string.Empty;
                string limitText = limitBoxes[original.DeviceId].Text.Trim();
                int? limitValue = null;
                if (!string.IsNullOrWhiteSpace(limitText))
                {
                    if (!int.TryParse(
                            limitText,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int parsedLimit) || parsedLimit is < 1 or > 2048)
                    {
                        error = $"Invalid limit for {original.Name}. Use an empty value or 1-2048.";
                        return false;
                    }
                    limitValue = parsedLimit;
                }
                request = new MsiDeviceApplyRequest(
                    original.DeviceId,
                    msiToggles[original.DeviceId].IsOn,
                    limitValue,
                    Math.Max(0, priorityBoxes[original.DeviceId].SelectedIndex));
                return true;
            }

            bool IsChanged(
                MsiDeviceConfiguration original,
                MsiDeviceApplyRequest request)
            {
                return request.MsiSupported != original.MsiSupported ||
                       request.MessageNumberLimit != original.MessageNumberLimit ||
                       request.DevicePriority != original.DevicePriority;
            }

            void UpdateApplyState()
            {
                if (loading || applyInProgress || !WindowsPrivilegeService.IsAdministrator())
                {
                    window.PrimaryButton.IsEnabled = false;
                    return;
                }
                window.PrimaryButton.IsEnabled = originals.Values.Any(original =>
                    TryReadRequestedValues(original, out MsiDeviceApplyRequest request, out _) &&
                    IsChanged(original, request));
            }

            ListViewItem CreateDeviceItem(MsiDeviceConfiguration device)
            {
                Grid row = CreateMsiEditorGrid();
                row.Padding = new Thickness(8, 6, 8, 6);
                StackPanel namePanel = new() { Spacing = 2 };
                namePanel.Children.Add(new TextBlock
                {
                    Text = device.Name,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                namePanel.Children.Add(new TextBlock
                {
                    Text = $"{device.DeviceClass} • {device.DeviceId}",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 70, 95)),
                    TextWrapping = TextWrapping.Wrap
                });
                TextBlock resultText = new()
                {
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 79, 146))
                };
                namePanel.Children.Add(resultText);
                row.Children.Add(namePanel);
                AddMsiGridText(row, Fallback(device.Irq), 1, false);

                ToggleSwitch msi = new()
                {
                    IsOn = device.MsiSupported,
                    OnContent = "ON",
                    OffContent = "OFF",
                    MinWidth = 120,
                    MinHeight = 34,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(msi, 2);
                row.Children.Add(msi);

                TextBox limit = new()
                {
                    Text = device.MessageNumberLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    PlaceholderText = "Default",
                    Width = 78,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(limit, 3);
                row.Children.Add(limit);
                AddMsiGridText(
                    row,
                    device.MaxLimit > 0
                        ? device.MaxLimit.ToString(CultureInfo.InvariantCulture)
                        : "-",
                    4,
                    false);
                AddMsiGridText(row, device.SupportedModes, 5, false);

                ComboBox priority = new()
                {
                    Width = 145,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    SelectedIndex = device.DevicePriority
                };
                priority.Items.Add("Undefined");
                priority.Items.Add("Low");
                priority.Items.Add("Normal");
                priority.Items.Add("High");
                Grid.SetColumn(priority, 6);
                row.Children.Add(priority);

                ListViewItem item = new()
                {
                    Content = row,
                    Tag = device,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0)
                };
                msiToggles[device.DeviceId] = msi;
                limitBoxes[device.DeviceId] = limit;
                priorityBoxes[device.DeviceId] = priority;
                resultTexts[device.DeviceId] = resultText;
                msi.Toggled += (_, _) =>
                {
                    deviceList.SelectedItem = item;
                    UpdateApplyState();
                };
                limit.TextChanged += (_, _) =>
                {
                    deviceList.SelectedItem = item;
                    UpdateApplyState();
                };
                priority.SelectionChanged += (_, _) =>
                {
                    deviceList.SelectedItem = item;
                    UpdateApplyState();
                };
                item.DoubleTapped += (_, _) =>
                {
                    SecuritySettingsLaunchResult result = MsiModeService.OpenRegistry(device);
                    statusText.Text = result.Message;
                };
                return item;
            }

            async Task LoadDevicesAsync(string? preserveDeviceId = null)
            {
                if (loading)
                {
                    return;
                }
                loading = true;
                window.PrimaryButton.IsEnabled = false;
                window.SecondaryButton.IsEnabled = false;
                openRegistryButton.IsEnabled = false;
                statusText.Text = "Parsing active PCI registry keys, IRQ resources, and driver INF files...";
                InlineAnalysisProgress progressWindow = window.CreateAnalysisProgress(
                    new[] { new CatalogProgressItem("MsiInventory", "MSI-compatible devices") });
                progressWindow.Show();
                progressWindow.BeginItem(
                    "MsiInventory",
                    "Reading: PCI registry, IRQ resources, and driver INF files");
                try
                {
                    IReadOnlyList<MsiDeviceConfiguration> devices =
                        await _msiModeService.ReadDevicesAsync();
                    progressWindow.VerifyItem(
                        "MsiInventory",
                        "Verifying: MSI-compatible device inventory");
                    originals.Clear();
                    msiToggles.Clear();
                    limitBoxes.Clear();
                    priorityBoxes.Clear();
                    resultTexts.Clear();
                    deviceList.Items.Clear();
                    ListViewItem? restore = null;
                    foreach (MsiDeviceConfiguration device in devices)
                    {
                        originals[device.DeviceId] = device;
                        ListViewItem item = CreateDeviceItem(device);
                        deviceList.Items.Add(item);
                        if (string.Equals(device.DeviceId, preserveDeviceId, StringComparison.OrdinalIgnoreCase))
                        {
                            restore = item;
                        }
                    }
                    UiDisplaySettings.Apply(tableGrid);
                    if (restore is not null)
                    {
                        deviceList.SelectedItem = restore;
                    }
                    else if (deviceList.Items.Count > 0)
                    {
                        deviceList.SelectedIndex = 0;
                    }
                    else
                    {
                        detailsBox.Text = "No compatible active PCI device with Interrupt Management configuration was found.";
                    }
                    statusText.Text = WindowsPrivilegeService.IsAdministrator()
                        ? $"{devices.Count} active PCI device(s) loaded. Edit MSI, Limit, or Interrupt Priority, then press Apply changes."
                        : $"{devices.Count} device(s) loaded in read-only mode. Run the app as Administrator to apply changes.";
                    progressWindow.CompleteItem(
                        "MsiInventory",
                        success: true,
                        $"Loaded {devices.Count} active PCI device(s).");
                    progressWindow.UpdateOverall(1, $"Loaded {devices.Count} device(s).");
                    progressWindow.Complete(success: true, $"Loaded {devices.Count} device(s).");
                }
                catch (Exception exception)
                {
                    progressWindow.CompleteItem(
                        "MsiInventory",
                        success: false,
                        exception.Message);
                    progressWindow.Complete(success: false, exception.Message);
                    detailsBox.Text = exception.ToString();
                    statusText.Text = $"Enumeration failed: {exception.Message}";
                }
                finally
                {
                    loading = false;
                    window.SecondaryButton.IsEnabled = true;
                    openRegistryButton.IsEnabled = GetSelectedDevice() is not null;
                    UpdateApplyState();
                }
            }

            int detailRequest = 0;
            bool detailWindowClosed = false;
            window.Closed += (_, _) => { detailWindowClosed = true; detailRequest++; };
            deviceList.SelectionChanged += async (_, _) =>
            {
                int request = ++detailRequest;
                MsiDeviceConfiguration? selected = GetSelectedDevice();
                openRegistryButton.IsEnabled = selected is not null && !loading;
                detailsBox.Text = selected?.Details ??
                    "Select a PCI device to inspect its PNP, PCI, IRQ, and driver-INF properties.";
                if (selected is null) return;
                try
                {
                    string details = await _msiModeService.ReadDetailsAsync(selected);
                    if (!detailWindowClosed && request == detailRequest) detailsBox.Text = details;
                }
                catch (Exception exception)
                {
                    if (!detailWindowClosed && request == detailRequest)
                        detailsBox.Text = selected.Details + "\n\nRuntime refresh unavailable: " + exception.Message;
                }
            };
            openRegistryButton.Click += (_, _) =>
            {
                MsiDeviceConfiguration? selected = GetSelectedDevice();
                if (selected is null)
                {
                    return;
                }
                SecuritySettingsLaunchResult result = MsiModeService.OpenRegistry(selected);
                statusText.Text = result.Message;
            };
            window.SecondaryButton.Click += async (_, _) =>
                await LoadDevicesAsync(GetSelectedDevice()?.DeviceId);

            window.PrimaryButton.Click += async (_, _) =>
            {
                if (applyInProgress)
                {
                    return;
                }

                applyInProgress = true;
                TaskActivityService.TaskActivityLease? taskLease = null;
                CatalogProgressWindow? progressWindow = null;
                try
                {
                    if (!WindowsPrivilegeService.IsAdministrator())
                    {
                        statusText.Text = "Administrator rights are required.";
                        statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28));
                        return;
                    }

                    List<MsiDeviceApplyRequest> requests = new();
                    foreach (MsiDeviceConfiguration original in originals.Values)
                    {
                        if (!TryReadRequestedValues(
                                original,
                                out MsiDeviceApplyRequest request,
                                out string error))
                        {
                            statusText.Text = error;
                            statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28));
                            return;
                        }
                        if (IsChanged(original, request))
                        {
                            requests.Add(request);
                        }
                    }

                    if (requests.Count == 0)
                    {
                        statusText.Text = "No changes selected.";
                        return;
                    }

                    taskLease = await AcquireManagedTaskAsync(
                        "MsiModeApply",
                        "MSI Mode Utility - Apply changes",
                        new[] { "SystemMutation", "DeviceInterruptPolicy", "DriverConfiguration" });
                    if (taskLease is null)
                    {
                        return;
                    }

                    progressWindow = new CatalogProgressWindow(
                        this,
                        "Applying",
                        requests
                            .Select(request => new CatalogProgressItem(
                                request.DeviceId,
                                originals[request.DeviceId].Name))
                            .ToArray());
                    progressWindow.Show();
                    window.PrimaryButton.IsEnabled = false;
                    int succeeded = 0;
                    int completed = 0;
                    List<string> failures = new();
                    foreach (MsiDeviceApplyRequest request in requests)
                    {
                        MsiDeviceConfiguration original = originals[request.DeviceId];
                        statusText.Text = $"Applying {original.Name}...";
                        taskLease.UpdateDetail($"Applying and verifying: {original.Name}.");
                        progressWindow.BeginItem(request.DeviceId);
                        MsiDeviceApplyResult result = await _msiModeService.ApplyAsync(request);
                        progressWindow.VerifyItem(request.DeviceId);
                        bool verified = result.Success && result.VerifiedConfiguration is not null;
                        resultTexts[request.DeviceId].Text = verified
                            ? "Completed — see the progress window."
                            : "Failed — see the progress window.";
                        resultTexts[request.DeviceId].Foreground = new SolidColorBrush(verified
                            ? Color.FromArgb(255, 0, 112, 60)
                            : Color.FromArgb(255, 185, 28, 28));
                        if (result.Success && result.VerifiedConfiguration is not null)
                        {
                            originals[request.DeviceId] = result.VerifiedConfiguration;
                            _pendingRestarts.Add("MSI Mode: " + original.Name);
                            succeeded++;
                        }
                        else
                        {
                            failures.Add($"{original.Name}:\n{result.Message}");
                        }
                        progressWindow.CompleteItem(
                            request.DeviceId,
                            verified,
                            result.Message);
                        completed++;
                        progressWindow.UpdateOverall(
                            completed,
                            $"Processed {completed}/{requests.Count}: {original.Name}");
                    }

                    string operationReport = failures.Count == 0
                        ? $"Applied and verified {succeeded} device change(s). Restart Windows before testing."
                        : $"Applied {succeeded}/{requests.Count}. {string.Join(" | ", failures)}";
                    statusText.Text = failures.Count == 0
                        ? $"Applied {succeeded} device change(s). See the progress window for details."
                        : $"MSI changes completed with {failures.Count} failure(s). See the progress window for details.";
                    statusText.Foreground = new SolidColorBrush(failures.Count == 0
                        ? Color.FromArgb(255, 0, 112, 60)
                        : Color.FromArgb(255, 185, 28, 28));
                    progressWindow.Complete(failures.Count == 0, operationReport);
                    if (failures.Count == 0)
                    {
                        await LoadDevicesAsync(GetSelectedDevice()?.DeviceId);
                    }
                    taskLease.Complete(
                        failures.Count == 0 ? "COMPLETED" : "WARNING",
                        operationReport);
                }
                catch (Exception exception)
                {
                    progressWindow?.Complete(success: false, exception.Message);
                    statusText.Text = exception.Message;
                    statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28));
                    taskLease?.Complete("FAILED", exception.Message);
                }
                finally
                {
                    taskLease?.Dispose();
                    RefreshManagedTaskHeader();
                    applyInProgress = false;
                    window.SecondaryButton.IsEnabled = true;
                    UpdateApplyState();
                }
            };

            await LoadDevicesAsync();
            await window.ShowAsync();
        }

        private static Grid CreateMsiEditorGrid()
        {
            Grid grid = new();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(195) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(175) });
            return grid;
        }

        private static void AddMsiGridText(
            Grid grid,
            string text,
            int column,
            bool bold)
        {
            TextBlock label = new()
            {
                Text = text,
                FontWeight = bold
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                HorizontalAlignment = column == 0
                    ? HorizontalAlignment.Left
                    : HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, column);
            grid.Children.Add(label);
        }

        private async void GpuDriverManagerButton_Click(object sender, RoutedEventArgs e)
        {
            TaskStatusMessage = "TASKS: GPU INVENTORY";
            try
            {
                await ShowGpuDriverManagerDialogAsync();
            }
            catch (Exception exception)
            {
                TaskStatusMessage = "TASKS: FAILED";
                await ShowMessageDialogAsync("GPU Driver Manager failed", exception.Message);
            }
            finally
            {
                RefreshManagedTaskHeader();
            }
        }

        private async Task ShowGpuDriverManagerDialogAsync()
        {
            Grid contentGrid = new();
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            GpuDriverHeader gpuHeader = new();
            contentGrid.Children.Add(gpuHeader);
            Grid header = CreateGpuDriverGrid();
            header.Background = new SolidColorBrush(Color.FromArgb(255, 176, 196, 222));
            header.Padding = new Thickness(8, 6, 8, 6);
            AddGpuGridText(header, "GPU", 0, true);
            AddGpuGridText(header, "VENDOR", 1, true);
            AddGpuGridText(header, "INSTALLED VERSION", 2, true);
            AddGpuGridText(header, "DRIVER DATE", 3, true);
            AddGpuGridText(header, "STATUS", 4, true);
            Grid.SetRow(header, 1);
            contentGrid.Children.Add(header);

            ListView list = new()
            {
                SelectionMode = ListViewSelectionMode.Single,
                MinHeight = 96,
                MaxHeight = 210,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 187, 199, 213)),
                BorderThickness = new Thickness(1, 0, 1, 1)
            };
            StackPanel driverDetails = new() { Spacing = 12 };
            driverDetails.Children.Add(list);
            ScrollViewer driverViewport = new()
            {
                Content = driverDetails,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(driverViewport, 2);
            contentGrid.Children.Add(driverViewport);

            TextBlock statusText = new()
            {
                Text = "Reading display-adapter inventory...",
                Margin = new Thickness(4, 10, 4, 0),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 84, 134))
            };
            driverDetails.Children.Add(statusText);
            GpuDriverUpdateView releaseView = new();
            driverDetails.Children.Add(releaseView);

            Button officialSourceButton = new()
            {
                Content = "Official source",
                MinWidth = 140,
                Height = 34,
                IsEnabled = false
            };
            Button refreshButton = new()
            {
                Content = "Refresh inventory",
                MinWidth = 150,
                Height = 34
            };
            StackPanel utilityButtons = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 10, 0, 0)
            };
            utilityButtons.Children.Add(officialSourceButton);
            utilityButtons.Children.Add(refreshButton);
            Grid.SetRow(utilityButtons, 3);
            contentGrid.Children.Add(utilityButtons);

            ToolWindow window = new(
                this,
                "GPU Driver Manager",
                contentGrid,
                closeButtonText: "Close",
                initialWidth: 1120,
                initialHeight: 720,
                minimumWidth: 720,
                minimumHeight: 450);
            gpuHeader.InstallButton.IsEnabled = false;
            gpuHeader.RepairButton.IsEnabled = false;

            OperationGate gpuGate = new();
            bool inventoryLoading = false;
            window.IsBusy = () => inventoryLoading || gpuGate.IsBusy;
            Dictionary<string, GpuDriverCatalog> catalogs = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, GpuDriverChannel> selectedChannels = new(StringComparer.OrdinalIgnoreCase);
            bool bindingChannels = false;
            GpuDriverChoice? GetChoice() => (gpuHeader.ChannelPicker.SelectedItem as ComboBoxItem)?.Tag as GpuDriverChoice;
            void ResetChannels()
            {
                bindingChannels = true;
                try { gpuHeader.ChannelPicker.Items.Clear(); }
                finally { bindingChannels = false; }
                gpuHeader.ChannelPicker.IsEnabled = false;
            }
            void ApplyCatalog(GpuDriverEntry device, GpuDriverCatalog catalog)
            {
                bindingChannels = true;
                try
                {
                    gpuHeader.ChannelPicker.Items.Clear();
                    bool hadPreference = selectedChannels.TryGetValue(device.PnpDeviceId, out var preferred);
                    foreach (var choice in catalog.Choices)
                    {
                        var item = new ComboBoxItem { Tag = choice,
                            Content = new TextBlock { Text = choice.Name, TextWrapping = TextWrapping.Wrap } };
                        gpuHeader.ChannelPicker.Items.Add(item);
                        if (hadPreference && choice.Id == preferred) gpuHeader.ChannelPicker.SelectedItem = item;
                    }
                    if (!hadPreference && gpuHeader.ChannelPicker.Items.Count > 0) gpuHeader.ChannelPicker.SelectedIndex = 0;
                    if (GetChoice() is { } selectedChoice) selectedChannels[device.PnpDeviceId] = selectedChoice.Id;
                    gpuHeader.ChannelPicker.PlaceholderText = catalog.Choices.Count == 0 ? "No compatible driver type verified — retry or use Official source" : "Select driver type";
                    UiDisplaySettings.Apply(gpuHeader);
                }
                finally { bindingChannels = false; }
            }
            CancellationTokenSource? releaseCheck = null;
            string? checkingDevice = null;
            void CancelReleaseCheck()
            {
                releaseCheck?.Cancel();
                releaseCheck = null;
                checkingDevice = null;
            }
            window.Closed += (_, _) => CancelReleaseCheck();

            async Task CheckUpdatesAsync(bool force = false)
            {
                GpuDriverEntry? selected = GetSelection();
                if (window.IsClosed || inventoryLoading || gpuGate.IsBusy || selected is null) return;
                if (!force && checkingDevice == selected.PnpDeviceId) return;
                if (!force && catalogs.TryGetValue(selected.PnpDeviceId, out var cached))
                {
                    ApplyCatalog(selected, cached);
                    UpdateSelection();
                    return;
                }
                CancelReleaseCheck();
                ResetChannels();
                using CancellationTokenSource request = new();
                releaseCheck = request;
                checkingDevice = selected.PnpDeviceId;
                releaseView.ShowChecking();
                UpdateSelection();
                try
                {
                    GpuDriverCatalog catalog = await _gpuDriverService.ReadDriverChannelsAsync(selected, request.Token);
                    if (window.IsClosed || request.IsCancellationRequested || releaseCheck != request ||
                        GetSelection()?.PnpDeviceId != selected.PnpDeviceId) return;
                    catalogs[selected.PnpDeviceId] = catalog;
                    ApplyCatalog(selected, catalog);
                }
                catch (OperationCanceledException) { /* Selection, refresh or close invalidates this response. */ }
                finally
                {
                    if (releaseCheck == request)
                    {
                        releaseCheck = null;
                        checkingDevice = null;
                        if (!window.IsClosed) UpdateSelection();
                    }
                }
            }

            ListViewItem CreateItem(GpuDriverEntry entry)
            {
                Grid row = CreateGpuDriverGrid();
                row.Padding = new Thickness(8, 7, 8, 7);
                AddGpuGridText(row, entry.Name, 0, false);
                AddGpuGridText(row, entry.Vendor, 1, false);
                AddGpuGridText(row, GpuDriverUpdates.DisplayInstalledVersion(entry.Vendor, entry.DriverVersion), 2, false);
                AddGpuGridText(row, Fallback(entry.DriverDate), 3, false);
                string state = !entry.DriverInstalled
                    ? "RECOVERY"
                    : entry.DeviceHealthy
                        ? "READY"
                        : $"CODE {entry.ProblemCode}";
                TextBlock status = AddGpuGridText(row, state, 4, true);
                status.Foreground = new SolidColorBrush(
                    entry.DriverInstalled && entry.DeviceHealthy
                        ? Color.FromArgb(255, 0, 112, 60)
                        : Color.FromArgb(255, 185, 28, 28));
                return new ListViewItem
                {
                    Content = row,
                    Tag = entry,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0)
                };
            }

            GpuDriverEntry? GetSelection()
            {
                return list.SelectedItem is ListViewItem item
                    ? item.Tag as GpuDriverEntry
                    : null;
            }

            void UpdateSelection()
            {
                GpuDriverEntry? selected = GetSelection();
                bool busy = inventoryLoading || gpuGate.IsBusy;
                list.IsEnabled = !busy;
                refreshButton.IsEnabled = !busy;
                bool supported = !busy && selected is not null &&
                    selected.Vendor is "NVIDIA" or "AMD" or "Intel";
                bool checking = selected is not null && checkingDevice == selected.PnpDeviceId;
                var choice = GetChoice();
                bool canInstall = supported && !checking && choice is not null;
                GpuDriverUpdate? update = null;
                if (!checking && selected is not null && catalogs.TryGetValue(selected.PnpDeviceId, out var catalog))
                {
                    update = choice is null ? new(GpuUpdateState.Unavailable, null, catalog.CheckedAt,
                        (catalog.Choices.Count > 0 ? "The previously selected driver type is unavailable. Select another verified type explicitly. " : "No compatible driver type was verified. ") + catalog.Detail)
                        : GpuDriverUpdates.Evaluate(selected.Vendor, selected.DriverVersion, selected.DriverInstalled, choice.Release, catalog.CheckedAt);
                    if (choice is not null && catalog.Detail.Length > 0) update = update with { Detail = update.Detail + "\nOther catalog results:\n" + catalog.Detail };
                    releaseView.ShowResult(update);
                }
                else if (!checking) releaseView.ShowNotChecked();
                if (update?.State == GpuUpdateState.NewerInstalled) canInstall = false;
                gpuHeader.InstallButton.IsEnabled = canInstall;
                gpuHeader.RepairButton.IsEnabled = canInstall && selected!.DriverInstalled;
                gpuHeader.ChannelPicker.IsEnabled = supported && !checking && gpuHeader.ChannelPicker.Items.Count > 0;
                officialSourceButton.IsEnabled = supported;
                gpuHeader.CheckButton.IsEnabled = supported && !checking;
                if (selected is null)
                {
                    return;
                }

                string health = selected.DeviceHealthy
                    ? "Device healthy"
                    : $"Device Manager problem code {selected.ProblemCode}";
                string identity = string.Equals(selected.Name, selected.RawName, StringComparison.OrdinalIgnoreCase)
                    ? selected.Name
                    : $"{selected.Name} (Windows name: {selected.RawName})";
                statusText.Text =
                    $"{identity}\nProvider: {Fallback(selected.DriverProvider)} | " +
                    $"INF: {Fallback(selected.InfName)} | {health}\n" +
                    $"PCI DEV_{Fallback(selected.PciDeviceId)} | {selected.PnpDeviceId}" +
                    (selected.PortableSystem
                        ? "\nPortable/hybrid system: the PC manufacturer may provide a customized graphics package."
                        : string.Empty);
            }

            async Task RefreshInventoryAsync(string? preserveDeviceId = null)
            {
                if (inventoryLoading || window.IsClosed) return;
                CancelReleaseCheck();
                catalogs.Clear();
                ResetChannels();
                releaseView.ShowNotChecked();
                gpuHeader.CheckButton.IsEnabled = false;
                inventoryLoading = true;
                gpuHeader.InstallButton.IsEnabled = false;
                gpuHeader.RepairButton.IsEnabled = false;
                officialSourceButton.IsEnabled = false;
                refreshButton.IsEnabled = false;
                statusText.Text = "Reading display-adapter inventory...";
                InlineAnalysisProgress progressWindow = window.CreateAnalysisProgress(
                    new[] { new CatalogProgressItem("GpuInventory", "GPU driver inventory") });
                progressWindow.Show();
                progressWindow.BeginItem(
                    "GpuInventory",
                    "Reading: display adapters and installed driver packages");
                try
                {
                    IReadOnlyList<GpuDriverEntry> entries =
                        await _gpuDriverService.ReadInventoryAsync();
                    progressWindow.VerifyItem(
                        "GpuInventory",
                        "Verifying: GPU driver inventory");
                    list.Items.Clear();
                    ListViewItem? restore = null;
                    foreach (GpuDriverEntry entry in entries)
                    {
                        ListViewItem item = CreateItem(entry);
                        list.Items.Add(item);
                        if (!string.IsNullOrWhiteSpace(preserveDeviceId) &&
                            string.Equals(entry.PnpDeviceId, preserveDeviceId, StringComparison.OrdinalIgnoreCase))
                        {
                            restore = item;
                        }
                    }
                    UiDisplaySettings.Apply(list);
                    if (restore is not null)
                    {
                        list.SelectedItem = restore;
                    }
                    else if (list.Items.Count > 0)
                    {
                        list.SelectedIndex = 0;
                    }
                    else
                    {
                        statusText.Text = "No NVIDIA, AMD, or Intel PCI display adapter was found.";
                    }
                    progressWindow.CompleteItem(
                        "GpuInventory",
                        success: true,
                        $"Loaded {entries.Count} display adapter(s).");
                    progressWindow.UpdateOverall(1, $"Loaded {entries.Count} display adapter(s).");
                    progressWindow.Complete(success: true, $"Loaded {entries.Count} display adapter(s).");
                }
                catch (Exception exception)
                {
                    progressWindow.CompleteItem(
                        "GpuInventory",
                        success: false,
                        exception.Message);
                    progressWindow.Complete(success: false, exception.Message);
                    statusText.Text = $"Inventory refresh failed: {exception.Message}";
                }
                finally
                {
                    inventoryLoading = false;
                    UpdateSelection();
                    _ = CheckUpdatesAsync();
                }
            }

            async Task RunOperationAsync(GpuDriverOperationMode mode)
            {
                if (inventoryLoading || window.IsClosed) return;
                using IDisposable? operationLease = gpuGate.TryEnter();
                if (operationLease is null) return;
                CancelReleaseCheck();
                try
                {
                    UpdateSelection();

                GpuDriverEntry? selected = GetSelection();
                GpuDriverChoice? choice = GetChoice();
                if (selected is null || choice is null)
                {
                    return;
                }

                string action = mode == GpuDriverOperationMode.Repair
                    ? "Repair driver"
                    : UiTextKeys.DownloadInstall;
                string explanation = $"{choice.Name}\nSelected release: {choice.Release.Version}\n" +
                    $"GPU: {selected.Name}\nInstalled: {GpuDriverUpdates.DisplayInstalledVersion(selected.Vendor, selected.DriverVersion)}\n\n" +
                    (mode == GpuDriverOperationMode.Repair
                        ? "Download and re-install this selected driver type/release? This may update the installed version or switch its driver type; it is not necessarily same-version repair."
                        : "Download, verify and install this selected driver type/release? Choosing a different type switches the driver channel.") +
                    "\nThe selected type will be revalidated for this GPU/OS. No substitute channel or automatic downgrade is allowed.";
                if (selected.PortableSystem)
                {
                    explanation += " This is a portable/hybrid system; its manufacturer may provide a customized graphics driver.";
                }
                explanation += " Administrator rights are required. Windows may request a restart after installation.";
                if (!await ShowConfirmationWindowAsync(
                        $"{action}: {selected.Name}",
                        explanation,
                        action))
                {
                    return;
                }

                using TaskActivityService.TaskActivityLease? taskLease =
                    await AcquireManagedTaskAsync(
                        $"GpuDriver:{selected.PnpDeviceId}",
                        $"{action} - {selected.Name}",
                        new[] { "SystemMutation", "DriverInstallation", "DisplayAdapter" });
                if (taskLease is null)
                {
                    return;
                }

                gpuHeader.InstallButton.IsEnabled = false;
                gpuHeader.RepairButton.IsEnabled = false;
                officialSourceButton.IsEnabled = false;
                refreshButton.IsEnabled = false;
                string[] stages =
                {
                    "Resolve official GPU driver",
                    "Download official driver package",
                    "Verify package integrity and vendor signature",
                    "Install or repair GPU driver",
                    "Re-enumerate selected GPU",
                    "Verify final driver state"
                };
                MaintenanceProgressWindow progressWindow = new(
                    window,
                    $"{action} - {selected.Name}",
                    $"Official {selected.Vendor} driver workflow. The installer cannot run until its HTTPS source and digital signature pass verification.",
                    stages);
                progressWindow.Show();
                TaskStatusMessage = "TASKS: GPU DRIVER";
                try
                {
                    taskLease.UpdateDetail($"Running verified: {selected.Vendor} driver workflow.");
                    GpuDriverOperationResult result = await _gpuDriverService.RunDriverOperationAsync(
                        selected,
                        mode,
                        progressWindow.Progress,
                        channel: choice.Id,
                        expectedVersion: choice.Release.Version);
                    TaskStatusMessage = result.Success
                        ? result.WarningCount > 0 || result.RestartRequired
                            ? "TASKS: WARNING"
                            : "TASKS: COMPLETE"
                        : "TASKS: FAILED";
                    if (result.Success && result.RestartRequired) _pendingRestarts.Add(selected.Vendor + " GPU driver");
                    string completion = result.Success
                        ? result.RestartRequired
                            ? "GPU driver operation completed. Restart Windows to finish applying the driver."
                            : "GPU driver operation completed and the selected device was verified."
                        : "GPU driver operation failed. Review the verified workflow log below.";
                    progressWindow.Complete(
                        result.Success,
                        result.WarningCount + (result.RestartRequired ? 1 : 0),
                        completion,
                        result.Report);
                    taskLease.Complete(
                        result.Success
                            ? result.WarningCount > 0 || result.RestartRequired ? "WARNING" : "COMPLETED"
                            : "FAILED",
                        completion);
                }
                catch (Exception exception)
                {
                    TaskStatusMessage = "TASKS: FAILED";
                    taskLease.Complete("FAILED", exception.Message);
                    progressWindow.Complete(
                        false,
                        0,
                        $"GPU driver operation failed: {exception.Message}",
                        exception.ToString());
                }
                finally
                {
                    taskLease.Dispose();
                    await RefreshInventoryAsync(selected.PnpDeviceId);
                    RefreshManagedTaskHeader();
                }
                }
                catch (Exception exception)
                {
                    statusText.Text = exception.Message;
                }
                finally
                {
                    operationLease.Dispose();
                    if (!window.IsClosed)
                    {
                        UpdateSelection();
                        _ = CheckUpdatesAsync();
                    }
                }
            }

            list.SelectionChanged += (_, _) =>
            {
                CancelReleaseCheck();
                ResetChannels();
                UpdateSelection();
                _ = CheckUpdatesAsync();
            };
            gpuHeader.ChannelPicker.SelectionChanged += (_, _) =>
            {
                if (bindingChannels || gpuGate.IsBusy || inventoryLoading) return;
                if (GetSelection() is { } device && GetChoice() is { } choice) selectedChannels[device.PnpDeviceId] = choice.Id;
                UpdateSelection();
            };
            gpuHeader.CheckButton.Click += async (_, _) => await CheckUpdatesAsync(force: true);
            officialSourceButton.Click += (_, _) =>
            {
                GpuDriverEntry? selected = GetSelection();
                if (selected is null)
                {
                    return;
                }
                SecuritySettingsLaunchResult result =
                    _gpuDriverService.OpenOfficialDownload(selected);
                statusText.Text = result.Message;
                statusText.Foreground = new SolidColorBrush(result.Success
                    ? Color.FromArgb(255, 0, 112, 60)
                    : Color.FromArgb(255, 185, 28, 28));
            };
            refreshButton.Click += async (_, _) =>
                await RefreshInventoryAsync(GetSelection()?.PnpDeviceId);
            gpuHeader.InstallButton.Click += async (_, _) =>
                await RunOperationAsync(GpuDriverOperationMode.InstallOrUpdate);
            gpuHeader.RepairButton.Click += async (_, _) =>
                await RunOperationAsync(GpuDriverOperationMode.Repair);

            await RefreshInventoryAsync();
            await window.ShowAsync();
        }

        private static Grid CreateGpuDriverGrid()
        {
            Grid grid = new();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
            return grid;
        }

        private static TextBlock AddGpuGridText(
            Grid grid,
            string text,
            int column,
            bool bold)
        {
            TextBlock label = new()
            {
                Text = text,
                FontWeight = bold
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = column == 0
                    ? HorizontalAlignment.Left
                    : HorizontalAlignment.Center
            };
            Grid.SetColumn(label, column);
            grid.Children.Add(label);
            return label;
        }

        private static string Fallback(string value) =>
            string.IsNullOrWhiteSpace(value) ? "<unknown>" : value;

        private async void SystemReportButton_Click(object sender, RoutedEventArgs e)
        {
            await RunTableReportTaskAsync(
                "TASKS: SYSTEM REPORT",
                "System Report",
                "SystemReport",
                () => Task.Run(_systemReportService.CollectAsync));
        }

        private async void WindowsActivationButton_Click(object sender, RoutedEventArgs e)
        {
            await RunTableReportTaskAsync(
                "TASKS: WINDOWS LICENSE",
                "Windows Activation Status",
                "WindowsActivationStatus",
                _licensingInformationService.ReadWindowsActivationAsync);
        }

        private async void OfficeActivationButton_Click(object sender, RoutedEventArgs e)
        {
            await RunTableReportTaskAsync(
                "TASKS: OFFICE LICENSE",
                "Office Activation Status",
                "OfficeActivationStatus",
                _licensingInformationService.ReadOfficeActivationAsync);
        }

        private async void LegacyWindowsPanelsButton_Click(object sender, RoutedEventArgs e)
        {
            TaskStatusMessage = "TASKS: LEGACY PANELS";

            try
            {
                await ShowLegacyWindowsPanelsDialogAsync();
            }
            finally
            {
                RefreshManagedTaskHeader();
            }
        }

        private async Task ShowLegacyWindowsPanelsDialogAsync()
        {
            Grid contentGrid = new();
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            StackPanel panelList = new()
            {
                Spacing = 7
            };

            TextBlock statusText = new()
            {
                Text = "Select a panel to open it.",
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 84, 134))
            };

            foreach (LegacyPanelDefinition panelEntry in _legacyWindowsPanelsService.GetPanels())
            {
                LegacyPanelDefinition panel = panelEntry;
                Button panelButton = new()
                {
                    Content = panel.Name,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };
                if (_normalToolButtonStyle is not null)
                {
                    panelButton.Style = _normalToolButtonStyle;
                }
                panelButton.Click += (_, _) =>
                {
                    LegacyPanelLaunchResult result = _legacyWindowsPanelsService.Launch(panel);
                    statusText.Text = result.Message;
                    statusText.Foreground = new SolidColorBrush(result.Success
                        ? Color.FromArgb(255, 0, 112, 60)
                        : Color.FromArgb(255, 185, 28, 28));
                };
                panelList.Children.Add(panelButton);
            }

            ScrollViewer panelViewer = new()
            {
                Content = panelList,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(panelViewer, 0);
            contentGrid.Children.Add(panelViewer);

            Grid.SetRow(statusText, 1);
            contentGrid.Children.Add(statusText);

            ToolWindow window = new(
                this,
                "Legacy Windows Panels",
                contentGrid,
                closeButtonText: "Close",
                initialWidth: 720,
                initialHeight: 760,
                minimumWidth: 480,
                minimumHeight: 420);
            await window.ShowAsync();
        }

        private async void BitLockerManagerButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowBitLockerManagerWindowAsync();
        }

        private async Task ShowBitLockerManagerWindowAsync()
        {
            TaskStatusMessage = "TASKS: BITLOCKER STATUS";

            Grid contentGrid = new();
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            ComboBox volumeSelector = new()
            {
                Header = "Volume",
                MinWidth = 180,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            contentGrid.Children.Add(volumeSelector);

            Grid actionGrid = new()
            {
                ColumnSpacing = 8,
                Margin = new Thickness(0, 10, 0, 10)
            };
            for (int column = 0; column < 4; column++)
            {
                actionGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
            }

            Button statusButton = new() { Content = "Full Status", MinHeight = 36 };
            Button suspendButton = new() { Content = "Suspend", MinHeight = 36 };
            Button resumeButton = new() { Content = "Resume", MinHeight = 36 };
            Button decryptButton = new() { Content = "Decrypt", MinHeight = 36 };
            Button[] actionButtons = { statusButton, suspendButton, resumeButton, decryptButton };
            for (int column = 0; column < actionButtons.Length; column++)
            {
                actionButtons[column].HorizontalAlignment = HorizontalAlignment.Stretch;
                Grid.SetColumn(actionButtons[column], column);
                actionGrid.Children.Add(actionButtons[column]);
            }
            if (_normalToolButtonStyle is not null)
            {
                statusButton.Style = _normalToolButtonStyle;
                suspendButton.Style = _normalToolButtonStyle;
                resumeButton.Style = _normalToolButtonStyle;
            }
            if (_primaryToolButtonStyle is not null)
            {
                decryptButton.Style = _primaryToolButtonStyle;
            }
            Grid.SetRow(actionGrid, 1);
            StackPanel bitLockerActions = new() { Spacing = 6 };
            bitLockerActions.Children.Add(actionGrid);
            Button automaticEncryptionButton = new() { Content = "Disable BitLocker automatic device encryption", HorizontalAlignment = HorizontalAlignment.Stretch };
            bitLockerActions.Children.Add(automaticEncryptionButton);
            Grid.SetRow(bitLockerActions, 1);
            contentGrid.Children.Add(bitLockerActions);

            TextBlock reportText = new()
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                IsTextSelectionEnabled = true,
                Margin = new Thickness(8)
            };
            ScrollViewer reportViewer = new()
            {
                Content = reportText,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Enabled,
                HorizontalScrollMode = ScrollMode.Enabled,
                ZoomMode = ZoomMode.Disabled
            };
            Grid.SetRow(reportViewer, 2);
            contentGrid.Children.Add(reportViewer);

            TextBlock statusText = new()
            {
                Margin = new Thickness(4, 8, 4, 0),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(statusText, 3);
            contentGrid.Children.Add(statusText);

            ToolWindow window = new(
                this,
                "BitLocker Manager",
                contentGrid,
                primaryButtonText: "Open Manage BitLocker",
                secondaryButtonText: "Copy",
                closeButtonText: "Close",
                initialWidth: 960,
                initialHeight: 720,
                minimumWidth: 620,
                minimumHeight: 440);

            bool operationInProgress = false;
            bool bitLockerLoading = false;
            OperationGate bitLockerGate = new();
            window.IsBusy = () => operationInProgress || bitLockerLoading || bitLockerGate.IsBusy;
            IReadOnlyList<SystemReportEntry> currentRows = Array.Empty<SystemReportEntry>();

            string? SelectedVolume() => volumeSelector.SelectedItem as string;

            void SetActionAvailability(bool enabled)
            {
                enabled &= !operationInProgress && !bitLockerLoading && !bitLockerGate.IsBusy;
                bool hasVolume = SelectedVolume() is not null;
                statusButton.IsEnabled = enabled;
                suspendButton.IsEnabled = enabled && hasVolume && WindowsPrivilegeService.IsAdministrator();
                resumeButton.IsEnabled = enabled && hasVolume && WindowsPrivilegeService.IsAdministrator();
                decryptButton.IsEnabled = enabled && hasVolume && WindowsPrivilegeService.IsAdministrator();
                automaticEncryptionButton.IsEnabled = enabled;
                volumeSelector.IsEnabled = enabled;
            }

            async Task ReloadAsync()
            {
                if (bitLockerLoading || window.IsClosed) return;
                bitLockerLoading = true;
                SetActionAvailability(false);
                statusText.Text = "Reading BitLocker status...";
                InlineAnalysisProgress progressWindow = window.CreateAnalysisProgress(
                    new[] { new CatalogProgressItem("BitLockerStatus", "BitLocker volumes") });
                progressWindow.Show();
                progressWindow.BeginItem(
                    "BitLockerStatus",
                    "Reading: BitLocker volume status");
                try
                {
                    string? priorSelection = SelectedVolume();
                    IReadOnlyList<BitLockerVolumeInfo> volumes =
                        await _securityInformationService.ReadBitLockerVolumesAsync();
                    volumeSelector.Items.Clear();
                    foreach (BitLockerVolumeInfo volume in volumes)
                    {
                        volumeSelector.Items.Add(volume.MountPoint);
                    }
                    if (volumeSelector.Items.Count > 0)
                    {
                        int priorIndex = priorSelection is null
                            ? -1
                            : volumeSelector.Items.IndexOf(priorSelection);
                        volumeSelector.SelectedIndex = priorIndex >= 0 ? priorIndex : 0;
                    }

                    currentRows = await _securityInformationService.ReadBitLockerStatusAsync();
                    progressWindow.VerifyItem(
                        "BitLockerStatus",
                        "Verifying: BitLocker volume status");
                    reportText.Text = BuildTableReportText(currentRows);
                    statusText.Text = volumes.Count == 0
                        ? "No fixed BitLocker volume was detected."
                        : WindowsPrivilegeService.IsAdministrator()
                            ? "Status loaded. Select a volume and operation."
                            : "Status loaded. Run the app as Administrator to suspend, resume, or decrypt.";
                    statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 84, 134));
                    progressWindow.CompleteItem(
                        "BitLockerStatus",
                        success: true,
                        $"Loaded {volumes.Count} fixed volume(s).");
                    progressWindow.UpdateOverall(1, $"Loaded {volumes.Count} volume(s).");
                    progressWindow.Complete(success: true, $"Loaded {volumes.Count} volume(s).");
                }
                catch (Exception exception)
                {
                    progressWindow.CompleteItem(
                        "BitLockerStatus",
                        success: false,
                        exception.Message);
                    progressWindow.Complete(success: false, exception.Message);
                    statusText.Text = $"Unable to read BitLocker status: {exception.Message}";
                    statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28));
                }
                finally
                {
                    bitLockerLoading = false;
                    SetActionAvailability(true);
                }
            }

            async Task RunOperationAsync(
                string operationName,
                string confirmation,
                Func<string, Task<BitLockerOperationResult>> operation,
                bool showDecryptionMonitor)
            {
                if (bitLockerLoading || window.IsClosed) return;
                using IDisposable? operationLease = bitLockerGate.TryEnter();
                if (operationLease is null) return;
                try
                {
                    SetActionAvailability(false);

                if (operationInProgress || SelectedVolume() is not string volume)
                {
                    return;
                }
                if (!WindowsPrivilegeService.IsAdministrator())
                {
                    await ShowMessageDialogAsync(
                        operationName,
                        "Administrator rights are required for this BitLocker operation.");
                    return;
                }
                if (!await ShowConfirmationWindowAsync(
                        operationName,
                        confirmation.Replace("{drive}", volume, StringComparison.Ordinal),
                        operationName))
                {
                    return;
                }

                string taskId = $"BitLocker:{operationName}:{volume}";
                using TaskActivityService.TaskActivityLease? taskLease =
                    await AcquireManagedTaskAsync(
                        taskId,
                        $"{operationName} {volume}",
                        new[] { "SystemMutation", "BitLocker", $"Volume:{volume}" });
                if (taskLease is null)
                {
                    return;
                }

                string progressVerb = operationName.StartsWith("Suspend", StringComparison.OrdinalIgnoreCase)
                    ? "Suspending"
                    : operationName.StartsWith("Resume", StringComparison.OrdinalIgnoreCase)
                        ? "Resuming"
                        : "Decrypting";
                CatalogProgressWindow progressWindow = new(
                    this,
                    progressVerb,
                    new[] { new CatalogProgressItem(volume, $"BitLocker {volume}") });
                progressWindow.Show();

                operationInProgress = true;
                SetActionAvailability(false);
                statusText.Text = $"{operationName} in progress...";
                try
                {
                    taskLease.UpdateDetail($"Applying {operationName} to {volume}.");
                    progressWindow.BeginItem(volume, $"{progressVerb}: BitLocker {volume}");
                    BitLockerOperationResult result = await operation(volume);
                    progressWindow.VerifyItem(volume, $"Verifying: BitLocker {volume}");
                    progressWindow.CompleteItem(volume, result.Success, result.Message);
                    progressWindow.UpdateOverall(1, result.Message);
                    progressWindow.Complete(result.Success, result.Message);
                    currentRows = result.Report;
                    reportText.Text = BuildTableReportText(currentRows);
                    statusText.Text = result.Message;
                    statusText.Foreground = new SolidColorBrush(result.Success
                        ? Color.FromArgb(255, 0, 112, 60)
                        : Color.FromArgb(255, 185, 28, 28));
                    if (result.Success && showDecryptionMonitor)
                    {
                        await ShowBitLockerDecryptionMonitorAsync(volume);
                    }
                    taskLease.Complete(
                        result.Success ? "COMPLETED" : "FAILED",
                        result.Message);
                    await ReloadAsync();
                }
                catch (Exception exception)
                {
                    progressWindow.CompleteItem(volume, success: false, exception.Message);
                    progressWindow.Complete(success: false, exception.Message);
                    statusText.Text = $"{operationName} failed: {exception.Message}";
                    statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28));
                    taskLease.Complete("FAILED", exception.Message);
                }
                finally
                {
                    taskLease.Dispose();
                    RefreshManagedTaskHeader();
                    operationInProgress = false;
                    SetActionAvailability(true);
                }
                }
                catch (Exception exception)
                {
                    statusText.Text = exception.Message;
                }
                finally
                {
                    operationLease.Dispose();
                    if (!window.IsClosed) SetActionAvailability(true);
                }
            }

            volumeSelector.SelectionChanged += (_, _) => SetActionAvailability(!operationInProgress);
            automaticEncryptionButton.Click += async (_, _) =>
            {
                if (operationInProgress || bitLockerLoading || bitLockerGate.IsBusy) return;
                using var entry = bitLockerGate.TryEnter();
                if (entry is null) return;
                SetActionAvailability(false);
                try
                {
                    await ShowToggleCatalogDialogAsync("BitLocker Automatic Device Encryption",
                        new FilteredToolToggleService(_debloatRegistryLabService, "PreventDeviceEncryption"));
                }
                catch (Exception exception) { statusText.Text = exception.Message; }
                finally { entry.Dispose(); if (!window.IsClosed) SetActionAvailability(true); }
            };
            statusButton.Click += async (_, _) => await ReloadAsync();
            suspendButton.Click += async (_, _) => await RunOperationAsync(
                "Suspend BitLocker",
                "Suspend BitLocker protection for {drive} until it is manually resumed?",
                _securityInformationService.SuspendBitLockerAsync,
                showDecryptionMonitor: false);
            resumeButton.Click += async (_, _) => await RunOperationAsync(
                "Resume BitLocker",
                "Resume BitLocker protection for {drive}?",
                _securityInformationService.ResumeBitLockerAsync,
                showDecryptionMonitor: false);
            decryptButton.Click += async (_, _) => await RunOperationAsync(
                "Decrypt BitLocker",
                "Permanently decrypt {drive}? This can take a long time and removes BitLocker encryption from the volume.",
                _securityInformationService.StartBitLockerDecryptionAsync,
                showDecryptionMonitor: true);

            window.PrimaryButton.Click += (_, _) =>
            {
                SecuritySettingsLaunchResult result = _securityInformationService.OpenBitLockerSettings();
                statusText.Text = result.Message;
                statusText.Foreground = new SolidColorBrush(result.Success
                    ? Color.FromArgb(255, 0, 112, 60)
                    : Color.FromArgb(255, 185, 28, 28));
            };
            window.SecondaryButton.Click += (_, _) =>
            {
                try
                {
                    DataPackage package = new();
                    package.SetText(BuildTableReportText(currentRows));
                    Clipboard.SetContent(package);
                    Clipboard.Flush();
                    statusText.Text = "Copied to Clipboard";
                }
                catch (Exception exception)
                {
                    statusText.Text = $"Copy failed: {exception.Message}";
                }
            };

            try
            {
                await ReloadAsync();
                TaskStatusMessage = "TASKS: COMPLETE";
                await window.ShowAsync();
            }
            finally
            {
                RefreshManagedTaskHeader();
            }
        }

        private async Task ShowBitLockerDecryptionMonitorAsync(string volume)
        {
            Grid monitorGrid = new();
            monitorGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            monitorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            monitorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            monitorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            ProgressBar progress = new()
            {
                Minimum = 0,
                Maximum = 100,
                IsIndeterminate = true,
                Height = 14,
                MinHeight = 14,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 3, 0, 4),
                Background = new SolidColorBrush(Color.FromArgb(255, 230, 230, 230)),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 124, 16))
            };
            Grid.SetRow(progress, 2);
            monitorGrid.Children.Add(progress);

            TextBlock percentText = new()
            {
                Text = "Calculating progress...",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 124, 16))
            };
            Grid.SetRow(percentText, 1);
            monitorGrid.Children.Add(percentText);

            TextBlock elapsedText = new()
            {
                Text = "Elapsed: 00:00:00",
                Margin = new Thickness(0, 0, 0, 4)
            };
            Grid.SetRow(elapsedText, 3);
            monitorGrid.Children.Add(elapsedText);

            TextBlock detailsText = new()
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            ScrollViewer detailsViewer = new()
            {
                Content = detailsText,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Enabled
            };
            Grid.SetRow(detailsViewer, 0);
            monitorGrid.Children.Add(detailsViewer);

            ToolWindow monitor = new(
                this,
                $"BitLocker Decryption - {volume}",
                monitorGrid,
                closeButtonText: "Close Monitor",
                initialWidth: 700,
                initialHeight: 460,
                minimumWidth: 500,
                minimumHeight: 340);
            Task<ToolWindowResult> closed = monitor.ShowAsync();
            Stopwatch elapsed = Stopwatch.StartNew();

            while (!closed.IsCompleted)
            {
                BitLockerVolumeInfo? current =
                    await _securityInformationService.ReadBitLockerVolumeAsync(volume);
                elapsedText.Text = $"Elapsed: {elapsed.Elapsed:hh\\:mm\\:ss}";
                if (current is BitLockerVolumeInfo status)
                {
                    if (status.EncryptionPercentage is int encrypted)
                    {
                        progress.IsIndeterminate = false;
                        progress.Value = Math.Clamp(100 - encrypted, 0, 100);
                        percentText.Text = $"{Math.Round(progress.Value):0}% complete — Decrypting {volume}";
                    }
                    string canonicalDetails =
                        $"Volume: {status.MountPoint}\n" +
                        $"Status: {status.VolumeStatus}\n" +
                        $"Encrypted: {(status.EncryptionPercentage is int percentage ? percentage + "%" : "Unknown")}\n" +
                        $"Protection: {status.ProtectionStatus}\n" +
                        $"Lock: {status.LockStatus}";
                    if (status.IsFullyDecrypted)
                    {
                        progress.IsIndeterminate = false;
                        progress.Value = 100;
                        percentText.Text = "100% complete";
                        detailsText.Text = canonicalDetails + "\n\nDecryption completed.";
                        break;
                    }
                    detailsText.Text = canonicalDetails;
                }
                else
                {
                    detailsText.Text = "Waiting for BitLocker status...";
                }

                Task delay = Task.Delay(TimeSpan.FromSeconds(2));
                await Task.WhenAny(delay, closed);
            }
        }

        private async void SmartAppControlButton_Click(object sender, RoutedEventArgs e)
        {
            await RunSecurityManagerTaskAsync(
                "TASKS: SMART APP CONTROL",
                "Smart App Control",
                "Open Windows Security",
                _securityInformationService.ReadSmartAppControlStatusAsync,
                _securityInformationService.OpenSmartAppControlSettings);
        }

        private async void DisableDefenderButton_Click(object sender, RoutedEventArgs e)
        {
            await RunDefenderPolicyAsync(DefenderPolicyMode.Disable);
        }

        private async void RestoreDefenderButton_Click(object sender, RoutedEventArgs e)
        {
            await RunDefenderPolicyAsync(DefenderPolicyMode.Restore);
        }

        private async Task RunDefenderPolicyAsync(DefenderPolicyMode mode)
        {
            string title = mode == DefenderPolicyMode.Disable
                ? "Disable Defender"
                : "Restore Defender";

            if (mode == DefenderPolicyMode.Disable &&
                !string.Equals(
                    DefenderPolicyService.ReadTamperProtectionState(),
                    "Off",
                    StringComparison.OrdinalIgnoreCase))
            {
                bool openSettings = await ShowConfirmationWindowAsync(
                    title,
                    "Tamper Protection must be turned OFF before Defender policies can be changed. No policy has been modified. Open Windows Security now?",
                    "Open Windows Security");
                if (openSettings)
                {
                    SecuritySettingsLaunchResult launchResult =
                        _defenderPolicyService.OpenTamperProtectionSettings();
                    if (!launchResult.Success)
                    {
                        await ShowMessageDialogAsync(title, launchResult.Message);
                    }
                }
                return;
            }

            string message = mode == DefenderPolicyMode.Disable
                ? "This reduces Microsoft Defender protection by writing 12 policy values. " +
                  "Tamper Protection must already be OFF. Continue?"
                : "Restore all 12 Microsoft Defender policy targets to their enabled/default value (DWORD 0)?";

            if (!await ShowConfirmationWindowAsync(
                    title,
                    message,
                    mode == DefenderPolicyMode.Disable
                        ? "Disable protection"
                        : "Restore protection"))
            {
                return;
            }

            TaskActivityService.TaskActivityLease? taskLease =
                await AcquireManagedTaskAsync(
                    mode == DefenderPolicyMode.Disable ? "DisableDefender" : "RestoreDefender",
                    title,
                    new[] { "SystemMutation", "DefenderPolicy", "WindowsSecurity" });
            if (taskLease is null)
            {
                return;
            }

            string progressVerb = mode == DefenderPolicyMode.Disable
                ? "Disabling"
                : "Restoring";
            CatalogProgressWindow progressWindow = new(
                this,
                progressVerb,
                new[] { new CatalogProgressItem("DefenderPolicy", "Microsoft Defender") });
            progressWindow.Show();

            TaskStatusMessage = mode == DefenderPolicyMode.Disable
                ? "TASKS: DISABLING DEFENDER"
                : "TASKS: RESTORING DEFENDER";

            try
            {
                taskLease.UpdateDetail($"Applying and verifying: {title} policy changes.");
                progressWindow.BeginItem("DefenderPolicy", $"{progressVerb}: Microsoft Defender");
                DefenderPolicyResult result = await _defenderPolicyService.ApplyAsync(mode);
                progressWindow.VerifyItem("DefenderPolicy", "Verifying: Microsoft Defender policies");
                progressWindow.CompleteItem(
                    "DefenderPolicy",
                    result.Success,
                    result.Message);
                progressWindow.UpdateOverall(1, result.Message);
                progressWindow.Complete(result.Success, result.Message);
                TaskStatusMessage = result.Success
                    ? "TASKS: COMPLETE"
                    : "TASKS: NOT VERIFIED";
                await ShowTableReportDialogAsync(
                    $"{title} - Verification",
                    mode == DefenderPolicyMode.Disable
                        ? "DefenderDisabledVerification"
                        : "DefenderRestoredVerification",
                    result.Report);
                taskLease.Complete(
                    result.Success ? "COMPLETED" : "WARNING",
                    result.Message);

                if (!result.Success && mode == DefenderPolicyMode.Disable &&
                    result.Message.Contains("Tamper Protection", StringComparison.OrdinalIgnoreCase))
                {
                    _defenderPolicyService.OpenTamperProtectionSettings();
                }
            }
            catch (Exception exception)
            {
                TaskStatusMessage = "TASKS: FAILED";
                progressWindow.CompleteItem(
                    "DefenderPolicy",
                    success: false,
                    exception.Message);
                progressWindow.Complete(success: false, exception.Message);
                taskLease.Complete("FAILED", exception.Message);
            }
            finally
            {
                taskLease.Dispose();
                RefreshManagedTaskHeader();
            }
        }

        private async Task RunSecurityManagerTaskAsync(
            string taskLabel,
            string dialogTitle,
            string settingsButtonText,
            Func<Task<IReadOnlyList<SystemReportEntry>>> reportFactory,
            Func<SecuritySettingsLaunchResult> openSettings)
        {
            TaskStatusMessage = taskLabel;
            InlineAnalysisProgress progressWindow = new(
                MainAnalysisHost,
                new[] { new CatalogProgressItem("SecurityReport", dialogTitle) });
            progressWindow.Show();
            progressWindow.BeginItem(
                "SecurityReport",
                $"Reading: {dialogTitle}");

            try
            {
                IReadOnlyList<SystemReportEntry> rows = await reportFactory();
                progressWindow.VerifyItem(
                    "SecurityReport",
                    $"Formatting: {dialogTitle}");
                progressWindow.CompleteItem(
                    "SecurityReport",
                    success: true,
                    $"Loaded {rows.Count} report row(s).");
                progressWindow.UpdateOverall(1, $"Loaded {dialogTitle}.");
                progressWindow.Complete(success: true, $"Loaded {dialogTitle}.");
                TaskStatusMessage = "TASKS: COMPLETE";
                await ShowSecurityManagerDialogAsync(
                    dialogTitle,
                    settingsButtonText,
                    rows,
                    openSettings);
            }
            catch (Exception exception)
            {
                progressWindow.CompleteItem(
                    "SecurityReport",
                    success: false,
                    exception.Message);
                progressWindow.Complete(success: false, exception.Message);
                TaskStatusMessage = "TASKS: FAILED";
                await ShowMessageDialogAsync($"{dialogTitle} failed", exception.Message);
            }
            finally
            {
                RefreshManagedTaskHeader();
            }
        }

        private async Task ShowSecurityManagerDialogAsync(
            string title,
            string settingsButtonText,
            IReadOnlyList<SystemReportEntry> rows,
            Func<SecuritySettingsLaunchResult> openSettings)
        {
            Grid contentGrid = new();
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            string plainText = BuildTableReportText(rows);
            TextBlock reportText = new()
            {
                Text = plainText,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                IsTextSelectionEnabled = true,
                Margin = new Thickness(10)
            };
            ScrollViewer reportViewer = new()
            {
                Content = reportText,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Enabled,
                HorizontalScrollMode = ScrollMode.Enabled,
                ZoomMode = ZoomMode.Disabled
            };
            contentGrid.Children.Add(reportViewer);

            TextBlock statusText = new()
            {
                Margin = new Thickness(8, 8, 8, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 84, 134))
            };
            Grid.SetRow(statusText, 1);
            contentGrid.Children.Add(statusText);

            ToolWindow window = new(
                this,
                title,
                contentGrid,
                primaryButtonText: settingsButtonText,
                secondaryButtonText: "Copy",
                closeButtonText: "Close",
                initialWidth: 900,
                initialHeight: 680,
                minimumWidth: 560,
                minimumHeight: 400);

            window.PrimaryButton.Click += (_, _) =>
            {
                SecuritySettingsLaunchResult result = openSettings();
                statusText.Text = result.Message;
                statusText.Foreground = new SolidColorBrush(result.Success
                    ? Color.FromArgb(255, 0, 112, 60)
                    : Color.FromArgb(255, 185, 28, 28));
            };

            window.SecondaryButton.Click += (_, _) =>
            {
                try
                {
                    DataPackage package = new();
                    package.SetText(plainText);
                    Clipboard.SetContent(package);
                    Clipboard.Flush();
                    statusText.Text = "Copied to Clipboard";
                }
                catch (Exception exception)
                {
                    statusText.Text = $"Copy failed: {exception.Message}";
                }
            };

            await window.ShowAsync();
        }

        private bool HasPendingWork => _utilityTaskInProgress || _profileApplyInProgress || _profileGate.IsBusy || _homeActionGate.IsBusy ||
            _taskActivityService.HasActiveTask || ToolWindow.HasBusyOwnedWindows(this);
        private bool HasBusyTasks => HasPendingWork || _pendingRestarts.IsPromptActive;

        private async void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            if (HasBusyTasks)
            {
                await ShowCloseWarningAsync();
                return;
            }
            Close();
        }

        private async Task RunInformationTaskAsync(
            string taskLabel,
            string dialogTitle,
            Func<Task<string>> reportFactory)
        {
            if (_profileApplyInProgress || _utilityTaskInProgress)
            {
                return;
            }

            _utilityTaskInProgress = true;
            TaskStatusMessage = taskLabel;
            UpdateProfileSelectionUi();

            try
            {
                string report = await reportFactory();
                TaskStatusMessage = "TASKS: COMPLETE";
                await ShowReportDialogAsync(dialogTitle, report);
            }
            catch (Exception exception)
            {
                TaskStatusMessage = "TASKS: FAILED";
                await ShowMessageDialogAsync(
                    $"{dialogTitle} failed",
                    exception.Message);
            }
            finally
            {
                _utilityTaskInProgress = false;
                TaskStatusMessage = "TASKS: IDLE";
                UpdateProfileSelectionUi();
            }
        }

        private async Task RunTableReportTaskAsync(
            string taskLabel,
            string dialogTitle,
            string suggestedFileName,
            Func<Task<IReadOnlyList<SystemReportEntry>>> reportFactory)
        {
            TaskActivityService.TaskActivityLease? taskLease =
                await AcquireManagedTaskAsync(
                    suggestedFileName,
                    dialogTitle,
                    Array.Empty<string>());
            if (taskLease is null)
            {
                return;
            }

            TaskStatusMessage = taskLabel;
            InlineAnalysisProgress progressWindow = new(
                MainAnalysisHost,
                new[] { new CatalogProgressItem(suggestedFileName, dialogTitle) });
            progressWindow.Show();

            try
            {
                taskLease.UpdateDetail("Collecting read-only system information.");
                progressWindow.BeginItem(
                    suggestedFileName,
                    $"Collecting: {dialogTitle}");
                IReadOnlyList<SystemReportEntry> rows = await reportFactory();
                progressWindow.VerifyItem(
                    suggestedFileName,
                    $"Formatting: {dialogTitle}");
                progressWindow.CompleteItem(
                    suggestedFileName,
                    success: true,
                    $"Loaded {rows.Count} report row(s).");
                progressWindow.UpdateOverall(1, $"Loaded {dialogTitle}.");
                progressWindow.Complete(success: true, $"Loaded {dialogTitle}.");
                TaskStatusMessage = "TASKS: COMPLETE";
                await ShowTableReportDialogAsync(
                    dialogTitle,
                    suggestedFileName,
                    rows);
                taskLease.Complete("COMPLETED", "Read-only report completed.");
            }
            catch (Exception exception)
            {
                progressWindow.CompleteItem(
                    suggestedFileName,
                    success: false,
                    exception.Message);
                progressWindow.Complete(success: false, exception.Message);
                TaskStatusMessage = "TASKS: FAILED";
                taskLease.Complete("FAILED", exception.Message);
                await ShowMessageDialogAsync(
                    $"{dialogTitle} failed",
                    exception.Message);
            }
            finally
            {
                taskLease.Dispose();
                RefreshManagedTaskHeader();
            }
        }

        private async Task ShowReportDialogAsync(string title, string report)
        {
            TextBlock reportText = new()
            {
                Text = report,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                IsTextSelectionEnabled = true,
                Margin = new Thickness(10)
            };

            ScrollViewer reportViewer = new()
            {
                Content = reportText,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Enabled,
                HorizontalScrollMode = ScrollMode.Enabled,
                ZoomMode = ZoomMode.Disabled
            };

            ToolWindow window = new(
                this,
                title,
                reportViewer,
                closeButtonText: "Close",
                initialWidth: 920,
                initialHeight: 700,
                minimumWidth: 520,
                minimumHeight: 360);
            await window.ShowAsync();
        }

        private async Task ShowTableReportDialogAsync(
            string title,
            string suggestedFileName,
            IReadOnlyList<SystemReportEntry> rows)
        {
            Grid contentGrid = new();
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid header = CreateReportGridRow(
                "Property",
                "Value",
                isSection: false,
                isHeader: true);
            bool diskDashboard = suggestedFileName == "StorageOverview";
            CheckBox addressOption = new()
            {
                Content = "Show IP and MAC addresses (also include in Copy / Save TXT)",
                IsChecked = false,
                Visibility = rows.Any(row => row.IsNetworkAddress) ? Visibility.Visible : Visibility.Collapsed,
                Margin = new Thickness(4, 0, 4, 10)
            };
            StackPanel reportHeader = new();
            reportHeader.Children.Add(addressOption);
            header.Visibility = diskDashboard ? Visibility.Collapsed : Visibility.Visible;
            reportHeader.Children.Add(header);
            Grid.SetRow(reportHeader, 0);
            contentGrid.Children.Add(reportHeader);

            ListView reportList = new()
            {
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 190, 200, 214)),
                BorderThickness = new Thickness(1, 0, 1, 1)
            };
            ScrollViewer.SetVerticalScrollBarVisibility(
                reportList,
                ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(
                reportList,
                ScrollBarVisibility.Disabled);

            string plainText = "";
            void RenderReport()
            {
                IReadOnlyList<SystemReportEntry> visible = NetworkReport.VisibleRows(rows, addressOption.IsChecked == true);
                plainText = BuildTableReportText(visible);
                reportList.Items.Clear();
                if (diskDashboard) return;
                foreach (SystemReportEntry row in visible)
                {
                    reportList.Items.Add(new ListViewItem
                    {
                        Content = CreateReportGridRow(row.Property, row.Value, row.IsSection, isHeader: false),
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(0), Margin = new Thickness(0), IsTabStop = false
                    });
                }
                UiDisplaySettings.Apply(reportList);
            }
            RenderReport();
            addressOption.Checked += (_, _) => RenderReport();
            addressOption.Unchecked += (_, _) => RenderReport();

            FrameworkElement reportSurface = diskDashboard ? new DiskInfoView(rows) : reportList;
            Grid.SetRow(reportSurface, 1);
            contentGrid.Children.Add(reportSurface);

            TextBlock statusText = new()
            {
                Text = "",
                Margin = new Thickness(4, 8, 4, 0),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 49, 84, 134)),
                FontSize = 12
            };
            Grid.SetRow(statusText, 2);
            contentGrid.Children.Add(statusText);

            ToolWindow window = new(
                this,
                title,
                contentGrid,
                primaryButtonText: "Copy",
                secondaryButtonText: "Save TXT",
                closeButtonText: "Close",
                initialWidth: 1040,
                initialHeight: 760,
                minimumWidth: 620,
                minimumHeight: 420);

            window.PrimaryButton.Click += (_, _) =>
            {
                try
                {
                    DataPackage package = new();
                    package.SetText(plainText);
                    Clipboard.SetContent(package);
                    Clipboard.Flush();
                    statusText.Text = "Copied to Clipboard";
                }
                catch (Exception exception)
                {
                    statusText.Text = $"Copy failed: {exception.Message}";
                }
            };

            bool savingReport = false;
            window.IsBusy = () => savingReport;
            window.SecondaryButton.Click += async (_, _) =>
            {
                if (savingReport) return;
                savingReport = true;
                addressOption.IsEnabled = false;
                window.SecondaryButton.IsEnabled = false;
                try
                {
                    string? fileName = await DesktopReportExport.SaveAsync(window, suggestedFileName, plainText);
                    if (fileName is not null && !window.IsClosed) statusText.Text = $"Saved: {fileName}";
                }
                catch (Exception exception)
                {
                    if (!window.IsClosed) statusText.Text = $"Save failed: {exception.Message}";
                }
                finally
                {
                    savingReport = false;
                    addressOption.IsEnabled = true;
                    if (!window.IsClosed) window.SecondaryButton.IsEnabled = true;
                }
            };

            await window.ShowAsync();
        }

        private static Grid CreateReportGridRow(
            string property,
            string value,
            bool isSection,
            bool isHeader)
        {
            Grid row = new()
            {
                MinHeight = isHeader ? 32 : 27,
                Padding = new Thickness(7, 4, 7, 4),
                Background = isSection
                    ? new SolidColorBrush(Color.FromArgb(255, 176, 196, 222))
                    : isHeader
                        ? new SolidColorBrush(Color.FromArgb(255, 238, 243, 249))
                        : new SolidColorBrush(Colors.Transparent)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock propertyText = new()
            {
                Text = property,
                FontWeight = isSection || isHeader
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = isSection
                    ? new SolidColorBrush(Color.FromArgb(255, 0, 47, 108))
                    : new SolidColorBrush(Color.FromArgb(255, 11, 21, 32)),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBlock valueText = new()
            {
                Text = isSection ? string.Empty : value,
                FontWeight = isHeader
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 11, 21, 32)),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(valueText, 1);

            row.Children.Add(propertyText);
            row.Children.Add(valueText);
            return row;
        }

        private static string BuildTableReportText(
            IReadOnlyList<SystemReportEntry> rows)
        {
            StringBuilder text = new();
            foreach (SystemReportEntry row in rows)
            {
                if (row.IsSection)
                {
                    text.AppendLine(row.Property);
                }
                else
                {
                    text.AppendLine($"{row.Property,-28} : {row.Value}");
                }
            }

            return text.ToString().TrimEnd();
        }

        private void RenderDetailText()
        {
            if (_latestGamingSnapshot.HasValue)
            {
                LiveDetailText.Text = GamingLiveStatusService.FormatDetails(
                    _latestGamingSnapshot.Value);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_gamingStatusError))
            {
                LiveDetailText.Text = "STATUS: Unable to display current Windows state.";
                return;
            }

            LiveDetailText.Text = "STATUS: Initializing...";
        }

        private static string FormatUptime(TimeSpan uptime)
        {
            return $"{(int)uptime.TotalDays}d {uptime.Hours:00}h {uptime.Minutes:00}m";
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _isClosed = true;
            UiDisplaySettings.Changed -= UiDisplaySettings_Changed;
            _clockTimer.Stop();
            _monitorTimer.Stop();
            _gamingStatusTimer.Stop();
            _systemMonitor.Dispose();
            _gamingActionsService.Dispose();
            (Application.Current as App)?.ReleaseSingleInstance();
        }

        private async void MainWindow_Closing(
            AppWindow sender,
            AppWindowClosingEventArgs args)
        {
            if (!HasBusyTasks)
            {
                return;
            }

            args.Cancel = true;
            await ShowCloseWarningAsync();
        }

        private async Task ShowCloseWarningAsync()
        {
            if (_closeWarningInProgress)
            {
                return;
            }

            _closeWarningInProgress = true;
            try
            {
                await ShowMessageDialogAsync(
                    "Task is still running",
                    "Wait until the active or queued task is complete before closing the application.");
                if (_isClosed) return;
                if (_taskManagerWindow is { IsClosed: false })
                {
                    _taskManagerWindow.Activate();
                }
                else
                {
                    TaskStatusButton_Click(TaskStatusButton, new RoutedEventArgs());
                }
            }
            catch (Exception exception)
            {
                App.WriteCrashLog(exception, "Displaying active-task close warning");
            }
            finally
            {
                _closeWarningInProgress = false;
            }
        }
    }
}
