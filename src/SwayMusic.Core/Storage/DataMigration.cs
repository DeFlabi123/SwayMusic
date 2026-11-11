using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwayMusic.Core.Storage;

public enum MigrationStatus { NoLegacyData, DestinationAlreadyExists, Migrated }

public sealed record MigrationResult(MigrationStatus Status, string? Message, string SourceDirectory, string DestinationDirectory);

public static class DataMigration
{
    private const string MarkerName = ".migration-metrolist.json";
    private const long MaximumJsonBytes = 64L * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static Task<MigrationResult> EnsureAsync(CancellationToken cancellationToken = default) => EnsureAsync(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Metrolist"),
        LibraryStore.DefaultDirectory, cancellationToken);

    public static async Task<MigrationResult> EnsureAsync(string sourceDirectory, string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        if (IsWithin(source, destination) || IsWithin(destination, source))
            throw new ArgumentException("Legacy and SwayMusic data directories must be separate, non-nested directories.");
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(source)) return new(MigrationStatus.NoLegacyData, null, source, destination);
            if (File.Exists(destination) || HasEntries(destination)) return Existing(source, destination);
            var files = Snapshot(source);
            if (files.Count == 0) return new(MigrationStatus.NoLegacyData, null, source, destination);
            string parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("The destination needs a parent directory.");
            Directory.CreateDirectory(parent);
            staging = Path.Combine(parent, "." + Path.GetFileName(destination) + ".migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string copied = Path.Combine(staging, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
                await using (var input = new FileStream(Path.Combine(source, file.RelativePath), FileMode.Open,
                                 FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(copied, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                var originalHash = await HashAsync(Path.Combine(source, file.RelativePath), cancellationToken).ConfigureAwait(false);
                var copiedHash = await HashAsync(copied, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(originalHash, copiedHash))
                    throw new IOException("Legacy data changed while it was copied. Close Metrolist and retry SwayMusic.");
            }
            var after = Snapshot(source);
            if (!files.SequenceEqual(after))
                throw new IOException("Legacy data changed during migration. Close Metrolist and retry SwayMusic.");

            foreach (var file in files)
            {
                if (!file.RelativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                string path = Path.Combine(staging, file.RelativePath);
                await RewritePathsAsync(path, source, destination, cancellationToken).ConfigureAwait(false);
            }
            string libraryPath = Path.Combine(staging, "library.json");
            if (File.Exists(libraryPath))
            {
                var library = await AtomicFile.ReadJsonAsync<LibraryState>(libraryPath, cancellationToken).ConfigureAwait(false);
                if (library is null || library.SchemaVersion != 1)
                    throw new InvalidDataException("The legacy library schema is not supported. The original data has been retained.");
            }
            await AtomicFile.WriteJsonAsync(Path.Combine(staging, MarkerName), new
            {
                Version = 1, SourceDirectory = source, MigratedAtUtc = DateTimeOffset.UtcNow, FileCount = files.Count
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destination) || HasEntries(destination)) return Existing(source, destination);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: false);
            try { Directory.Move(staging, destination); }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { return Existing(source, destination); }
            staging = null;
            return new(MigrationStatus.Migrated,
                "Your Metrolist library, settings and downloads were copied to SwayMusic. The original Metrolist folder was retained as a backup.",
                source, destination);
        }
        finally
        {
            try
            {
                if (staging is not null && Directory.Exists(staging))
                {
                    string resolved = Path.GetFullPath(staging);
                    string expectedParent = Path.GetDirectoryName(destination)!;
                    string expectedPrefix = "." + Path.GetFileName(destination) + ".migration-";
                    if (!string.Equals(Path.GetDirectoryName(resolved), expectedParent, PathComparison)
                        || !Path.GetFileName(resolved).StartsWith(expectedPrefix, StringComparison.Ordinal))
                        throw new IOException("Refusing to remove a migration stage outside its expected directory.");
                    Directory.Delete(resolved, recursive: true);
                }
            }
            finally { Gate.Release(); }
        }
    }

    private static MigrationResult Existing(string source, string destination) => new(MigrationStatus.DestinationAlreadyExists,
        File.Exists(Path.Combine(destination, MarkerName)) ? null :
            "Existing SwayMusic data was kept. Your separate Metrolist folder was not changed or merged.", source, destination);

    private static bool HasEntries(string path) => Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
    private static bool IsWithin(string candidate, string root) => string.Equals(candidate, root, PathComparison)
        || candidate.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison);

    private static List<FileSnapshot> Snapshot(string root)
    {
        var result = new List<FileSnapshot>();
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Migration does not follow linked data directories. The original data has been retained.");
            foreach (string path in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Migration does not follow linked data files. The original data has been retained.");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(path);
                else
                {
                    var info = new FileInfo(path);
                    result.Add(new(Path.GetRelativePath(root, path), info.Length, info.LastWriteTimeUtc.Ticks));
                }
            }
        }
        result.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        return result;
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RewritePathsAsync(string path, string source, string destination, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > MaximumJsonBytes)
            throw new InvalidDataException("A legacy metadata file exceeds the migration limit. The original data has been retained.");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
        var node = await JsonNode.ParseAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("A legacy metadata file contains no JSON value.");
        bool changed = Rewrite(node, source, destination);
        await input.DisposeAsync().ConfigureAwait(false);
        if (changed) await AtomicFile.WriteJsonAsync(path, node, cancellationToken).ConfigureAwait(false);
    }

    private static bool Rewrite(JsonNode node, string source, string destination)
    {
        bool changed = false;
        if (node is JsonObject properties)
        {
            foreach (var pair in properties.ToArray())
            {
                if (pair.Key.Equals("localPath", StringComparison.OrdinalIgnoreCase) && pair.Value is JsonValue value
                    && value.TryGetValue<string>(out string? path) && path is not null && Path.IsPathFullyQualified(path))
                {
                    string fullPath = Path.GetFullPath(path);
                    if (IsWithin(fullPath, source))
                    {
                        properties[pair.Key] = Path.Combine(destination, Path.GetRelativePath(source, fullPath));
                        changed = true;
                    }
                }
                else if (pair.Value is { } child) changed |= Rewrite(child, source, destination);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) if (child is not null) changed |= Rewrite(child, source, destination);
        return changed;
    }

    private sealed record FileSnapshot(string RelativePath, long Length, long LastWriteTicks);
}
