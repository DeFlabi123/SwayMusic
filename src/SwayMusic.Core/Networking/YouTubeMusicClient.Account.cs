using System.Text.Json;

namespace SwayMusic.Core.Networking;

public sealed partial class YouTubeMusicClient
{
    /// <summary>Validates the current YouTube session and returns its active account or channel.</summary>
    public async Task<AccountInfo> AccountInfoAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_cookie is null || Sapisid(_cookie) is null)
            throw new MusicServiceException("Sign in to Google to connect your YouTube Music account.");

        JsonDocument response;
        try { response = await PostAsync("account/account_menu", new(), ct).ConfigureAwait(false); }
        catch (JsonException)
        {
            throw new MusicServiceException("YouTube returned an invalid account response. Please try signing in again.");
        }
        using (response)
        {
            var actions = At(response.RootElement, "actions");
            if (actions.ValueKind == JsonValueKind.Array)
                foreach (var action in actions.EnumerateArray())
                {
                    var account = At(action, "openPopupAction", "popup", "multiPageMenuRenderer", "header", "activeAccountHeaderRenderer");
                    var name = OptionalAccountText(At(account, "accountName"));
                    if (name is null) continue;
                    return new AccountInfo(name,
                        OptionalAccountText(At(account, "email")),
                        OptionalAccountText(At(account, "channelHandle")),
                        String(account, "channelId") ?? String(At(account, "navigationEndpoint", "browseEndpoint"), "browseId"),
                        AccountThumbnail(At(account, "accountPhoto", "thumbnails")));
                }
        }
        throw new MusicServiceException("YouTube did not confirm a signed-in account. Please sign in again.");
    }

    private static string? Sapisid(string cookie)
    {
        foreach (var name in new[] { "SAPISID", "__Secure-3PAPISID", "__Secure-1PAPISID" })
            if (FindCookie(cookie, name) is { } value && !string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    private static string? OptionalAccountText(JsonElement value)
    {
        var text = Text(value).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? AccountThumbnail(JsonElement thumbnails)
    {
        string? result = null;
        if (thumbnails.ValueKind == JsonValueKind.Array)
            foreach (var thumbnail in thumbnails.EnumerateArray())
                if (String(thumbnail, "url") is { } value && Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps) result = value;
        return result;
    }
}
