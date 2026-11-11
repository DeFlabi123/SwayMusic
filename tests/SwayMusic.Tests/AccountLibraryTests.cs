using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwayMusic.Core;
using SwayMusic.Core.Networking;
using SwayMusic.Core.Storage;

namespace SwayMusic.Tests;

internal static class AccountLibraryTests
{
    public static async Task RunAsync()
    {
        using var handler = new LibraryHttp(); using var http = new HttpClient(handler);
        using var client = new YouTubeMusicClient(http) { SessionCookie = "SAPISID=fixture", AuthUser = 2, DataSyncId = "fixture-channel" };
        var library = await client.GetAccountLibraryAsync();
        Check(library.LikedSongs.Count == 2 && library.Artists.Count == 1 && library.Playlists.Count == 2, "all collections and song continuation");
        Check(library.Playlists[0].CanEdit && !library.Playlists[1].CanEdit, "source EDIT-menu ownership detection");
        Check(library.LikedSongs[0].PlaylistSetVideoId == "entry-1", "playlist entry identity");
        await client.SetVideoLikeAsync("abcdefghijk", false);
        await client.SetArtistSubscriptionAsync("UC_artist", false);
        Check(await client.CreateAccountPlaylistAsync("Cloud created") == "PL_created", "create playlist identity");
        await client.EditAccountPlaylistAsync("VLPL_owned", "rename", "Renamed");
        await client.EditAccountPlaylistAsync("VLPL_owned", "add", "abcdefghijk");
        await client.EditAccountPlaylistAsync("VLPL_owned", "remove", "abcdefghijk", setVideoId: "entry-1");
        await client.DeleteAccountPlaylistAsync("VLPL_owned", true);
        await client.DeleteAccountPlaylistAsync("VLPL_saved", false);
        var mutations = handler.Requests.Where(r => r.Path != "browse").ToArray();
        Check(mutations.Any(r => r.Path == "subscription/unsubscribe" && r.Body.Contains("EgIIAhgA", StringComparison.Ordinal)), "subscription endpoint and params");
        Check(mutations.Any(r => r.Path == "playlist/create" && r.Body.Contains("PRIVATE", StringComparison.Ordinal)), "new playlists are private");
        Check(mutations.Any(r => r.Body.Contains("ACTION_REMOVE_VIDEO", StringComparison.Ordinal) && r.Body.Contains("entry-1", StringComparison.Ordinal)), "exact duplicate playlist entry removal");
        Check(mutations.Any(r => r.Path == "like/removelike" && r.Body.Contains("PL_saved", StringComparison.Ordinal)), "saved playlists are removed rather than deleted");
        using var failing = new LibraryHttp { FailMutations = true }; using var failedHttp = new HttpClient(failing);
        using var failedClient = new YouTubeMusicClient(failedHttp) { SessionCookie = "SAPISID=fixture" };
        try { await failedClient.CreateAccountPlaylistAsync("No retry"); throw new Exception("Failed mutation accepted"); } catch (MusicServiceException) { }
        Check(failing.Requests.Count == 1, "mutations are not blindly retried");
        using var broken = new LibraryHttp { RepeatContinuation = true }; using var brokenHttp = new HttpClient(broken);
        using var brokenClient = new YouTubeMusicClient(brokenHttp) { SessionCookie = "SAPISID=fixture" };
        try { await brokenClient.GetAccountLibraryAsync(); throw new Exception("Looping continuation accepted"); } catch (MusicServiceException) { }
        handler.LoggedOut = true;
        try { await client.GetAccountLibraryAsync(); throw new Exception("Logged-out library accepted"); } catch (MusicServiceException) { }
        Console.WriteLine("PASS account library pagination, ownership, identity, mutations, logged-out rejection and retry safety");
    }

    public static async Task LiveAsync()
    {
        using var vault = new CredentialStore();
        var session = await vault.LoadAccountAsync();
        if (session is null) { Console.WriteLine("SKIP personal library check: no saved Google session."); return; }
        using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
        using var client = new YouTubeMusicClient(http) { SessionCookie = session.CookieHeader, VisitorData = session.VisitorData, DataSyncId = session.DataSyncId, AuthUser = session.AuthUser };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await client.AccountInfoAsync(timeout.Token);
        var library = await client.GetAccountLibraryAsync(timeout.Token);
        Console.WriteLine($"PASS read-only personal library: {library.LikedSongs.Count} liked songs, {library.Playlists.Count} playlists, {library.Artists.Count} subscribed artists. No account changes made.");
    }

