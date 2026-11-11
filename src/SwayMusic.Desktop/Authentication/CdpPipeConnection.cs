using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwayMusic.Desktop.Authentication;

/// <summary>Browser CDP over inherited anonymous pipes; no listening socket is opened.</summary>
internal sealed class CdpPipeConnection : IAsyncDisposable
{
    internal const int MaximumMessageBytes = 1024 * 1024;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Task _reader;
    private int _nextId;
    private int _disposed;
    private Exception? _failure;

    public CdpPipeConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
        _reader = Task.Run(ReadAsync);
    }

    public async Task<JsonElement> SendAsync(string method, JsonObject? parameters = null,
        string? sessionId = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_failure is { } failure) throw new IOException("The sign-in browser connection closed.", failure);
        int id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("Browser command identifier collision.");
        try
        {
            var command = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new() };
            if (sessionId is not null) command["sessionId"] = sessionId;
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(command);
            await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await _output.WriteAsync(new byte[1], cancellationToken).ConfigureAwait(false);
                await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _writer.Release(); }
            if (_failure is { } closed) completion.TrySetException(new IOException("The sign-in browser connection closed.", closed));
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadAsync()
    {
        var frame = new ArrayBufferWriter<byte>();
        byte[] buffer = new byte[8192];
        try
        {
            while (true)
            {
                int read = await _input.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The sign-in browser was closed.");
                int start = 0;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != 0) continue;
                    Append(frame, buffer.AsSpan(start, i - start));
                    if (frame.WrittenCount != 0) Dispatch(frame.WrittenMemory);
                    frame.Clear();
                    start = i + 1;
                }
                Append(frame, buffer.AsSpan(start, read - start));
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or JsonException or InvalidDataException)
        {
            _failure = error;
            foreach (var pending in _pending.Values)
                pending.TrySetException(new IOException("The sign-in browser connection closed.", error));
        }
    }

    private static void Append(ArrayBufferWriter<byte> frame, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumMessageBytes - frame.WrittenCount)
            throw new InvalidDataException("The sign-in browser returned an oversized message.");
        frame.Write(bytes);
    }

    private void Dispatch(ReadOnlyMemory<byte> bytes)
    {
        using var message = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        var root = message.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var identifier) ||
            identifier.ValueKind != JsonValueKind.Number || !identifier.TryGetInt32(out int id) || !_pending.TryRemove(id, out var pending)) return;
        if (root.TryGetProperty("error", out var error))
        {
            int code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var rawCode) && rawCode.ValueKind == JsonValueKind.Number && rawCode.TryGetInt32(out int number) ? number : 0;
            string? errorMessage = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var rawMessage) && rawMessage.ValueKind == JsonValueKind.String ? rawMessage.GetString() : null;
            pending.TrySetException(new CdpCommandException(code, CdpCommandException.IsTransientNavigation(errorMessage)));
        }
        else if (root.TryGetProperty("result", out var result)) pending.TrySetResult(result.Clone());
        else pending.TrySetException(new InvalidDataException("The sign-in browser returned an invalid reply."));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _output.Dispose();
        _input.Dispose();
        // The owning browser session closes/terminates the child first, making a synchronous
        // anonymous-pipe read reach EOF before disposal.
        await _reader.ConfigureAwait(false);
        _writer.Dispose();
    }
}

internal sealed class CdpCommandException(int code, bool isNavigationFailure) : InvalidOperationException("The installed browser could not perform the sign-in command. Try updating Edge or Chrome.")
{
    public int ErrorCode { get; } = code;
    public bool IsNavigationFailure { get; } = isNavigationFailure;

    internal static bool IsTransientNavigation(string? message) => message is not null && new[]
    {
        "Execution context was destroyed", "Cannot find context with specified id", "Cannot find default execution context",
        "Inspected target navigated or closed", "No target with given id", "Session with given id not found",
        "No session with given id", "Target closed", "Not attached to an active page"
    }.Any(fragment => message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
