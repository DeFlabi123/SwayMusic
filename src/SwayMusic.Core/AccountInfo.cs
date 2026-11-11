namespace SwayMusic.Core;

public sealed record AccountInfo(string Name, string? Email = null, string? Handle = null,
    string? ChannelId = null, string? ThumbnailUrl = null);
