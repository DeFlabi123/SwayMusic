namespace Metrolist.Core;

public sealed record Track(string Id, string Title, string Artist, string? Album = null,
    TimeSpan? Duration = null, string? ThumbnailUrl = null, string? LocalPath = null);

public sealed record MusicPage(IReadOnlyList<Track> Tracks, string? Continuation = null,
    IReadOnlyList<BrowseItem>? Items = null);

public sealed record BrowseItem(string Id, string Title, string Subtitle, string? ThumbnailUrl, string Kind);

public sealed record StreamInfo(Uri Url, string MimeType, int Bitrate, DateTimeOffset? ExpiresAt = null);
