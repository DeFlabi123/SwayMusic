using System.Diagnostics;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using SwayMusic.Core;
using SwayMusic.Core.Networking;
using SwayMusic.Core.Storage;
using SwayMusic.Desktop;
using SwayMusic.Desktop.Controls;
using SwayMusic.Desktop.Playback;
using SwayMusic.Desktop.ViewModels;
using static SwayMusic.Tests.HeadlessTests;

namespace SwayMusic.Tests;

internal static class ArtworkTests
{
    public static void Run(string directory)
    {
        var track = new Track("abcdefghijk", "Restored song", "Artwork fixture");
        var state = new LibraryState { History = [new(track, DateTimeOffset.UtcNow)], Session = new() { Queue = [track], CurrentIndex = 0 } };
        Await(AtomicFile.WriteJsonAsync(Path.Combine(directory, "library.json"), state));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Images { Hold = held.Task };
        var model = new MainViewModel(directory, httpHandler: handler);
        var window = new MainWindow(model, integrateSystem: false);
        try
        {
            window.Show(); Await(model.InitializeAsync());
            Check(model.IsReady && model.Artwork is null && model.HomeCards.Count == 1, $"pending artwork does not block restored startup (ready={model.IsReady}, artwork={model.Artwork is not null}, cards={model.HomeCards.Count}, status={model.Status})");
            held.SetResult();
            Wait(() => model.Artwork is not null && model.HomeCards[0].Artwork is not null);
            Check(model.Player.State == PlayerState.Stopped, "restored artwork loads without starting playback");
            Check(window.GetVisualDescendants().OfType<Image>().Count(image => ReferenceEquals(image.Source, model.Artwork)) >= 2, "restored artwork reaches both player bindings");
            Check(handler.Urls.All(url => url == "https://i.ytimg.com/vi/abcdefghijk/hqdefault.jpg"), "missing stored URLs use the matching video thumbnail");
            var list = window.FindControl<ListBox>("TrackList")!;
            var queue = window.FindControl<ListBox>("QueueList")!;
            Wait(() => list.GetVisualDescendants().OfType<TrackCover>().Any(cover => cover.Source is not null)
                && queue.GetVisualDescendants().OfType<TrackCover>().Any(cover => cover.Source is not null));
            model.Tracks.Clear();
            for (int index = 0; index < 4096; index++) model.Tracks.Add(new(new Track(index.ToString("D11"), "Cover " + index, "Fixture")));
            Flush();
            var visible = list.GetVisualDescendants().OfType<TrackCover>().Where(cover => cover.IsEffectivelyVisible).ToArray();
            Check(visible.Length is > 0 and < 100, "cover controls remain virtualized in a 4096-song library");
            Wait(() => visible.Any(cover => cover.Source is not null));
            list.SelectedIndex = 3000; list.ScrollIntoView(3000); Flush();
            Wait(() => list.GetVisualDescendants().OfType<TrackCover>().Any(cover => cover.Track?.Id == "00000003000" && cover.Source is not null));
            Check(handler.Urls.Count < 100, "scrolling fetches covers for realized rows rather than the entire library");

            using var http = new HttpClient(new Images());
            using var fallback = new HomeCard(track with { ThumbnailUrl = "https://images.example/expired" });
            Await(fallback.LoadAsync(http, CancellationToken.None));
            Check(fallback.Artwork is not null, "an expired explicit URL falls back to the matching video image");
            using var malformed = new HomeCard(track with { ThumbnailUrl = "https://images.example/malformed" });
            Await(malformed.LoadAsync(http, CancellationToken.None));
            Check(malformed.Artwork is not null, "undecodable explicit images also use the fallback");
            using var oversized = new HomeCard(track with { ThumbnailUrl = "https://images.example/oversized" });
            Await(oversized.LoadAsync(http, CancellationToken.None));
            Check(oversized.Artwork is not null, "oversized explicit images are bounded before fallback");
            using var local = new HomeCard(new("local-file", "Local song", "Fixture"));
            Await(local.LoadAsync(http, CancellationToken.None)); Check(local.Artwork is null, "local files do not invent a remote cover");

            var pending = new Images { Hold = Task.Delay(Timeout.Infinite) };
            using var pendingHttp = new HttpClient(pending);
            using var closing = new HomeCard(track);
            var loading = closing.LoadAsync(pendingHttp, CancellationToken.None);
            closing.Dispose(); Await(loading);
            Check(closing.Artwork is null, "disposing a cover cancels pending image work");
            Console.WriteLine("PASS startup/history artwork, player/song/queue covers, 4096-row cover virtualization, image fallback and disposal cancellation");
        }
        finally { Await(model.DisposeAsync().AsTask()); window.Close(); Flush(); }
    }

