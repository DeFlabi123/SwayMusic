using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Metrolist.Core.Networking;

/// <summary>Desktop adaptation of Metrolist's InnerTube facade and renderer parsing.</summary>
public sealed class YouTubeMusicClient(HttpClient http)
{
    private const string Origin = "https://music.youtube.com";
    private const string SongFilter = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D";
    private string? _cookie;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (StreamInfo Stream, DateTimeOffset Until)> _streams = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _streamResolution = new(1, 1);
    private long _sessionGeneration;
    public string? SessionCookie
    {
        get => _cookie;
        set
        {
            if (value is not null && value.AsSpan().ContainsAny('\r', '\n')) throw new ArgumentException("Cookie must be a single HTTP header line.", nameof(value));
            var cookie = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            lock (_cacheLock)
            {
                if (cookie == _cookie) return;
                _cookie = cookie;
                _sessionGeneration++;
                _streams.Clear();
            }
        }
    }
    public string Country { get; set; } = "US";
    public string Language { get; set; } = "en";
    public string? VisitorData { get; set; }
    /// <summary>Optional path to a separately installed yt-dlp executable for ciphered streams.</summary>
    public string? ExtractorPath { get; set; }

    public void InvalidateStream(string trackId)
    {
        lock (_cacheLock) _streams.Remove(trackId);
    }

    public async Task<MusicPage> SearchAsync(string query, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        using var response = await PostAsync("search", new() { ["query"] = query.Trim(), ["params"] = SongFilter }, ct).ConfigureAwait(false);
        return ParsePage(response.RootElement);
    }

