using System.Text.Json;
using SwayMusic.Core;
using SwayMusic.Core.Storage;

namespace SwayMusic.Tests;

public static class MigrationTests
{
    public static async Task RunAsync()
    {
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SwayMusic-migration-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(parent);
        try
        {
            await RoundTripAsync(Path.Combine(parent, "roundtrip"));
            await ExistingDestinationAsync(Path.Combine(parent, "existing"));
            await FailureAndRetryAsync(Path.Combine(parent, "retry"));
            await LockedSourceAsync(Path.Combine(parent, "locked"));
            await CancellationAsync(Path.Combine(parent, "cancel"));
            await ConcurrentAsync(Path.Combine(parent, "concurrent"));
            await RejectNestedAsync(Path.Combine(parent, "nested"));
            Check((await DataMigration.EnsureAsync(Path.Combine(parent, "missing"), Path.Combine(parent, "unused"))).Status == MigrationStatus.NoLegacyData,
                "Missing legacy data should not create a new directory.");
            Check(!Directory.Exists(Path.Combine(parent, "unused")), "No-op migration should not write anything.");
            Console.WriteLine("PASS safe data migration, preserved backup/credentials, path remapping, existing-data protection, cancellation, failure/retry and concurrency");
        }
        finally
        {
            if (!parent.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Refusing test cleanup outside the temporary directory.");
            Directory.Delete(parent, recursive: true);
        }
    }

    private static async Task RoundTripAsync(string parent)
    {
        string source = Path.Combine(parent, "Metrolist"), destination = Path.Combine(parent, "SwayMusic");
        Directory.CreateDirectory(Path.Combine(source, "downloads"));
        Directory.CreateDirectory(Path.Combine(source, "lyrics"));
        string oldMedia = Path.Combine(source, "downloads", "media.m4a");
        string external = Path.Combine(parent, "external-song.mp3");
        await File.WriteAllBytesAsync(oldMedia, Enumerable.Range(0, 200000).Select(i => (byte)(i % 251)).ToArray());
        await File.WriteAllTextAsync(Path.Combine(source, "lyrics", "cached.json"), "{\"text\":\"unchanged lyrics\"}");
        using (var library = new LibraryStore(source))
        {
            await library.LoadAsync();
            var track = new Track("song", "Song", "Artist", LocalPath: oldMedia);
            await library.ToggleLikeAsync(track);
            var playlist = await library.CreatePlaylistAsync("Evening");
            await library.AddToPlaylistAsync(playlist.Id, track);
            await library.AddToPlaylistAsync(playlist.Id, track with { Id = "external", LocalPath = external });
            await library.AddHistoryAsync(track);
            library.State.Session = new() { Queue = [track], CurrentIndex = 0, Position = TimeSpan.FromSeconds(31) };
            library.State.Settings.Volume = 0.42;
            await library.SaveAsync();
        }
        using (var credentials = new CredentialStore(source)) await credentials.SaveCookieAsync("SAPISID=migration-secret; SID=private");
        byte[] protectedBytes = await File.ReadAllBytesAsync(Path.Combine(source, "account.dpapi"));
        string originalLibrary = await File.ReadAllTextAsync(Path.Combine(source, "library.json"));
        await File.WriteAllTextAsync(Path.Combine(source, "downloads", "media.json"), JsonSerializer.Serialize(new
        {
            track = new Track("song", "Song", "Artist", LocalPath: oldMedia), downloadedAt = DateTimeOffset.UtcNow
        }, AtomicFile.JsonOptions));
        var result = await DataMigration.EnsureAsync(source, destination);
        Check(result.Status == MigrationStatus.Migrated && !string.IsNullOrEmpty(result.Message), "Initial migration should report a verified copy.");
        Check(originalLibrary == await File.ReadAllTextAsync(Path.Combine(source, "library.json")), "The original library is the untouched backup.");
        byte[] copiedProtectedBytes = await File.ReadAllBytesAsync(Path.Combine(destination, "account.dpapi"));
        Check(protectedBytes.SequenceEqual(copiedProtectedBytes), "DPAPI bytes must be copied without decrypting/re-encrypting.");
        using (var credentials = new CredentialStore(destination))
            Check(await credentials.LoadCookieAsync() == "SAPISID=migration-secret; SID=private", "Copied credentials should remain readable for the same Windows user.");
        using (var library = new LibraryStore(destination))
        {
            await library.LoadAsync();
            string newMedia = Path.Combine(destination, "downloads", "media.m4a");
            Check(library.State.LikedTracks[0].LocalPath == newMedia && library.State.History[0].Track.LocalPath == newMedia,
                "Migrated library/history pointers should use the new data directory.");
            Check(library.State.Playlists[0].Tracks[1].LocalPath == external, "External local files must keep their original locations.");
            Check(library.State.Session.Queue[0].LocalPath == newMedia && library.State.Session.Position.TotalSeconds == 31 && library.State.Settings.Volume == 0.42,
                "Queue position and settings should survive migration.");
        }
        Check((await File.ReadAllTextAsync(Path.Combine(destination, "downloads", "media.json"))).Contains("SwayMusic", StringComparison.Ordinal),
            "Download manifests with local paths must be remapped too.");
        byte[] originalMedia = await File.ReadAllBytesAsync(oldMedia);
        byte[] copiedMedia = await File.ReadAllBytesAsync(Path.Combine(destination, "downloads", "media.m4a"));
        Check(originalMedia.SequenceEqual(copiedMedia),
            "Compressed audio should be preserved exactly.");
        Check((await DataMigration.EnsureAsync(source, destination)).Status == MigrationStatus.DestinationAlreadyExists,
            "Repeated startup should not copy or overwrite an existing migration.");
        Check(!Directory.EnumerateDirectories(parent, ".SwayMusic.migration-*").Any(), "Committed migration should leave no stage.");
    }

    private static async Task ExistingDestinationAsync(string parent)
    {
        string source = Path.Combine(parent, "Metrolist"), destination = Path.Combine(parent, "SwayMusic");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "library.json"), "{\"schemaVersion\":1}");
        await File.WriteAllTextAsync(Path.Combine(destination, "library.json"), "existing-user-content");
        var result = await DataMigration.EnsureAsync(source, destination);
        Check(result.Status == MigrationStatus.DestinationAlreadyExists && result.Message is not null, "A separate existing library should report a non-destructive conflict.");
        Check(await File.ReadAllTextAsync(Path.Combine(destination, "library.json")) == "existing-user-content", "Never overwrite existing SwayMusic data.");
    }

    private static async Task FailureAndRetryAsync(string parent)
    {
        string source = Path.Combine(parent, "Metrolist"), destination = Path.Combine(parent, "SwayMusic");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "library.json"), "{not-json");
        await ExpectAsync<JsonException>(() => DataMigration.EnsureAsync(source, destination));
        Check(!Directory.Exists(destination) && !Directory.EnumerateDirectories(parent, ".SwayMusic.migration-*").Any(), "Failed validation must not commit partial data.");
        Check(await File.ReadAllTextAsync(Path.Combine(source, "library.json")) == "{not-json", "Failure must preserve the original bytes.");
        await File.WriteAllTextAsync(Path.Combine(source, "library.json"), "{\"schemaVersion\":1}");
        Check((await DataMigration.EnsureAsync(source, destination)).Status == MigrationStatus.Migrated, "A failed migration must remain retryable.");
    }

    private static async Task CancellationAsync(string parent)
    {
        string source = Path.Combine(parent, "Metrolist"), destination = Path.Combine(parent, "SwayMusic");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "library.json"), "{\"schemaVersion\":1}");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => DataMigration.EnsureAsync(source, destination, canceled.Token));
        Check(!Directory.Exists(destination), "Canceled migration should not commit a destination.");
    }

    private static async Task LockedSourceAsync(string parent)
    {
        string source = Path.Combine(parent, "Metrolist"), destination = Path.Combine(parent, "SwayMusic");
        Directory.CreateDirectory(source);
        string path = Path.Combine(source, "library.json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1}");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            await ExpectAsync<IOException>(() => DataMigration.EnsureAsync(source, destination));
        Check(!Directory.Exists(destination) && !Directory.EnumerateDirectories(parent, ".SwayMusic.migration-*").Any(),
            "An in-use source file must not leave partial SwayMusic data or an owned stage.");
        Check((await DataMigration.EnsureAsync(source, destination)).Status == MigrationStatus.Migrated,
            "After the old application releases its files, migration should retry successfully.");
    }

    private static async Task ConcurrentAsync(string parent)
    {
        string source = Path.Combine(parent, "Metrolist"), destination = Path.Combine(parent, "SwayMusic");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "library.json"), "{\"schemaVersion\":1}");
        var results = await Task.WhenAll(DataMigration.EnsureAsync(source, destination), DataMigration.EnsureAsync(source, destination));
        Check(results.Count(result => result.Status == MigrationStatus.Migrated) == 1, "Concurrent calls should commit once and preserve the same destination.");
    }

    private static async Task RejectNestedAsync(string parent)
    {
        Directory.CreateDirectory(parent);
        await ExpectAsync<ArgumentException>(() => DataMigration.EnsureAsync(parent, Path.Combine(parent, "nested")));
        await ExpectAsync<ArgumentException>(() => DataMigration.EnsureAsync(parent, parent));
        await ExpectAsync<ArgumentException>(() => DataMigration.EnsureAsync(Path.GetPathRoot(parent)!, parent));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
