using Avalonia;
using Avalonia.Controls;
using Thumb = Avalonia.Controls.Primitives.Thumb;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using SwayMusic.Core;
using SwayMusic.Core.Storage;
using SwayMusic.Desktop;
using SwayMusic.Desktop.ViewModels;
using static SwayMusic.Tests.HeadlessTests;

namespace SwayMusic.Tests;

public static class DesignTests
{
    public static void Run(string directory)
    {
        var amber = new Track("design-a", "Amber room", "Design fixture A", "Layout verification", TimeSpan.FromSeconds(222));
        var evening = new Track("design-b", "Evening tide", "Design fixture B", "Layout verification", TimeSpan.FromSeconds(248));
        var glass = new Track("design-c", "Glass garden", "Design fixture C", "Layout verification", TimeSpan.FromSeconds(196));
        var state = new LibraryState
        {
            LikedTracks = [evening, amber],
            History = [new(glass, DateTimeOffset.UtcNow), new(amber, DateTimeOffset.UtcNow.AddMinutes(-1)), new(evening, DateTimeOffset.UtcNow.AddMinutes(-2))],
            Playlists = [new() { Name = "Design verification", Tracks = [amber, glass] }],
            Session = new() { Queue = [amber, evening], CurrentIndex = 1 },
            Settings = new() { WindowWidth = 1280, WindowHeight = 800, FetchLyrics = false, SidePanelOpen = true }
        };
        Await(AtomicFile.WriteJsonAsync(Path.Combine(directory, "library.json"), state));
        var model = new MainViewModel(directory);
        var window = new MainWindow(model, integrateSystem: false);
        try
        {
            window.Show(); Await(model.InitializeAsync()); Resize(window, 1280, 800);
            Check(window.Title == "SwayMusic", "The native window title should use SwayMusic.");
            Check(model.IsSidePanelVisible && model.IsQueuePanel && model.QueueTracks.Count == 2,
                "The wide layout should show the restored queue without starting audio or fetching music.");
            Check(model.QueueTracks.Count(row => row.IsCurrent) == 1 && model.QueueTracks[1].IsCurrent,
                "The docked queue should identify its current entry.");
            Check(model.ShowHomeCards && model.HomeCards.Count == 3,
                "Home should render real history-backed cards without requiring a startup network request.");
            Capture(window, "sway-home-populated.png");
            Activate(window, model.LibraryCommand);
            Check(model.IsLibrary && model.Tracks.Count == 3, "Your library should combine and deduplicate saved music.");
            var filter = window.FindControl<TextBox>("LibraryFilterBox")!;
            filter.Focus(); window.KeyTextInput("Glass"); Flush();
            Check(model.Tracks.Count == 1 && model.Tracks[0].Track.Id == glass.Id,
                "Library keyboard filtering should update the actual song list binding.");
            filter.Text = ""; Flush();
            window.FindControl<ComboBox>("LibrarySortPicker")!.SelectedIndex = 1; Flush();
            Check(model.Tracks[0].Track.Id == amber.Id, "Title sort should update library order.");

            var list = window.FindControl<ListBox>("TrackList")!;
            list.SelectedIndex = 0; Flush();
            var container = list.ContainerFromIndex(0) as Control ?? throw new InvalidOperationException("The first song was not realized.");
            var menuTarget = new[] { (Control)list, container }.Concat(container.GetVisualDescendants().OfType<Control>())
                .FirstOrDefault(control => control.ContextMenu is not null)
                ?? throw new InvalidOperationException("Song rows must expose their context actions.");
            var menu = menuTarget.ContextMenu!;
            menu.Open(menuTarget); Flush();
            var enqueue = menu.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Header, "Add to queue"))
                ?? throw new InvalidOperationException("The row menu should contain Add to queue.");
            int before = model.Player.Queue.Count;
            enqueue.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Flush();
            Check(model.Player.Queue.Count == before + 1 && model.Player.Queue[^1].Id == amber.Id,
                "The row menu should enqueue the selected song without starting playback.");
            menu.Close();

            Activate(window, model.SettingsCommand);
            var appearance = window.FindControl<ComboBox>("AppearancePicker")!;
            appearance.SelectedItem = "Light"; Flush();
            Check(model.Appearance == "Light" && Application.Current!.RequestedThemeVariant == ThemeVariant.Light,
                "The Light picker must change the actual application theme.");
            appearance.SelectedItem = "System"; Flush();
            Check(model.Appearance == "System" && Application.Current!.RequestedThemeVariant == ThemeVariant.Default,
                "System appearance must follow the platform theme.");
            appearance.SelectedItem = "Light";
            window.FindControl<CheckBox>("CompactRowsCheck")!.IsChecked = true; Flush();
            Check(model.CompactRows && model.TrackRowHeight == 44, "Compact density should use 44 px rows.");
            Activate(window, model.SaveSettingsCommand); Await(((AsyncCommand)model.SaveSettingsCommand).RunningTask);
            using (var saved = new LibraryStore(directory))
            {
                Await(saved.LoadAsync());
                Check(saved.State.Settings.Appearance == "Light" && saved.State.Settings.CompactRows,
                    "Appearance and density changes should persist with application settings.");
            }
            Activate(window, model.LibraryCommand);
            Check(list.GetRealizedContainers().All(row => row.Bounds.Height is >= 40 and <= 48),
                "Compact preferences must change realized row geometry, not just a stored flag.");
            var seekThumb = window.FindControl<Slider>("SeekSlider")!.GetVisualDescendants().OfType<Thumb>().Single();
            Check(seekThumb.Background is ISolidColorBrush thumbBrush && thumbBrush.Color == Color.Parse("#425B22"),
                "The Light seek thumb must use Sway's olive accent instead of the platform's blue accent.");
            Check(FindButton(window, model.LibraryCommand).Focus(NavigationMethod.Tab),
                "Primary navigation must remain reachable with keyboard focus.");
            Capture(window, "sway-library-light.png");
            model.Appearance = "Dark"; model.CompactRows = false; Flush();
            Check(list.GetRealizedContainers().All(row => row.Bounds.Height is >= 56 and <= 64),
                "Comfortable density should restore 60 px song rows.");
            Capture(window, "sway-library-dark.png");
            foreach (double scale in new[] { 1.5, 2.0 })
            {
                window.SetRenderScaling(scale); Flush();
                using var scaled = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The scaled design did not render.");
                Check(scaled.PixelSize.Width == (int)(window.ClientSize.Width * scale)
                    && scaled.PixelSize.Height == (int)(window.ClientSize.Height * scale),
                    "High-DPI rendering must preserve logical layout and produce the expected physical pixel dimensions.");
                if (scale == 2) scaled.Save(Path.GetFullPath(Path.Combine("docs", "sway-high-dpi.png")), PngBitmapEncoderOptions.Default);
            }
            window.SetRenderScaling(1); Flush();