    public async Task<MusicPage> SearchContinuationAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        using var response = await PostAsync("search", new() { ["continuation"] = token }, ct).ConfigureAwait(false);
        return ParsePage(response.RootElement);
    }

    public Task<MusicPage> HomeAsync(CancellationToken ct = default) => BrowseAsync("FEmusic_home", ct);

    public async Task<MusicPage> BrowseAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.StartsWith("PL", StringComparison.Ordinal) || id.StartsWith("OLAK", StringComparison.Ordinal) || id.StartsWith("RD", StringComparison.Ordinal) || id is "LL" or "LM") id = "VL" + id;
        using var response = await PostAsync("browse", new() { ["browseId"] = id }, ct).ConfigureAwait(false);
        return ParsePage(response.RootElement, deduplicateTracks: !id.StartsWith("VL", StringComparison.Ordinal));
    }

    public async Task<MusicPage> BrowseContinuationAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        using var response = await PostAsync("browse", new() { ["continuation"] = token }, ct).ConfigureAwait(false);
        return ParsePage(response.RootElement, deduplicateTracks: false);
    }

    public async Task<MusicPage> RadioAsync(Track track, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        using var response = await PostAsync("next", new() { ["videoId"] = track.Id, ["playlistId"] = "RDAMVM" + track.Id, ["isAudioOnly"] = true }, ct).ConfigureAwait(false);
        return ParsePage(response.RootElement);
    }

    public async Task<StreamInfo> GetStreamAsync(Track track, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.LocalPath is { Length: > 0 } local) return new(new Uri(Path.GetFullPath(local)), "audio/mp4", 0);
        if (!IsVideoId(track.Id)) throw new ArgumentException("A valid YouTube video identifier is required.", nameof(track));
        ct.ThrowIfCancellationRequested();
        lock (_cacheLock)
            if (_streams.TryGetValue(track.Id, out var cached) && cached.Until > DateTimeOffset.UtcNow) return cached.Stream;
        await _streamResolution.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long generation;
            lock (_cacheLock)
            {
                if (_streams.TryGetValue(track.Id, out var cached) && cached.Until > DateTimeOffset.UtcNow) return cached.Stream;
                generation = _sessionGeneration;
            }
            var stream = await ResolveStreamAsync(track, ct).ConfigureAwait(false);
            lock (_cacheLock)
            {
                if (generation == _sessionGeneration)
                {
                    if (_streams.Count >= 24)
                    {
                        string? oldest = null;
                        var deadline = DateTimeOffset.MaxValue;
                        foreach (var entry in _streams)
                            if (entry.Value.Until < deadline) { deadline = entry.Value.Until; oldest = entry.Key; }
                        if (oldest is not null) _streams.Remove(oldest);
                    }
                    var expiresAt = stream.ExpiresAt ?? MediaExpiry(stream.Url) ?? DateTimeOffset.UtcNow.AddMinutes(5);
                    _streams[track.Id] = (stream, expiresAt.AddMinutes(-2));
                }
            }
            return stream;
        }
        finally { _streamResolution.Release(); }
    }

    private async Task<StreamInfo> ResolveStreamAsync(Track track, CancellationToken ct)
    {
        string? reason = null;
        try
        {
            using var response = await PostAsync("player", new() { ["videoId"] = track.Id, ["contentCheckOk"] = true, ["racyCheckOk"] = true }, ct).ConfigureAwait(false);
            var stream = ParseStream(response.RootElement);
            if (stream is not null && await IsStreamAccessibleAsync(stream.Url, ct).ConfigureAwait(false)) return stream;
            reason = String(At(response.RootElement, "playabilityStatus"), "reason");
        }
        catch (MusicServiceException error) { reason = error.Message; }
        var extractor = ExtractorPath;
        if (string.IsNullOrWhiteSpace(extractor))
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe");
            if (File.Exists(bundled)) extractor = bundled;
        }
        if (!string.IsNullOrWhiteSpace(extractor)) return await ResolveExternalAsync(extractor, track.Id, ct).ConfigureAwait(false);
        throw new MusicServiceException(reason is { Length: > 0 }
            ? "YouTube could not provide a playable AAC stream: " + reason
            : "YouTube returned a ciphered or token-protected stream. Install yt-dlp.exe in the application's tools directory to enable the external stream resolver. No browser or Java runtime is used.");
    }

    private async Task<bool> IsStreamAccessibleAsync(Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is "text/html" or "application/json") return false;
        await using var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var firstByte = new byte[1];
        return await content.ReadAsync(firstByte, ct).ConfigureAwait(false) == 1;
    }

    private static DateTimeOffset? MediaExpiry(Uri url)
    {
        foreach (var field in url.Query.TrimStart('?').Split('&'))
            if (field.StartsWith("expire=", StringComparison.Ordinal) && long.TryParse(field.AsSpan(7), CultureInfo.InvariantCulture, out var seconds))
            {
                try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
                catch (ArgumentOutOfRangeException) { return null; }
            }
        return null;
    }

    private async Task<JsonDocument> PostAsync(string endpoint, JsonObject body, CancellationToken ct)
    {
        body["context"] = new JsonObject
        {
            ["client"] = new JsonObject
            {
                ["clientName"] = "WEB_REMIX", ["clientVersion"] = "1.20260707.12.00", ["hl"] = Language,
                ["gl"] = Country, ["visitorData"] = VisitorData, ["utcOffsetMinutes"] = 0
            },
            ["user"] = new JsonObject { ["lockedSafetyMode"] = false }
        };
        var json = body.ToJsonString();
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, Origin + "/youtubei/v1/" + endpoint + "?prettyPrint=false");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("Origin", Origin);
            request.Headers.TryAddWithoutValidation("Referer", Origin + "/");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "67");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "1.20260707.12.00");
            if (!string.IsNullOrWhiteSpace(VisitorData)) request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", VisitorData);
            if (_cookie is { } cookie)
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
                var sapisid = FindCookie(cookie, "SAPISID") ?? FindCookie(cookie, "__Secure-3PAPISID");
                if (sapisid is not null)
                {
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                    var digest = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(timestamp + " " + sapisid + " " + Origin)));
                    request.Headers.TryAddWithoutValidation("Authorization", "SAPISIDHASH " + timestamp + "_" + digest);
                    request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
                }
            }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (attempt < 2 && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode is 500 or 502 or 503 or 504))
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(400 * (1 << attempt));
                await Task.Delay(delay > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delay, ct).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new MusicServiceException($"YouTube {endpoint} request failed (HTTP {(int)response.StatusCode}).");
            await using var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var bounded = new LimitedReadStream(content, 16 * 1024 * 1024);
            var document = await JsonDocument.ParseAsync(bounded, new JsonDocumentOptions { MaxDepth = 128 }, ct).ConfigureAwait(false);
            if (At(document.RootElement, "error").ValueKind == JsonValueKind.Object)
            {
                var message = String(At(document.RootElement, "error"), "message") ?? "YouTube returned an API error.";
                document.Dispose();
                throw new MusicServiceException(message);
            }
            VisitorData ??= String(At(document.RootElement, "responseContext"), "visitorData");
            return document;
        }
    }

    public static MusicPage ParsePage(JsonElement root, bool deduplicateTracks = true)
    {
        var tracks = new List<Track>();
        var items = new List<BrowseItem>();
        var seenTracks = deduplicateTracks ? new HashSet<string>(StringComparer.Ordinal) : null;
        var seenItems = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;
        void Visit(JsonElement node, int depth)
        {
            if (depth > 100) return;
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in node.EnumerateArray()) Visit(child, depth + 1);
                return;
            }
            if (node.ValueKind != JsonValueKind.Object) return;
            foreach (var property in node.EnumerateObject())
            {
                if (property.Name is "musicResponsiveListItemRenderer" or "musicTwoRowItemRenderer" or "playlistPanelVideoRenderer" or "musicMultiRowListItemRenderer")
                {
                    AddRenderer(property.Value, tracks, items, seenTracks, seenItems);
                    continue;
                }
                if (property.Name is "nextContinuationData" or "reloadContinuationData") continuation ??= String(property.Value, "continuation");
                if (property.Name == "continuationCommand") continuation ??= String(property.Value, "token");
                // Menus contain duplicate IDs, unrelated destinations and continuation tokens.
                if (property.Name is "menu" or "trackingParams" or "frameworkUpdates" or "playerOverlays") continue;
                Visit(property.Value, depth + 1);
            }
        }
        Visit(root, 0);
        return new(tracks, continuation, items);
    }

    private static void AddRenderer(JsonElement renderer, List<Track> tracks, List<BrowseItem> items, HashSet<string>? seenTracks, HashSet<string> seenItems)
    {
        var columns = At(renderer, "flexColumns");
        var titleNode = At(renderer, "title");
        var metadata = At(renderer, "subtitle");
        if (columns.ValueKind == JsonValueKind.Array && columns.GetArrayLength() > 0)
        {
            titleNode = At(columns[0], "musicResponsiveListItemFlexColumnRenderer", "text");
            if (columns.GetArrayLength() > 1) metadata = At(columns[1], "musicResponsiveListItemFlexColumnRenderer", "text");
        }
        var title = Text(titleNode);
        if (string.IsNullOrWhiteSpace(title)) return;
        var videoId = String(At(renderer, "playlistItemData"), "videoId")
            ?? String(renderer, "videoId")
            ?? String(At(renderer, "navigationEndpoint", "watchEndpoint"), "videoId")
            ?? String(At(renderer, "overlay", "musicItemThumbnailOverlayRenderer", "content", "musicPlayButtonRenderer", "playNavigationEndpoint", "watchEndpoint"), "videoId")
            ?? FirstRunEndpoint(titleNode, "watchEndpoint", "videoId");
        var thumbnail = Thumbnail(renderer);
        if (videoId is { Length: > 0 })
        {
            if (seenTracks is not null && !seenTracks.Add(videoId)) return;
            var artistParts = new List<string>();
            string? album = null;
            TimeSpan? duration = ParseDuration(Text(At(renderer, "lengthText")));
            void Metadata(JsonElement text)
            {
                var runs = At(text, "runs");
                if (runs.ValueKind != JsonValueKind.Array) return;
                foreach (var run in runs.EnumerateArray())
                {
                    var label = String(run, "text");
                    if (label is null) continue;
                    var browse = At(run, "navigationEndpoint", "browseEndpoint");
                    var pageType = String(At(browse, "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig"), "pageType");
                    if (pageType == "MUSIC_PAGE_TYPE_ALBUM") album ??= label;
                    else if (pageType is "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL" || String(browse, "browseId")?.StartsWith("UC", StringComparison.Ordinal) == true)
                    { if (!artistParts.Contains(label)) artistParts.Add(label); }
                    duration ??= ParseDuration(label);
                }
            }
            Metadata(metadata);
            if (columns.ValueKind == JsonValueKind.Array)
                for (var i = 2; i < columns.GetArrayLength(); i++) Metadata(At(columns[i], "musicResponsiveListItemFlexColumnRenderer", "text"));
            var fixedColumns = At(renderer, "fixedColumns");
            if (fixedColumns.ValueKind == JsonValueKind.Array)
                foreach (var column in fixedColumns.EnumerateArray()) duration ??= ParseDuration(Text(At(column, "musicResponsiveListItemFixedColumnRenderer", "text")));
            var artist = artistParts.Count > 0 ? string.Join(", ", artistParts) : Text(At(renderer, "longBylineText"));
            if (string.IsNullOrWhiteSpace(artist))
            {
                var subtitle = Text(metadata);
                var parts = subtitle.Split('•', '·').Select(x => x.Trim()).Where(x => x.Length > 0 && x is not "Song" and not "Video" and not "Episode" && ParseDuration(x) is null).ToArray();
                artist = parts.FirstOrDefault() ?? "Unknown artist";
            }
            tracks.Add(new(videoId, title, artist, album, duration, thumbnail));
            return;
        }
        var browseId = String(At(renderer, "navigationEndpoint", "browseEndpoint"), "browseId") ?? FirstRunEndpoint(titleNode, "browseEndpoint", "browseId");
        if (browseId is null || !seenItems.Add(browseId)) return;
        var kind = String(At(renderer, "navigationEndpoint", "browseEndpoint", "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig"), "pageType") ?? "Browse";
        items.Add(new(browseId, title, Text(metadata), thumbnail, kind));
    }

    public static StreamInfo? ParseStream(JsonElement root)
    {
        var streaming = At(root, "streamingData");
        JsonElement? best = null;
        var bitrate = -1;
        foreach (var name in new[] { "adaptiveFormats", "formats" })
        {
            var formats = At(streaming, name);
            if (formats.ValueKind != JsonValueKind.Array) continue;
            foreach (var format in formats.EnumerateArray())
            {
                if (String(format, "mimeType")?.StartsWith("audio/mp4", StringComparison.OrdinalIgnoreCase) != true) continue;
                if (!TryMediaUri(String(format, "url"), out _)) continue;
                var bitrateNode = At(format, "bitrate");
                var value = bitrateNode.ValueKind == JsonValueKind.Number && bitrateNode.TryGetInt32(out var number) ? number : 0;
                if (value > bitrate) { bitrate = value; best = format; }
            }
        }
        if (best is not { } selected || !TryMediaUri(String(selected, "url"), out var url)) return null;
        DateTimeOffset? expiry = null;
        if (int.TryParse(String(streaming, "expiresInSeconds"), CultureInfo.InvariantCulture, out var seconds)) expiry = DateTimeOffset.UtcNow.AddSeconds(seconds);
        return new(url!, String(selected, "mimeType")!, Math.Max(0, bitrate), expiry);
    }

    private static bool TryMediaUri(string? value, out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" && (uri.Host == "googlevideo.com" || uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase))) return true;
        uri = null;
        return false;
    }

    private async Task<StreamInfo> ResolveExternalAsync(string executable, string id, CancellationToken ct)
    {
        if (!File.Exists(executable)) throw new MusicServiceException("The configured yt-dlp executable was not found.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--ignore-config", "--no-cache-dir", "--no-playlist", "--no-warnings", "--skip-download", "--no-progress", "--socket-timeout", "20", "--format", "bestaudio[ext=m4a][protocol=https]", "--get-url" }) info.ArgumentList.Add(argument);
        var quickJs = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "qjs.exe");
        if (File.Exists(quickJs))
        {
            info.ArgumentList.Add("--js-runtimes");
            info.ArgumentList.Add("quickjs:" + quickJs);
        }
        info.ArgumentList.Add("--");
        info.ArgumentList.Add("https://www.youtube.com/watch?v=" + id);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var resolveToken = timeout.Token;
        using var process = Process.Start(info) ?? throw new MusicServiceException("Could not start yt-dlp.");
        using var registration = resolveToken.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var outputTask = ReadBoundedAsync(process.StandardOutput, resolveToken);
        var errorTask = ReadBoundedAsync(process.StandardError, resolveToken);
        await process.WaitForExitAsync(resolveToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new MusicServiceException("yt-dlp could not resolve this track. Update the external extractor or try another track.");
        var line = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!TryMediaUri(line, out var uri)) throw new MusicServiceException("The external extractor returned an invalid media URL.");
        if (!await IsStreamAccessibleAsync(uri!, ct).ConfigureAwait(false)) throw new MusicServiceException("YouTube rejected the resolved audio stream. Try another track or update the external extractor.");
        return new(uri!, "audio/mp4", 0);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > 512 * 1024) throw new MusicServiceException("The external extractor produced excessive output.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    public static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return null;
        long seconds = 0;
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0 || number > 999) return null;
            if (index > 0 && number > 59) return null;
            seconds = seconds * 60 + number;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    public static bool IsVideoId(string id) => id.Length == 11 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static string? FindCookie(string cookies, string name)
    {
        foreach (var part in cookies.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith(name + "=", StringComparison.Ordinal)) return trimmed[(name.Length + 1)..];
        }
        return null;
    }
    private static JsonElement At(JsonElement element, params string[] path)
    {
        foreach (var name in path) if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return default;
        return element;
    }
    private static string? String(JsonElement element, string name)
    {
        var value = At(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
    }
    private static string Text(JsonElement value)
    {
        if (String(value, "simpleText") is { } simple) return simple;
        var runs = At(value, "runs");
        if (runs.ValueKind != JsonValueKind.Array) return "";
        var builder = new StringBuilder();
        foreach (var run in runs.EnumerateArray()) builder.Append(String(run, "text"));
        return builder.ToString();
    }
    private static string? FirstRunEndpoint(JsonElement node, string endpoint, string key)
    {
        var runs = At(node, "runs");
        if (runs.ValueKind == JsonValueKind.Array)
            foreach (var run in runs.EnumerateArray()) if (String(At(run, "navigationEndpoint", endpoint), key) is { } value) return value;
        return null;
    }
    private static string? Thumbnail(JsonElement renderer)
    {
        string? result = null;
        var bestWidth = -1;
        void Visit(JsonElement node, int depth)
        {
            if (depth > 8) return;
            if (node.ValueKind == JsonValueKind.Array) { foreach (var child in node.EnumerateArray()) Visit(child, depth + 1); return; }
            if (node.ValueKind != JsonValueKind.Object) return;
            if (String(node, "url") is { } url)
            {
                var widthNode = At(node, "width");
                var width = widthNode.ValueKind == JsonValueKind.Number && widthNode.TryGetInt32(out var number) ? number : 0;
                if (width >= bestWidth && width <= 600 && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https") { result = url; bestWidth = width; }
            }
            foreach (var property in node.EnumerateObject()) Visit(property.Value, depth + 1);
        }
        Visit(At(renderer, "thumbnail"), 0);
        return result;
    }

    private sealed class LimitedReadStream(Stream inner, long maximum) : Stream
    {
        private long _read;
        private void Count(int count) { _read += count; if (_read > maximum) throw new MusicServiceException("YouTube's response exceeded the supported size."); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); Count(read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); Count(read); return read; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class MusicServiceException(string message) : Exception(message);
