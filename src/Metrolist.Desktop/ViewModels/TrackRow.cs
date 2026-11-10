using Metrolist.Core;

namespace Metrolist.Desktop.ViewModels;

public sealed class TrackRow(Track track)
{
    public Track Track { get; } = track;
    public string Title => Track.Title;
    public string Artist => string.IsNullOrWhiteSpace(Track.Album) ? Track.Artist : $"{Track.Artist} · {Track.Album}";
    public string Duration => Track.Duration is { } duration ? $"{(int)duration.TotalMinutes}:{duration.Seconds:00}" : "";
    public string Source => Track.LocalPath is null ? "" : "LOCAL";
}
