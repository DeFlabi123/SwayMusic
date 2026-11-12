using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SwayMusic.Core;
using SwayMusic.Core.Storage;
using SwayMusic.Desktop;
using SwayMusic.Desktop.ViewModels;

namespace SwayMusic.Tests;

public static class HeadlessTests
{
    public static void Run()
    {
        var bindingLog = new BindingLog();
        Logger.Sink = bindingLog;
        AppBuilder.Configure<App>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        string testRoot = Path.GetFullPath("artifacts");
        string directory = Path.GetFullPath(Path.Combine(testRoot, "headless-" + Guid.NewGuid().ToString("N")));
        Check(directory.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "The isolated headless library must remain in the artifacts directory.");
        var viewModel = new MainViewModel(directory);
        var window = new MainWindow(viewModel, integrateSystem: false);
        try
        {
            window.Show();
            Flush();
            Check(ReferenceEquals(window.DataContext, viewModel), "The actual window must use the isolated view model.");
            Check(viewModel.PageTitle == "Home" && viewModel.ShowTracks, "Initial navigation should show Home without a network request.");

            var search = window.FindControl<TextBox>("SearchBox") ?? throw new InvalidOperationException("SearchBox was not loaded from XAML.");
            window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
            window.KeyRelease(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
            Check(search.IsFocused, "Ctrl+F should focus the actual search field.");
            window.KeyTextInput("Miles Davis");
            Flush();
            Check(search.Text == "Miles Davis" && viewModel.Query == "Miles Davis", "Keyboard text input should update the compiled two-way binding.");

            Click(window, "Liked songs");
            Check(viewModel.PageTitle == "Liked songs" && viewModel.ShowTracks, "The sidebar should navigate to liked songs.");
            Click(window, "Downloads", useMouse: true);
            Check(viewModel.PageTitle == "Downloads", "The downloads sidebar should navigate without network access.");
            Click(window, "Playlists");
            Check(viewModel.ShowBrowse && viewModel.ShowCreatePlaylist, "The playlist page should expose creation controls.");
            var playlistName = window.GetVisualDescendants().OfType<TextBox>().First(box => box.PlaceholderText == "Playlist name");
            playlistName.Focus();
            window.KeyTextInput("Headless validation playlist");
            Click(window, "Create");
            Await(((AsyncCommand)viewModel.CreatePlaylistCommand).RunningTask);
            Check(viewModel.Playlists.Count == 1 && viewModel.Playlists[0].Name == "Headless validation playlist",
                "Playlist creation through the actual controls should persist and refresh navigation.");

            Click(window, "Settings & account");
            Check(viewModel.ShowSettings && !viewModel.ShowTracks, "Settings navigation should update visibility bindings.");
            var lyricOption = window.GetVisualDescendants().OfType<CheckBox>()
                .First(option => Equals(option.Content, "Fetch lyrics when opening the lyrics page"));
            Check(lyricOption.IsChecked == true, "The settings checkbox should reflect stored defaults.");
            ClickControl(window, lyricOption);
            Check(!viewModel.FetchLyrics, "Mouse input should update the settings checkbox binding.");
            Click(window, "Save settings");
            Await(((AsyncCommand)viewModel.SaveSettingsCommand).RunningTask);
            Check(viewModel.Status == "Settings saved.", "Saving settings should complete without a binding error.");

            viewModel.CookieInput = "SAPISID=headless-race-test; SID=" + new string('x', 8192);
            viewModel.SaveAccountCommand.Execute(null);
            viewModel.SignOutCommand.Execute(null);
            Await(Task.WhenAll(((AsyncCommand)viewModel.SaveAccountCommand).RunningTask,
                ((AsyncCommand)viewModel.SignOutCommand).RunningTask));
            using (var vault = new CredentialStore(directory))
            {
                var loadedCookie = vault.LoadCookieAsync();
                Await(loadedCookie);
                Check(loadedCookie.Result is null && viewModel.AccountStatus == "Not signed in",
                    "Sign-out during account saving must leave both durable credentials and the live account state signed out.");
            }

            Click(window, "Liked songs");
            var list = window.FindControl<ListBox>("TrackList") ?? throw new InvalidOperationException("TrackList was not loaded from XAML.");
            for (int index = 0; index < 4096; index++)
                viewModel.Tracks.Add(new(new Track("headless-" + index, "Virtualization test track " + index, "Test fixture", Duration: TimeSpan.FromSeconds(180))));
            Flush();
            Check(list.ItemsPanelRoot is VirtualizingStackPanel, "The production track list should use a virtualizing panel.");
            int realized = list.GetRealizedContainers().Count();
            Check(realized > 0 && realized < 200, $"The 4096-track list realized {realized} rows; expected fewer than 200.");
            list.SelectedIndex = 2500;
            list.ScrollIntoView(2500);
            Flush();
            Check(viewModel.SelectedTrack?.Track.Id == "headless-2500", "A virtualized selection should update the view model.");
            Check(list.GetRealizedContainers().Count() < 200, "Scrolling must keep the realized list bounded.");

            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            Flush();
            Check(window.ClientSize.Width >= window.MinWidth && window.ClientSize.Height >= window.MinHeight,
                "The minimum supported window size should remain usable.");
            Check(search.Bounds.Width > 100, "Search must remain visible at minimum window width.");
            window.Width = 1400;
            window.Height = 900;
            Flush();
            Check(window.ClientSize.Width >= 1400 && window.ClientSize.Height >= 900, "The native layout should resize to a larger window.");

            TestShutdownDuringInitialization(Path.Combine(directory, "startup-close"));
            DesignTests.Run(Path.Combine(directory, "design"));
            AccountUiTests.Run(Path.Combine(directory, "account-ui"));
            AccountLibraryUiTests.Run(Path.Combine(directory, "account-library"));
            ArtistUiTests.Run(Path.Combine(directory, "artist-ui"));
            ArtworkTests.Run(Path.Combine(directory, "artwork"));
            OrbitTests.Run(Path.Combine(directory, "orbit"));
            LyricsServiceTests.Ui(Path.Combine(directory, "lyrics-errors"));
            string screenshot = Path.GetFullPath(Path.Combine("docs", "screenshot.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            CaptureCleanStartup(Path.Combine(directory, "preview"), screenshot);
            CaptureCleanStartup(Path.Combine(directory, "account-preview"), Path.GetFullPath(Path.Combine("docs", "sway-account-login.png")), settings: true);
            Check(new FileInfo(screenshot).Length > 5000, "The screenshot should contain actual rendered controls.");
            Check(bindingLog.Errors.Count == 0, "Compiled bindings reported errors: " + string.Join("; ", bindingLog.Errors));
            string previousFailure = Path.GetFullPath(Path.Combine("docs", "headless-failure.png"));
            if (File.Exists(previousFailure)) File.Delete(previousFailure);
            Console.WriteLine($"PASS headless XAML, navigation, keyboard/mouse input, settings, playlists, concurrent account actions, startup-close, resizing, 4096-item virtualization ({realized} realized rows)");
            Console.WriteLine("Headless Skia frame: " + screenshot);
        }
        catch
        {
            try
            {
                string failurePath = Path.GetFullPath(Path.Combine("docs", "headless-failure.png"));
                Directory.CreateDirectory(Path.GetDirectoryName(failurePath)!);
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(failurePath, PngBitmapEncoderOptions.Default);
                Console.Error.WriteLine("Headless failure frame: " + failurePath);
            }
            catch (Exception error) { Console.Error.WriteLine("Could not capture failure frame: " + error.Message); }
            throw;
        }
        finally
        {
            Await(viewModel.DisposeAsync().AsTask());
            window.Close();
            Flush();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestShutdownDuringInitialization(string directory)
    {
        var preservedTrack = new Track("startup-preserve", "Restored queue", "Test fixture");
        var state = new LibraryState
        {
            Settings = new() { Volume = 0.37, Shuffle = true },
            Session = new() { Queue = Enumerable.Repeat(preservedTrack, 10000).ToList(), CurrentIndex = 21, Position = TimeSpan.FromSeconds(12) }
        };
        Await(AtomicFile.WriteJsonAsync(Path.Combine(directory, "library.json"), state));
        var initializing = new MainViewModel(directory);
        var startup = initializing.InitializeAsync();
        Await(Task.WhenAll(startup, initializing.DisposeAsync().AsTask()));
        using var reopened = new LibraryStore(directory);
        Await(reopened.LoadAsync());
        Check(reopened.State.Session.Queue.Count == 10000 && reopened.State.Session.CurrentIndex == 21
            && reopened.State.Settings.Volume == 0.37 && reopened.State.Settings.Shuffle,
            "Closing during initialization must preserve the durable session and settings without disposing active file operations.");
    }

    private static void CaptureCleanStartup(string directory, string screenshot, bool settings = false)
    {
        var previewModel = new MainViewModel(directory);
        var preview = new MainWindow(previewModel, integrateSystem: false);
        try
        {
            preview.Show();
            Await(previewModel.InitializeAsync());
            preview.Width = 1280;
            preview.Height = 800;
            if (settings) previewModel.SettingsCommand.Execute(null);
            Flush();
            using var frame = preview.CaptureRenderedFrame() ?? throw new InvalidOperationException("Skia did not render a headless frame.");
            Check(frame.PixelSize.Width >= 850 && frame.PixelSize.Height >= 600, "The rendered frame should contain the complete application window.");
            frame.Save(screenshot, PngBitmapEncoderOptions.Default);
        }
        finally
        {
            Await(previewModel.DisposeAsync().AsTask());
            preview.Close();
            Flush();
        }
    }

    private static void Click(Window window, string content, bool useMouse = false)
    {
        var button = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
            Equals(button.Content, content) || AutomationProperties.GetName(button) == content)
            ?? throw new InvalidOperationException("Button not found: " + content);
        if (useMouse) { ClickControl(window, button); return; }
        button.BringIntoView();
        Flush();
        Check(button.Focus(), "Button could not receive keyboard focus: " + content);
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Flush();
    }

    private static void ClickControl(Window window, Control control)
    {
        control.BringIntoView();
        Flush();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Control is not attached to the test window.");
        window.MouseMove(point, RawInputModifiers.None);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Flush();
    }

    internal static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    internal static void Await(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("A headless UI operation did not complete.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
        Flush();
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BindingLog : ILogSink
    {
        public List<string> Errors { get; } = [];
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (IsEnabled(level, area)) Errors.Add(messageTemplate);
        }
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area)) Errors.Add(messageTemplate + " " + string.Join(", ", propertyValues));
        }
    }
}
