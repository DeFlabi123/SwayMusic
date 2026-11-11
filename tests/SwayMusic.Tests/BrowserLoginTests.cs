using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SwayMusic.Desktop.Authentication;

namespace SwayMusic.Tests;

internal static class BrowserLoginTests
{
    public static async Task RunAsync()
    {
        CookieAndMetadataTests();
        await FramingAsync();
        await CancellationAsync();
        await ProtocolErrorsAsync();
        await InvalidProtocolAsync();
        await CleanupFailureAsync();
        Console.WriteLine("PASS private browser-login pipe framing, response matching, cancellation, bounds, cookie scoping and trusted metadata");
    }

    private static void CookieAndMetadataTests()
    {
        Check(BrowserLoginData.IsMusicUrl("https://music.youtube.com/"), "trusted Music URL");
        foreach (string url in new[] { "http://music.youtube.com/", "https://music.youtube.com.evil.test/", "https://user@music.youtube.com/", "https://music.youtube.com:8443/", "https://accounts.google.com/" })
            Check(!BrowserLoginData.IsMusicUrl(url), "untrusted Music URL rejected");
        var cookies = new JsonArray
        {
            Cookie("SAPISID", "rootSecret", ".youtube.com"),
            Cookie("SAPISID", "specificSecret", "music.youtube.com", "/youtubei"),
            Cookie("__Secure-3PSID", "httpOnlySecret", ".youtube.com", httpOnly: true),
            Cookie("GOOGLE", "neverImport", ".google.com"),
            Cookie("HOSTONLY", "wrongHost", "youtube.com"),
            Cookie("EVIL", "wrongDomain", ".youtube.com.evil.test"),
            Cookie("PATH", "wrongPath", ".youtube.com", "/youtubeix"),
            Cookie("EXPIRED", "old", ".youtube.com", expires: 1),
            Cookie("CRLF", "bad\r\nCookie: injected", ".youtube.com"),
            Cookie("SEPARATOR", "bad; OTHER=value", ".youtube.com"),
            Cookie("bad name", "invalid", ".youtube.com"),
            Cookie("PARTITIONED", "wrongPartition", ".youtube.com")
        };
        cookies[^1]!["partitionKey"] = new JsonObject { ["topLevelSite"] = "https://example.com" };
        string? header = BrowserLoginData.CookieHeader(JsonSerializer.SerializeToElement(cookies), DateTimeOffset.UtcNow);
        Check(header == "SAPISID=specificSecret; SAPISID=rootSecret; __Secure-3PSID=httpOnlySecret", "cookie applicability, safe values, duplicate ordering and HttpOnly");
        Check(BrowserLoginData.CookieHeader(JsonSerializer.SerializeToElement(new JsonArray { Cookie("VISITOR_INFO1_LIVE", "guest", ".youtube.com") }), DateTimeOffset.UtcNow) is null, "guest cookies do not complete sign-in");
        Check(BrowserLoginData.CookieHeader(JsonSerializer.SerializeToElement(new JsonArray { Cookie("__Secure-1PAPISID", "oneParty", ".youtube.com") }), DateTimeOffset.UtcNow) == "__Secure-1PAPISID=oneParty", "first-party auth cookie fallback");
        var metadata = JsonSerializer.SerializeToElement(new { visitorData = "visitor-id", dataSyncId = "channel-id||account-id", authUser = "2" });
        var login = BrowserLoginData.Result(metadata, header!, "Test Browser");
        Check(login is { VisitorData: "visitor-id", DataSyncId: "channel-id", AuthUser: 2 }, "metadata normalization");
        Check(!login!.ToString().Contains("Secret", StringComparison.Ordinal), "record diagnostics redact cookies");
        Check(BrowserLoginData.Result(JsonSerializer.SerializeToElement(new { visitorData = "", authUser = 0 }), header!, "Test") is null, "missing visitor data rejected");
        Check(BrowserLoginData.Result(JsonSerializer.SerializeToElement(new { visitorData = "visitor", authUser = -1 }), header!, "Test") is null, "invalid account index rejected");
        Check(BrowserLoginData.Result(JsonSerializer.SerializeToElement(new { visitorData = "visitor", authUser = "invalid" }), header!, "Test") is null, "malformed account index rejected");
        Check(BrowserLoginData.MetadataExpression.Contains("location.origin !== 'https://music.youtube.com'", StringComparison.Ordinal) &&
            !BrowserLoginData.MetadataExpression.Contains("document.", StringComparison.Ordinal), "metadata expression guards exact origin and reads no page contents");
    }

