using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SwayMusic.Core;
using SwayMusic.Desktop.Controls;

namespace SwayMusic.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private bool _isOrbitOpen, _orbitCalm, _orbitShowLyrics = true, _orbitHasSyncedLyrics;
    private string _orbitPrevious2 = "", _orbitPrevious = "", _orbitCurrent = "", _orbitNext = "", _orbitNext2 = "";
    private string? _lyricTrackId, _lyricsFetchTrackId;
    private Task? _lyricsFetchTask;
    public ICommand LikeCurrentCommand { get; private set; } = null!;
    public bool IsOrbitOpen => _isOrbitOpen;
    public bool OrbitCalm
    {
        get => _orbitCalm;
        set { if (Set(ref _orbitCalm, value)) Player.VisualizationEnabled = IsOrbitOpen && !value; }
    }
    public bool OrbitShowLyrics
    {
        get => _orbitShowLyrics;
        set { if (Set(ref _orbitShowLyrics, value) && value && IsOrbitOpen) TrackBackground(FetchLyricsAsync()); }
    }
    public bool HasCurrentTrack => Player.CurrentTrack is not null;
    public bool IsCurrentLiked => Player.CurrentTrack is { } track && (IsSignedIn && Core.Networking.YouTubeMusicClient.IsVideoId(track.Id) ? _cloud.LikedSongs.Any(song => song.Id == track.Id) : _library.IsLiked(track.Id));
    public string NowAlbum => Player.CurrentTrack?.Album ?? "NOW PLAYING";
    public bool OrbitRepeatOn => Player.Repeat != Playback.RepeatMode.Off;
    public string OrbitRepeatLabel => Player.Repeat == Playback.RepeatMode.One ? "1" : "";
    public string OrbitPrevious2 { get => _orbitPrevious2; private set => Set(ref _orbitPrevious2, value); }
    public string OrbitPrevious { get => _orbitPrevious; private set => Set(ref _orbitPrevious, value); }
    public string OrbitCurrent { get => _orbitCurrent; private set => Set(ref _orbitCurrent, value); }
    public string OrbitNext { get => _orbitNext; private set => Set(ref _orbitNext, value); }
    public string OrbitNext2 { get => _orbitNext2; private set => Set(ref _orbitNext2, value); }
    public bool OrbitHasSyncedLyrics { get => _orbitHasSyncedLyrics; private set => Set(ref _orbitHasSyncedLyrics, value); }
    public IBrush OrbitBaseBrush { get; private set; } = new SolidColorBrush(Color.Parse("#101412"));
    public IBrush OrbitPrimaryBrush { get; private set; } = new SolidColorBrush(Color.Parse("#D6F68A"));
    public IBrush OrbitSecondaryBrush { get; private set; } = new SolidColorBrush(Color.Parse("#E9BDA8"));
    public Bitmap? OrbitBackdrop { get; private set; }

    public void SetOrbitVisible(bool visible)
    {
        _isOrbitOpen = visible; OnPropertyChanged(nameof(IsOrbitOpen));
        Player.VisualizationEnabled = visible && !OrbitCalm;
        UpdateTimer(); RefreshQueue();
        if (visible && OrbitShowLyrics) TrackBackground(FetchLyricsAsync());
        UpdateOrbitLyrics();
    }
    private Task FetchLyricsAsync()
    {
        var id = Player.CurrentTrack?.Id;
        if (id == _lyricTrackId && _lyricDocument is not null) return Task.CompletedTask;
        if (id == _lyricsFetchTrackId && _lyricsFetchTask is { IsCompleted: false }) return _lyricsFetchTask;
        _lyricsFetchTrackId = id;
        return _lyricsFetchTask = FetchLyricsCoreAsync();
    }
    internal void UpdateOrbitLyrics(TimeSpan? position = null)
    {
        var document = _lyricTrackId == Player.CurrentTrack?.Id ? _lyricDocument : null;
        OrbitHasSyncedLyrics = document is { IsSynced: true };
        int index = document?.GetActiveLine(position ?? Player.Position) ?? -1;
        string Line(int offset) => document is { IsSynced: true } && index + offset >= 0 && index + offset < document.Lines.Count ? document.Lines[index + offset].Text : "";
        OrbitPrevious2 = Line(-2); OrbitPrevious = Line(-1);
        OrbitCurrent = OrbitHasSyncedLyrics ? index < 0 || string.IsNullOrWhiteSpace(Line(0)) ? "♪" : Line(0) : "";
        OrbitNext = Line(1); OrbitNext2 = Line(2);
    }
    private void UpdateOrbitPalette()
    {
        if (Artwork is null && Player.CurrentTrack is not null && OrbitBackdrop is not null) return;
        var palette = OrbitPalette.FromBitmap(Artwork);
        var previous = OrbitBackdrop;
        OrbitBackdrop = Controls.OrbitBackdrop.Create(Artwork, palette.Dominant);
        OnPropertyChanged(nameof(OrbitBackdrop)); previous?.Dispose();
        OrbitBaseBrush = new SolidColorBrush(palette.Background);
        OrbitPrimaryBrush = new SolidColorBrush(palette.Primary);
        OrbitSecondaryBrush = new SolidColorBrush(palette.Secondary);
        OnPropertyChanged(nameof(OrbitBaseBrush)); OnPropertyChanged(nameof(OrbitPrimaryBrush)); OnPropertyChanged(nameof(OrbitSecondaryBrush));
    }
}
