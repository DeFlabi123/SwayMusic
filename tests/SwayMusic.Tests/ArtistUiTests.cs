using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using SwayMusic.Core;
using SwayMusic.Desktop;
using SwayMusic.Desktop.ViewModels;
using static SwayMusic.Tests.HeadlessTests;

namespace SwayMusic.Tests;

internal static class ArtistUiTests
{
    public static void Run(string directory)
    {
        var handler = new ArtistTests.Handler();
        var model = new MainViewModel(directory, httpHandler: handler);
        var window = new MainWindow(model, integrateSystem: false);
        try
        {
            window.Show(); Await(model.InitializeAsync()); window.Width = 1280; window.Height = 800; Flush();
            model.Tracks.Add(new(new Track("song-before-click", "Song before navigation", "Fixture Artist", Artists: [new("Fixture Artist", "UC_fixture")])));
            Flush();
            var tracks = window.FindControl<ListBox>("TrackList")!;
            tracks.SelectedIndex = 0; Flush();
            Check(model.Player.Queue.Count == 0, "selection alone does not start playback");
            var action = tracks.GetVisualDescendants().OfType<Button>().First(button => button.Tag is TrackRow);
            Click(window, action);
            Check(model.Player.Queue.Count == 0, "song action button does not start playback");
            tracks.ContextMenu!.Close(); Flush();
            Click(window, tracks.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "Song before navigation"), MouseButton.Right);
            Check(model.Player.Queue.Count == 0, "right-clicking a song opens actions without starting playback");
            tracks.ContextMenu.Close(); Flush();
            Click(window, tracks.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "Song before navigation"));
            try { Await(((AsyncCommand)model.PlaySelectedCommand).RunningTask); } catch (ArgumentException) { }
            Check(model.Player.CurrentTrack?.Id == "song-before-click", "one click on a song starts that song");
            model.Player.RestoreQueue([]); Flush();
            var link = window.FindControl<ListBox>("TrackList")!.GetVisualDescendants().OfType<Button>()
                .First(button => button.Tag is ArtistReference { Id: "UC_fixture" });
            Click(window, link); Wait(() => model.IsArtistPage && !model.Busy);
            Check(model.PageTitle == "Fixture Artist" && model.HasArtistDescription && model.ArtistStats.Contains("monthly listeners"), "artist link opens the detailed artist profile");
            Check(handler.SearchCount == 0 && model.Player.Queue.Count == 0, "artist link uses its ID without searching or starting playback");
            Check(model.Tracks.Count == 3 && model.HasArtistCatalog, "artist page shows songs and browsable releases");
            Click(window, window.FindControl<ListBox>("ArtistTrackList")!.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "Second song"));
            try { Await(((AsyncCommand)model.PlaySelectedCommand).RunningTask); } catch (ArgumentException) { }
            Check(model.Player.Queue.Count == 3 && model.Player.CurrentIndex == 1, "one click on an artist song starts the clicked row in its collection");
            Click(window, window.FindControl<ListBox>("QueueList")!.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "Third song"));
            Await(model.Player.StopAsync());
            Check(model.Player.Queue.Count == 3 && model.Player.CurrentIndex == 2, "one click in the queue starts that entry without replacing the queue");
            model.Player.RestoreQueue([]); Flush();
            model.DismissStatusCommand.Execute(null); Flush();
            Wait(() => model.ArtistArtwork is not null);
            Check(model.ArtistCatalog[0].Artwork is null, "catalog artwork stays deferred until the releases tab opens");
            Capture(window, "sway-artist-profile.png");
            model.ArtistSectionIndex = 2; Flush(); Capture(window, "sway-artist-about.png");
            model.ArtistSectionIndex = 1; Flush();
            Wait(() => model.ArtistCatalog[0].Artwork is not null);
            model.ArtistSectionIndex = 0; Flush();
            window.Width = 850; window.Height = 600; Flush();
            var list = window.FindControl<ListBox>("ArtistTrackList")!;
            Check(list.Bounds.Height >= 80, "small artist page keeps a usable song list");
            Capture(window, "sway-artist-small.png");

            // Playback resolution deliberately rejects fixture video IDs. Verify the queue and selected
            // start before opening audio, without a network request or a real sound device.
            var songs = model.Tracks.Select(t => t.Track).ToArray();
            var button = window.FindControl<Button>("ArtistShuffleButton")!;
            Click(window, button);
            try { Await(((AsyncCommand)model.ShuffleCollectionCommand).RunningTask); } catch (ArgumentException) { }
            Check(model.Player.Queue.SequenceEqual(songs) && model.Player.CurrentTrack == model.SelectedTrack?.Track, "collection shuffle queues every displayed song and plays the randomly selected entry");
            Check(!model.Shuffle, "random-start action preserves the existing player shuffle mode");
            model.Tracks.Clear(); Await(model.ShuffleCollectionAsync());

            Await(model.OpenArtistAsync(new("Fixture Artist")));
            Check(handler.SearchCount == 1 && model.IsArtistPage, "legacy name-only songs can open an artist page");
            handler.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = model.OpenArtistAsync(new("Guest Artist", "UC_guest"));
            model.SettingsCommand.Execute(null); handler.Hold.SetResult();
            try { Await(pending); } catch (OperationCanceledException) { }
            Check(model.ShowSettings && model.ArtistArtwork is null && model.ArtistCatalog.Count == 0, "late artist loads cannot replace the next page or retain old artwork");
            Console.WriteLine("PASS clickable artist names, detailed artist page, small layout, random-start collection queue, legacy lookup and stale navigation");
        }
        finally { Await(model.DisposeAsync().AsTask()); window.Close(); Flush(); }
    }
    private static void Wait(Func<bool> condition)
    {
        var time = Stopwatch.StartNew();
        while (!condition()) { if (time.Elapsed.TotalSeconds > 15) throw new TimeoutException("Artist UI did not complete"); Flush(); Thread.Sleep(1); }
        Flush();
    }
    private static void Click(Window window, Control control, MouseButton button = MouseButton.Left)
    {
        control.BringIntoView(); Flush();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point, RawInputModifiers.None); window.MouseDown(point, button, RawInputModifiers.None); window.MouseUp(point, button, RawInputModifiers.None); Flush();
    }
    private static void Capture(Window window, string name)
    {
        using var frame = window.CaptureRenderedFrame();
        string path = Path.GetFullPath(Path.Combine("docs", name));
        try { frame?.Save(path, PngBitmapEncoderOptions.Default); }
        catch (IOException) when (File.Exists(path)) { Console.WriteLine("Optional artist preview is open; retaining its previous capture."); }
    }
}
