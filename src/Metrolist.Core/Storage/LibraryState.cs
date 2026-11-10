namespace Metrolist.Core.Storage;

public sealed class AppSettings
{
    public double Volume { get; set; } = 0.8;
    public bool Shuffle { get; set; }
    public string RepeatMode { get; set; } = "Off";
    public string Country { get; set; } = "CH";
    public string Language { get; set; } = "en";
    public long DownloadBudgetBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long CacheBudgetBytes { get; set; } = 256L * 1024 * 1024;
    public bool CacheAudio { get; set; }
    public bool FetchLyrics { get; set; } = true;
    public double WindowWidth { get; set; } = 1140;
    public double WindowHeight { get; set; } = 760;
}

public sealed class LocalPlaylist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Track> Tracks { get; set; } = [];
}

public sealed record HistoryEntry(Track Track, DateTimeOffset PlayedAt);

public sealed class PlaybackSession
{
    public List<Track> Queue { get; set; } = [];
    public int CurrentIndex { get; set; } = -1;
    public TimeSpan Position { get; set; }
}

public sealed class LibraryState
{
    public int SchemaVersion { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<Track> LikedTracks { get; set; } = [];
    public List<LocalPlaylist> Playlists { get; set; } = [];
    public List<HistoryEntry> History { get; set; } = [];
    public PlaybackSession Session { get; set; } = new();
}
