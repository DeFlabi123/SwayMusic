using Windows.Media;

namespace SwayMusic.Desktop.Playback;

public sealed class WindowsMediaControls : IDisposable
{
    private readonly PlaybackService _player;
    private readonly SystemMediaTransportControls _controls;
    private readonly Action<Action> _dispatch;
    private string? _trackId;
    private bool _disposed;

    public WindowsMediaControls(PlaybackService player, nint windowHandle, Action<Action> dispatch)
    {
        _player = player;
        _dispatch = dispatch;
        _controls = SystemMediaTransportControlsInterop.GetForWindow(windowHandle);
        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsStopEnabled = true;
        _controls.IsNextEnabled = true;
        _controls.IsPreviousEnabled = true;
        _controls.ButtonPressed += OnButtonPressed;
        _controls.PlaybackPositionChangeRequested += OnPositionChangeRequested;
        _player.Changed += OnChanged;
        Update();
    }

    public event EventHandler<Exception>? Error;

    private void OnChanged(object? sender, EventArgs args) => _dispatch(Update);

    private void Update()
    {
        if (_disposed) return;
        var track = _player.CurrentTrack;
        _controls.PlaybackStatus = _player.State switch
        {
            PlayerState.Playing => MediaPlaybackStatus.Playing,
            PlayerState.Paused => MediaPlaybackStatus.Paused,
            PlayerState.Loading => MediaPlaybackStatus.Changing,
            _ => MediaPlaybackStatus.Stopped
        };
        if (track is not null && _trackId != track.Id)
        {
            _trackId = track.Id;
            var display = _controls.DisplayUpdater;
            display.Type = MediaPlaybackType.Music;
            display.MusicProperties.Title = track.Title;
            display.MusicProperties.Artist = track.Artist;
            display.MusicProperties.AlbumTitle = track.Album ?? string.Empty;
            display.Update();
        }
        var duration = _player.Duration;
        if (duration > TimeSpan.Zero)
        {
            _controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                EndTime = duration,
                MaxSeekTime = duration,
                Position = _player.Position
            });
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        => _dispatch(() => _ = ExecuteAsync(args.Button));

    private async Task ExecuteAsync(SystemMediaTransportControlsButton button)
    {
        try
        {
            switch (button)
            {
                case SystemMediaTransportControlsButton.Play: _player.Play(); break;
                case SystemMediaTransportControlsButton.Pause: _player.Pause(); break;
                case SystemMediaTransportControlsButton.Stop: await _player.StopAsync(); break;
                case SystemMediaTransportControlsButton.Next: await _player.NextAsync(); break;
                case SystemMediaTransportControlsButton.Previous: await _player.PreviousAsync(); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Error?.Invoke(this, error); }
    }

    private void OnPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
        => _dispatch(() => _ = SeekAsync(args.RequestedPlaybackPosition));

    private async Task SeekAsync(TimeSpan position)
    {
        try { await _player.SeekAsync(position); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Error?.Invoke(this, error); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.Changed -= OnChanged;
        _controls.ButtonPressed -= OnButtonPressed;
        _controls.PlaybackPositionChangeRequested -= OnPositionChangeRequested;
        _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        _controls.IsEnabled = false;
        _controls.DisplayUpdater.ClearAll();
    }
}
