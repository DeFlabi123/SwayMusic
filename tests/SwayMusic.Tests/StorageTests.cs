using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SwayMusic.Core;
using SwayMusic.Core.Downloads;
using SwayMusic.Core.Lyrics;
using SwayMusic.Core.Storage;

public static class StorageTests
{
    public static async Task RunAsync()
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        string directory = Path.GetFullPath(Path.Combine(temporaryRoot, "Metrolist-tests-" + Guid.NewGuid().ToString("N")));
        Check(directory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase), "Test cleanup must remain inside the temporary directory.");
        Directory.CreateDirectory(directory);
        try
        {
            await TestLibraryAsync(Path.Combine(directory, "library"));
            await TestCredentialsAsync(Path.Combine(directory, "credentials"));
            await TestDownloadsAsync(Path.Combine(directory, "downloads"));
            await TestLyricsAsync(Path.Combine(directory, "lyrics"));
            TestLrcParser();
            Console.WriteLine("PASS library persistence, credentials, download quotas/cancellation, lyrics selection/cache/synchronization");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task TestLibraryAsync(string directory)
    {
        var track = new Track("track-1", "Café — Song", "Artist", Duration: TimeSpan.FromMinutes(3));
        string playlistId;
        using (var store = new LibraryStore(directory))
        {
            await store.LoadAsync();
            Check(await store.ToggleLikeAsync(track), "First like should add the track.");
            var playlist = await store.CreatePlaylistAsync("  Evening 音楽  ");
            playlistId = playlist.Id;
            await store.AddToPlaylistAsync(playlist.Id, track);
            await store.AddToPlaylistAsync(playlist.Id, track);
            await store.RemoveFromPlaylistAsync(playlist.Id, 0);
            await store.AddHistoryAsync(track);
            await store.AddHistoryAsync(track with { Title = "Updated metadata" });
            store.State.Session.Queue.Add(track);
            store.State.Session.CurrentIndex = 0;
            store.State.Session.Position = TimeSpan.FromSeconds(42);
            store.State.Settings.Volume = 0.35;
            await store.SaveAsync();
        }
        using (var reopened = new LibraryStore(directory))
        {
            await reopened.LoadAsync();
            Check(reopened.IsLiked(track.Id), "Likes should survive reopening.");
            Check(reopened.State.Playlists[0].Name == "Evening 音楽", "Unicode playlist names should persist.");
            Check(reopened.State.Playlists[0].Id == playlistId && reopened.State.Playlists[0].Tracks.Count == 1,
                "Removing an indexed duplicate should retain the other entry.");
            Check(reopened.State.History.Count == 1 && reopened.State.History[0].Track.Title == "Updated metadata", "History should deduplicate by ID.");
            Check(reopened.State.Session.Position.TotalSeconds == 42 && reopened.State.Settings.Volume == 0.35, "Session/settings should persist.");
            Check(!await reopened.ToggleLikeAsync(track), "Second like should remove the track.");
            string before = await File.ReadAllTextAsync(Path.Combine(directory, "library.json"));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => reopened.SaveAsync(canceled.Token));
            Check(before == await File.ReadAllTextAsync(Path.Combine(directory, "library.json")), "Cancellation must preserve the prior file.");
            using (var locked = new FileStream(Path.Combine(directory, "library.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await ExpectPersistenceFailureAsync(() => reopened.ToggleLikeAsync(track));
                Check(!reopened.IsLiked(track.Id), "A failed like save must roll back its in-memory mutation.");
                await ExpectPersistenceFailureAsync(() => reopened.CreatePlaylistAsync("Should not exist"));
                Check(reopened.State.Playlists.Count == 1, "A failed playlist creation must roll back.");
                await ExpectPersistenceFailureAsync(() => reopened.RenamePlaylistAsync(playlistId, "Should not rename"));
                Check(reopened.State.Playlists[0].Name == "Evening 音楽", "A failed rename must restore the original name.");
                await ExpectPersistenceFailureAsync(() => reopened.DeletePlaylistAsync(playlistId));
                Check(reopened.State.Playlists.Count == 1 && reopened.State.Playlists[0].Id == playlistId, "A failed deletion must restore the original playlist.");
                await ExpectPersistenceFailureAsync(() => reopened.AddToPlaylistAsync(playlistId, track));
                Check(reopened.State.Playlists[0].Tracks.Count == 1, "A failed addition must not retain a duplicate.");
                await ExpectPersistenceFailureAsync(() => reopened.RemoveFromPlaylistAsync(playlistId, 0));
                Check(reopened.State.Playlists[0].Tracks.Count == 1 && reopened.State.Playlists[0].Tracks[0].Id == track.Id,
                    "A failed removal must restore the original indexed entry.");
                await ExpectPersistenceFailureAsync(() => reopened.AddHistoryAsync(new Track("other", "Other", "Artist")));
                Check(reopened.State.History.Count == 1 && reopened.State.History[0].Track.Id == track.Id, "A failed history update must restore prior entries.");
                await ExpectPersistenceFailureAsync(() => reopened.ClearHistoryAsync());
                Check(reopened.State.History.Count == 1, "A failed history clear must restore prior entries.");
            }
            Check(before == await File.ReadAllTextAsync(Path.Combine(directory, "library.json")), "Failed mutations must leave the durable library unchanged.");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "library.json"), "{bad json");
        using var recovered = new LibraryStore(directory);
        await recovered.LoadAsync();
        Check(recovered.RecoveryMessage is not null && Directory.GetFiles(directory, "library.json.corrupt-*").Length == 1,
            "Corrupt libraries should be preserved with a visible recovery message.");
        Check(recovered.State.LikedTracks.Count == 0, "Recovery should start with an empty library.");
    }

    private static async Task TestCredentialsAsync(string directory)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var vault = new CredentialStore(directory);
        const string cookie = "SAPISID=secret-value; __Secure-3PAPISID=another-value";
        await vault.SaveCookieAsync(cookie);
        Check(await vault.LoadCookieAsync() == cookie, "DPAPI should round-trip the account cookie for the same Windows user.");
        string file = Path.Combine(directory, "account.dpapi");
        Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)).Contains("secret-value", StringComparison.Ordinal),
            "The account cookie must not be stored as plaintext.");
        await ExpectAsync<ArgumentException>(() => vault.SaveCookieAsync("cookie\r\ninjected-header: value"));
        await vault.SaveCookieAsync(null);
        Check(await vault.LoadCookieAsync() is null, "Sign-out should remove stored credentials.");
        Task pendingSave = vault.SaveCookieAsync(cookie);
        Task signOut = vault.SaveCookieAsync(null);
        await Task.WhenAll(pendingSave, signOut);
        Check(await vault.LoadCookieAsync() is null, "Sign-out initiated during an in-flight save must remove the newly written credential too.");
    }

    private static async Task TestDownloadsAsync(string directory)
    {
        using var handler = new StubHandler(_ => AudioResponse(new byte[60]));
        using var http = new HttpClient(handler);
        using var downloads = new DownloadService(http, directory, 100);
        var one = new Track("one", "One", "Artist");
        var two = new Track("two", "Two", "Artist");
        var url = new Uri("https://audio.invalid/stream");
        var entry = await downloads.DownloadAsync(one, url);
        Check(File.Exists(entry.FilePath) && entry.Bytes == 60 && downloads.ListDownloads().Count == 1, "A finished stream should commit metadata and audio.");
        await downloads.DownloadAsync(one, url);
        Check(handler.RequestCount == 1, "An already downloaded track must not be downloaded twice.");
        await ExpectAsync<StorageQuotaExceededException>(() => downloads.DownloadAsync(two, url));
        Check(downloads.FindDownloaded(one.Id) is not null, "Quota errors must preserve existing offline downloads.");

        using var cache = new DownloadService(http, directory, 100, isCache: true);
        await cache.DownloadAsync(one, url);
        await cache.DownloadAsync(two, url);
        Check(cache.FindDownloaded(one.Id) is null && cache.FindDownloaded(two.Id) is not null, "Audio cache must evict older entries within its budget.");
        cache.BudgetBytes = 0;
        await cache.TrimAsync();
        Check(cache.ListDownloads().Count == 0, "Setting a zero cache budget should remove cached files.");

        using var canceled = new CancellationTokenSource();
        using var cancelDownloads = new DownloadService(http, Path.Combine(directory, "cancel"), 1000);
        await ExpectAsync<OperationCanceledException>(() => cancelDownloads.DownloadAsync(one, url,
            new SynchronousProgress<DownloadProgress>(_ => canceled.Cancel()), canceled.Token));
        Check(Directory.GetFiles(Path.Combine(directory, "cancel", "downloads")).Length == 0,
            "Cancellation should leave no partial media or manifest.");

        using var shortHandler = new StubHandler(_ =>
        {
            var response = AudioResponse(new byte[50]);
            response.Content.Headers.ContentLength = 100;
            return response;
        });
        using var shortHttp = new HttpClient(shortHandler);
        using var shortDownloads = new DownloadService(shortHttp, Path.Combine(directory, "truncated"), 1000);
        await ExpectAsync<InvalidDataException>(() => shortDownloads.DownloadAsync(one, url));
        Check(shortDownloads.FindDownloaded(one.Id) is null, "Truncated streams must never appear as finished downloads.");
        await downloads.DeleteAsync(one.Id);
        Check(downloads.ListDownloads().Count == 0, "Deleting an offline track should remove its metadata.");

        const int rangedLength = 600 * 1024 + 17;
        int rangeRequests = 0;
        using var rangeHandler = new StubHandler(request =>
        {
            if (request.Headers.Range is null) throw new IOException("The media server resets unrestricted downloads.");
            rangeRequests++;
            return RangedAudioResponse(request, rangedLength);
        });
        using var rangeHttp = new HttpClient(rangeHandler);
        using var rangedDownloads = new DownloadService(rangeHttp, Path.Combine(directory, "ranged"), rangedLength);
        var mediaUrl = new Uri("https://r1.googlevideo.com/videoplayback");
        var rangedEntry = await rangedDownloads.DownloadAsync(one, mediaUrl);
        byte[] audio = await File.ReadAllBytesAsync(rangedEntry.FilePath);
        Check(audio.Length == rangedLength && rangeRequests == 3, "Googlevideo downloads should use bounded ranges instead of a reset-prone full request.");
        for (int index = 0; index < audio.Length; index++)
            Check(audio[index] == index % 251, "Every downloaded range must retain its exact byte order.");
        await rangedDownloads.DownloadAsync(one, mediaUrl);
        Check(rangeRequests == 3, "A completed ranged download should be reused without network access.");

        using var rangeCancellation = new CancellationTokenSource();
        int canceledRequests = 0;
        using var canceledRangeHandler = new StubHandler(request =>
        {
            if (++canceledRequests == 2) { rangeCancellation.Cancel(); rangeCancellation.Token.ThrowIfCancellationRequested(); }
            return RangedAudioResponse(request, rangedLength);
        });
        using var canceledRangeHttp = new HttpClient(canceledRangeHandler);
        using var canceledRangeDownloads = new DownloadService(canceledRangeHttp, Path.Combine(directory, "ranged-cancel"), rangedLength);
        await ExpectAsync<OperationCanceledException>(() => canceledRangeDownloads.DownloadAsync(one, mediaUrl, cancellationToken: rangeCancellation.Token));
        Check(canceledRequests == 2 && canceledRangeDownloads.ListDownloads().Count == 0
            && Directory.GetFiles(Path.Combine(directory, "ranged-cancel", "downloads")).Length == 0,
            "Cancellation between media ranges must remove partial files and never commit a manifest.");

        using var documentHandler = new StubHandler(request =>
        {
            var response = RangedAudioResponse(request, rangedLength);
            response.Content.Headers.ContentType = new("text/html");
            return response;
        });
        using var documentHttp = new HttpClient(documentHandler);
        using var documentDownloads = new DownloadService(documentHttp, Path.Combine(directory, "ranged-document"), rangedLength);
        await ExpectAsync<InvalidDataException>(() => documentDownloads.DownloadAsync(one, mediaUrl));
    }

    private static async Task TestLyricsAsync(string directory)
    {
        const string candidates = """
            [{"trackName":"Song","artistName":"Artist","duration":180,"plainLyrics":"plain","syncedLyrics":null},
             {"trackName":"Song","artistName":"Artist","duration":184,"plainLyrics":"words","syncedLyrics":"[00:01.50]words"},
             {"trackName":"Song","artistName":"Artist","duration":230,"plainLyrics":"wrong version","syncedLyrics":"[00:01.00]wrong"}]
            """;
        using var handler = new StubHandler(request =>
        {
            Check(request.RequestUri!.Query.Contains("track_name=Song", StringComparison.Ordinal), "YouTube title decorations should be cleaned.");
            return new(HttpStatusCode.OK) { Content = new StringContent(candidates, Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        var track = new Track("lyrics-track", "Song (Official Video)", "Artist feat. Guest", Duration: TimeSpan.FromSeconds(180));
        using (var service = new LyricsService(http, directory))
        {
            var lyrics = await service.GetAsync(track);
            Check(lyrics is { IsSynced: true } && lyrics.Lines[0].Text == "words", "Synced lyrics within five seconds should outrank plain lyrics.");
            await service.GetAsync(track);
            Check(handler.RequestCount == 1, "Repeated lyric requests should use bounded memory cache.");
        }
        using var diskService = new LyricsService(http, directory);
        Check((await diskService.GetAsync(track))?.IsSynced == true && handler.RequestCount == 1, "A fresh disk lyric cache should avoid another network request.");
        Check(LyricsService.CleanArtist("Artist & Guest") == "Artist", "Primary artist cleanup should match Android behavior.");
    }

    private static void TestLrcParser()
    {
        var lyrics = LrcParser.Parse("[offset:-500]\n[00:01.00][00:05.250]One\n[00:02.5]<00:02.5>Two\n[00:00.100]Intro");
        Check(lyrics.Lines.Count == 4 && lyrics.Lines[0].Time == TimeSpan.Zero, "Offset and repeated LRC timestamps should be supported.");
        Check(lyrics.Lines[2].Time == TimeSpan.FromSeconds(2) && lyrics.Lines[2].Text == "Two", "Fractional and word timestamps should parse correctly.");
        Check(lyrics.GetActiveLine(TimeSpan.FromSeconds(1)) == 1 && lyrics.GetActiveLine(TimeSpan.FromSeconds(5)) == 3,
            "Synchronized line lookup should follow playback position.");
        Check(!LrcParser.Parse("Plain lyrics\nSecond line").IsSynced, "Unsynchronized text should remain available without fake timestamps.");
        Check(LrcParser.Parse("[00:05.00]Later").GetActiveLine(TimeSpan.Zero) == -1, "Playback before the first lyric should have no active line.");
    }

    private static HttpResponseMessage AudioResponse(byte[] content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mp4");
        return response;
    }

    private static HttpResponseMessage RangedAudioResponse(HttpRequestMessage request, int length)
    {
        var requested = request.Headers.Range?.Ranges.Single() ?? throw new IOException("A bounded byte range was required.");
        long start = requested.From ?? throw new InvalidDataException("A range start was required.");
        long end = Math.Min(requested.To ?? length - 1, length - 1);
        Check(end - start + 1 <= 256 * 1024, "A media request must not exceed its bounded range size.");
        byte[] bytes = new byte[checked((int)(end - start + 1))];
        for (int index = 0; index < bytes.Length; index++) bytes[index] = (byte)((start + index) % 251);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("audio/mp4");
        response.Content.Headers.ContentRange = new(start, end, length);
        return response;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static async Task ExpectPersistenceFailureAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Expected the locked library file to reject the save.");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
