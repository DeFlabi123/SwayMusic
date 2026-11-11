using SwayMusic.Core;

namespace SwayMusic.Desktop.ViewModels;

public sealed class TrackRow(Track track) : ObservableObject
{
    private bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    public Track Track { get; } = track;
    public string Title => Track.Title;
    public string ArtistName => Track.Artist;
    public IReadOnlyList<ArtistReference> ArtistLinks => Track.Artists is { Count: > 0 } artists ? artists : [new(Track.Artist)];
    public string AlbumTitle => Track.Album ?? "—";
    public string Artist => string.IsNullOrWhiteSpace(Track.Album) ? Track.Artist : $"{Track.Artist} · {Track.Album}";
    public string Duration => Track.Duration is { } duration ? $"{(int)duration.TotalMinutes}:{duration.Seconds:00}" : "";
    public string Source => Track.LocalPath is null ? "" : "LOCAL";
}
