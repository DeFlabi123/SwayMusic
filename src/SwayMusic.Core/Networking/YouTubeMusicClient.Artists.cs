using System.Text.Json;

namespace SwayMusic.Core.Networking;

public sealed partial class YouTubeMusicClient
{
    // Artist headers and search filter follow Metrolist's YouTube.artist and FILTER_ARTIST.
    public async Task<ArtistPage> GetArtistAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var response = await PostAsync("browse", new() { ["browseId"] = id }, ct).ConfigureAwait(false);
        return ParseArtistPage(response.RootElement, id);
    }

    public async Task<MusicPage> SearchArtistsAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var response = await PostAsync("search", new()
        { ["query"] = name.Trim(), ["params"] = "EgWKAQIgAWoKEAkQChAFEAMQBA%3D%3D" }, ct).ConfigureAwait(false);
        return ParsePage(response.RootElement);
    }

    public static ArtistPage ParseArtistPage(JsonElement root, string id)
    {
        var header = At(root, "header");
        var info = new[] { "musicImmersiveHeaderRenderer", "musicVisualHeaderRenderer", "musicHeaderRenderer", "musicDetailHeaderRenderer" }
            .Select(name => At(header, name)).FirstOrDefault(value => value.ValueKind == JsonValueKind.Object);
        string name = Text(At(info, "title"));
        if (string.IsNullOrWhiteSpace(name)) throw new MusicServiceException("YouTube Music did not return an artist profile.");
        string description = Text(At(info, "description"));
        // The description shelf contains the full biography when the header has only a preview.
        var shelf = FindArtistNode(At(root, "contents"), "musicDescriptionShelfRenderer");
        var fullDescription = Text(At(shelf, "description"));
        if (!string.IsNullOrWhiteSpace(fullDescription)) description = fullDescription;
        var stats = new List<string>();
        var listeners = Text(At(info, "monthlyListenerCount"));
        if (!string.IsNullOrWhiteSpace(listeners)) stats.Add(listeners);
        foreach (var buttonName in new[] { "subscriptionButton", "subscriptionButton2" })
        {
            var button = At(info, buttonName, "subscribeButtonRenderer");
            foreach (var field in new[] { "longSubscriberCountText", "subscriberCountWithSubscribeText", "shortSubscriberCountText" })
            {
                var value = Text(At(button, field));
                if (!string.IsNullOrWhiteSpace(value)) { if (!stats.Contains(value)) stats.Add(value); break; }
            }
            if (stats.Count > (string.IsNullOrWhiteSpace(listeners) ? 0 : 1)) break;
        }
        var photo = Thumbnail(info, 3000);
        if (photo is null && At(info, "foregroundThumbnail").ValueKind == JsonValueKind.Object)
            photo = ThumbnailWithRoot(At(info, "foregroundThumbnail"));
        return new(id, name, photo, description, string.Join(" · ", stats), ParsePage(At(root, "contents")));
    }

    private static string? ThumbnailWithRoot(JsonElement node)
    {
        // Reuse the bounded thumbnail selector for visual artist headers.
        using var wrapped = JsonDocument.Parse("{\"thumbnail\":" + node.GetRawText() + "}");
        return Thumbnail(wrapped.RootElement, 3000);
    }

    private static JsonElement FindArtistNode(JsonElement node, string name, int depth = 0)
    {
        if (depth > 30) return default;
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty(name, out var found)) return found;
            foreach (var property in node.EnumerateObject())
            {
                if (property.Name is "menu" or "trackingParams") continue;
                var result = FindArtistNode(property.Value, name, depth + 1);
                if (result.ValueKind == JsonValueKind.Object) return result;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray())
            {
                var result = FindArtistNode(child, name, depth + 1);
                if (result.ValueKind == JsonValueKind.Object) return result;
            }
        return default;
    }
}