    private static async Task FramingAsync()
    {
        var input = new ReplyStream();
        int firstId = 0;
        var output = new CommandStream(command =>
        {
            int id = command.GetProperty("id").GetInt32();
            if (command.GetProperty("method").GetString() == "Test.first") { firstId = id; return; }
            input.Enqueue(Encoding.UTF8.GetBytes("{\"method\":\"Target.targetInfoChanged\",\"params\":{}}\0"));
            byte[] second = Encoding.UTF8.GetBytes($"{{\"id\":{id},\"result\":{{\"value\":\"二\"}}}}\0");
            for (int i = 0; i < second.Length; i++) input.Enqueue([second[i]]);
            input.Enqueue(Encoding.UTF8.GetBytes($"{{\"id\":{firstId},\"result\":{{\"value\":\"first\"}}}}\0"));
        });
        await using var connection = new CdpPipeConnection(input, output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = connection.SendAsync("Test.first", cancellationToken: deadline.Token);
        var second = connection.SendAsync("Test.second", sessionId: "page-session", cancellationToken: deadline.Token);
        Check((await second).GetProperty("value").GetString() == "二", "split UTF8 and interleaved notification");
        Check((await first).GetProperty("value").GetString() == "first", "out-of-order replies match request IDs");
        Check(output.Commands[1].GetProperty("sessionId").GetString() == "page-session", "flattened target session transmitted");
    }

    private static async Task CancellationAsync()
    {
        var input = new ReplyStream();
        var output = new CommandStream(_ => { });
        await using var connection = new CdpPipeConnection(input, output);
        using var cancel = new CancellationTokenSource();
        var request = connection.SendAsync("Wait", cancellationToken: cancel.Token);
        cancel.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () => await request, "pending pipe command cancels");
        input.Enqueue(Encoding.UTF8.GetBytes("{\"id\":1,\"result\":{}}\0"));
        input.Complete();
    }

    private static async Task InvalidProtocolAsync()
    {
        foreach (bool oversized in new[] { false, true })
        {
            var input = new ReplyStream();
            var output = new CommandStream(_ => input.Enqueue(oversized ? new byte[CdpPipeConnection.MaximumMessageBytes + 1].Select(_ => (byte)'x').ToArray() : Encoding.UTF8.GetBytes("{broken-json}\0")));
            await using var connection = new CdpPipeConnection(input, output);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ThrowsAsync<IOException>(async () => await connection.SendAsync("Test", cancellationToken: deadline.Token), oversized ? "oversized pipe reply bounded" : "malformed pipe reply rejected");
        }
    }

