using System.Text.Json;
using System.Text.Json.Nodes;
using SwayMusic.Core.Storage;

namespace SwayMusic.Core.Networking;

public sealed partial class YouTubeMusicClient
{
    // Adapted from Metrolist LibraryPage/SyncUtils and pinned InnerTubeX v0.11.2 mutation bodies (GPL-3.0).
    private void RequireAccount()
    {
        if (_cookie is null || Sapisid(_cookie) is null) throw new MusicServiceException("Sign in with Google to sync your YouTube Music library.");
    }

    public async Task<AccountLibrarySnapshot> GetAccountLibraryAsync(CancellationToken ct = default)
    {
        RequireAccount();
        var liked = ReadCollectionAsync("FEmusic_liked_videos", ct);
        var playlists = ReadCollectionAsync("FEmusic_liked_playlists", ct);
        var artists = ReadCollectionAsync("FEmusic_library_corpus_artists", ct);
        await Task.WhenAll(liked, playlists, artists).ConfigureAwait(false);
        return new()
        {
            SyncedAt = DateTimeOffset.UtcNow,
            LikedSongs = (await liked).Tracks.ToList(),
            Playlists = (await playlists).Items!.Where(p => p.Id is not ("VLLM" or "VLSE" or "LM" or "SE")).Select(p => p with { Kind = "remote-playlist" }).ToList(),
            Artists = (await artists).Items!.Select(p => p with { Kind = "remote-artist" }).ToList()
        };
    }

    private async Task<MusicPage> ReadCollectionAsync(string id, CancellationToken ct)
    {
        var tracks = new List<Track>(); var items = new List<BrowseItem>();
        var trackIds = new HashSet<string>(StringComparer.Ordinal); var itemIds = new HashSet<string>(StringComparer.Ordinal);
        var continuations = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;
        for (int pageNumber = 0; pageNumber < 1000; pageNumber++)
        {
            using var response = await PostAsync("browse", next is null ? new() { ["browseId"] = id } : new() { ["continuation"] = next }, ct).ConfigureAwait(false);
            if (At(response.RootElement, "responseContext", "mainAppWebResponseContext", "loggedOut").ValueKind == JsonValueKind.True)
                throw new MusicServiceException("Your Google session has expired. Sign in again to sync your library.");
            var root = CollectionContents(response.RootElement);
            if (root.ValueKind == JsonValueKind.Undefined)
                throw new MusicServiceException("YouTube returned an unsupported library response. Your previous library was kept.");
            var page = ParsePage(root);
            foreach (var track in page.Tracks) if (trackIds.Add(track.Id)) tracks.Add(track);
            foreach (var item in page.Items ?? []) if (itemIds.Add(item.Id))
                items.Add(item with { CanEdit = Editable(root, item.Id), ChannelId = item.Id.StartsWith("UC", StringComparison.Ordinal) ? item.Id : null });
            next = page.Continuation;
            if (tracks.Count > 100000 || items.Count > 50000 || next is not null && !continuations.Add(next))
                throw new MusicServiceException("YouTube library pagination exceeded its safety limit. Your previous library was kept.");
            if (next is null) return new(tracks, Items: items);
        }
        throw new MusicServiceException("YouTube library pagination did not finish. Your previous library was kept.");
    }

    private static JsonElement CollectionContents(JsonElement root)
    {
        var continuations = At(root, "continuationContents");
        foreach (var name in new[] { "musicShelfContinuation", "musicPlaylistShelfContinuation", "gridContinuation", "sectionListContinuation" })
            if (At(continuations, name).ValueKind == JsonValueKind.Object) return At(continuations, name);
        foreach (var column in new[] { "singleColumnBrowseResultsRenderer", "twoColumnBrowseResultsRenderer" })
        {
            var tabs = At(root, "contents", column, "tabs");
            if (tabs.ValueKind != JsonValueKind.Array || tabs.GetArrayLength() == 0) continue;
            var selected = tabs[0];
            foreach (var tab in tabs.EnumerateArray()) if (At(tab, "tabRenderer", "selected").ValueKind == JsonValueKind.True) { selected = tab; break; }
            return FindShelf(At(selected, "tabRenderer", "content"));
        }
        foreach (var name in new[] { "onResponseReceivedActions", "onResponseReceivedEndpoints" })
        {
            var actions = At(root, name);
            if (actions.ValueKind == JsonValueKind.Array)
                foreach (var action in actions.EnumerateArray())
                {
                    var contents = At(action, "appendContinuationItemsAction", "continuationItems");
                    if (contents.ValueKind == JsonValueKind.Array) return contents;
                }
        }
        return default;
    }

