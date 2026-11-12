using System.Net;
using System.Text;
using System.Text.Json;
using SwayMusic.Core;
using SwayMusic.Core.Lyrics;
using SwayMusic.Core.Storage;
using SwayMusic.Desktop.ViewModels;
using static SwayMusic.Tests.HeadlessTests;

namespace SwayMusic.Tests;

internal static class LyricsServiceTests
{
    public static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sway-lyrics-schema-" + Guid.NewGuid().ToString("N"));
        try
        {
            var target = new Track("lyrics-schema", "Song", "Artist", Duration: TimeSpan.FromSeconds(180));
            const string mixed = """
            [null, 5, {"trackName":null,"artistName":"Artist","duration":180},
             {"trackName":"Other song","artistName":"Other artist","duration":null,"syncedLyrics":"[00:01]wrong"},
             {"trackName":"Song","artistName":"Artist","duration":null,"plainLyrics":"unknown duration"},
             {"trackName":"Song","artistName":"Artist","duration":"184.0","syncedLyrics":"[00:01]matched"},
             {"trackName":"Song","artistName":"Artist","duration":{"bad":true},"syncedLyrics":false}]
            """;
            using (var http = new HttpClient(new Handler(_ => mixed)))
            using (var service = new LyricsService(http, Path.Combine(directory, "mixed")))
                Check((await service.GetAsync(target))?.Lines.Single().Text == "matched", "one invalid entry cannot discard valid lyrics and numeric duration strings are accepted");

            foreach (string duration in new[] { "null", "\"180\"", "\"unknown\"", "\"NaN\"", "-1", "false" })
            {
                string json = "[{\"trackName\":\"Other song\",\"artistName\":\"Other artist\",\"duration\":null,\"syncedLyrics\":\"[00:01]wrong\"},{\"trackName\":\"Song\",\"artistName\":\"Artist\",\"duration\":" + duration + ",\"syncedLyrics\":\"[00:01]correct\"}]";
                using var http = new HttpClient(new Handler(_ => json));
                using var service = new LyricsService(http, Path.Combine(directory, Guid.NewGuid().ToString("N")));
                Check((await service.GetAsync(target))?.Lines.Single().Text == "correct", "missing/unusable duration uses a strong metadata match instead of failing or selecting an unrelated song");
            }
            using (var http = new HttpClient(new Handler(_ => """[{"trackName":"Song","artistName":"Artist","duration":230,"syncedLyrics":"[00:01]wrong version"}]""")))
            using (var service = new LyricsService(http, Path.Combine(directory, "mismatch")))
                Check(await service.GetAsync(target) is null, "a known duration mismatch is not treated as missing metadata");

            var retry = new Handler(call => call == 1 ? "not json" : """[{"trackName":"Song","artistName":"Artist","duration":180,"syncedLyrics":"[00:01]recovered"}]""");
            using (var http = new HttpClient(retry))
            using (var service = new LyricsService(http, Path.Combine(directory, "retry")))
            {
                try { await service.GetAsync(target); throw new Exception("Malformed JSON was accepted"); } catch (JsonException) { }
                Check((await service.GetAsync(target))?.Lines.Single().Text == "recovered" && retry.Requests == 2, "a failed response is not cached as a six-hour lyrics miss");
            }
            Console.WriteLine("PASS lyrics response schema: nullable/string/invalid durations, per-entry resilience, safe matching and retry after malformed JSON");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    public static void Ui(string directory)
    {
        var track = new Track("lyrics-error-fixture", "Song", "Artist");
        Await(AtomicFile.WriteJsonAsync(Path.Combine(directory, "library.json"), new LibraryState { Session = new() { Queue = [track], CurrentIndex = 0 } }));
        var model = new MainViewModel(directory, httpHandler: new Handler(_ => "not json"));
        try
        {
            Await(model.InitializeAsync()); model.LyricsCommand.Execute(null); Await(((AsyncCommand)model.LyricsCommand).RunningTask);
            Check(model.LyricsText == "Lyrics are temporarily unavailable. Try opening lyrics again shortly.", "provider failures show a friendly message instead of JSON/class diagnostics");
        }
        finally { Await(model.DisposeAsync().AsTask()); }
        Console.WriteLine("PASS lyrics failure message does not expose JSON diagnostics");
    }

    public static async Task LiveAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sway-lyrics-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false });
            using var service = new LyricsService(http, directory);
            var lyrics = await service.GetAsync(new("lyrics-live-save", "Save That Shit", "Lil Peep"));
            Check(lyrics is { IsSynced: true }, "the public song with a null-duration search result loads its synchronized lyrics");
            Console.WriteLine($"PASS live lyrics lookup with nullable provider metadata: {lyrics!.Lines.Count} synced lines loaded (lyrics content omitted)");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
    private sealed class Handler(Func<int, string> reply) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(++Requests), Encoding.UTF8, "application/json") });
        }
    }
}
