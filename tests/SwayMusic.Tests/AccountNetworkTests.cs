using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwayMusic.Core.Networking;

namespace SwayMusic.Tests;

public static class AccountNetworkTests
{
    private const string Profile = """
        {"actions":[{"unrelatedAction":{}},{"openPopupAction":{"popup":{"multiPageMenuRenderer":{"header":{"activeAccountHeaderRenderer":{
          "accountName":{"runs":[{"text":"Fixture "},{"text":"channel"}]},
          "email":{"runs":[{"text":"fixture@example.invalid"}]},
          "channelHandle":{"runs":[{"text":"@fixture"}]},
          "navigationEndpoint":{"browseEndpoint":{"browseId":"UC_fixture"}},
          "accountPhoto":{"thumbnails":[{"url":"https://yt3.googleusercontent.com/small"},{"url":"https://yt3.googleusercontent.com/large"},{"url":"http://unsafe.invalid/photo"}]}
        }}}}}}]}
        """;

    public static async Task RunAsync()
    {
        using var recording = new Handler(() => Json(Profile));
        using var http = new HttpClient(recording);
        var client = new YouTubeMusicClient(http);
        await ExpectAsync<MusicServiceException>(() => client.AccountInfoAsync());
        client.SessionCookie = "SID=fixture";
        await ExpectAsync<MusicServiceException>(() => client.AccountInfoAsync());
        client.SessionCookie = "SAPISID= ; SID=fixture";
        await ExpectAsync<MusicServiceException>(() => client.AccountInfoAsync());
        Check(recording.Requests == 0, "Missing authentication must fail before a network request.");

        client.SessionCookie = "SAPISID=fixture-secret; SID=fixture";
        client.AuthUser = 2;
        client.DataSyncId = "channel-sync-id";
        client.VisitorData = "fixture-visitor";
        client.Country = "CH"; client.Language = "de";
        var account = await client.AccountInfoAsync();
        Check(account.Name == "Fixture channel" && account.Email == "fixture@example.invalid" && account.Handle == "@fixture",
            "The active profile should come from the account header, including split text runs.");
        Check(account.ChannelId == "UC_fixture" && account.ThumbnailUrl == "https://yt3.googleusercontent.com/large",
            "Channel identity and the last HTTPS account photo should be parsed without fetching the photo.");
        Check(recording.Uri?.AbsoluteUri == "https://music.youtube.com/youtubei/v1/account/account_menu?prettyPrint=false"
            && recording.Method == HttpMethod.Post, "Validation must use the source account menu endpoint.");
        Check(recording.Headers["X-Goog-AuthUser"] == "2" && recording.Headers["X-Goog-Visitor-Id"] == "fixture-visitor"
            && recording.Headers["X-YouTube-Client-Name"] == "67" && recording.Headers["Origin"] == "https://music.youtube.com",
            "Account requests must carry the selected Google session and WEB_REMIX identity.");
        Check(recording.Headers["Cookie"] == client.SessionCookie, "The complete YouTube cookie header should be preserved.");
        Check(recording.Headers["X-Origin"] == "https://music.youtube.com"
            && recording.Headers["X-Goog-Api-Format-Version"] == "1"
            && recording.Headers["User-Agent"] == "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0",
            "The account request should match the pinned source's WEB transport headers.");
        var authorization = recording.Headers["Authorization"];
        var timestamp = authorization["SAPISIDHASH ".Length..].Split('_')[0];
        var expected = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(timestamp + " fixture-secret https://music.youtube.com")));
        Check(long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && authorization == "SAPISIDHASH " + timestamp + "_" + expected,
            "Account authentication must hash the cookie, timestamp, and actual request origin.");
        using (var body = JsonDocument.Parse(recording.Body!))
        {
            var context = body.RootElement.GetProperty("context");
            Check(context.GetProperty("user").GetProperty("onBehalfOfUser").GetString() == "channel-sync-id"
                && context.GetProperty("client").GetProperty("visitorData").GetString() == "fixture-visitor"
                && context.GetProperty("client").GetProperty("gl").GetString() == "CH",
                "Account validation must preserve visitor, locale, and active channel context.");
        }

        using var minimalHandler = new Handler(() => Json("""
            {"actions":[{"openPopupAction":{"popup":{"multiPageMenuRenderer":{"header":{"activeAccountHeaderRenderer":{"accountName":{"simpleText":"A personal account"}}}}}}}]}
            """));
        using var minimalHttp = new HttpClient(minimalHandler);
        var minimal = new YouTubeMusicClient(minimalHttp) { SessionCookie = "__Secure-1PAPISID=fixture" };
        var personal = await minimal.AccountInfoAsync();
        Check(personal.Name == "A personal account" && personal.Email is null && personal.Handle is null
            && personal.ChannelId is null && personal.ThumbnailUrl is null, "Optional profile fields may be absent.");
        using (var body = JsonDocument.Parse(minimalHandler.Body!))
            Check(!body.RootElement.GetProperty("context").GetProperty("user").TryGetProperty("onBehalfOfUser", out _)
                && minimalHandler.Headers["X-Goog-AuthUser"] == "0", "A normal account should not invent a channel identity.");

        foreach (var cookieName in new[] { "SAPISID", "__Secure-3PAPISID", "__Secure-1PAPISID" })
        {
            minimal.SessionCookie = cookieName + "=fixture";
            Check((await minimal.AccountInfoAsync()).Name == personal.Name, "Supported source authentication cookie variants should validate.");
        }
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            using var deniedHandler = new Handler(() => new(status) { Content = new StringContent("signed out") });
            using var deniedHttp = new HttpClient(deniedHandler);
            await ExpectAsync<MusicServiceException>(() => SignedIn(deniedHttp).AccountInfoAsync());
            Check(deniedHandler.Requests == 1, "Expired sessions should not retry authentication errors.");
        }
        foreach (var invalid in new[]
        {
            "{}", "[]", "{\"actions\":{}}", "{\"actions\":[{\"signInEndpoint\":{}}]}",
            "{\"actions\":[{\"openPopupAction\":{\"popup\":{\"multiPageMenuRenderer\":{\"header\":{\"activeAccountHeaderRenderer\":{\"accountName\":{\"simpleText\":\" \"}}}}}}}]}",
            "{\"error\":{\"code\":403,\"message\":\"Access denied\"}}", "{broken json"
        })
        {
            if (invalid != "{broken json") { using var fixture = JsonDocument.Parse(invalid); }
            using var invalidHttp = new HttpClient(new Handler(() => Json(invalid)));
            await ExpectAsync<MusicServiceException>(() => SignedIn(invalidHttp).AccountInfoAsync());
        }
        using var largeHttp = new HttpClient(new Handler(() => Json(new string(' ', 16 * 1024 * 1024 + 1) + "{}")));
        await ExpectAsync<MusicServiceException>(() => SignedIn(largeHttp).AccountInfoAsync());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var beforeCancellation = recording.Requests;
        await ExpectAsync<OperationCanceledException>(() => client.AccountInfoAsync(cancelled.Token));
        Check(recording.Requests == beforeCancellation, "Cancelled account validation must not issue a request.");
        try { client.AuthUser = -1; throw new InvalidOperationException("A negative Google session index was accepted."); }
        catch (ArgumentOutOfRangeException) { }
        Console.WriteLine("PASS account profile validation, selected-session headers/context, expired/malformed/bounded responses and cancellation");
    }

    private static YouTubeMusicClient SignedIn(HttpClient http) => new(http) { SessionCookie = "SAPISID=fixture" };
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Account assertion: " + message); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + " from account validation.");
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++; Uri = request.RequestUri; Method = request.Method;
            Headers.Clear();
            foreach (var header in request.Headers) Headers[header.Key] = string.Join(",", header.Value);
            Headers["User-Agent"] = request.Headers.UserAgent.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return response();
        }
    }
}
