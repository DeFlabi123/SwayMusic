using System.Net;
using System.Text;
using System.Text.Json;
using Metrolist.Core;
using Metrolist.Core.Downloads;
using Metrolist.Core.Networking;
using Metrolist.Desktop.Playback;

namespace Metrolist.Tests;

public static class NetworkTests
{
    public static async Task RunAsync()
    {
        const string fixture = """
        {"contents":{"sectionListRenderer":{"contents":[{"musicShelfRenderer":{"contents":[
          {"musicResponsiveListItemRenderer":{"playlistItemData":{"videoId":"abcdefghijk"},"flexColumns":[
            {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"A song"}]}}},
            {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[
              {"text":"An artist","navigationEndpoint":{"browseEndpoint":{"browseId":"UC_artist","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ARTIST"}}}}},
              {"text":" • "},{"text":"An album","navigationEndpoint":{"browseEndpoint":{"browseId":"MPRE_album","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ALBUM"}}}}},
              {"text":" • "},{"text":"3:42"}]}}}],"thumbnail":{"musicThumbnailRenderer":{"thumbnail":{"thumbnails":[{"url":"https://lh3.googleusercontent.com/a","width":120}]}}}}},
          {"musicResponsiveListItemRenderer":{"navigationEndpoint":{"watchEndpoint":{"videoId":"abcdefghijk"}},"flexColumns":[{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"duplicate"}]}}}]}}
        ],"continuations":[{"nextContinuationData":{"continuation":"next_page"}}]}},
        {"musicTwoRowItemRenderer":{"title":{"runs":[{"text":"A playlist"}]},"subtitle":{"runs":[{"text":"Curator"}]},"navigationEndpoint":{"browseEndpoint":{"browseId":"VLplaylist"}}}}
        ]}}}
        """;
        using var document = JsonDocument.Parse(fixture);
        var page = YouTubeMusicClient.ParsePage(document.RootElement);
        Check(page.Tracks.Count == 1 && page.Tracks[0].Title == "A song", "deduplicate search renderers by video ID");
        Check(YouTubeMusicClient.ParsePage(document.RootElement, deduplicateTracks: false).Tracks.Count == 2, "playlist parsing preserves repeated song entries");
        Check(page.Tracks[0].Artist == "An artist" && page.Tracks[0].Album == "An album", "artist and album endpoint metadata");
        Check(page.Tracks[0].Duration == TimeSpan.FromSeconds(222), "track duration");
        Check(page.Tracks[0].ThumbnailUrl == "https://lh3.googleusercontent.com/a", "nested thumbnail renderer");
        Check(page.Continuation == "next_page", "continuation extraction");
        Check(page.Items is { Count: 1 } && page.Items[0].Id == "VLplaylist", "browse item extraction");
        Check(YouTubeMusicClient.ParseDuration("1:02:03") == TimeSpan.FromSeconds(3723), "hour duration");
        Check(YouTubeMusicClient.ParseDuration("4.2M views") is null, "ignore non-duration metadata");

        using var streamDoc = JsonDocument.Parse("""
        {"streamingData":{"expiresInSeconds":"3600","adaptiveFormats":[
          {"mimeType":"audio/webm","bitrate":160000,"url":"https://r1.googlevideo.com/opus"},
          {"mimeType":"audio/mp4; codecs=\"mp4a.40.2\"","bitrate":128000,"url":"https://r1.googlevideo.com/aac"},
          {"mimeType":"audio/mp4","bitrate":256000,"url":"http://127.0.0.1/private"},
          {"mimeType":"audio/mp4","bitrate":256000,"url":"https://googlevideo.com.attacker.example/aac"}
        ]}}
        """);
        var stream = YouTubeMusicClient.ParseStream(streamDoc.RootElement);
        Check(stream is { Bitrate: 128000 } && stream.Url.AbsolutePath == "/aac", "AAC selection and hostile media URL rejection");
        using var protectedDoc = JsonDocument.Parse("""{"streamingData":{"adaptiveFormats":[{"mimeType":"audio/mp4","bitrate":128000,"signatureCipher":"s=protected"}]}}""");
        Check(YouTubeMusicClient.ParseStream(protectedDoc.RootElement) is null, "ciphered formats require real resolver");

        var handler = new RecordingHandler(fixture);
        using var http = new HttpClient(handler);
        var client = new YouTubeMusicClient(http) { Country = "CH", Language = "de", SessionCookie = "SAPISID=test-session; SID=test" };
        var result = await client.SearchAsync("a query");
        Check(result.Tracks.Count == 1, "HTTP search response");
        Check(handler.LastUri?.Host == "music.youtube.com" && handler.LastUri.AbsolutePath.EndsWith("/search"), "source search endpoint");
        Check(handler.Authorization?.StartsWith("SAPISIDHASH ", StringComparison.Ordinal) == true, "authenticated SAPISID header");
        using var requestDoc = JsonDocument.Parse(handler.LastBody!);
        var clientContext = requestDoc.RootElement.GetProperty("context").GetProperty("client");
        Check(clientContext.GetProperty("gl").GetString() == "CH" && clientContext.GetProperty("hl").GetString() == "de", "locale context");
        Check(requestDoc.RootElement.GetProperty("params").GetString() == "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D", "original song filter");
        await client.SearchContinuationAsync("test_continuation");
        Check(handler.LastBody!.Contains("test_continuation", StringComparison.Ordinal), "continuation request");

        try { client.SessionCookie = "cookie\r\ninjected"; throw new InvalidOperationException("header injection accepted"); }
        catch (ArgumentException) { }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await client.HomeAsync(cancellation.Token); throw new InvalidOperationException("cancelled request accepted"); }
        catch (OperationCanceledException) { }

