using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Threading;
using Metrolist.Desktop.ViewModels;

namespace Metrolist.Desktop;

internal static class ReleaseVerification
{
    public static MainWindow? CreateWindow(string[]? args)
    {
        if (args is not ["--verify-release", var report]) return null;
        string reportPath = Path.GetFullPath(report);
        string root = Path.GetDirectoryName(reportPath)!;
        string dataDirectory = Path.Combine(root, "release-check-" + Guid.NewGuid().ToString("N"));
        var clock = Stopwatch.StartNew();
        var window = new MainWindow(new MainViewModel(dataDirectory)) { ShowActivated = false };
        double openedMilliseconds = 0;
        window.Opened += (_, _) =>
        {
            openedMilliseconds = clock.Elapsed.TotalMilliseconds;
            DispatcherTimer.RunOnce(window.Close, TimeSpan.FromSeconds(3));
        };
        window.Closed += (_, _) =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                Opened = openedMilliseconds > 0, CleanClose = true,
                WindowConstructionMilliseconds = openedMilliseconds,
                Runtime = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString()
            }));
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        };
        return window;
    }
}
