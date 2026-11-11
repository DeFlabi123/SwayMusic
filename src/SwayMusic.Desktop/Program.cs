using Avalonia;
using System.Runtime.InteropServices;

namespace SwayMusic.Desktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstance = new Mutex(true, "Local\\SwayMusicNativeWindows", out var created);
        if (!created)
        {
            var window = FindWindow(null, "SwayMusic");
            if (window != 0) { ShowWindow(window, 9); SetForegroundWindow(window); }
            return;
        }
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { singleInstance.ReleaseMutex(); }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseWin32().UseSkia().UseHarfBuzz()
        .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string? windowName);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
