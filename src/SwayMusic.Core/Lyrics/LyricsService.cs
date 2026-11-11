using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwayMusic.Core.Storage;

namespace SwayMusic.Core.Lyrics;

public sealed partial class LyricsService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, MemoryEntry> _memory = new(StringComparer.Ordinal);
    private readonly Queue<string> _memoryOrder = new();

    public LyricsService(HttpClient httpClient, string? dataDirectory = null)
    {
        _httpClient = httpClient;
        _cacheDirectory = Path.Combine(dataDirectory ?? LibraryStore.DefaultDirectory, "lyrics");
    }

    public async Task<LyricsDocument?> GetAsync(Track track, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(track.Id + "\n" + track.Title + "\n" + track.Artist)));
            if (_memory.TryGetValue(key, out var memory) && IsFresh(memory.Metadata)) return memory.Document;
            CacheEntry? entry;
            string path = Path.Combine(_cacheDirectory, key + ".json");
            try
            {
                entry = await AtomicFile.ReadJsonAsync<CacheEntry>(path, cancellationToken).ConfigureAwait(false);
                if (entry is not null && IsFresh(entry))
                {
                    return Remember(key, entry);
                }
            }
            catch (Exception error) when (error is IOException or JsonException) { /* Cache misses may be refetched. */ }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            string? lyrics = await FindLyricsAsync(track, timeout.Token).ConfigureAwait(false);
            entry = new(lyrics, DateTimeOffset.UtcNow);
            var document = Remember(key, entry);
            try
            {
                await AtomicFile.WriteJsonAsync(path, entry, cancellationToken).ConfigureAwait(false);
                TrimDiskCache();
            }
            catch (IOException) { /* Persistence must not prevent lyrics from being displayed. */ }
            catch (UnauthorizedAccessException) { }
            return document;
        }
        finally { _gate.Release(); }
    }

    private async Task<string?> FindLyricsAsync(Track track, CancellationToken cancellationToken)
    {
        string title = CleanTitle(track.Title), artist = CleanArtist(track.Artist);
        List<string> searches =
        [
            "track_name=" + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(artist)
                + (string.IsNullOrWhiteSpace(track.Album) ? "" : "&album_name=" + Uri.EscapeDataString(track.Album)),
            "track_name=" + Uri.EscapeDataString(title),
            "q=" + Uri.EscapeDataString(artist + " " + title),
            "q=" + Uri.EscapeDataString(title)
        ];
        if (title != track.Title.Trim()) searches.Add("track_name=" + Uri.EscapeDataString(track.Title.Trim())
            + "&artist_name=" + Uri.EscapeDataString(track.Artist.Trim()));
        foreach (string parameters in searches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = await SearchAsync(parameters, cancellationToken).ConfigureAwait(false);
            Candidate? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate.SyncedLyrics) && string.IsNullOrWhiteSpace(candidate.PlainLyrics)) continue;
                double score;
                if (track.Duration is { } duration && duration > TimeSpan.Zero)
                {
                    double difference = Math.Abs(candidate.Duration - duration.TotalSeconds);
                    if (difference > 5) continue;
                    score = (string.IsNullOrWhiteSpace(candidate.SyncedLyrics) ? 0 : 10) - difference;
                }
                else
                {
                    double similarity = (Similarity(title, candidate.TrackName) + Similarity(artist, candidate.ArtistName)) / 2;
                    if (similarity <= 0.6) continue;
                    score = similarity + (string.IsNullOrWhiteSpace(candidate.SyncedLyrics) ? 0 : 0.1);
                }
                if (score > bestScore) { best = candidate; bestScore = score; }
            }
            if (best is not null) return string.IsNullOrWhiteSpace(best.SyncedLyrics) ? best.PlainLyrics : best.SyncedLyrics;
        }
        return null;
    }

    private async Task<List<Candidate>> SearchAsync(string parameters, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://lrclib.net/api/search?" + parameters);
        request.Headers.UserAgent.ParseAdd("SwayMusic/1.0 (+https://github.com/MetrolistGroup/Metrolist)");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("Lyrics response exceeded its size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var limited = new SizeLimitedReadStream(stream, 4 * 1024 * 1024);
        var result = await JsonSerializer.DeserializeAsync<List<Candidate>>(limited, AtomicFile.JsonOptions, timeout.Token).ConfigureAwait(false);
        return result ?? [];
    }

    private LyricsDocument? Remember(string key, CacheEntry entry)
    {
        if (!_memory.ContainsKey(key)) _memoryOrder.Enqueue(key);
        var document = entry.Text is null ? null : LrcParser.Parse(entry.Text);
        _memory[key] = new(entry, document);
        while (_memory.Count > 3) _memory.Remove(_memoryOrder.Dequeue());
        return document;
    }

    private static bool IsFresh(CacheEntry entry) => DateTimeOffset.UtcNow - entry.FetchedAt
        < (entry.Text is null ? TimeSpan.FromHours(6) : TimeSpan.FromDays(30));

    private void TrimDiskCache()
    {
        var files = new DirectoryInfo(_cacheDirectory).GetFiles("*.json");
        Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        long bytes = 0;
        for (int index = 0; index < files.Length; index++)
        {
            bytes += files[index].Length;
            if (index >= 128 || bytes > 32 * 1024 * 1024) files[index].Delete();
        }
    }

    public static string CleanTitle(string title)
    {
        string cleaned = BracketedDecoration().Replace(title.Trim(), "");
        cleaned = JapaneseDecoration().Replace(cleaned, "");
        cleaned = SuffixDecoration().Replace(cleaned, "");
        return FeaturedArtist().Replace(cleaned, "").Trim();
    }

    public static string CleanArtist(string artist)
    {
        ReadOnlySpan<string> separators = [" & ", " and ", ", ", " x ", " feat. ", " feat ", " ft. ", " ft ", " featuring ", " with "];
        foreach (string separator in separators)
        {
            int index = artist.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return artist[..index].Trim();
        }
        return artist.Trim();
    }

    private static double Similarity(string left, string right)
    {
        left = left.Trim().ToLowerInvariant(); right = right.Trim().ToLowerInvariant();
        if (left == right) return 1;
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal)) return 0.8;
        // Two rows preserve the original edit-distance ranking without a quadratic allocation.
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (int j = 0; j <= right.Length; j++) previous[j] = j;
        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++) current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return 1.0 - (double)previous[right.Length] / Math.Max(left.Length, right.Length);
    }

    [GeneratedRegex(@"\s*[\(\[].*?(official|video|audio|lyrics|lyric|visualizer|hd|hq|4k|remaster|remix|live|acoustic|version|edit|extended|radio|clean|explicit).*?[\)\]]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BracketedDecoration();
    [GeneratedRegex(@"\s*【.*?】", RegexOptions.CultureInvariant)]
    private static partial Regex JapaneseDecoration();
    [GeneratedRegex(@"\s*\|.*$|\s*-\s*(official|video|audio|lyrics|lyric|visualizer).*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SuffixDecoration();
    [GeneratedRegex(@"\s*\((feat|ft)\..*?\)|\s*(feat|ft)\..*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FeaturedArtist();

    public void Dispose() => _gate.Dispose();
    private sealed record CacheEntry(string? Text, DateTimeOffset FetchedAt);
    private sealed record MemoryEntry(CacheEntry Metadata, LyricsDocument? Document);
    private sealed record Candidate(string TrackName, string ArtistName, double Duration, string? PlainLyrics, string? SyncedLyrics);

    private sealed class SizeLimitedReadStream(Stream source, long limit) : Stream
    {
        private long _received;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _received; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            CheckSize(read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            CheckSize(read);
            return read;
        }
        private void CheckSize(int read)
        {
            _received += read;
            if (_received > limit) throw new InvalidDataException("Lyrics response exceeded its size limit.");
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
