using System.Text.Json;

namespace SwayMusic.Core.Storage;

public sealed class LibraryStore : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwayMusic");

    public LibraryStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? DefaultDirectory;
        _path = Path.Combine(DataDirectory, "library.json");
    }

    public string DataDirectory { get; }
    public LibraryState State { get; private set; } = new();
    public string? RecoveryMessage { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                State = await AtomicFile.ReadJsonAsync<LibraryState>(_path, cancellationToken).ConfigureAwait(false) ?? new();
                if (State.SchemaVersion != 1)
                    throw new InvalidDataException($"Library schema {State.SchemaVersion} is not supported by this version.");
                Normalize(State);
            }
            catch (JsonException)
            {
                // Preserve unreadable user data instead of overwriting it during the next save.
                var backup = _path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
                File.Move(_path, backup);
                RecoveryMessage = $"The library could not be read. The original was preserved at {backup}.";
                State = new();
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public bool IsLiked(string trackId) => State.LikedTracks.Exists(t => t.Id == trackId);

    public async Task<bool> ToggleLikeAsync(Track track, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int index = State.LikedTracks.FindIndex(t => t.Id == track.Id);
            Track? previous = index >= 0 ? State.LikedTracks[index] : null;
            if (index >= 0) State.LikedTracks.RemoveAt(index);
            else State.LikedTracks.Insert(0, track);
            try { await SaveCoreAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                if (previous is not null) State.LikedTracks.Insert(index, previous);
                else State.LikedTracks.RemoveAt(0);
                throw;
            }
            return index < 0;
        }
        finally { _gate.Release(); }
    }

    public async Task<LocalPlaylist> CreatePlaylistAsync(string name, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var playlist = new LocalPlaylist { Name = name };
            State.Playlists.Add(playlist);
            try { await SaveCoreAsync(cancellationToken).ConfigureAwait(false); }
            catch { State.Playlists.Remove(playlist); throw; }
            return playlist;
        }
        finally { _gate.Release(); }
    }

    public async Task RenamePlaylistAsync(string playlistId, string name, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var playlist = GetPlaylist(playlistId);
            string previous = playlist.Name;
            playlist.Name = name;
            try { await SaveCoreAsync(cancellationToken).ConfigureAwait(false); }
            catch { playlist.Name = previous; throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int index = State.Playlists.FindIndex(p => p.Id == playlistId);
            if (index < 0) return;
            var playlist = State.Playlists[index];
            State.Playlists.RemoveAt(index);
            try { await SaveCoreAsync(cancellationToken).ConfigureAwait(false); }
            catch { State.Playlists.Insert(index, playlist); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task AddToPlaylistAsync(string playlistId, Track track, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The Android app supports duplicate playlist entries; preserve their order here too.
            var tracks = GetPlaylist(playlistId).Tracks;
            int index = tracks.Count;
            tracks.Add(track);
            try { await SaveCoreAsync(cancellationToken).ConfigureAwait(false); }
            catch { tracks.RemoveAt(index); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveFromPlaylistAsync(string playlistId, int index, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tracks = GetPlaylist(playlistId).Tracks;
            if (index < 0 || index >= tracks.Count) throw new ArgumentOutOfRangeException(nameof(index));
            var track = tracks[index];
            tracks.RemoveAt(index);
            try { await SaveCoreAsync(cancellationToken).ConfigureAwait(false); }
            catch { tracks.Insert(index, track); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task AddHistoryAsync(Track track, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = State.History.ToArray();
            State.History.RemoveAll(h => h.Track.Id == track.Id);
            State.History.Insert(0, new(track, DateTimeOffset.UtcNow));
            if (State.History.Count > 500) State.History.RemoveRange(500, State.History.Count - 500);
            try { await SaveCoreAsync(cancellationToken).ConfigureAwait(false); }
            catch { State.History.Clear(); State.History.AddRange(previous); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = State.History.ToArray();
            State.History.Clear();
            try { await SaveCoreAsync(cancellationToken).ConfigureAwait(false); }
            catch { State.History.AddRange(previous); throw; }
        }
        finally { _gate.Release(); }
    }

    private Task SaveCoreAsync(CancellationToken cancellationToken) => AtomicFile.WriteJsonAsync(_path, State, cancellationToken);

    private LocalPlaylist GetPlaylist(string id) => State.Playlists.Find(p => p.Id == id)
        ?? throw new KeyNotFoundException("The playlist no longer exists.");

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 200) throw new ArgumentException("Playlist names must be 200 characters or fewer.", nameof(name));
        return name.Trim();
    }

    private static void Normalize(LibraryState state)
    {
        state.Settings ??= new();
        state.LikedTracks ??= [];
        state.Playlists ??= [];
        state.History ??= [];
        state.Session ??= new();
        state.Session.Queue ??= [];
        state.Settings.Volume = double.IsFinite(state.Settings.Volume) ? Math.Clamp(state.Settings.Volume, 0, 1) : 0.8;
        state.Settings.DownloadBudgetBytes = Math.Max(0, state.Settings.DownloadBudgetBytes);
        state.Settings.CacheBudgetBytes = Math.Max(0, state.Settings.CacheBudgetBytes);
        foreach (var playlist in state.Playlists) playlist.Tracks ??= [];
        if (state.History.Count > 500) state.History.RemoveRange(500, state.History.Count - 500);
        state.Session.CurrentIndex = Math.Clamp(state.Session.CurrentIndex, -1, state.Session.Queue.Count - 1);
        if (state.Session.Position < TimeSpan.Zero) state.Session.Position = TimeSpan.Zero;
    }

    public void Dispose() => _gate.Dispose();
}
