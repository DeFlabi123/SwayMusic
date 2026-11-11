namespace SwayMusic.Core;

public sealed record Track(string Id, string Title, string Artist, string? Album = null,
    TimeSpan? Duration = null, string? ThumbnailUrl = null, string? LocalPath = null, string? PlaylistSetVideoId = null,
    IReadOnlyList<ArtistReference>? Artists = null);

public sealed record ArtistReference(string Name, string? Id = null);

public sealed record ArtistPage(string Id, string Name, string? ThumbnailUrl, string Description,
    string Stats, MusicPage Music);

public sealed record MusicPage(IReadOnlyList<Track> Tracks, string? Continuation = null,
    IReadOnlyList<BrowseItem>? Items = null);

public sealed record BrowseItem(string Id, string Title, string Subtitle, string? ThumbnailUrl, string Kind,
    bool CanEdit = false, string? ChannelId = null);

public sealed record StreamInfo(Uri Url, string MimeType, int Bitrate, DateTimeOffset? ExpiresAt = null);
