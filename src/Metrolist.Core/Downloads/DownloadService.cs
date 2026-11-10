using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Metrolist.Core.Networking;
using Metrolist.Core.Storage;

namespace Metrolist.Core.Downloads;

public sealed record DownloadProgress(string TrackId, long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes > 0 ? (double)BytesReceived / TotalBytes : null;
}

public sealed record DownloadEntry(Track Track, string FilePath, long Bytes, DateTimeOffset DownloadedAt);

public sealed class StorageQuotaExceededException(string message) : IOException(message);

public sealed class DownloadService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _directory;
    private readonly bool _isCache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _budgetBytes;

    public DownloadService(HttpClient httpClient, string? dataDirectory = null,
        long budgetBytes = 2L * 1024 * 1024 * 1024, bool isCache = false)
    {
        if (budgetBytes < 0) throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        _httpClient = httpClient;
        _budgetBytes = budgetBytes;
        _isCache = isCache;
        _directory = Path.Combine(dataDirectory ?? LibraryStore.DefaultDirectory, isCache ? "audio-cache" : "downloads");
    }

    public long BudgetBytes
    {
        get => Interlocked.Read(ref _budgetBytes);
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            Interlocked.Exchange(ref _budgetBytes, value);
        }
    }

    public string? FindDownloaded(string trackId)
    {
        string path = MediaPath(Key(trackId));
        if (!File.Exists(path)) return null;
        if (_isCache)
        {
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return path;
    }

    public IReadOnlyList<DownloadEntry> ListDownloads()
    {
        if (!Directory.Exists(_directory)) return [];
        var entries = new List<DownloadEntry>();
        foreach (string file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                using var input = File.OpenRead(file);
                var manifest = JsonSerializer.Deserialize<Manifest>(input, AtomicFile.JsonOptions);
                if (manifest is null) continue;
                string media = MediaPath(Key(manifest.Track.Id));
                if (File.Exists(media)) entries.Add(new(manifest.Track, media, new FileInfo(media).Length, manifest.DownloadedAt));
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        }
        entries.Sort((a, b) => b.DownloadedAt.CompareTo(a.DownloadedAt));
        return entries;
    }

    public async Task<DownloadEntry> DownloadAsync(Track track, Uri url,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme is not ("http" or "https")) throw new ArgumentException("An HTTP audio stream is required.", nameof(url));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string key = Key(track.Id), destination = MediaPath(key);
            if (File.Exists(destination))
                return new(track, destination, new FileInfo(destination).Length, File.GetCreationTimeUtc(destination));
            Directory.CreateDirectory(_directory);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            await using var source = await OpenSourceAsync(url, timeout.Token).ConfigureAwait(false);
            long? total = source.Length;
            if (total == 0) throw new InvalidDataException("The audio stream is empty.");
            MakeRoom(total ?? 0, excludedPath: null);
            long existingBytes = GetStoredBytes();
            string temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            bool mediaCommitted = false, manifestCommitted = false;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            long received = 0;
            try
            {
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var interval = Stopwatch.StartNew();
                    progress?.Report(new(track.Id, 0, total));
                    while (true)
                    {
                        int read = await source.Stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        received += read;
                        if (existingBytes + received > BudgetBytes)
                        {
                            if (!_isCache) throw new StorageQuotaExceededException("The download exceeds the configured storage limit. Remove downloads or increase the limit.");
                            MakeRoom(received, excludedPath: null);
                            existingBytes = GetStoredBytes();
                            if (existingBytes + received > BudgetBytes) throw new StorageQuotaExceededException("The track exceeds the audio cache limit.");
                        }
                        await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                        if (interval.ElapsedMilliseconds >= 250)
                        {
                            progress?.Report(new(track.Id, received, total));
                            interval.Restart();
                        }
                    }
                    if (received == 0) throw new InvalidDataException("The audio stream is empty.");
                    if (total is { } expected && expected != received) throw new InvalidDataException("The audio download was truncated.");
                    await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, destination, overwrite: false);
                mediaCommitted = true;
                var timestamp = DateTimeOffset.UtcNow;
                await AtomicFile.WriteJsonAsync(Path.Combine(_directory, key + ".json"),
                    new Manifest(track, timestamp), cancellationToken).ConfigureAwait(false);
                manifestCommitted = true;
                progress?.Report(new(track.Id, received, total ?? received));
                return new(track, destination, received, timestamp);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                if (mediaCommitted && !manifestCommitted && File.Exists(destination)) File.Delete(destination);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<DownloadSource> OpenSourceAsync(Uri url, CancellationToken cancellationToken)
    {
        if (url.Host.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase)
            || url.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase))
        {
            var stream = await HttpRangeStream.OpenAsync(_httpClient, url, cancellationToken).ConfigureAwait(false);
            return new(stream, stream.Length);
        }

        var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The server returned a document instead of an audio stream.");
            return new(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                response.Content.Headers.ContentLength, response);
        }
        catch { response.Dispose(); throw; }
    }

    public async Task DeleteAsync(string trackId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string key = Key(trackId);
            if (File.Exists(MediaPath(key))) File.Delete(MediaPath(key));
            string manifest = Path.Combine(_directory, key + ".json");
            if (File.Exists(manifest)) File.Delete(manifest);
        }
        finally { _gate.Release(); }
    }

    public async Task TrimAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isCache) return;
            MakeRoom(0, null);
        }
        finally { _gate.Release(); }
    }

    private void MakeRoom(long incomingBytes, string? excludedPath)
    {
        if (incomingBytes > BudgetBytes) throw new StorageQuotaExceededException("The track exceeds the configured storage limit.");
        var files = Directory.Exists(_directory) ? new DirectoryInfo(_directory).GetFiles("*.m4a") : [];
        long usedBytes = 0;
        foreach (var file in files) usedBytes += file.Length;
        if (usedBytes + incomingBytes <= BudgetBytes) return;
        if (!_isCache) throw new StorageQuotaExceededException("The download storage limit was reached. Remove downloads or increase the limit.");
        Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        foreach (var file in files)
        {
            if (file.FullName == excludedPath) continue;
            try
            {
                long size = file.Length;
                file.Delete();
                usedBytes -= size;
                string manifest = Path.ChangeExtension(file.FullName, ".json");
                if (File.Exists(manifest)) File.Delete(manifest);
            }
            catch (IOException) { /* A currently playing file may be locked by the decoder. */ }
            if (usedBytes + incomingBytes <= BudgetBytes) return;
        }
        throw new StorageQuotaExceededException("The audio cache is full and its active files could not be released.");
    }

    private long GetStoredBytes()
    {
        long total = 0;
        if (Directory.Exists(_directory)) foreach (string file in Directory.EnumerateFiles(_directory, "*.m4a")) total += new FileInfo(file).Length;
        return total;
    }

    private static string Key(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    private string MediaPath(string key) => Path.Combine(_directory, key + ".m4a");
    public void Dispose() => _gate.Dispose();
    private sealed class DownloadSource(Stream stream, long? length, HttpResponseMessage? response = null) : IAsyncDisposable
    {
        public Stream Stream { get; } = stream;
        public long? Length { get; } = length;
        public async ValueTask DisposeAsync()
        {
            try { await Stream.DisposeAsync().ConfigureAwait(false); }
            finally { response?.Dispose(); }
        }
    }
    private sealed record Manifest(Track Track, DateTimeOffset DownloadedAt);
}