    private static JsonElement FindShelf(JsonElement node, int depth = 0)
    {
        if (depth > 12) return default;
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "musicShelfRenderer", "musicPlaylistShelfRenderer", "gridRenderer" })
                if (At(node, name).ValueKind == JsonValueKind.Object) return At(node, name);
            foreach (var property in node.EnumerateObject())
            {
                if (property.Name is "header" or "menu") continue;
                var found = FindShelf(property.Value, depth + 1); if (found.ValueKind != JsonValueKind.Undefined) return found;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) { var found = FindShelf(child, depth + 1); if (found.ValueKind != JsonValueKind.Undefined) return found; }
        return default;
    }

    private static bool Editable(JsonElement node, string id, int depth = 0)
    {
        if (depth > 30) return false;
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (String(At(node, "navigationEndpoint", "browseEndpoint"), "browseId") == id && HasEditIcon(At(node, "menu"))) return true;
            foreach (var p in node.EnumerateObject())
            {
                if (p.Name is "playlistEditEndpoint" or "playlistEditorEndpoint" or "deletePlaylistEndpoint")
                {
                    var playlist = String(p.Value, "playlistId");
                    if (playlist is not null && PlaylistId(playlist) == PlaylistId(id)) return true;
                }
                if (Editable(p.Value, id, depth + 1)) return true;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) if (Editable(child, id, depth + 1)) return true;
        return false;
    }
    private static bool HasEditIcon(JsonElement node, int depth = 0)
    {
        if (depth > 15) return false;
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (String(At(node, "icon"), "iconType") == "EDIT") return true;
            foreach (var p in node.EnumerateObject()) if (HasEditIcon(p.Value, depth + 1)) return true;
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) if (HasEditIcon(child, depth + 1)) return true;
        return false;
    }

    private async Task<JsonDocument> MutateAsync(string endpoint, JsonObject body, CancellationToken ct)
    {
        RequireAccount();
        var result = await PostAsync(endpoint, body, ct, retryTransient: false).ConfigureAwait(false);
        if (String(result.RootElement, "status") is { } status && status is not ("STATUS_SUCCEEDED" or "STATUS_SUCCESS"))
        { result.Dispose(); throw new MusicServiceException("YouTube did not confirm the library change. Refresh before trying again."); }
        return result;
    }
    private static string PlaylistId(string id) => id.StartsWith("VL", StringComparison.Ordinal) ? id[2..] : id;
    public async Task SetVideoLikeAsync(string id, bool like, CancellationToken ct = default)
    {
        if (!IsVideoId(id)) throw new ArgumentException("A YouTube song is required.");
        using var result = await MutateAsync(like ? "like/like" : "like/removelike", new() { ["target"] = new JsonObject { ["videoId"] = id } }, ct).ConfigureAwait(false);
    }
    public async Task SetArtistSubscriptionAsync(string channelId, bool subscribe, CancellationToken ct = default)
    {
        if (!channelId.StartsWith("UC", StringComparison.Ordinal) || channelId.Length > 128) throw new ArgumentException("A YouTube artist channel is required.");
        using var result = await MutateAsync(subscribe ? "subscription/subscribe" : "subscription/unsubscribe",
            new() { ["channelIds"] = new JsonArray(channelId), ["params"] = "EgIIAhgA" }, ct).ConfigureAwait(false);
    }
    public async Task<string> CreateAccountPlaylistAsync(string title, CancellationToken ct = default)
    {
        using var result = await MutateAsync("playlist/create", new() { ["title"] = title, ["privacyStatus"] = "PRIVATE" }, ct).ConfigureAwait(false);
        return String(result.RootElement, "playlistId") ?? throw new MusicServiceException("YouTube did not return the new playlist. Refresh your library before retrying.");
    }
    public async Task EditAccountPlaylistAsync(string id, string action, string value, CancellationToken ct = default, string? setVideoId = null)
    {
        JsonObject edit = action switch
        {
            "rename" => new() { ["action"] = "ACTION_SET_PLAYLIST_NAME", ["playlistName"] = value },
            "add" when IsVideoId(value) => new() { ["action"] = "ACTION_ADD_VIDEO", ["addedVideoId"] = value },
            "remove" when IsVideoId(value) && !string.IsNullOrWhiteSpace(setVideoId) => new() { ["action"] = "ACTION_REMOVE_VIDEO", ["removedVideoId"] = value, ["setVideoId"] = setVideoId },
            _ => throw new ArgumentException("Unsupported playlist edit.")
        };
        using var result = await MutateAsync("browse/edit_playlist", new() { ["playlistId"] = PlaylistId(id), ["actions"] = new JsonArray(edit) }, ct).ConfigureAwait(false);
    }
    public async Task DeleteAccountPlaylistAsync(string id, bool owned, CancellationToken ct = default)
    {
        using var result = await MutateAsync(owned ? "playlist/delete" : "like/removelike", owned
            ? new() { ["playlistId"] = PlaylistId(id) }
            : new() { ["target"] = new JsonObject { ["playlistId"] = PlaylistId(id) } }, ct).ConfigureAwait(false);
    }
}