            Resize(window, 850, 600);
            Check(!model.IsSidePanelVisible, "The side panel must collapse at the minimum window width.");
            var search = window.FindControl<TextBox>("SearchBox")!;
            Check(search.Bounds.Width > 100, "Search must remain usable at 850 px width.");
            AssertInside(window, search, "Search field");
            AssertInside(window, FindButton(window, model.ToggleCommand), "Playback control");
            AssertInside(window, FindButton(window, model.SettingsCommand), "Settings navigation");
            var settingsButton = FindButton(window, model.SettingsCommand);
            var settingsBounds = VisibleBounds(window, settingsButton);
            foreach (var navigation in window.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("nav") && button != settingsButton))
            {
                var intersection = VisibleBounds(window, navigation).Intersect(settingsBounds);
                Check(intersection.Width < 2 || intersection.Height < 2,
                    "Sidebar navigation must not paint over the pinned Settings action at the minimum size.");
            }
            Activate(window, model.DownloadsCommand);
            Check(model.IsDownloads, "Secondary navigation must remain keyboard accessible at the minimum size.");
            Activate(window, model.QueueCommand);
            Check(model.IsQueue && model.ShowTracks && model.Tracks.Count == model.Player.Queue.Count,
                "Queue must remain accessible as a page when its docked panel cannot fit.");
            Capture(window, "sway-minimum-window.png");
            Resize(window, 1920, 1080);
            model.Player.Enqueue(glass); Flush();
            Check(model.IsQueue && model.Tracks.Count == model.Player.Queue.Count && model.Tracks[^1].Track.Id == glass.Id,
                "The full queue page must stay synchronized when its docked queue changes after expanding the window.");
            model.ClosePanelCommand.Execute(null); Flush();
            Check(!model.IsSidePanelVisible, "Closing a docked panel should reclaim its content space.");
            Activate(window, model.QueueCommand);
            Check(model.IsQueuePanel && model.IsSidePanelVisible, "Queue should reopen as a panel at larger widths.");
            model.LyricsCommand.Execute(null); Await(((AsyncCommand)model.LyricsCommand).RunningTask);
            Check(model.IsLyricsPanel && model.LyricsText.Contains("disabled", StringComparison.OrdinalIgnoreCase),
                "The docked lyrics panel should show a disabled state without contacting a provider.");
            Console.WriteLine("PASS Sway design: library filter/sort, context actions, Dark/Light/System, density persistence, adaptive queue/lyrics, 850/1280/1920 layouts and 150/200% DPI");
        }
        finally
        {
            Await(model.DisposeAsync().AsTask()); window.Close(); Flush();
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        }
    }

    private static void Resize(Window window, double width, double height)
    { window.Width = width; window.Height = height; Flush(); }

    private static void Capture(Window window, string name)
    {
        Flush();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The design frame did not render.");
        frame.Save(Path.GetFullPath(Path.Combine("docs", name)), PngBitmapEncoderOptions.Default);
    }

    private static Button FindButton(Window window, System.Windows.Input.ICommand command)
        => window.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => ReferenceEquals(button.Command, command) && button.IsEffectivelyVisible)
            ?? throw new InvalidOperationException("An expected application action has no visible button.");

    private static void Activate(Window window, System.Windows.Input.ICommand command)
    {
        var button = FindButton(window, command);
        button.BringIntoView(); Flush(); Check(button.Focus(), "Application actions must accept keyboard focus.");
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None); Flush();
    }

    private static void AssertInside(Window window, Control control, string description)
    {
        var origin = control.TranslatePoint(default, window) ?? throw new InvalidOperationException(description + " is detached.");
        Check(origin.X >= -1 && origin.Y >= -1 && origin.X + control.Bounds.Width <= window.ClientSize.Width + 1
            && origin.Y + control.Bounds.Height <= window.ClientSize.Height + 1, description + " must fit inside the client area.");
    }

    private static Rect VisibleBounds(Window window, Control control)
    {
        if (!control.IsEffectivelyVisible) return default;
        var origin = control.TranslatePoint(default, window) ?? default;
        var result = new Rect(origin, control.Bounds.Size);
        foreach (var ancestor in control.GetVisualAncestors().Where(visual => visual.ClipToBounds))
        {
            var clipOrigin = ancestor.TranslatePoint(default, window);
            if (clipOrigin is { } point) result = result.Intersect(new Rect(point, ancestor.Bounds.Size));
        }
        return result;
    }
}