    public static void Live()
    {
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All });
        foreach (var id in new[] { "2ixN7aGjuuk", "LbfGmHyzOJM", "WvV5TbJc9tQ" })
        {
            using var card = new HomeCard(new(id, "Public artwork check", ""));
            Await(card.LoadAsync(http, CancellationToken.None));
            Check(card.Artwork is { PixelSize.Width: 256 }, "public cover from the reported missing artwork decodes correctly");
        }
        using var music = new YouTubeMusicClient(http);
        var search = music.SearchAsync("Lil Peep OMFG"); Await(search);
        var song = search.Result.Tracks.First();
        using var fresh = new HomeCard(song); Await(fresh.LoadAsync(http, CancellationToken.None));
        Check(song.ThumbnailUrl is not null && fresh.Artwork is not null, "fresh search artwork parses and decodes");
        string directory = Path.Combine(Path.GetTempPath(), "SwayMusic-cover-preview-" + Guid.NewGuid().ToString("N"));
        var songs = new[] { new Track("2ixN7aGjuuk", "Star Shopping (Live In Ghent, Belgium - April 2017)", "Lil Peep"), new Track("LbfGmHyzOJM", "OMFG", "Lil Peep"), new Track("WvV5TbJc9tQ", "Save That Shit", "Lil Peep") };
        Await(AtomicFile.WriteJsonAsync(Path.Combine(directory, "library.json"), new LibraryState
        { History = songs.Select(t => new HistoryEntry(t, DateTimeOffset.UtcNow)).ToList(), Session = new() { Queue = songs.ToList(), CurrentIndex = 0 } }));
        var model = new MainViewModel(directory); var window = new MainWindow(model, integrateSystem: false);
        try
        {
            window.Show(); Await(model.InitializeAsync());
            Wait(() => window.FindControl<ListBox>("TrackList")!.GetVisualDescendants().OfType<TrackCover>().Count(cover => cover.Source is not null) == 3);
            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.GetFullPath("docs/sway-song-covers.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Check(model.Player.State == PlayerState.Stopped, "live row covers do not trigger playback");
            model.ArtistsCommand.Execute(null); Await(((AsyncCommand)model.ArtistsCommand).RunningTask);
            foreach (var name in new[] { "Lil Peep", "$uicideboy$" })
            {
                var result = music.SearchArtistsAsync(name); Await(result);
                var artist = result.Result.Items?.FirstOrDefault(item => item.Title.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new Exception("Public artist not found");
                Check(artist.ThumbnailUrl is not null, "public artist search includes its image URL");
                model.BrowseItems.Add(artist);
            }
            Flush();
            Wait(() => window.FindControl<ListBox>("BrowseList")!.GetVisualDescendants().OfType<TrackCover>().Count(cover => cover.Source is not null) == 2);
        }
        finally { Await(model.DisposeAsync().AsTask()); window.Close(); Flush(); Directory.Delete(directory, recursive: true); }
        Console.WriteLine("PASS live images: three previously missing stored-song covers plus fresh search artwork downloaded and decoded in Skia");
    }

    internal static void Wait(Func<bool> complete)
    {
        var time = Stopwatch.StartNew();
        while (!complete()) { if (time.Elapsed.TotalSeconds > 15) throw new TimeoutException("Artwork did not arrive"); Flush(); Thread.Sleep(1); }
        Flush();
    }

    private sealed class Images : HttpMessageHandler
    {
        public Task? Hold;
        public List<string> Urls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            if (Hold is not null) await Hold.WaitAsync(token);
            if (request.RequestUri.AbsolutePath == "/expired") return new(HttpStatusCode.NotFound);
            var content = new ByteArrayContent(request.RequestUri.AbsolutePath == "/malformed" ? [1, 2, 3] : File.ReadAllBytes("src/SwayMusic.Desktop/Assets/sway.png"));
            if (request.RequestUri.AbsolutePath == "/oversized") content.Headers.ContentLength = 3 * 1024 * 1024;
            return new(HttpStatusCode.OK) { Content = content };
        }
    }
}
