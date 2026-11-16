using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace Naufal_Windows_Tech_s_Powertoys;

public sealed partial class MainWindow
{
    private bool _startupCheckStarted;

    // Opt-in developer smoke check of the real released EXE/startup/backend reads.
    // No Apply/Restore/repair is dispatched and the prerequisite wizard is skipped
    // only for this run. Captures contain actual Home state, not seeded test data.
    private async Task CaptureStartupCheckAsync()
    {
        _startupCheckStarted = true;
        string directory = Path.Combine(AppContext.BaseDirectory, "startup-check");
        string report = Path.Combine(directory, "result.txt");
        ElementTheme originalTheme = UiDisplaySettings.Theme;
        int originalScale = UiDisplaySettings.TextScalePercent;
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(report, $"Started: {DateTimeOffset.Now:O}\nProduct: {AppIdentity.Product}\nVersion: {AppIdentity.Version}\n");
            for (int attempt = 0; attempt < 120 && (!_latestGamingSnapshot.HasValue || !_latestSystemSnapshot.HasValue); attempt++)
                await Task.Delay(500);
            if (!_latestGamingSnapshot.HasValue || !_latestSystemSnapshot.HasValue)
                throw new InvalidOperationException("Real Home status did not become ready within 60 seconds.");
            if (Title != AppIdentity.Product) throw new InvalidOperationException("Window title mismatch.");
            if (Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride != "en-US")
                throw new InvalidOperationException("Framework resource language did not initialize to English.");
            var pages = new FrameworkElement[] { HomePage, RepairPage, InfoPage, SecurityPage, AdvancedPage, NetworkPage };
            if (MainNavigation.MenuItems.Count != pages.Length) throw new InvalidOperationException("Sidebar page count mismatch.");
            for (int index = 0; index < pages.Length; index++)
            {
                MainNavigation.SelectedItem = MainNavigation.MenuItems[index];
                await Task.Delay(60);
                if (pages[index].Visibility != Visibility.Visible || pages.Count(page => page.Visibility == Visibility.Visible) != 1)
                    throw new InvalidOperationException("Sidebar selection failed.");
            }
            MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
            foreach (ElementTheme theme in new[] { ElementTheme.Dark, ElementTheme.Light })
            {
                UiDisplaySettings.SetPreviewDisplay(theme, 100);
                MainNavigation.IsPaneOpen = true;
                await Task.Delay(900);
                RootLayout.UpdateLayout();
                RenderTargetBitmap bitmap = new();
                await bitmap.RenderAsync(RootLayout);
                byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
                if (bitmap.PixelWidth < 1 || bitmap.PixelHeight < 1) throw new InvalidOperationException("Empty Home capture.");
                using var file = File.Create(Path.Combine(directory, $"dashboard-{theme.ToString().ToLowerInvariant()}.png"));
                using var stream = file.AsRandomAccessStream();
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
                await encoder.FlushAsync();
            }
            File.AppendAllText(report, "PASS: actual application startup, English framework resources, six sidebar pages, live Home reads, and Dark/Light captures. No system changes requested.\n");
        }
        catch (Exception exception)
        {
            App.WriteCrashLog(exception, "Startup check");
            try { File.AppendAllText(report, "FAIL: " + exception + "\n"); } catch { }
            Environment.ExitCode = 1;
        }
        finally
        {
            UiDisplaySettings.SetPreviewDisplay(originalTheme, originalScale);
            Close();
        }
    }
}
