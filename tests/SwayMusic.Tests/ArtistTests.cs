using System.Net;
using System.Text;
using System.Text.Json;
using SwayMusic.Core.Networking;

namespace SwayMusic.Tests;

internal static class ArtistTests
{
    internal const string Fixture = """
    {
      "header":{"musicImmersiveHeaderRenderer":{
        "title":{"runs":[{"text":"Fixture Artist"}]},
        "description":{"runs":[{"text":"Short biography."}]},
        "thumbnail":{"musicThumbnailRenderer":{"thumbnail":{"thumbnails":[{"url":"https://images.example/portrait.png","width":1200}]}}},
        "monthlyListenerCount":{"runs":[{"text":"1.2M monthly listeners"}]},
        "subscriptionButton":{"subscribeButtonRenderer":{"longSubscriberCountText":{"runs":[{"text":"400K subscribers"}]}}}
      }},
      "contents":{"singleColumnBrowseResultsRenderer":{"tabs":[{"tabRenderer":{"content":{"sectionListRenderer":{"contents":[
        {"musicShelfRenderer":{"contents":[
          {"musicResponsiveListItemRenderer":{"playlistItemData":{"videoId":"fixture-song-one"},"flexColumns":[
            {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"First song"}]}}},
            {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[
              {"text":"Fixture Artist","navigationEndpoint":{"browseEndpoint":{"browseId":"UC_fixture","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ARTIST"}}}}},
              {"text":" & "},
              {"text":"Guest Artist","navigationEndpoint":{"browseEndpoint":{"browseId":"UC_guest","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ARTIST"}}}}}
            ]}}}
          ]}},
          {"playlistPanelVideoRenderer":{"videoId":"fixture-song-two","title":{"simpleText":"Second song"},"longBylineText":{"runs":[{"text":"Fixture Artist","navigationEndpoint":{"browseEndpoint":{"browseId":"UC_fixture"}}}]}}},
          {"playlistPanelVideoRenderer":{"videoId":"fixture-song-three","title":{"simpleText":"Third song"},"longBylineText":{"runs":[{"text":"Fixture Artist","navigationEndpoint":{"browseEndpoint":{"browseId":"UC_fixture"}}}]}}}
        ]}},
        {"musicCarouselShelfRenderer":{"contents":[{"musicTwoRowItemRenderer":{"title":{"simpleText":"Fixture album"},"subtitle":{"simpleText":"Album · 2026"},"thumbnail":{"musicThumbnailRenderer":{"thumbnail":{"thumbnails":[{"url":"https://images.example/album.png","width":256}]}}},"navigationEndpoint":{"browseEndpoint":{"browseId":"MPRE_fixture","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ALBUM"}}}}}}]}},
        {"musicDescriptionShelfRenderer":{"description":{"runs":[{"text":"The full biography of the artist. "},{"text":"This longer description comes from YouTube Music and includes the artist’s background and music."}]}}}
      ]}}}}]}}
    }
    """;

    public static async Task RunAsync()
    {
        using var json = JsonDocument.Parse(Fixture);
        var page = YouTubeMusicClient.ParseArtistPage(json.RootElement, "UC_fixture");
        Check(page.Name == "Fixture Artist" && page.ThumbnailUrl == "https://images.example/portrait.png", "artist title and large portrait");
        Check(page.Description.StartsWith("The full biography", StringComparison.Ordinal) && page.Stats.Contains("1.2M") && page.Stats.Contains("400K"), "full biography and audience stats");
        Check(page.Music.Tracks.Count == 3 && page.Music.Items?.Single().Id == "MPRE_fixture", "songs and browsable release catalog");
        var artists = page.Music.Tracks[0].Artists!;
        Check(artists.Count == 2 && artists[0].Id == "UC_fixture" && artists[1].Id == "UC_guest", "each artist retains its own identity");
        Check(page.Music.Tracks[1].Artists?.Single().Id == "UC_fixture", "queue byline artist identity");
        using var handler = new Handler(); using var http = new HttpClient(handler); using var client = new YouTubeMusicClient(http);
        var result = await client.SearchArtistsAsync("Fixture Artist");
        Check(result.Items?.Single().Id == "UC_fixture" && handler.SearchCount == 1, "artist-only legacy lookup");
        var remote = await client.GetArtistAsync("UC_fixture"); Check(remote.Name == page.Name, "native artist browse path");
        Console.WriteLine("PASS artist identity, multi-artist bylines, profile metadata, catalog and legacy artist search");
    }

    public static async Task LiveAsync()
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false }); using var client = new YouTubeMusicClient(http);
        var found = await client.SearchArtistsAsync("Lil Peep");
        var artist = found.Items?.FirstOrDefault(a => a.Title.Equals("Lil Peep", StringComparison.OrdinalIgnoreCase)) ?? throw new Exception("Lil Peep artist search failed");
        var profile = await client.GetArtistAsync(artist.Id);
        Check(profile.Name.Length > 0 && profile.ThumbnailUrl is not null && profile.Description.Length > 0 && profile.Music.Tracks.Count > 0, "public artist profile contains artwork, biography and songs");
        using var photo = await http.GetAsync(profile.ThumbnailUrl);
        photo.EnsureSuccessStatusCode();
        var pixels = await photo.Content.ReadAsByteArrayAsync();
        Check(pixels.Length is > 100 and <= 2 * 1024 * 1024 && photo.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) == true, "live artist artwork fits the native image loader");
        Console.WriteLine($"PASS public artist profile: {profile.Music.Tracks.Count} songs, {profile.Music.Items?.Count ?? 0} releases/browse items, {profile.Description.Length} biography characters, {pixels.Length:N0} portrait bytes");
    }

    internal sealed class Handler : HttpMessageHandler
    {
        public int SearchCount;
        public TaskCompletionSource? Hold;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes("src/SwayMusic.Desktop/Assets/sway.png")) };
            if (Hold is not null) await Hold.Task; // Deliberately ignore cancellation to exercise stale navigation.
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal))
            {
                SearchCount++;
                Check(body.RootElement.GetProperty("params").GetString() == "EgWKAQIgAWoKEAkQChAFEAMQBA%3D%3D", "filtered artist search body");
                return Json("""{"contents":[{"musicTwoRowItemRenderer":{"title":{"simpleText":"Fixture Artist"},"navigationEndpoint":{"browseEndpoint":{"browseId":"UC_fixture","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ARTIST"}}}}}}]}""");
            }
            return Json(Fixture);
        }
        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException("Artist: " + message); }
}
