using System.Globalization;
using System.Text.Json;

namespace SwayMusic.Desktop.Authentication;

public interface IBrowserLoginService
{
    Task<BrowserLoginResult> SignInAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record BrowserLoginResult(string CookieHeader, string? VisitorData, string? DataSyncId, int AuthUser, string BrowserName)
{
    // Cookie values must never appear in diagnostics through the record's generated formatter.
    public override string ToString() => $"BrowserLoginResult {{ BrowserName = {BrowserName}, AuthUser = {AuthUser} }}";
}

internal static class BrowserLoginData
{
    internal const string MusicOrigin = "https://music.youtube.com";
    internal const string MetadataExpression = """
        (() => {
          if (location.origin !== 'https://music.youtube.com') return null;
          const c = window.yt && window.yt.config_;
          if (!c) return null;
          return { visitorData: c.VISITOR_DATA, dataSyncId: c.DATASYNC_ID, authUser: c.SESSION_INDEX };
        })()
        """;

    internal static bool IsMusicUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.Host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo);

    internal static string? CookieHeader(JsonElement cookies, DateTimeOffset now)
    {
        if (cookies.ValueKind != JsonValueKind.Array) return null;
        const string requestPath = "/youtubei/v1/";
        var applicable = new List<(string Name, string Value, string Path, string Domain)>();
        foreach (var cookie in cookies.EnumerateArray())
        {
            if (cookie.ValueKind != JsonValueKind.Object) continue;
            string? domain = Text(cookie, "domain"), path = Text(cookie, "path"), name = Text(cookie, "name"), value = Text(cookie, "value");
            if (domain is not (".youtube.com" or ".music.youtube.com" or "music.youtube.com") || path is null || !path.StartsWith('/') ||
                !requestPath.StartsWith(path, StringComparison.Ordinal) ||
                (path.Length != requestPath.Length && !path.EndsWith('/') && requestPath[path.Length] != '/') ||
                !IsName(name) || !IsValue(value) || cookie.TryGetProperty("partitionKey", out _) ||
                (cookie.TryGetProperty("partitionKeyOpaque", out var opaque) && opaque.ValueKind == JsonValueKind.True)) continue;
            bool session = cookie.TryGetProperty("session", out var sessionValue) && sessionValue.ValueKind == JsonValueKind.True;
            if (!session && cookie.TryGetProperty("expires", out var expires) && expires.TryGetDouble(out double timestamp) &&
                (!double.IsFinite(timestamp) || timestamp >= 0 && timestamp <= now.ToUnixTimeSeconds())) continue;
            applicable.Add((name!, value!, path, domain));
        }
        // Match Cookie header ordering for different-path duplicates; the current client uses
        // the first occurrence when deriving its SAPISID hash.
        var ordered = applicable.OrderByDescending(c => c.Path.Length).ThenByDescending(c => c.Domain.Length).ToArray();
        if (!ordered.Any(c => c.Name is "SAPISID" or "__Secure-3PAPISID" or "__Secure-1PAPISID" && c.Value.Length != 0)) return null;
        string header = string.Join("; ", ordered.Select(c => c.Name + "=" + c.Value));
        return header.Length <= 65536 ? header : null;
    }

    internal static BrowserLoginResult? Result(JsonElement value, string cookieHeader, string browserName)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        string? visitor = SafeMetadata(Text(value, "visitorData"));
        if (string.IsNullOrWhiteSpace(visitor)) return null;
        string? dataSync = SafeMetadata(Text(value, "dataSyncId"));
        if (dataSync is not null)
        {
            int separator = dataSync.IndexOf("||", StringComparison.Ordinal);
            if (separator >= 0) dataSync = dataSync[..separator];
            if (dataSync.Length == 0) dataSync = null;
        }
        int authUser = 0;
        if (value.TryGetProperty("authUser", out var auth))
        {
            if (auth.ValueKind == JsonValueKind.Number && !auth.TryGetInt32(out authUser)) return null;
            if (auth.ValueKind == JsonValueKind.String && !int.TryParse(auth.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out authUser)) return null;
            if (auth.ValueKind is not (JsonValueKind.Number or JsonValueKind.String or JsonValueKind.Null)) return null;
        }
        if (authUser is < 0 or > 99) return null;
        return new(cookieHeader, visitor, dataSync, authUser, browserName);
    }

    private static string? SafeMetadata(string? value) => value is { Length: > 0 and <= 8192 } && !value.Any(char.IsControl) ? value : null;
    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static bool IsName(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 256 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));
    private static bool IsValue(string? value) => value is { Length: <= 16384 } &&
        value.All(c => c is >= (char)0x21 and <= (char)0x7e && c is not ('"' or ',' or ';' or '\\'));
}
