using System.Net;
using System.Net.Http.Headers;

namespace SwayMusic.Core.Networking;

/// <summary>A seekable compressed-audio stream with a single bounded network chunk.</summary>
public sealed class HttpRangeStream : Stream
{
    private const int ChunkSize = 256 * 1024;
    private readonly HttpClient _http;
    private readonly Uri _url;
    private readonly CancellationTokenSource _lifetime;
    private readonly object _sync = new();
    private byte[] _buffer = new byte[ChunkSize];
    private long _length, _position, _bufferStart;
    private int _bufferCount;
    private int _disposeStarted;
    private bool _disposed;
    private long _bytesFetched, _bytesRead, _rangeRequests;
    public long BytesFetched => Interlocked.Read(ref _bytesFetched);
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    public long RangeRequests => Interlocked.Read(ref _rangeRequests);

    private HttpRangeStream(HttpClient http, Uri url, CancellationToken ct)
    {
        _http = http;
        _url = url;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    public static async Task<HttpRangeStream> OpenAsync(HttpClient http, Uri url, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme != "https") throw new ArgumentException("HTTPS is required for remote audio.", nameof(url));
        var stream = new HttpRangeStream(http, url, ct);
        try { await stream.FetchAsync(0, ct).ConfigureAwait(false); return stream; }
        catch { stream.Dispose(); throw; }
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length { get { ObjectDisposedException.ThrowIf(_disposed, this); return _length; } }
    public override long Position
    {
        get { lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); return _position; } }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> destination)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lifetime.Token.ThrowIfCancellationRequested();
            var total = 0;
            while (_position < _length && total < destination.Length)
            {
                if (_position < _bufferStart || _position >= _bufferStart + _bufferCount)
                    FetchAsync(_position, _lifetime.Token).GetAwaiter().GetResult();
                var offset = checked((int)(_position - _bufferStart));
                var count = Math.Min(destination.Length - total, _bufferCount - offset);
                if (count <= 0) throw new EndOfStreamException("The remote audio ended before its declared length.");
                _buffer.AsSpan(offset, count).CopyTo(destination[total..]);
                _position += count;
                total += count;
            }
            Interlocked.Add(ref _bytesRead, total);
            return total;
        }
    }

    // Media Foundation calls this stream on its decode worker. No polling or producer task is required.
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    private async Task FetchAsync(long start, CancellationToken ct)
    {
        _bufferCount = 0;
        var result = await FetchRangeAsync(start, _buffer, ct).ConfigureAwait(false);
        _length = result.Length;
        _bufferStart = start;
        _bufferCount = result.Count;
    }

    /// <summary>Reads bounded container metadata without changing the playback cursor or its cached chunk.</summary>
    public async Task<byte[]> ReadRangeAsync(long offset, int count, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || offset > _length) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0 || count > 1024 * 1024 || count > _length - offset) throw new ArgumentOutOfRangeException(nameof(count));
        ct.ThrowIfCancellationRequested();
        _lifetime.Token.ThrowIfCancellationRequested();
        var bytes = new byte[count];
        if (count == 0) return bytes;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (offset >= _bufferStart && offset + count <= _bufferStart + _bufferCount)
            {
                _buffer.AsSpan(checked((int)(offset - _bufferStart)), count).CopyTo(bytes);
                return bytes;
            }
        }
        var result = await FetchRangeAsync(offset, bytes, ct).ConfigureAwait(false);
        if (result.Count != count) throw new EndOfStreamException("The audio container metadata was truncated.");
        return bytes;
    }

    private async Task<(long Length, int Count)> FetchRangeAsync(long start, Memory<byte> target, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await FetchRangeCoreAsync(start, target, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("The audio server did not deliver the requested bytes within 30 seconds.", error);
        }
    }

    private async Task<(long Length, int Count)> FetchRangeCoreAsync(long start, Memory<byte> target, CancellationToken requestToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        request.Headers.Range = new RangeHeaderValue(start, checked(start + target.Length - 1));
        Interlocked.Increment(ref _rangeRequests);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The audio server returned a document instead of audio.");
        var range = response.Content.Headers.ContentRange;
        var length = range?.Length ?? (response.StatusCode == HttpStatusCode.OK ? response.Content.Headers.ContentLength : null);
        if (length is null || length <= 0) throw new IOException("The audio server did not provide a stream length.");
        if (_length > 0 && length != _length) throw new IOException("The audio stream length changed during playback.");
        if (start >= length) throw new IOException("The audio stream length changed during playback.");
        if ((response.StatusCode != HttpStatusCode.PartialContent || range?.From != start) && (start != 0 || length > ChunkSize))
            throw new IOException("The audio server does not support the byte ranges required for seeking.");
        var wanted = checked((int)Math.Min(target.Length, length.Value - start));
        if (range?.To is { } rangeEnd)
        {
            if (rangeEnd < start || rangeEnd >= length || rangeEnd - start + 1 > wanted)
                throw new IOException("The audio server returned an invalid byte range.");
            wanted = checked((int)(rangeEnd - start + 1));
        }
        await using var body = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
        var count = 0;
        while (count < wanted)
        {
            var read = await body.ReadAsync(target.Slice(count, wanted - count), requestToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count != wanted) throw new EndOfStreamException("The remote audio byte range was truncated.");
        Interlocked.Add(ref _bytesFetched, count);
        return (length.Value, count);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (position < 0 || position > _length) throw new IOException("Cannot seek outside the audio stream.");
            _position = position;
            return position;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposeStarted, 1) == 0)
        {
            _lifetime.Cancel();
            lock (_sync)
            {
                if (!_disposed) { _disposed = true; _buffer = []; _lifetime.Dispose(); }
            }
        }
        base.Dispose(disposing);
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
