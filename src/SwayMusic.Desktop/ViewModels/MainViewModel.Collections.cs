using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using SwayMusic.Core;
using SwayMusic.Core.Storage;

namespace SwayMusic.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _cloudOperations = new(1, 1);
    private readonly bool _autoSyncAccount;
    private CancellationTokenSource _accountLibraryLifetime = new();
    private CancellationTokenSource? _syncRequest;
    private Task? _syncTask;
    private string? _cloudKey;
    private AccountLibrarySnapshot _cloud = new();
    private bool _isSyncing;
    private string _syncStatus = "Sign in to sync your YouTube Music library.";
    public ICommand ArtistsCommand { get; private set; } = null!;
    public ICommand SyncLibraryCommand { get; private set; } = null!;
    public ICommand CancelLibrarySyncCommand { get; private set; } = null!;
    public ICommand ToggleArtistSubscriptionCommand { get; private set; } = null!;
    public bool IsArtists => _pageKind == "artists";
    public bool IsArtistPage => _pageKind == "artist";
    public string ArtistSubscriptionText => IsArtists || _cloud.Artists.Any(a => (a.ChannelId ?? a.Id) == _browseId) ? "Unsubscribe" : "Subscribe";
    public bool CanChangeArtistSubscription => IsSignedIn && (IsArtists ? SelectedBrowseItem is not null : IsArtistPage);
    public string BrowseIconKind => IsArtists ? "artist" : "playlist";
    public bool IsAccountCollection => _pageKind is "likes" or "playlists" or "artists";
    public bool IsSyncing { get => _isSyncing; private set => Set(ref _isSyncing, value); }
    public string SyncStatus { get => _syncStatus; private set => Set(ref _syncStatus, value); }
    public IEnumerable<LocalPlaylist> PlaylistTargets => Playlists.Where(p => !p.IsRemote || p.CanEdit);

    private void InitializeCollections()
    {
        ArtistsCommand = Async(() => ShowAccountCollectionAsync("artists"));
        SyncLibraryCommand = Async(() => EnsureAccountLibraryAsync(force: true));
        CancelLibrarySyncCommand = new RelayCommand(() => _syncRequest?.Cancel());
        ToggleArtistSubscriptionCommand = Async(ToggleArtistSubscriptionAsync);
    }
    private string? CloudCachePath(string? key) => key is null ? null : Path.Combine(_library.DataDirectory, "account-library", key + ".json");
    private void ResetAccountLibrary(AccountSession? session)
    {
        _accountLibraryLifetime.Cancel(); _accountLibraryLifetime.Dispose();
        _accountLibraryLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _syncRequest?.Cancel(); _syncTask = null;
        string? identity = session is null ? null : !string.IsNullOrWhiteSpace(session.ChannelId) ? session.ChannelId.Trim()
            : !string.IsNullOrWhiteSpace(session.DataSyncId) ? session.DataSyncId.Trim() : session.CookieHeader;
        _cloudKey = session is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "|" + session.AuthUser)));
        _cloud = new() { AccountKey = _cloudKey ?? "" };
        OnPropertyChanged(nameof(IsCurrentLiked));
        SyncStatus = session is null ? "Sign in to sync your YouTube Music library." : "Library has not synced yet.";
        IsSyncing = false;
        RefreshPlaylists();
        if (IsAccountCollection)
        {
            if (IsSignedIn) RenderAccountCollection(_pageKind);
            else if (_pageKind == "likes") ShowLocal("likes");
            else if (_pageKind == "playlists") ShowPlaylists();
            else { BrowseItems.Clear(); PageSubtitle = "Sign in with Google to see your subscribed artists."; }
        }
        if (IsArtistPage || _pageKind == "online-playlist")
        { SetPage("home", "Home", "Search for music or load your account recommendations."); ShowTracksFor(_library.State.History.Select(h => h.Track)); }
        if (IsReady && IsSignedIn && _autoSyncAccount) TrackBackground(EnsureAccountLibraryAsync(force: true));
    }
    private Task EnsureAccountLibraryAsync(bool force = false)
    {
        if (!IsSignedIn || _cloudKey is null || _disposed) return Task.CompletedTask;
        if (_syncTask is { IsCompleted: false }) return _syncTask;
        if (!force && _cloud.SyncedAt > DateTimeOffset.UtcNow.AddSeconds(-30)) return Task.CompletedTask;
        return _syncTask = SyncAccountLibraryAsync(_cloudKey, _accountLibraryLifetime.Token);
    }
    private async Task SyncAccountLibraryAsync(string key, CancellationToken accountToken)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(accountToken, _lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(60));
        _syncRequest = request; IsSyncing = true; SyncStatus = "Syncing your YouTube Music library…";
        try
        {
            await _cloudOperations.WaitAsync(request.Token);
            try
            {
                string path = CloudCachePath(key)!;
                if (_cloud.SyncedAt == default)
                {
                    try
                    {
                        if (File.Exists(path) && new FileInfo(path).Length <= 20 * 1024 * 1024)
                        {
                            var cached = await AtomicFile.ReadJsonAsync<AccountLibrarySnapshot>(path, request.Token);
                            request.Token.ThrowIfCancellationRequested();
                            if (key == _cloudKey && cached is { Version: 1, LikedSongs: not null, Playlists: not null, Artists: not null } && cached.AccountKey == key)
                            { _cloud = cached; RefreshCloudViews(); }
                        }
                    }
                    catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
                }
                var snapshot = await _music.GetAccountLibraryAsync(request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (key != _cloudKey || _disposed) return;
                snapshot.AccountKey = key;
                await AtomicFile.WriteJsonAsync(path, snapshot, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (key != _cloudKey || _disposed) return;
                _cloud = snapshot; RefreshCloudViews();
                SyncStatus = $"Synced · Liked songs: {snapshot.LikedSongs.Count:N0} · Playlists: {snapshot.Playlists.Count:N0} · Artists: {snapshot.Artists.Count:N0}";
            }
            finally { _cloudOperations.Release(); }
        }
        catch (OperationCanceledException)
        {
            if (key == _cloudKey && !_disposed) SyncStatus = accountToken.IsCancellationRequested ? "Sync cancelled." : "Sync cancelled or timed out. Your last synced library was kept.";
        }
        catch (Exception error)
        {
            if (key == _cloudKey && !_disposed) SyncStatus = "Sync failed. Your last synced library was kept. " + error.Message;
        }
        finally
        {
            if (_syncRequest == request) { _syncRequest = null; IsSyncing = false; }
        }
    }
    private void RefreshCloudViews()
    {
        OnPropertyChanged(nameof(IsCurrentLiked));
        RefreshPlaylists();
        OnPropertyChanged(nameof(ArtistSubscriptionText)); OnPropertyChanged(nameof(CanChangeArtistSubscription));
        if (IsAccountCollection && IsSignedIn) RenderAccountCollection(_pageKind);
        else if (IsLibrary) ShowLibrary();
    }
    private async Task ShowAccountCollectionAsync(string kind)
    {
        if (!IsSignedIn)
        {
            if (kind == "likes") ShowLocal("likes");
            else if (kind == "playlists") ShowPlaylists();
            else { SetPage("artists", "Artists", "Sign in with Google to see your subscribed artists."); BrowseItems.Clear(); }
            return;
        }
        RenderAccountCollection(kind);
        await EnsureAccountLibraryAsync();
    }
    private void RenderAccountCollection(string kind)
    {
        if (kind == "playlists") { ShowPlaylists(); PageSubtitle = "Your YouTube Music playlists and saved playlists, plus playlists on this device."; return; }
        SetPage(kind, kind == "artists" ? "Artists" : "Liked songs", "Your YouTube Music account · use Sync library to refresh changes from other devices.");
        if (kind == "likes") ShowTracksFor(_cloud.LikedSongs);
        else
        {
            BrowseItems.Clear(); foreach (var item in _cloud.Artists) BrowseItems.Add(item);
            SelectedBrowseItem = BrowseItems.FirstOrDefault();
        }
    }
    private async Task MutateCloudAsync(Func<CancellationToken, Task> mutation)
    {
        var key = _cloudKey;
        if (key is null || !IsSignedIn) throw new InvalidOperationException("Sign in with Google first.");
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_accountLibraryLifetime.Token, _lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(45));
        await _cloudOperations.WaitAsync(request.Token);
        try
        {
            request.Token.ThrowIfCancellationRequested();
            if (key != _cloudKey) return;
            await mutation(request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (key != _cloudKey) return;
            try { await AtomicFile.WriteJsonAsync(CloudCachePath(key)!, _cloud, request.Token); SyncStatus = "Account change saved to YouTube Music."; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SyncStatus = "The account change was saved, but its local cache could not be written."; }
            RefreshCloudViews();
        }
        catch (OperationCanceledException)
        {
            if (key == _cloudKey && !_disposed) Status = "The account change was interrupted. Refresh your library before retrying; the request may have reached YouTube Music.";
            throw;
        }
        finally { _cloudOperations.Release(); }
    }
    private async Task ToggleCloudLikeAsync(Track track, bool? desired = null)
    {
        var generation = _accountGeneration; var key = _cloudKey;
        await EnsureAccountLibraryAsync();
        if (generation != _accountGeneration || key != _cloudKey || !IsSignedIn) throw new OperationCanceledException();
        if (_cloud.SyncedAt == default && desired is null) throw new InvalidOperationException("Sync your account library before changing a song's like status.");
        await MutateCloudAsync(async token =>
        {
            bool like = desired ?? !_cloud.LikedSongs.Any(t => t.Id == track.Id);
            await _music.SetVideoLikeAsync(track.Id, like, token); token.ThrowIfCancellationRequested();
            _cloud.LikedSongs.RemoveAll(t => t.Id == track.Id); if (like) _cloud.LikedSongs.Insert(0, track);
            Status = like ? "Added to your YouTube Music liked songs." : "Removed from your YouTube Music liked songs.";
        });
    }
    private async Task ToggleArtistSubscriptionAsync()
    {
        if (!IsSignedIn) { Status = "Sign in with Google to subscribe to artists."; return; }
        var artist = IsArtists ? SelectedBrowseItem : IsArtistPage && _browseId is { } id ? new BrowseItem(id, PageTitle, "", null, "remote-artist", ChannelId: id) : null;
        if (artist is null) return;
        string channel = artist.ChannelId ?? artist.Id;
        var generation = _accountGeneration; var key = _cloudKey;
        await EnsureAccountLibraryAsync();
        if (generation != _accountGeneration || key != _cloudKey || !IsSignedIn) throw new OperationCanceledException();
        if (_cloud.SyncedAt == default) throw new InvalidOperationException("Sync your account library before changing an artist subscription.");
        await MutateCloudAsync(async token =>
        {
            bool subscribe = !_cloud.Artists.Any(a => (a.ChannelId ?? a.Id) == channel);
            await _music.SetArtistSubscriptionAsync(channel, subscribe, token); token.ThrowIfCancellationRequested();
            _cloud.Artists.RemoveAll(a => (a.ChannelId ?? a.Id) == channel);
            if (subscribe) _cloud.Artists.Add(artist with { Kind = "remote-artist", ChannelId = channel });
            Status = subscribe ? "Subscribed on YouTube Music." : "Unsubscribed on YouTube Music.";
        });
    }
}
