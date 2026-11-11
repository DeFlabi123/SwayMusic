using System.Net;
using System.Text.Json;
using SwayMusic.Core;
using SwayMusic.Core.Networking;

namespace SwayMusic.Tests;

internal static class NativeResolverTests
{
    private const string Player = """
        {"playabilityStatus":{"status":"OK"},"streamingData":{"expiresInSeconds":"3600","adaptiveFormats":[{"itag":140,"mimeType":"audio/mp4; codecs=\"mp4a.40.2\"","bitrate":128000,"url":"https://rr1.googlevideo.com/videoplayback?expire=4102444800"}]}}
        """;
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "sway-native-resolver-" + Guid.NewGuid().ToString("N"));
        var publicHandler = new Handler(async (request, token) =>
        {
            Check(!request.Headers.Contains("Cookie") && !request.Headers.Contains("Authorization"), "public bootstrap/client must not receive account credentials");
            if (request.Method == HttpMethod.Get) return Text("<script>ytcfg.set({\"VISITOR_DATA\":\"public-visitor\"});</script>");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Check(body.RootElement.GetProperty("context").GetProperty("client").GetProperty("clientName").GetString() == "VISIONOS", "source client profile");
            Check(body.RootElement.GetProperty("context").GetProperty("client").GetProperty("visitorData").GetString() == "public-visitor", "shared public configuration");
            Check(!body.RootElement.GetProperty("context").GetProperty("user").TryGetProperty("onBehalfOfUser", out _), "no delegated account in anonymous client");
            return Text(Player);
        });
        var transportHandler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Post ? Text(Player)
            : new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1]) { Headers = { ContentType = new("audio/mp4") } } }));
        using var publicHttp = new HttpClient(publicHandler); using var transport = new HttpClient(transportHandler);
        try
        {
            using (var client = new YouTubeMusicClient(transport, publicHttp) { EnableNativePlayback = true, PlaybackCacheDirectory = root, SessionCookie = "SAPISID=never-public" })
            {
                await Task.WhenAll(client.PrewarmPlaybackAsync(), client.PrewarmPlaybackAsync());
                Check(publicHandler.Gets == 1, "prewarm is single-flight");
                await client.GetStreamAsync(new Track("abcdefghijk", "Fixture", "Fixture"));
                await client.GetStreamAsync(new Track("bcdefghijkl", "Fixture", "Fixture"));
                Check(publicHandler.Gets == 1 && publicHandler.Posts == 2 && client.LastResolutionPath == "native-visionos-visitor", "different new songs reuse bootstrap without external processes");
                client.InvalidateStream("abcdefghijk");
                await client.GetStreamAsync(new Track("abcdefghijk", "Fixture", "Fixture"));
                Check(publicHandler.Posts == 2 && client.LastResolutionPath == "web-or-helper", "later transport rejection quarantines the native client and falls back");
            }
            using (var reopened = new YouTubeMusicClient(transport, publicHttp) { EnableNativePlayback = true, PlaybackCacheDirectory = root })
            {
                await reopened.PrewarmPlaybackAsync();
                Check(publicHandler.Gets == 1, "restart restores the bounded public configuration cache");
            }
            await File.WriteAllTextAsync(Path.Combine(root, "public-bootstrap.json"), "{broken}");
            using (var corrupt = new YouTubeMusicClient(transport, publicHttp) { EnableNativePlayback = true, PlaybackCacheDirectory = root })
            {
                await corrupt.PrewarmPlaybackAsync(); Check(publicHandler.Gets == 2, "corrupt configuration is refetched");
            }
            var blocking = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Text(""); });
            using var unavailableHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Text("<html>No visitor configuration</html>"))));
            using (var unavailable = new YouTubeMusicClient(transport, unavailableHttp) { EnableNativePlayback = true })
            {
                await unavailable.GetStreamAsync(new Track("defghijklmn", "Fixture", "Fixture"));
                Check(unavailable.LastResolutionPath == "web-or-helper", "missing bootstrap falls back without blocking playback");
            }
            using var blockingHttp = new HttpClient(blocking);
            using var cancelled = new YouTubeMusicClient(transport, blockingHttp) { EnableNativePlayback = true };
            using var cancellation = new CancellationTokenSource();
            var pending = cancelled.GetStreamAsync(new Track("cdefghijklm", "Fixture", "Fixture"), cancellation.Token);
            cancellation.Cancel();
            try { await pending; throw new InvalidOperationException("Cancelled native extraction continued."); } catch (OperationCanceledException) { }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS native extraction profile, public-only metadata, single-flight prewarm, persistent bootstrap, fallback, corruption and cancellation");
    }
    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Native resolver: " + message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        public int Gets, Posts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { if (request.Method == HttpMethod.Get) Interlocked.Increment(ref Gets); else Interlocked.Increment(ref Posts); return action(request, ct); }
    }
}
