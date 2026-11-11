namespace SwayMusic.Core.Storage;

public sealed class AccountLibrarySnapshot
{
    public int Version { get; set; } = 1;
    public string AccountKey { get; set; } = "";
    public DateTimeOffset SyncedAt { get; set; }
    public List<Track> LikedSongs { get; set; } = [];
    public List<BrowseItem> Playlists { get; set; } = [];
    public List<BrowseItem> Artists { get; set; } = [];
}