    private static async Task ProtocolErrorsAsync()
    {
        foreach (bool navigation in new[] { false, true })
        {
            var input = new ReplyStream();
            var output = new CommandStream(command =>
            {
                int id = command.GetProperty("id").GetInt32();
                string message = navigation ? "Execution context was destroyed. sensitive-value" : "Method not found. sensitive-value";
                input.Enqueue(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, error = new { code = navigation ? -32000 : -32601, message } }) + "\0"));
            });
            await using var connection = new CdpPipeConnection(input, output);
            try { await connection.SendAsync("Test"); throw new Exception("Browser login: missing protocol error"); }
            catch (CdpCommandException error)
            {
                Check(error.IsNavigationFailure == navigation && error.ErrorCode == (navigation ? -32000 : -32601), "retry only recognized navigation errors");
                Check(!error.ToString().Contains("sensitive-value", StringComparison.Ordinal), "protocol diagnostics redact raw errors");
            }
        }
        var eof = new ReplyStream();
        await using var closed = new CdpPipeConnection(eof, new CommandStream(_ => eof.Complete()));
        await ThrowsAsync<IOException>(async () => await closed.SendAsync("Test"), "browser EOF completes pending requests");
    }

    private static async Task CleanupFailureAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "SwayMusicBrowserCleanupTest-" + Guid.NewGuid().ToString("N"));
        string profile = Path.Combine(root, "signin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            using (var locked = new FileStream(Path.Combine(profile, "locked-cookie-store"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                await ThrowsAsync<BrowserProfileCleanupException>(() => BrowserLoginSession.DeleteProfileAsync(root, profile), "blocked profile cleanup is a visible failure rather than sign-in cancellation");
                Check(Directory.Exists(profile), "failed cleanup preserves its owned path for retry");
            }
            await BrowserLoginSession.DeleteProfileAsync(root, profile);
            Check(!Directory.Exists(profile), "profile cleanup retries after lock release");
            string foreign = Path.Combine(root, "not-an-owned-profile");
            Directory.CreateDirectory(foreign);
            await ThrowsAsync<InvalidOperationException>(() => BrowserLoginSession.DeleteProfileAsync(root, foreign), "cleanup refuses unrelated directories");
            Directory.Delete(foreign);
        }
        finally
        {
            if (Directory.Exists(profile)) await BrowserLoginSession.DeleteProfileAsync(root, profile);
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
    }

    public static async Task SmokeAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "SwayMusicBrowserLoginSmoke-" + Guid.NewGuid().ToString("N"));
        var watch = Stopwatch.StartNew();
        int processId;
        string profile;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await using (var session = await BrowserLoginSession.StartAsync(root, "about:blank", headless: true, deadline.Token))
            {
                processId = session.ProcessId;
                profile = session.ProfileDirectory;
                var version = await session.CommandAsync("Browser.getVersion", cancellationToken: deadline.Token);
                var targets = await session.CommandAsync("Target.getTargets", cancellationToken: deadline.Token);
                var cookies = await session.CommandAsync("Storage.getCookies", cancellationToken: deadline.Token);
                Check(version.TryGetProperty("product", out var product) && product.ValueKind == JsonValueKind.String, "installed browser version handshake");
                Check(targets.GetProperty("targetInfos").EnumerateArray().Any(t => t.GetProperty("url").GetString() == "about:blank"), "headless blank page");
                Check(cookies.GetProperty("cookies").ValueKind == JsonValueKind.Array, "browser cookie command through anonymous pipe");
                Check(!File.Exists(Path.Combine(profile, "DevToolsActivePort")), "no TCP debugger endpoint");
                Console.WriteLine($"BROWSER LOGIN SMOKE {session.BrowserName}: {product.GetString()}, anonymous pipes, about:blank only.");
            }
            Check(!Directory.Exists(profile), "temporary browser profile deleted");
            try { using var leftover = Process.GetProcessById(processId); Check(leftover.HasExited, "owned browser exited"); }
            catch (ArgumentException) { }
            Console.WriteLine($"PASS installed-browser pipe handshake, cookies, close and profile cleanup in {watch.Elapsed.TotalSeconds:F2}s; no Google sign-in or real profile accessed");
        }
        finally { if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); }
    }

    private static JsonObject Cookie(string name, string value, string domain, string path = "/", bool httpOnly = false, double expires = -1) => new()
    {
        ["name"] = name, ["value"] = value, ["domain"] = domain, ["path"] = path, ["httpOnly"] = httpOnly,
        ["secure"] = true, ["session"] = expires < 0, ["expires"] = expires
    };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Browser login: " + message); }
    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Browser login: " + message);
    }

    private sealed class ReplyStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;
        public void Enqueue(byte[] bytes) => _chunks.Writer.TryWrite(bytes);
        public void Complete() => _chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_current is null || _offset == _current.Length)
            {
                try { _current = await _chunks.Reader.ReadAsync(cancellationToken); _offset = 0; }
                catch (ChannelClosedException) { return 0; }
            }
            int length = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, length).CopyTo(buffer);
            _offset += length;
            return length;
        }
        protected override void Dispose(bool disposing) { Complete(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class CommandStream(Action<JsonElement> command) : Stream
    {
        private readonly MemoryStream _frame = new();
        public List<JsonElement> Commands { get; } = [];
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            foreach (byte value in buffer.Span)
            {
                if (value != 0) { _frame.WriteByte(value); continue; }
                using var document = JsonDocument.Parse(_frame.ToArray());
                var request = document.RootElement.Clone();
                Commands.Add(request); command(request); _frame.SetLength(0);
            }
            return ValueTask.CompletedTask;
        }
        protected override void Dispose(bool disposing) { _frame.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
}