    internal sealed class LibraryHttp : HttpMessageHandler
    {
        public ConcurrentQueue<(string Path, string Body)> Requests { get; } = new();
        public bool FailMutations, FailRead, RepeatContinuation, LoggedOut;
        public bool Liked = true, Subscribed = true;
        public Task? Hold { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath.Split("/v1/")[1];
            string json = await request.Content!.ReadAsStringAsync(ct);
            Requests.Enqueue((path, json)); Started.TrySetResult();
            Check(request.Headers.Contains("Authorization") && request.Headers.Contains("Cookie"), "account requests need authentication");
            if (Hold is { } hold) await hold; // Deliberately ignores cancellation to exercise stale-result rejection.
            var body = JsonNode.Parse(json)!;
            if (path == "browse")
            {
                if (FailRead) return new(HttpStatusCode.ServiceUnavailable);
                if (LoggedOut) return Json(new JsonObject { ["responseContext"] = new JsonObject { ["mainAppWebResponseContext"] = new JsonObject { ["loggedOut"] = true } } });
                string? continuation = body["continuation"]?.GetValue<string>();
                if (continuation is not null)
                {
                    var shelf = Shelf("contents", [Song("bcdefghijkl", "Second cloud song", "entry-2")], RepeatContinuation ? "cloud-next" : null);
                    return Json(new JsonObject { ["continuationContents"] = new JsonObject { ["musicShelfContinuation"] = shelf } });
                }
                string? id = body["browseId"]?.GetValue<string>();
                return id switch
                {
                    "FEmusic_liked_videos" => Json(Page("musicShelfRenderer", Shelf("contents", Liked ? [Song("abcdefghijk", "Cloud song", "entry-1")] : [], Liked ? "cloud-next" : null))),
                    "FEmusic_liked_playlists" => Json(Page("gridRenderer", Shelf("items", [Item("VLPL_owned", "Cloud playlist", "MUSIC_PAGE_TYPE_PLAYLIST", true), Item("VLPL_saved", "Saved playlist", "MUSIC_PAGE_TYPE_PLAYLIST", false)]))),
                    "FEmusic_library_corpus_artists" => Json(Page("gridRenderer", Shelf("items", Subscribed ? [Item("UC_artist", "Cloud artist", "MUSIC_PAGE_TYPE_ARTIST", false)] : []))),
                    _ => throw new InvalidOperationException("Unexpected fixture collection")
                };
            }
            if (FailMutations) return new(HttpStatusCode.ServiceUnavailable);
            if (path == "like/removelike" && body["target"]?["videoId"] is not null) Liked = false;
            if (path == "like/like") Liked = true;
            if (path == "subscription/unsubscribe") Subscribed = false;
            if (path == "subscription/subscribe") Subscribed = true;
            return Json(path == "playlist/create" ? new JsonObject { ["playlistId"] = "PL_created" } : new JsonObject { ["status"] = "STATUS_SUCCEEDED" });
        }
    }
    private static JsonObject Song(string id, string name, string entry) => new()
    {
        ["musicResponsiveListItemRenderer"] = new JsonObject
        {
            ["playlistItemData"] = new JsonObject { ["videoId"] = id, ["playlistSetVideoId"] = entry },
            ["flexColumns"] = new JsonArray(new JsonObject { ["musicResponsiveListItemFlexColumnRenderer"] = new JsonObject { ["text"] = new JsonObject { ["runs"] = new JsonArray(new JsonObject { ["text"] = name }) } } })
        }
    };
    private static JsonObject Item(string id, string title, string type, bool editable) => new()
    {
        ["musicTwoRowItemRenderer"] = new JsonObject
        {
            ["title"] = new JsonObject { ["runs"] = new JsonArray(new JsonObject { ["text"] = title }) },
            ["navigationEndpoint"] = new JsonObject { ["browseEndpoint"] = new JsonObject { ["browseId"] = id, ["browseEndpointContextSupportedConfigs"] = new JsonObject { ["browseEndpointContextMusicConfig"] = new JsonObject { ["pageType"] = type } } } },
            ["menu"] = new JsonObject { ["menuRenderer"] = new JsonObject { ["items"] = editable ? new JsonArray(new JsonObject { ["menuNavigationItemRenderer"] = new JsonObject { ["icon"] = new JsonObject { ["iconType"] = "EDIT" } } }) : new JsonArray() } }
        }
    };
    private static JsonObject Shelf(string key, JsonNode[] rows, string? continuation = null)
    {
        var result = new JsonObject { [key] = new JsonArray(rows) };
        if (continuation is not null) result["continuations"] = new JsonArray(new JsonObject { ["nextContinuationData"] = new JsonObject { ["continuation"] = continuation } });
        return result;
    }
    private static JsonObject Page(string shelfName, JsonObject shelf) => new()
    {
        ["contents"] = new JsonObject { ["singleColumnBrowseResultsRenderer"] = new JsonObject { ["tabs"] = new JsonArray(new JsonObject { ["tabRenderer"] = new JsonObject { ["selected"] = true, ["content"] = new JsonObject { [shelfName] = shelf } } }) } }
    };
    private static HttpResponseMessage Json(JsonObject node) => new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException("Account library: " + message); }
}
