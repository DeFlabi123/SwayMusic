using Metrolist.Core;
using Metrolist.Core.Networking;
using NAudio.Wave;

namespace Metrolist.Desktop.Playback;

public sealed class PlaybackService : IAsyncDisposable
{
    private readonly Func<Track, CancellationToken, Task<string>> _resolve;
    private readonly Func<Uri, CancellationToken, Task<Stream>> _openRemote;
    private readonly HttpClient? _ownedHttp;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _sync = new();
    private readonly PlaybackQueue _queue = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _loadCancellation;
    private Session? _session;
    private PlayerState _state;
    private float _volume = 0.8f;
    private long _requestVersion;
    private bool _disposed;

    public PlaybackService(Func<Track, CancellationToken, Task<string>> resolver,
        Func<Uri, CancellationToken, Task<Stream>>? streamFactory = null)
    {
        _resolve = resolver;
        if (streamFactory is not null) _openRemote = streamFactory;
        else
        {
            _ownedHttp = new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 2
            }) { Timeout = TimeSpan.FromSeconds(30) };
            _ownedHttp.DefaultRequestHeaders.UserAgent.ParseAdd("MetrolistWindows/1.0");
            _openRemote = async (uri, ct) => await HttpRangeStream.OpenAsync(_ownedHttp, uri, ct).ConfigureAwait(false);
        }
    }

    public event EventHandler? Changed;
    public event EventHandler<Exception>? PlaybackFailed;

    public PlayerState State { get { lock (_sync) return _state; } }
    public Track? CurrentTrack { get { lock (_sync) return _queue.Current; } }
    public int CurrentIndex { get { lock (_sync) return _queue.CurrentIndex; } }
    public IReadOnlyList<Track> Queue { get { lock (_sync) return _queue.Snapshot(); } }
    public TimeSpan Position
    {
        get { lock (_sync) return _session?.Reader.CurrentTime ?? TimeSpan.Zero; }
    }
    public TimeSpan Duration
    {
        get { lock (_sync) return _session?.ExactDuration ?? _queue.Current?.Duration ?? _session?.Reader.TotalTime ?? TimeSpan.Zero; }
    }

    public float Volume
    {
        get { lock (_sync) return _volume; }
        set
        {
            lock (_sync)
            {
                _volume = Math.Clamp(value, 0, 1);
                if (_session is { } session) session.Player.Volume = _volume;
            }
            Notify();
        }
    }
    public bool Shuffle
    {
        get { lock (_sync) return _queue.Shuffle; }
        set { lock (_sync) _queue.Shuffle = value; Notify(); }
    }
    public RepeatMode Repeat
    {
        get { lock (_sync) return _queue.Repeat; }
        set { lock (_sync) _queue.Repeat = value; Notify(); }
    }

    public async Task SetQueueAsync(IEnumerable<Track> tracks, int startIndex = 0, CancellationToken cancellationToken = default)
    {
        var snapshot = tracks.ToArray();
        using var operation = BeginLoad(cancellationToken);
        await _operations.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            lock (_sync) _queue.Set(snapshot, startIndex);
            await OpenCurrentAsync(operation.Token).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    public Task PlayTrackAsync(Track track, CancellationToken cancellationToken = default) => SetQueueAsync([track], 0, cancellationToken);

    public void RestoreQueue(IEnumerable<Track> tracks, int currentIndex = 0)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null || _state == PlayerState.Loading)
                throw new InvalidOperationException("A queue can only be restored before audio is opened.");
            _queue.Set(tracks, currentIndex);
            _state = PlayerState.Stopped;
        }
        Notify();
    }

    public void Move(int index, int direction)
    {
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); _queue.Move(index, direction); }
        Notify();
    }

    public async Task RemoveAtAsync(int index, CancellationToken cancellationToken = default)
    {
        using var operation = BeginLoad(cancellationToken);
        await _operations.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            bool removedCurrent;
            bool opened;
            bool playing;
            lock (_sync)
            {
                opened = _session is not null;
                playing = _state == PlayerState.Playing;
                removedCurrent = _queue.RemoveAt(index);
            }
            if (removedCurrent && opened) await OpenCurrentAsync(operation.Token, playing).ConfigureAwait(false);
            else Notify();
        }
        finally { _operations.Release(); }
    }

    public void Enqueue(Track track)
    {
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); _queue.Add(track); }
        Notify();
    }

    public void Play()
    {
        var restart = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is { } session)
            {
                session.Player.Play();
                _state = PlayerState.Playing;
            }
            else restart = _queue.Current is not null && _state != PlayerState.Loading;
        }
        if (restart) _ = RestartSafelyAsync();
        Notify();
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_session is { } session) { session.Player.Pause(); _state = PlayerState.Paused; }
            else if (_state == PlayerState.Loading) { CancelLoad(); _state = PlayerState.Stopped; }
        }
        Notify();
    }

    public void Toggle() { if (State is PlayerState.Playing or PlayerState.Loading) Pause(); else Play(); }

    private async Task RestartSafelyAsync()
    {
        try
        {
            using var operation = BeginLoad(CancellationToken.None);
            await _operations.WaitAsync(operation.Token).ConfigureAwait(false);
            try { await OpenCurrentAsync(operation.Token).ConfigureAwait(false); }
            finally { _operations.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* OpenCurrentAsync reports the concrete playback failure. */ }
    }

    public Task NextAsync(CancellationToken cancellationToken = default) => MoveAsync(false, cancellationToken);
    public Task PreviousAsync(CancellationToken cancellationToken = default) => MoveAsync(true, cancellationToken);

    private async Task MoveAsync(bool previous, CancellationToken cancellationToken)
    {
        using var operation = BeginLoad(cancellationToken);
        await _operations.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            bool moved;
            lock (_sync)
            {
                if (previous && _session is { } session && session.Reader.CurrentTime.TotalSeconds > 3)
                    moved = true;
                else moved = previous ? _queue.MovePrevious() : _queue.MoveNext(false);
            }
            if (moved) await OpenCurrentAsync(operation.Token).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                lock (_sync)
                {
                    if (_disposed || _session is not { } session) return;
                    var playing = _state == PlayerState.Playing;
                    session.Player.Pause();
                    var duration = session.ExactDuration ?? _queue.Current?.Duration ?? session.Reader.TotalTime;
                    session.Reader.CurrentTime = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, duration.Ticks));
                    if (playing) session.Player.Play();
                }
            }, cancellationToken).ConfigureAwait(false);
            Notify();
        }
        finally { _operations.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancelLoad();
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ReleaseSessionAsync().ConfigureAwait(false); SetState(PlayerState.Stopped); }
        finally { _operations.Release(); }
    }

    private LoadScope BeginLoad(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loadCancellation?.Cancel();
            _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _requestVersion++;
            return new LoadScope(this, _loadCancellation);
        }
    }

    private void CompleteLoad(CancellationTokenSource source)
    {
        WasapiPlayer? completed = null;
        lock (_sync)
        {
            if (_loadCancellation == source)
            {
                _loadCancellation = null;
                if (_session is { } session)
                {
                    // A cancelled/no-op request still leaves the existing session responsible for its end event.
                    _session = session with { RequestVersion = _requestVersion };
                    if (!_disposed && _state == PlayerState.Playing && session.Player.PlaybackState == PlaybackState.Stopped)
                        completed = session.Player;
                }
            }
        }
        source.Dispose();
        if (completed is not null) _ = HandleStoppedAsync(completed, null);
    }

    private void CancelLoad()
    {
        lock (_sync)
        {
            _loadCancellation?.Cancel();
        }
    }

    private async Task OpenCurrentAsync(CancellationToken cancellationToken, bool autoPlay = true)
    {
        await ReleaseSessionAsync().ConfigureAwait(false);
        Track? track;
        long requestVersion;
        lock (_sync) { track = _queue.Current; requestVersion = _requestVersion; }
        if (track is null) { SetState(PlayerState.Stopped); return; }
        SetState(PlayerState.Loading);
        try
        {
            var source = track.LocalPath ?? await _resolve(track, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var session = await CreateSessionAsync(source, requestVersion, cancellationToken).ConfigureAwait(false);
            bool rejected;
            lock (_sync)
            {
                rejected = cancellationToken.IsCancellationRequested || _disposed;
                if (!rejected)
                {
                    _session = session;
                    session.Player.Volume = _volume;
                    session.Player.PlaybackStopped += OnPlaybackStopped;
                    if (autoPlay) session.Player.Play();
                    _state = autoPlay ? PlayerState.Playing : PlayerState.Paused;
                }
            }
            if (rejected)
            {
                await DisposeSessionAsync(session).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(PlaybackService));
            }
            Notify();
        }
        catch (OperationCanceledException) { SetState(PlayerState.Stopped); throw; }
        catch (Exception error)
        {
            await ReleaseSessionAsync().ConfigureAwait(false);
            SetState(PlayerState.Failed);
            PlaybackFailed?.Invoke(this, error);
            throw;
        }
    }

    private async Task<Session> CreateSessionAsync(string source, long requestVersion, CancellationToken cancellationToken)
    {
        Stream? input = null;
        TimeSpan? exactDuration = null;
        CancellationTokenSource? transportLifetime = null;
        MediaFoundationReader? reader = null;
        WasapiPlayer? player = null;
        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            {
                // The transport survives the short load request and is cancelled explicitly when its session ends.
                transportLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                using var registration = cancellationToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), transportLifetime);
                input = await _openRemote(uri, transportLifetime.Token).ConfigureAwait(false);
                var adapted = await FragmentedAacStream.TryCreateAsync(input, transportLifetime.Token).ConfigureAwait(false);
                if (adapted is not null) { input = adapted; exactDuration = adapted.Duration; }
                await OpenDecoderAsync().ConfigureAwait(false);
            }
            else
            {
                var local = File.OpenRead(source);
                try
                {
                    var adapted = await FragmentedAacStream.TryCreateAsync(local, cancellationToken).ConfigureAwait(false);
                    if (adapted is not null) { input = adapted; exactDuration = adapted.Duration; }
                    else local.Dispose();
                }
                catch { local.Dispose(); throw; }
                await OpenDecoderAsync().ConfigureAwait(false);
            }
            return new Session(reader!, player!, requestVersion, input, transportLifetime, exactDuration);
        }
        catch
        {
            transportLifetime?.Cancel();
            try { player?.Dispose(); }
            finally
            {
                try { reader?.Dispose(); }
                finally { input?.Dispose(); transportLifetime?.Dispose(); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        Task OpenDecoderAsync() => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            reader = input is null ? new MediaFoundationReader(source)
                : new StreamMediaFoundationReader(input, contentType: exactDuration is null ? "audio/mp4" : "audio/aac",
                    originName: exactDuration is null ? "track.m4a" : "track.aac");
            cancellationToken.ThrowIfCancellationRequested();
            player = new WasapiPlayerBuilder().WithEventSync().WithLatency(200).Build();
            player.Init(reader);
            cancellationToken.ThrowIfCancellationRequested();
        }, cancellationToken);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args) => _ = HandleStoppedAsync(sender, args.Exception);

    private async Task HandleStoppedAsync(object? sender, Exception? error)
    {
        try
        {
            await _operations.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    if (_disposed || _session is not { } active || active.Player != sender || active.RequestVersion != _requestVersion) return;
                }
                if (error is not null)
                {
                    await ReleaseSessionAsync().ConfigureAwait(false);
                    SetState(PlayerState.Failed);
                    PlaybackFailed?.Invoke(this, error);
                    return;
                }
                bool moved;
                LoadScope operation;
                lock (_sync)
                {
                    operation = BeginLoad(CancellationToken.None);
                    moved = _queue.MoveNext(true);
                }
                using (operation)
                {
                if (moved) await OpenCurrentAsync(operation.Token).ConfigureAwait(false);
                else { await ReleaseSessionAsync().ConfigureAwait(false); SetState(PlayerState.Stopped); }
                }
            }
            finally { _operations.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error2) { SetState(PlayerState.Failed); PlaybackFailed?.Invoke(this, error2); }
    }

    private async ValueTask ReleaseSessionAsync()
    {
        Session? session;
        lock (_sync)
        {
            session = _session;
            _session = null;
            if (session is not null) session.Player.PlaybackStopped -= OnPlaybackStopped;
        }
        if (session is null) return;
        await DisposeSessionAsync(session).ConfigureAwait(false);
    }

    private static async ValueTask DisposeSessionAsync(Session session)
    {
        session.TransportLifetime?.Cancel();
        try { await session.Player.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { session.Reader.Dispose(); }
            finally
            {
                session.SourceStream?.Dispose(); session.TransportLifetime?.Dispose();
            }
        }
    }

    private void SetState(PlayerState state) { lock (_sync) _state = state; Notify(); }
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        _lifetime.Cancel();
        CancelLoad();
        await _operations.WaitAsync().ConfigureAwait(false);
        try { await ReleaseSessionAsync().ConfigureAwait(false); }
        finally { _operations.Release(); _lifetime.Dispose(); _ownedHttp?.Dispose(); }
    }

    private sealed record Session(MediaFoundationReader Reader, WasapiPlayer Player, long RequestVersion,
        Stream? SourceStream, CancellationTokenSource? TransportLifetime, TimeSpan? ExactDuration);

    private sealed class LoadScope(PlaybackService owner, CancellationTokenSource source) : IDisposable
    {
        public CancellationToken Token => source.Token;
        public void Dispose() => owner.CompleteLoad(source);
    }
}
