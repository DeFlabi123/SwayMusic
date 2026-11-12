using System.Net;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using ToggleButton = Avalonia.Controls.Primitives.ToggleButton;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Platform;
using System.Runtime.InteropServices;
using SwayMusic.Desktop.Controls;
using SwayMusic.Core;
using SwayMusic.Core.Storage;
using SwayMusic.Desktop;
using SwayMusic.Desktop.Playback;
using SwayMusic.Desktop.ViewModels;
using static SwayMusic.Tests.HeadlessTests;

namespace SwayMusic.Tests;

internal static class OrbitTests
{
    private static byte[]? _liveCover;
    public static void Native()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "sway-orbit-native-" + Guid.NewGuid().ToString("N"));
            using var loop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                SwayMusic.Desktop.Program.BuildAvaloniaApp().SetupWithoutStarting();
                var model = new MainViewModel(directory, httpHandler: new Handler());
                var main = new MainWindow(model, integrateSystem: false) { ShowActivated = false };
                OrbitView? orbit = null;
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        main.Show(); await model.InitializeAsync();
                        Directory.CreateDirectory(directory); string wave = Path.Combine(directory, "native-orbit.wav"); AudioSpectrumTests.WriteWave(wave, 15);
                        model.Volume = 0;
                        await model.Player.PlayTrackAsync(new Track("orbit-native", "Afterglow", "Sway Sessions", Duration: TimeSpan.FromSeconds(15), LocalPath: wave));
                        var handle = main.TryGetPlatformHandle()!.Handle;
                        Open(main); orbit = main.OrbitView!; main.WindowState = WindowState.FullScreen;
                        await Task.Delay(500);
                        Check(main.WindowState == WindowState.FullScreen && model.IsOrbitOpen && ReferenceEquals(TopLevel.GetTopLevel(orbit), main) && main.TryGetPlatformHandle()!.Handle == handle, "native Orbit enters real fullscreen");
                        main.WindowState = WindowState.Minimized; await Task.Delay(150);
                        Check(!model.Player.VisualizationEnabled && model.Player.State == PlayerState.Playing, "minimizing the same window stops Orbit work while preserving music");
                        main.WindowState = WindowState.FullScreen; await Task.Delay(4700);
                        Check(orbit.FindControl<Grid>("TopChrome")!.Opacity < 0.05 && model.Player.State == PlayerState.Playing, "native controls fade on an actual timer while the main window is minimized");
                        var progress = orbit.FindControl<ProgressBar>("OrbitAmbientProgress")!;
                        Check(progress.IsEffectivelyVisible && progress.Value > 0, "subtle song progress stays visible and advances after the transport controls fade");
                        orbit.FindControl<Button>("OrbitQueueButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(450);
                        Check(orbit.FindControl<Grid>("TopChrome")!.Opacity > 0.95, "native controls fade back in on interaction");
                        await model.Player.SeekAsync(TimeSpan.FromSeconds(8)); await Task.Delay(350);
                        Check(progress.Value >= 7.9, "the quiet progress line follows an actual playback seek");
                        model.Player.Pause(); main.ExitOrbit();
                        Check(!model.IsOrbitOpen && !model.Player.VisualizationEnabled && model.Player.State == PlayerState.Paused, "closing native Orbit preserves the shared paused session");
                        Console.WriteLine("PASS native single-window Orbit fullscreen, minimized animation suspension, timed fade, wake, exit and playback preservation");
                    }
                    catch (Exception error) { failure = error; }
                    finally
                    {
                        main.ExitOrbit(); await model.DisposeAsync();
                        main.Close(); loop.Cancel();
                    }
                });
                Dispatcher.UIThread.MainLoop(loop.Token);
            }
            catch (Exception error) { failure ??= error; }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    public static void Standalone(bool audio = false, bool liveCover = false)
    {
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        string directory = Path.Combine(Path.GetTempPath(), "sway-orbit-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (liveCover) { using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false }); var loading = http.GetByteArrayAsync("https://i.ytimg.com/vi/LbfGmHyzOJM/hqdefault.jpg"); Await(loading); _liveCover = loading.Result; }
            Run(directory, audio);
        } finally { Directory.Delete(directory, recursive: true); }
    }
    public static void Run(string directory, bool audio = false)
    {
        var current = new Track("orbit-fixture", "Afterglow", "Sway Sessions", "Night Drive · Vol. 01", TimeSpan.FromSeconds(120), "https://images.example/cover.png");
        var other = new Track("other-fixture", "Another song", "Fixture");
        Await(AtomicFile.WriteJsonAsync(Path.Combine(directory, "library.json"), new LibraryState { Session = new() { Queue = [current, other], CurrentIndex = 0 }, LikedTracks = [other] }));
        var model = new MainViewModel(directory, httpHandler: new Handler());
        var main = new MainWindow(model, integrateSystem: false);
        try
        {
            main.Show(); Await(model.InitializeAsync());
            Open(main); Flush();
            var orbit = main.OrbitView ?? throw new Exception("Top-right Orbit button did not open Orbit");
            Check(model.IsOrbitOpen && ReferenceEquals(orbit.DataContext, model) && model.Player.State == PlayerState.Stopped, "Orbit shares the active session and never autoplays");
            Check(ReferenceEquals(TopLevel.GetTopLevel(orbit), main) && !main.FindControl<Grid>("LibraryView")!.IsVisible, "Orbit is embedded in the existing native window");
            main.FindControl<Button>("OpenOrbitButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ReferenceEquals(main.OrbitView, orbit), "entering Orbit twice reuses the same embedded view");
            ArtworkTests.Wait(() => model.OrbitHasSyncedLyrics && model.Artwork is not null);
            Check(model.OrbitCurrent == "Let the city fade away", "Orbit displays real timestamped lyric data");
            var backdrop = model.OrbitBackdrop ?? throw new Exception("Missing blurred backdrop");
            using (var converted = new WriteableBitmap(backdrop.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul))
            using (var pixels = converted.Lock())
            {
                backdrop.CopyPixels(pixels); byte[] sample = new byte[4]; Marshal.Copy(pixels.Address + pixels.RowBytes * 20 + 20 * 4, sample, 0, 4);
                Check(_liveCover is not null || sample[2] > 90 && sample[1] < 25 && sample[0] < 25, "a mostly red cover produces a saturated red blurred backdrop");
            }
            var play = orbit.FindControl<Button>("OrbitPlayPauseButton")!;
            var icon = orbit.FindControl<SwayIcon>("OrbitPlayPauseIcon")!;
            var center = icon.TranslatePoint(new Point(12, 12), play)!.Value;
            Check(Math.Abs(center.X - play.Bounds.Width / 2) < 0.5 && Math.Abs(center.Y - play.Bounds.Height / 2) < 0.5, "play/pause glyph uses a centered 24-pixel coordinate frame");
            model.UpdateOrbitLyrics(TimeSpan.FromSeconds(25));
            Check(model.OrbitCurrent == "Stay until the blue turns gold" && model.OrbitPrevious == "Nothing left for us to chase" && model.OrbitNext == "Let the night move slowly", "lyrics follow timestamps and retain the surrounding lines");
            model.UpdateOrbitLyrics(TimeSpan.FromSeconds(5)); Check(model.OrbitCurrent == "Let the city fade away", "lyrics rewind correctly");
            model.UpdateOrbitLyrics(TimeSpan.FromSeconds(25));
            main.Width = 1440; main.Height = 900; Flush(); Capture(main, _liveCover is not null ? "sway-orbit-red-cover.png" : "sway-orbit.png");
            main.Width = 850; main.Height = 600; Flush(); Capture(main, _liveCover is not null ? "sway-orbit-red-cover-small.png" : "sway-orbit-small.png");
            var scene = orbit.FindControl<Grid>("OrbitScene")!;
            Check(scene.Bounds.Width > 500 && orbit.FindControl<Viewbox>("OrbitDisc")!.Bounds.Height >= 180, "Orbit remains usable at minimum window size");
            orbit.FindControl<ToggleButton>("OrbitCalmToggle")!.IsChecked = true; Flush();
            Check(model.OrbitCalm && !model.Player.VisualizationEnabled, "calm mode disables audio analysis");
            orbit.FindControl<ToggleButton>("OrbitCalmToggle")!.IsChecked = false; Flush();
            Check(model.Player.VisualizationEnabled, "leaving calm mode re-enables the real audio tap");
            orbit.FindControl<ToggleButton>("OrbitLyricsToggle")!.IsChecked = false; Flush();
            Check(!model.OrbitShowLyrics && !orbit.FindControl<Grid>("OrbitLyricsArea")!.IsVisible, "lyrics can be hidden for a cover-only view");
            orbit.FindControl<ToggleButton>("OrbitLyricsToggle")!.IsChecked = true; Flush();
            orbit.FindControl<Button>("OrbitQueueButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
            Check(orbit.FindControl<Border>("OrbitQueuePanel")!.IsVisible && model.QueueTracks.Count == 2, "Orbit queue uses the existing playback queue");
            main.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); main.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Flush();
            Check(!orbit.FindControl<Border>("OrbitQueuePanel")!.IsVisible && main.OrbitView is not null, "Escape dismisses the queue before closing Orbit");
            model.SelectedTrack = new(other);
            model.LikeCurrentCommand.Execute(null); Await(((AsyncCommand)model.LikeCurrentCommand).RunningTask);
            Check(model.IsCurrentLiked, "Orbit heart targets the playing song instead of a selected library row");
            if (audio)
            {
                string wave = Path.Combine(directory, "orbit-tone.wav"); AudioSpectrumTests.WriteWave(wave, 12);
                model.Volume = 0;
                main.MouseMove(new Point(250, 200), RawInputModifiers.None);
                Await(model.Player.SetQueueAsync([current with { LocalPath = wave }, other], 0)); Await(Task.Delay(150));
                Check(orbit.FindControl<SwayIcon>("OrbitPlayPauseIcon")!.Kind == "pause", "the centered pause glyph follows live playback");
                orbit.FindControl<OrbitHalo>("Halo")!.Update(model.Player);
                Check(orbit.FindControl<OrbitHalo>("Halo")!.Energy > 0, "the stronger halo responds to decoded audio");
                Capture(main, "sway-orbit-playing.png");
                main.WindowState = WindowState.Minimized; Flush(); Check(!model.IsOrbitOpen && !model.Player.VisualizationEnabled, "minimized Orbit stops animation resources"); main.WindowState = WindowState.Normal; Flush();
                orbit.FindControl<Grid>("TopChrome")!.Transitions = null; orbit.FindControl<Grid>("BottomChrome")!.Transitions = null;
                orbit.HideControlsIfIdle(); Flush();
                Check(orbit.FindControl<Grid>("TopChrome")!.Opacity < 0.05 && model.Player.State == PlayerState.Playing, $"controls fade while shared audio continues with the main window minimized (opacity={orbit.FindControl<Grid>("TopChrome")!.Opacity}, state={model.Player.State}, pointer={orbit.FindControl<Grid>("TopChrome")!.IsPointerOver}/{orbit.FindControl<Grid>("BottomChrome")!.IsPointerOver}, focus={orbit.FindControl<Grid>("TopChrome")!.IsKeyboardFocusWithin}/{orbit.FindControl<Grid>("BottomChrome")!.IsKeyboardFocusWithin}, orbit={model.IsOrbitOpen})");
                main.MouseMove(new Point(250, 200), RawInputModifiers.None); Await(Task.Delay(450));
                Check(orbit.FindControl<Grid>("TopChrome")!.Opacity > 0.95, "moving the pointer brings controls back");
                Await(model.Player.SeekAsync(TimeSpan.FromSeconds(10))); model.Player.Pause(); Flush();
                Check(model.OrbitCurrent == "Nothing left for us to chase", "Orbit lyrics follow a real playback seek while the main window is minimized");
                Await(model.Player.StopAsync()); model.Player.RestoreQueue([current, other], 0); Flush();
                main.WindowState = WindowState.Normal;
            }
            main.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Flush();
            Check(main.OrbitView is null && !model.IsOrbitOpen && !model.Player.VisualizationEnabled && model.Player.Queue.Count == 2, "exit preserves playback state and stops Orbit resources");
            main.FindControl<Button>("OpenOrbitButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
            main.Close(); Flush();
            ArtworkTests.Wait(() => !main.IsVisible);
            Check(main.OrbitView is null && !model.IsOrbitOpen, "closing Sway also closes its Orbit window");
            Console.WriteLine("PASS Orbit entry/reuse, current-session sharing, artwork palette, synced lyric navigation, calm/lyrics/queue controls, current-song likes, Escape, layout and shutdown");
        }
        finally { main.ExitOrbit(); Await(model.DisposeAsync().AsTask()); main.Close(); Flush(); }
    }
    private static void Open(MainWindow main)
    {
        var button = main.FindControl<Button>("OpenOrbitButton")!;
        if (!button.IsEffectivelyVisible) button = main.FindControl<Button>("OpenOrbitCompactButton")!;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Capture(Window window, string file)
    {
        using var image = window.CaptureRenderedFrame();
        string path = Path.GetFullPath(Path.Combine("docs", file));
        try { image?.Save(path, PngBitmapEncoderOptions.Default); } catch (IOException) when (File.Exists(path)) { }
    }
    private static byte[] RedCover()
    {
        using var image = new WriteableBitmap(new PixelSize(128, 128), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var pixels = image.Lock())
        {
            byte[] row = new byte[pixels.RowBytes];
            for (int x = 0; x < 128; x++) { row[x * 4] = 3; row[x * 4 + 1] = 8; row[x * 4 + 2] = 225; row[x * 4 + 3] = 255; }
            for (int y = 0; y < 128; y++) Marshal.Copy(row, 0, pixels.Address + y * pixels.RowBytes, row.Length);
        }
        using var data = new MemoryStream(); image.Save(data, PngBitmapEncoderOptions.Default); return data.ToArray();
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "lrclib.net")
            {
                string content = JsonSerializer.Serialize(new[] { new { trackName = "Afterglow", artistName = "Sway Sessions", duration = 120, syncedLyrics = "[00:00]Let the city fade away\n[00:10]Nothing left for us to chase\n[00:20]Stay until the blue turns gold\n[00:30]Let the night move slowly\n[00:40]We have nowhere else to go" } });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_liveCover ?? RedCover()) });
        }
    }
}