        using var retryHttp = new HttpClient(new RecordingHandler(fixture, failures: 1));
        Check((await new YouTubeMusicClient(retryHttp).SearchAsync("retry")).Tracks.Count == 1, "bounded transient retry");

        var streamHandler = new StreamHandler();
        using var streamHttp = new HttpClient(streamHandler);
        var streamingClient = new YouTubeMusicClient(streamHttp);
        var song = new Track("abcdefghijk", "fixture", "fixture");
        await Task.WhenAll(streamingClient.GetStreamAsync(song), streamingClient.GetStreamAsync(song));
        Check(streamHandler.PlayerRequests == 1, "duplicate stream resolutions coalesced and cached");
        streamingClient.SessionCookie = "SAPISID=new-account";
        await streamingClient.GetStreamAsync(song);
        Check(streamHandler.PlayerRequests == 2, "account change invalidates signed stream cache");

        var rangeHandler = new RangeHandler();
        using var rangeHttp = new HttpClient(rangeHandler);
        await using var rangeStream = await HttpRangeStream.OpenAsync(rangeHttp, new Uri("https://r1.googlevideo.com/range"));
        Check(rangeStream.Length == RangeHandler.TotalLength, "compressed audio range length");
        var readBuffer = new byte[16];
        Check(rangeStream.Read(readBuffer) == 16 && readBuffer[1] == 1, "first compressed audio range");
        rangeStream.Seek(700003, SeekOrigin.Begin);
        Check(rangeStream.Read(readBuffer) == 16 && readBuffer[0] == 700003 % 251, "forward byte seek");
        rangeStream.Seek(2, SeekOrigin.Begin);
        Check(rangeStream.Read(readBuffer) == 16 && readBuffer[0] == 2, "backward byte seek");
        rangeStream.Seek(0, SeekOrigin.End);
        Check(rangeStream.Read(readBuffer) == 0, "bounded stream EOF");
        Check(rangeHandler.Requests == 3 && rangeHandler.MaximumRequestedBytes <= 256 * 1024, "bounded on-demand compressed audio network requests");
        rangeStream.Seek(100, SeekOrigin.Begin);
        var crossing = new byte[300 * 1024];
        Check(rangeStream.Read(crossing) == crossing.Length && crossing[^1] == (100 + crossing.Length - 1) % 251, "native read spans multiple network chunks without premature EOF");
        var playbackCursor = rangeStream.Position;
        var metadata = await rangeStream.ReadRangeAsync(800000, 4096);
        Check(metadata.Length == 4096 && metadata[0] == 800000 % 251 && metadata[^1] == 804095 % 251, "sparse container metadata preserves exact source bytes");
        Check(rangeHandler.LastRequestedBytes == 4096 && rangeStream.Position == playbackCursor, "metadata requests are sparse and preserve playback cursor");
        var requestsBeforeRead = rangeHandler.Requests;
        Check(rangeStream.Read(readBuffer) == readBuffer.Length && rangeHandler.Requests == requestsBeforeRead, "metadata reads preserve the current compressed payload cache");

