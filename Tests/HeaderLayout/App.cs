using Microsoft.UI.Xaml;

namespace Naufal_Windows_Tech_s_Powertoys;

// This isolated frontend host does not compile or instantiate any OS mutation,
// hardware, repair, first-run, task scheduler or original app startup service.
public partial class App : Application
{
    private Window? _window;
    internal static void WriteCrashLog(Exception exception, string context) =>
        File.AppendAllText(ResultPath, "NOTE: " + context + ": " + exception.Message + Environment.NewLine);
    internal static readonly string ResultPath = Path.Combine(AppContext.BaseDirectory, "header-layout-results.txt");
    public App()
    {
        UnhandledException += (_, e) =>
        {
            File.AppendAllText(ResultPath, "FAIL UNHANDLED: " + e.Exception + Environment.NewLine);
            Environment.ExitCode = 1;
        };
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        File.WriteAllText(ResultPath, "RUNNING: " + DateTimeOffset.Now.ToString("O") + Environment.NewLine);
        string theme = Environment.GetCommandLineArgs().Contains("--startup-theme=Dark") ? "Dark" : "Light";
        Directory.CreateDirectory(AppDataPaths.SettingsDirectory);
        File.WriteAllText(Path.Combine(AppDataPaths.SettingsDirectory, "ui-theme.txt"), theme);
        File.AppendAllText(ResultPath, "Saved startup theme: " + theme + Environment.NewLine);
        string? startup = Environment.GetCommandLineArgs().FirstOrDefault(value => value.StartsWith("--startup-scale="));
        if (startup is not null && int.TryParse(startup[16..], out int scale) &&
            new[] { 25, 50, 75, 100, 125, 150, 175, 200 }.Contains(scale))
        {
            Directory.CreateDirectory(AppDataPaths.SettingsDirectory);
            File.WriteAllText(Path.Combine(AppDataPaths.SettingsDirectory, "ui-font-scale.txt"), scale.ToString());
            File.AppendAllText(ResultPath, $"Saved startup scale: {scale}%\n");
        }
        _window = new MainWindow();
        _window.Activate();
    }
}

internal static class AppDataPaths
{
    internal static string LocalRoot => SettingsDirectory;
    // Never change the user's actual app preferences during a scale matrix.
    internal static string SettingsDirectory { get; } = Path.Combine(AppContext.BaseDirectory,
        "test-preferences", Guid.NewGuid().ToString("N"));
}
