using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SwayMusic.Core.Storage;

namespace SwayMusic.Core.Networking;

public sealed partial class YouTubeMusicClient
{
    // Adapted from InnerTubeX v0.11.2 (GPL-3.0), visitor-backed VISIONOS_0_1 direct audio path.
    // Its native request carries neither account cookies nor a signature timestamp.
    private const string NativeUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15";
    private static readonly Regex VisitorPattern = new("\"(?:VISITOR_DATA|visitorData)\"\\s*:\\s*\"([^\"\\\\]+)\"", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly SemaphoreSlim _bootstrapGate = new(1, 1);
    private readonly Lazy<HttpClient> _publicPlayback = new(() => publicPlaybackHttp ?? new HttpClient(new SocketsHttpHandler
    {
        UseCookies = false, AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(10), MaxConnectionsPerServer = 2
    }) { Timeout = TimeSpan.FromSeconds(8) });
    private PlaybackBootstrap? _bootstrap;
    private DateTimeOffset _nativeRetryAt;
    private bool _bootstrapDiskChecked;
    private readonly HashSet<string> _nativeVideos = new(StringComparer.Ordinal);
    public bool EnableNativePlayback { get; set; }
    public string? PlaybackCacheDirectory { get; set; }
    public string LastResolutionPath { get; private set; } = "none";

    private sealed record PlaybackBootstrap(int Version, string Visitor, DateTimeOffset SavedAt);
    private static bool Fresh(PlaybackBootstrap? value) => value is { Version: 1, Visitor.Length: > 0 and <= 8192 }
        && !value.Visitor.Any(char.IsControl) && value.SavedAt <= DateTimeOffset.UtcNow && value.SavedAt > DateTimeOffset.UtcNow.AddHours(-3);

    public async Task PrewarmPlaybackAsync(CancellationToken ct = default)
    {
        if (!EnableNativePlayback) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        try { await GetPlaybackBootstrapAsync(budget.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or RegexMatchTimeoutException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private async Task<PlaybackBootstrap> GetPlaybackBootstrapAsync(CancellationToken ct)
    {
        await _bootstrapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Fresh(_bootstrap)) return _bootstrap!;
            string? path = PlaybackCacheDirectory is { } directory ? Path.Combine(directory, "public-bootstrap.json") : null;
            if (!_bootstrapDiskChecked)
            {
                _bootstrapDiskChecked = true;
                try
                {
                    if (path is not null && File.Exists(path) && new FileInfo(path).Length <= 32768)
                        _bootstrap = await AtomicFile.ReadJsonAsync<PlaybackBootstrap>(path, ct).ConfigureAwait(false);
                    if (Fresh(_bootstrap)) return _bootstrap!;
                }
                catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/watch?v=jNQXAC9IVRw&bpctr=9999999999&has_verified=1");
            request.Headers.UserAgent.ParseAdd(WebUserAgent);
            using var response = await _publicPlayback.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var html = new StringBuilder(); var buffer = new char[8192];
            while (html.Length < 256 * 1024)
            {
                int count = await reader.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (count == 0) break;
                html.Append(buffer, 0, count);
                var match = VisitorPattern.Match(html.ToString());
                if (!match.Success) continue;
                var bootstrap = new PlaybackBootstrap(1, match.Groups[1].Value, DateTimeOffset.UtcNow);
                if (!Fresh(bootstrap)) break;
                _bootstrap = bootstrap;
                if (path is not null)
                {
                    try { await AtomicFile.WriteJsonAsync(path, bootstrap, ct).ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                return bootstrap;
            }
            throw new InvalidDataException("YouTube did not supply public playback configuration.");
        }
        finally { _bootstrapGate.Release(); }
    }

    private async Task<StreamInfo?> TryNativePlaybackAsync(string id, CancellationToken ct)
    {
        if (_nativeRetryAt > DateTimeOffset.UtcNow) return null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var config = await GetPlaybackBootstrapAsync(budget.Token).ConfigureAwait(false);
            var body = new JsonObject
            {
                ["context"] = new JsonObject
                {
                    ["client"] = new JsonObject
                    {
                        ["clientName"] = "VISIONOS", ["clientVersion"] = "0.1", ["osName"] = "VISION_OS", ["osVersion"] = "1.3",
                        ["deviceMake"] = "Apple", ["deviceModel"] = "RealityDevice14,1", ["platform"] = "MOBILE",
                        ["visitorData"] = config.Visitor, ["hl"] = Language, ["gl"] = Country
                    },
                    ["user"] = new JsonObject { ["lockedSafetyMode"] = false }
                },
                ["videoId"] = id, ["contentCheckOk"] = true, ["racyCheckOk"] = true
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, Origin + "/youtubei/v1/player?prettyPrint=false");
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            request.Headers.UserAgent.ParseAdd(NativeUserAgent);
            request.Headers.Add("X-YouTube-Client-Name", "101"); request.Headers.Add("X-YouTube-Client-Version", "0.1");
            request.Headers.Add("X-Goog-Api-Format-Version", "1"); request.Headers.Add("X-Goog-Visitor-Id", config.Visitor);
            request.Headers.Add("Origin", Origin); request.Headers.Add("X-Origin", Origin); request.Headers.Referrer = new Uri(Origin + "/");
            using var response = await _publicPlayback.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var content = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            using var bounded = new LimitedReadStream(content, 4 * 1024 * 1024);
            using var document = await JsonDocument.ParseAsync(bounded, new JsonDocumentOptions { MaxDepth = 64 }, budget.Token).ConfigureAwait(false);
            if (String(At(document.RootElement, "playabilityStatus"), "status") != "OK") return null;
            var stream = ParseStream(document.RootElement);
            if (stream is null || stream.Url.Query.Split('&', '?').Any(p => p.StartsWith("n=", StringComparison.Ordinal))) return null;
            if (!await IsStreamAccessibleAsync(stream.Url, budget.Token).ConfigureAwait(false))
            {
                _bootstrap = null; _nativeRetryAt = DateTimeOffset.UtcNow.AddMinutes(2); return null;
            }
            LastResolutionPath = "native-visionos-visitor";
            lock (_cacheLock)
            {
                if (_nativeVideos.Count >= 24) _nativeVideos.Clear();
                _nativeVideos.Add(id);
            }
            return stream;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { _nativeRetryAt = DateTimeOffset.UtcNow.AddSeconds(30); return null; }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or RegexMatchTimeoutException)
        { _nativeRetryAt = DateTimeOffset.UtcNow.AddSeconds(30); return null; }
    }

    public void Dispose()
    {
        if (_publicPlayback.IsValueCreated && publicPlaybackHttp is null) _publicPlayback.Value.Dispose();
        _bootstrapGate.Dispose();
    }
}
