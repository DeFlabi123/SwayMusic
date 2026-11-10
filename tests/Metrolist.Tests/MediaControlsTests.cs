using System.Runtime.InteropServices;
using Metrolist.Core;
using Metrolist.Desktop.Playback;
using Windows.Media;

namespace Metrolist.Tests;

public static class MediaControlsTests
{
    public static async Task SmokeAsync()
    {
        var handle = CreateWindowEx(0, "STATIC", "Metrolist SMTC verification", 0, 0, 0, 1, 1, 0, 0, 0, 0);
        if (handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            await using var player = new PlaybackService((_, _) => throw new InvalidOperationException("This metadata test must not open audio."));
            using var integration = new WindowsMediaControls(player, handle, action => action());
            var controls = SystemMediaTransportControlsInterop.GetForWindow(handle);
            player.RestoreQueue([new Track("smtc", "SMTC title", "SMTC artist", "SMTC album", TimeSpan.FromMinutes(3))]);
            if (!controls.IsEnabled || !controls.IsPlayEnabled || !controls.IsPauseEnabled ||
                !controls.IsNextEnabled || !controls.IsPreviousEnabled ||
                controls.DisplayUpdater.MusicProperties.Title != "SMTC title" ||
                controls.DisplayUpdater.MusicProperties.Artist != "SMTC artist" ||
                controls.DisplayUpdater.MusicProperties.AlbumTitle != "SMTC album" ||
                controls.PlaybackStatus != MediaPlaybackStatus.Stopped)
                throw new InvalidOperationException("The native window's Windows media session did not receive metadata and control state.");
        }
        finally { DestroyWindow(handle); }
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint handle);
}
