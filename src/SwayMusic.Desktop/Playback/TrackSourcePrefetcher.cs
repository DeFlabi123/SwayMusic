using SwayMusic.Core;

namespace SwayMusic.Desktop.Playback;

// Prepare only one URL, never a decoder or full audio file. Foreground selections take priority.
internal sealed class TrackSourcePrefetcher(Func<Track, CancellationToken, Task<string>> resolve) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly List<Task> _pending = [];
    private string? _id;
    private CancellationTokenSource? _request;
    private Task<string>? _task;
    private bool _disposed;

    public void Prepare(Track? next)
    {
        lock (_sync)
        {
            if (_disposed || (next is not null && next.Id == _id)) return;
            CancelLocked();
            if (next is null || next.LocalPath is not null) return;
            _id = next.Id;
            var request = _request = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _task = Task.Run(() => resolve(next, request.Token));
            _pending.RemoveAll(t => t.IsCompleted);
            _pending.Add(ObserveAsync(_task, request));
        }
    }

    public async Task<string> ResolveAsync(Track track, CancellationToken token)
    {
        Task<string>? prepared;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            prepared = _id == track.Id && _task is { IsCompleted: false } ? _task : null;
            if (prepared is null) CancelLocked();
        }
        if (prepared is not null)
        {
            // Reuse work already in progress when Next is pressed. Cancelling playback also stops it.
            using var cancel = token.Register(Cancel);
            try { return await prepared.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { Cancel(); throw; }
            catch (Exception) when (!token.IsCancellationRequested) { /* A speculative failure must not prevent normal playback. */ }
        }
        token.ThrowIfCancellationRequested();
        // Completed preparations are read through the resolver's expiry-aware URL cache.
        return await resolve(track, token).ConfigureAwait(false);
    }

    public void Cancel() { lock (_sync) CancelLocked(); }
    private void CancelLocked()
    {
        _id = null; _task = null;
        _request?.Cancel(); _request = null;
    }
    private async Task ObserveAsync(Task task, CancellationTokenSource request)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* Speculative errors are retried by the foreground resolver. */ }
        finally
        {
            lock (_sync)
            {
                if (_request == request) _request = null;
                request.Dispose();
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_sync) { _disposed = true; CancelLocked(); pending = _pending.ToArray(); }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }
}