        using var documentHttp = new HttpClient(new StaticHandler(() => new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"error\":\"not audio\"}", Encoding.UTF8, "application/json")
        }));
        await ExpectAsync<InvalidDataException>(() => HttpRangeStream.OpenAsync(documentHttp, new("https://r1.googlevideo.com/document")));
        using var truncatedHttp = new HttpClient(new StaticHandler(() =>
        {
            var content = new ByteArrayContent(new byte[128]);
            content.Headers.ContentRange = new(0, 1023, 2048);
            content.Headers.ContentType = new("audio/mp4");
            return new(HttpStatusCode.PartialContent) { Content = content };
        }));
        await ExpectAsync<EndOfStreamException>(() => HttpRangeStream.OpenAsync(truncatedHttp, new("https://r1.googlevideo.com/truncated")));
    }

    public static async Task LiveAsync(string toolsDirectory)
    {
        using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MetrolistWindows/1.0");
        var client = new YouTubeMusicClient(http) { ExtractorPath = Path.Combine(toolsDirectory, "yt-dlp.exe") };
        var page = await client.SearchAsync("Bach cello suite");
        Check(page.Tracks.Count > 0, "live music search");
        Console.WriteLine($"LIVE search returned {page.Tracks.Count} tracks; first: {page.Tracks[0].Title}");
        if (page.Continuation is { } continuation)
        {
            var next = await client.SearchContinuationAsync(continuation);
            Check(next.Tracks.Count > 0, "live search continuation");
            Console.WriteLine($"LIVE search continuation returned {next.Tracks.Count} tracks.");
        }
        var home = await client.HomeAsync();
        Check(home.Tracks.Count > 0 || home.Items is { Count: > 0 }, "live home recommendations");
        Console.WriteLine($"LIVE home returned {home.Tracks.Count} tracks and {home.Items?.Count ?? 0} browse items.");
        var browseItem = home.Items?.FirstOrDefault(item => item.Id.StartsWith("VL", StringComparison.Ordinal) || item.Id.StartsWith("MPRE", StringComparison.Ordinal));
        if (browseItem is not null)
        {
            var playlist = await client.BrowseAsync(browseItem.Id);
            Check(playlist.Tracks.Count > 0, "live playlist or album browse");
            Console.WriteLine($"LIVE browse returned {playlist.Tracks.Count} tracks.");
        }
        var radio = await client.RadioAsync(page.Tracks[0]);
        Check(radio.Tracks.Count > 0, "live song radio");
        Console.WriteLine($"LIVE radio returned {radio.Tracks.Count} tracks.");
        var playbackTrack = page.Tracks.FirstOrDefault(t => t.Duration is { } d && d > TimeSpan.FromMinutes(1) && d < TimeSpan.FromMinutes(15)) ?? page.Tracks[0];
        Console.WriteLine($"LIVE native playback target: {playbackTrack.Title} ({playbackTrack.Duration}).");
        var stream = await client.GetStreamAsync(playbackTrack);
        Console.WriteLine($"LIVE resolved validated {stream.MimeType} stream; no signed URL logged.");
        HttpRangeStream? audioTransport = null;
        Exception? playbackError = null;
        await using var player = new PlaybackService((_, _) => Task.FromResult(stream.Url.AbsoluteUri), async (url, ct) => audioTransport = await HttpRangeStream.OpenAsync(http, url, ct)) { Volume = 0 };
        player.PlaybackFailed += (_, error) => playbackError = error;
        await player.PlayTrackAsync(playbackTrack);
        Console.WriteLine($"LIVE native opened: state={player.State}, duration={player.Duration.TotalSeconds:F2}s, ranges={audioTransport?.RangeRequests}, compressedFetched={audioTransport?.BytesFetched}, consumed={audioTransport?.BytesRead}, sourceLength={audioTransport?.Length}.");
        for (var retry = 0; retry < 20 && player.Position == TimeSpan.Zero && player.State == PlayerState.Playing; retry++) await Task.Delay(500);
        Console.WriteLine($"LIVE native diagnostics: state={player.State}, position={player.Position.TotalSeconds:F2}s, duration={player.Duration.TotalSeconds:F2}s, ranges={audioTransport?.RangeRequests}, compressedFetched={audioTransport?.BytesFetched}, consumed={audioTransport?.BytesRead}.");
        if (playbackError is not null) throw new InvalidOperationException("Live native playback failed.", playbackError);
        if (player.State != PlayerState.Playing || player.Position == TimeSpan.Zero)
            await DiagnoseDownloadedAacAsync(http, stream.Url, playbackTrack);
        Check(player.State == PlayerState.Playing && player.Position > TimeSpan.Zero, "live AAC audio decoding and WASAPI clock");
        await Task.Delay(TimeSpan.FromSeconds(25));
        Console.WriteLine($"LIVE sustained diagnostics: state={player.State}, position={player.Position.TotalSeconds:F2}s, ranges={audioTransport?.RangeRequests}, compressedFetched={audioTransport?.BytesFetched}, consumed={audioTransport?.BytesRead}.");
        if (playbackError is not null) throw new InvalidOperationException("Live sustained playback failed.", playbackError);
        Check(player.State == PlayerState.Playing && player.Position >= TimeSpan.FromSeconds(20), "live sustained playback across compressed audio chunks");
        player.Pause();
        Check(player.State == PlayerState.Paused, "live stream pause");
        await Task.Delay(500);
        var pausedBytes = audioTransport?.BytesFetched;
        await Task.Delay(TimeSpan.FromSeconds(2));
        Check(audioTransport?.BytesFetched == pausedBytes, "paused streaming performs no further upstream reads");
        Console.WriteLine($"LIVE paused transport remained idle: compressedFetched={pausedBytes}.");
        await player.SeekAsync(TimeSpan.FromSeconds(10));
        player.Play();
        await Task.Delay(TimeSpan.FromSeconds(1));
        Check(player.State == PlayerState.Playing, "live stream seek/resume");
        await player.StopAsync();
        Console.WriteLine("LIVE AAC stream decoded and played muted through Media Foundation/WASAPI for25s; pause, seek, resume, stop passed.");
        await VerifyOfflineDownloadAsync(http, stream.Url, playbackTrack);
    }

    private static async Task VerifyOfflineDownloadAsync(HttpClient http, Uri url, Track track)
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "live-download-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            using var downloads = new DownloadService(http, directory, 16 * 1024 * 1024);
            var entry = await downloads.DownloadAsync(track, url);
            Check(entry.Bytes > 0 && entry.Bytes <= 16 * 1024 * 1024, "bounded actual AAC download");
            Check(downloads.FindDownloaded(track.Id) == entry.FilePath && new FileInfo(entry.FilePath).Length == entry.Bytes,
                "actual AAC download manifest and local media commit");
            await using var offline = new PlaybackService((_, _) => throw new InvalidOperationException("Offline playback attempted network resolution.")) { Volume = 0 };
            Exception? playbackError = null;
            offline.PlaybackFailed += (_, error) => playbackError = error;
            await offline.PlayTrackAsync(track with { LocalPath = entry.FilePath });
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (playbackError is not null) throw new InvalidOperationException("Downloaded AAC playback failed.", playbackError);
            Check(offline.State == PlayerState.Playing && offline.Position > TimeSpan.FromSeconds(2), "downloaded AAC decoded offline");
            offline.Pause();
            await offline.SeekAsync(TimeSpan.FromSeconds(10));
            offline.Play();
            await Task.Delay(500);
            Check(offline.State == PlayerState.Playing && offline.Position.TotalSeconds >= 10, "downloaded AAC seek and resume offline");
            await offline.StopAsync();
            Console.WriteLine($"LIVE downloaded {entry.Bytes} AAC bytes through bounded ranges; offline native decode, pause, seek, resume, stop passed without network resolution.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException("Network: " + name); }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Network: expected " + typeof(T).Name);
    }

    private sealed class StaticHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response());
        }
    }

    private static async Task DiagnoseDownloadedAacAsync(HttpClient http, Uri url, Track track)
    {
        const long maximum = 8 * 1024 * 1024;
        var path = Path.Combine(Path.GetTempPath(), "metrolist-youtube-aac-" + Guid.NewGuid().ToString("N") + ".m4a");
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximum) { Console.WriteLine("DIAGNOSTIC local AAC skipped: source exceeds8MiB cap."); return; }
            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous))
            {
                var buffer = new byte[16384];
                long written = 0;
                int count;
                while ((count = await input.ReadAsync(buffer)) > 0)
                {
                    written += count;
                    if (written > maximum) throw new InvalidOperationException("Diagnostic AAC exceeded8MiB cap.");
                    await output.WriteAsync(buffer.AsMemory(0, count));
                }
            }
            await using var local = new PlaybackService((_, _) => Task.FromResult(path)) { Volume = 0 };
            Exception? error = null;
            local.PlaybackFailed += (_, value) => error = value;
            await local.PlayTrackAsync(track with { LocalPath = path });
            await Task.Delay(TimeSpan.FromSeconds(3));
            Console.WriteLine($"DIAGNOSTIC SAME AAC LOCAL FILE: bytes={new FileInfo(path).Length}, state={local.State}, position={local.Position.TotalSeconds:F2}s, duration={local.Duration.TotalSeconds:F2}s, error={error?.GetType().Name ?? "none"}.");
            await local.StopAsync();
            var diagnostic = Path.GetFullPath(Path.Combine("artifacts", "diagnostics", "youtube-aac.m4a"));
            Directory.CreateDirectory(Path.GetDirectoryName(diagnostic)!);
            File.Copy(path, diagnostic, overwrite: true);
            Console.WriteLine("DIAGNOSTIC bounded AAC retained for container inspection: " + diagnostic);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class RecordingHandler(string fixture, int failures = 0) : HttpMessageHandler
    {
        private int _remainingFailures = failures;
        public Uri? LastUri { get; private set; }
        public string? LastBody { get; private set; }
        public string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.TryGetValues("Authorization", out var values) ? values.FirstOrDefault() : null;
            if (_remainingFailures-- > 0) return new(HttpStatusCode.ServiceUnavailable);
            return new(HttpStatusCode.OK) { Content = new StringContent(fixture, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StreamHandler : HttpMessageHandler
    {
        public int PlayerRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                PlayerRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"streamingData":{"expiresInSeconds":"3600","adaptiveFormats":[{"mimeType":"audio/mp4","bitrate":128000,"url":"https://r1.googlevideo.com/aac"}]}}""", Encoding.UTF8, "application/json")
                });
            }
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new("audio/mp4");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }
    }

    private sealed class RangeHandler : HttpMessageHandler
    {
        public const int TotalLength = 1024 * 1024;
        public int Requests { get; private set; }
        public long MaximumRequestedBytes { get; private set; }
        public long LastRequestedBytes { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests++;
            var range = request.Headers.Range!.Ranges.Single();
            var start = range.From!.Value;
            var end = Math.Min(range.To!.Value, TotalLength - 1);
            MaximumRequestedBytes = Math.Max(MaximumRequestedBytes, end - start + 1);
            LastRequestedBytes = end - start + 1;
            var bytes = new byte[checked((int)(end - start + 1))];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((start + i) % 251);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new("audio/mp4");
            content.Headers.ContentRange = new(start, end, TotalLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }
    }
}
