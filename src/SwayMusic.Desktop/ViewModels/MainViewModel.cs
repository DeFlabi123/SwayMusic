using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SwayMusic.Core;
using SwayMusic.Core.Downloads;
using SwayMusic.Core.Lyrics;
using SwayMusic.Core.Networking;
using SwayMusic.Core.Storage;
using SwayMusic.Desktop.Playback;
using SwayMusic.Desktop.Authentication;

namespace SwayMusic.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly LibraryStore _library;
    private readonly CredentialStore _credentials;
    private readonly YouTubeMusicClient _music;
    private readonly TrackSourcePrefetcher _sourcePrefetch;
    private readonly LyricsService _lyrics;
    private DownloadService _downloads;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _accountOperations = new(1, 1);
    private readonly SemaphoreSlim _rowArtworkOperations = new(4, 4);
    private Task? _initializationTask;
    private readonly List<AsyncCommand> _commands = [];
    private readonly HashSet<Task> _backgroundTasks = [];
    private CancellationTokenSource? _pageRequest;
    private CancellationTokenSource? _artworkRequest;
    private CancellationTokenSource? _lyricsRequest;
    private string? _highlightedTrackId;
    private int _highlightedQueueIndex = -1;
    private readonly DispatcherTimer _positionTimer;
    private LyricsDocument? _lyricDocument;
    private string? _continuation, _browseId, _lastTrackId, _localPlaylistId;
    private string _pageKind = "home", _query = "", _pageTitle = "Home", _pageSubtitle = "Search for music, open local audio, or load your recommendations.";
    private string _status = "", _newPlaylistName = "", _cookieInput = "", _accountStatus = "Not signed in";
    private string _lyricsText = "Play a song, then open Lyrics.", _activeLyric = "";
    private bool _busy, _visible = true, _disposed, _initialized, _isSeeking;
    private TrackRow? _selectedTrack;
    private BrowseItem? _selectedBrowseItem;
    private LocalPlaylist? _selectedPlaylist;
    private Bitmap? _artwork;
    private string? _artworkTrackId, _artworkUrl;
    private TimeSpan _restoredPosition;
    private bool _restoreSeekPending, _sessionRestored;

    public MainViewModel(string? dataDirectory = null, IBrowserLoginService? browserLogin = null, HttpMessageHandler? httpHandler = null, bool? autoSyncAccount = null)
    {
        _http = new(httpHandler ?? new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 4
        }) { Timeout = TimeSpan.FromSeconds(45) };
        _browserLogin = browserLogin ?? new BrowserLoginService();
        _library = new(dataDirectory);
        _autoSyncAccount = autoSyncAccount ?? dataDirectory is null;
        _migrateLegacy = dataDirectory is null;
        _credentials = new(dataDirectory);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SwayMusic/1.0");
        _music = new(_http) { EnableNativePlayback = true, PlaybackCacheDirectory = Path.Combine(_library.DataDirectory, "player-cache") };
        _lyrics = new(_http, dataDirectory);
        _downloads = new(_http, _library.DataDirectory, 2L * 1024 * 1024 * 1024);
        _sourcePrefetch = new(ResolveAsync);
        Player = new(_sourcePrefetch.ResolveAsync, async (uri, token) => await HttpRangeStream.OpenAsync(_http, uri, token));
        Player.Changed += OnPlayerChanged;
        Player.PlaybackFailed += OnPlayerFailed;
        _positionTimer = new(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdatePosition());
        _positionTimer.Stop();
        SearchCommand = Async(SearchAsync);
        HomeCommand = Async(LoadHomeAsync);
        LikesCommand = Async(() => ShowAccountCollectionAsync("likes"));
        HistoryCommand = new RelayCommand(() => ShowLocal("history"));
        DownloadsCommand = new RelayCommand(() => ShowLocal("downloads"));
        QueueCommand = new RelayCommand(ToggleQueuePanel);
        PlaylistsCommand = Async(() => ShowAccountCollectionAsync("playlists"));
        SettingsCommand = new RelayCommand(() => SetPage("settings", "Settings & account", "Your settings and library are stored locally."));
        LyricsCommand = Async(ToggleLyricsPanelAsync);
        PlaySelectedCommand = Async(PlaySelectedAsync);
        ShuffleCollectionCommand = Async(ShuffleCollectionAsync);
        ToggleCommand = new RelayCommand(Player.Toggle);
        NextCommand = Async(() => Player.NextAsync(_lifetime.Token));
        PreviousCommand = Async(() => Player.PreviousAsync(_lifetime.Token));
        EnqueueCommand = new RelayCommand(() => { if (SelectedTrack is { } row) { Player.Enqueue(row.Track); Status = "Added to queue."; } });
        LikeCommand = Async(LikeAsync);
        LikeCurrentCommand = Async(() => LikeTrackAsync(Player.CurrentTrack));
        RadioCommand = Async(RadioAsync);
        DownloadCommand = Async(DownloadSelectedAsync);
        RemoveCommand = Async(RemoveAsync);
        MoveUpCommand = new RelayCommand(() => MoveQueue(-1));
        MoveDownCommand = new RelayCommand(() => MoveQueue(1));
        RepeatCommand = new RelayCommand(() => { Player.Repeat = (RepeatMode)(((int)Player.Repeat + 1) % 3); OnPropertyChanged(nameof(RepeatText)); });
        MoreCommand = Async(LoadMoreAsync);
        CreatePlaylistCommand = Async(CreatePlaylistAsync);
        RenamePlaylistCommand = Async(RenamePlaylistAsync);
        DeletePlaylistCommand = Async(DeletePlaylistAsync);
        OpenPlaylistCommand = new RelayCommand(() => { if (SelectedPlaylist is { } playlist) OpenLocalPlaylist(playlist); });
        AddToPlaylistCommand = Async(AddToPlaylistAsync);
        SaveAccountCommand = Async(SaveAccountAsync);
        SignOutCommand = Async(SignOutAsync);
        SignInCommand = Async(SignInWithGoogleAsync);
        CancelSignInCommand = new RelayCommand(() => _signInRequest?.Cancel());
        SaveSettingsCommand = Async(SaveSettingsAsync);
        CancelCommand = new RelayCommand(() => _pageRequest?.Cancel());
        InitializeDesign();
        InitializeCollections();
    }

    private AsyncCommand Async(Func<Task> action)
    {
        var command = new AsyncCommand(action, ReportError);
        _commands.Add(command);
        return command;
    }
    public PlaybackService Player { get; }
    public ObservableCollection<TrackRow> Tracks { get; } = [];
    public ObservableCollection<BrowseItem> BrowseItems { get; } = [];
    public ObservableCollection<LocalPlaylist> Playlists { get; } = [];
    public TrackRow? SelectedTrack { get => _selectedTrack; set => Set(ref _selectedTrack, value); }
    public BrowseItem? SelectedBrowseItem { get => _selectedBrowseItem; set { if (Set(ref _selectedBrowseItem, value)) OnPropertyChanged(nameof(CanChangeArtistSubscription)); } }
    public LocalPlaylist? SelectedPlaylist { get => _selectedPlaylist; set => Set(ref _selectedPlaylist, value); }
    public string Query { get => _query; set => Set(ref _query, value); }
    public string PageTitle { get => _pageTitle; private set => Set(ref _pageTitle, value); }
    public string PageSubtitle { get => _pageSubtitle; private set => Set(ref _pageSubtitle, value); }
    public string Status { get => _status; private set { if (Set(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => Status.Length > 0;
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) { OnPropertyChanged(nameof(ShowEmpty)); OnPropertyChanged(nameof(CanRetryStartup)); } } }
    public bool ShowSettings => _pageKind == "settings";
    public bool ShowLyrics => _pageKind == "lyrics";
    public bool ShowBrowse => _pageKind is "playlists" or "browse-items" or "artists";
    public bool ShowTracks => !ShowSettings && !ShowLyrics && !ShowBrowse;
    public bool ShowCreatePlaylist => _pageKind == "playlists";
    public bool IsQueue => _pageKind == "queue";
    public bool HasMore => _continuation is not null;
    public bool HasPlaylists => PlaylistTargets.Any();
    public string NewPlaylistName { get => _newPlaylistName; set => Set(ref _newPlaylistName, value); }
    public string CookieInput { get => _cookieInput; set => Set(ref _cookieInput, value); }
    public string AccountStatus { get => _accountStatus; private set => Set(ref _accountStatus, value); }
    public bool FetchLyrics { get => _library.State.Settings.FetchLyrics; set { _library.State.Settings.FetchLyrics = value; OnPropertyChanged(); } }
    public string Country { get => _library.State.Settings.Country; set { _library.State.Settings.Country = value; OnPropertyChanged(); } }
    public string Language { get => _library.State.Settings.Language; set { _library.State.Settings.Language = value; OnPropertyChanged(); } }
    public decimal DownloadBudgetMiB { get => _library.State.Settings.DownloadBudgetBytes / (1024m * 1024); set { _library.State.Settings.DownloadBudgetBytes = (long)(value * 1024 * 1024); OnPropertyChanged(); } }
    public double Volume { get => Player.Volume * 100; set { Player.Volume = (float)(value / 100); OnPropertyChanged(); } }
    public bool Shuffle { get => Player.Shuffle; set { Player.Shuffle = value; OnPropertyChanged(); } }
    public string RepeatText => "Repeat: " + Player.Repeat;
    public string PlayButtonText => Player.State == PlayerState.Playing ? "Pause" : Player.State == PlayerState.Loading ? "Cancel" : "Play";
    public string NowTitle => Player.CurrentTrack?.Title ?? "Nothing playing";
    public string NowArtist => Player.CurrentTrack?.Artist ?? "Choose a song to begin";
    public Bitmap? Artwork { get => _artwork; private set => Set(ref _artwork, value); }
    public double DurationSeconds => Math.Max(1, Player.Duration.TotalSeconds);
    public double PositionSeconds => Player.Position.TotalSeconds;
    public string PositionText => FormatTime(Player.Position);
    public string DurationText => FormatTime(Player.Duration);
    public string LyricsText { get => _lyricsText; private set => Set(ref _lyricsText, value); }
    public string ActiveLyric { get => _activeLyric; private set => Set(ref _activeLyric, value); }

    public ICommand SearchCommand { get; }
    public ICommand HomeCommand { get; }
    public ICommand LikesCommand { get; }
    public ICommand HistoryCommand { get; }
    public ICommand DownloadsCommand { get; }
    public ICommand QueueCommand { get; }
    public ICommand PlaylistsCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand LyricsCommand { get; }
    public ICommand PlaySelectedCommand { get; }
    public ICommand ShuffleCollectionCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand EnqueueCommand { get; }
    public ICommand LikeCommand { get; }
    public ICommand RadioCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand RepeatCommand { get; }
    public ICommand MoreCommand { get; }
    public ICommand CreatePlaylistCommand { get; }
    public ICommand RenamePlaylistCommand { get; }
    public ICommand DeletePlaylistCommand { get; }
    public ICommand OpenPlaylistCommand { get; }
    public ICommand AddToPlaylistCommand { get; }
    public ICommand SaveAccountCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand CancelCommand { get; }

    public Task InitializeAsync() => _initializationTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        Busy = true; Status = "Preparing your library…";
        try
        {
            string? migrationMessage = null;
            if (_migrateLegacy) migrationMessage = (await DataMigration.EnsureAsync(_lifetime.Token)).Message;
            await _library.LoadAsync(_lifetime.Token);
            _initialized = true;
            await _accountOperations.WaitAsync(_lifetime.Token);
            try
            {
                try { ApplyAccountSession(await _credentials.LoadAccountAsync(_lifetime.Token)); }
                catch (Exception error) when (error is IOException or CryptographicException or UnauthorizedAccessException)
                {
                    ApplyAccountSession(null);
                    AccountStatus = "Your saved session could not be read. Sign in again to reconnect.";
                }
            }
            finally { _accountOperations.Release(); }
            _lifetime.Token.ThrowIfCancellationRequested();
            _music.Country = _library.State.Settings.Country;
            _music.Language = _library.State.Settings.Language;
            Player.Volume = (float)_library.State.Settings.Volume;
            Player.Shuffle = _library.State.Settings.Shuffle;
            if (Enum.TryParse<RepeatMode>(_library.State.Settings.RepeatMode, out var repeat)) Player.Repeat = repeat;
            _downloads.BudgetBytes = _library.State.Settings.DownloadBudgetBytes;
            RestoreDesignPreferences();
            RefreshPlaylists();
            var session = _library.State.Session;
            Player.RestoreQueue(session.Queue, session.CurrentIndex);
            _sessionRestored = true;
            _restoredPosition = session.Position;
            _restoreSeekPending = _restoredPosition > TimeSpan.Zero;
            foreach (var property in new[] { nameof(Country), nameof(Language), nameof(DownloadBudgetMiB), nameof(FetchLyrics), nameof(Volume), nameof(Shuffle), nameof(RepeatText) }) OnPropertyChanged(property);
            if (_library.RecoveryMessage is { } recovery) Status = recovery;
            else if (!string.IsNullOrWhiteSpace(migrationMessage)) Status = migrationMessage;
            else Status = "";
            if (_library.State.History.Count > 0)
            {
                ShowTracksFor(_library.State.History.Select(h => h.Track));
                PageSubtitle = "Recently played. Select Home to load online recommendations.";
            }
            RefreshHomeCards();
            IsReady = true;
            if (IsSignedIn && _autoSyncAccount) TrackBackground(EnsureAccountLibraryAsync(force: true));
            if (_migrateLegacy) TrackBackground(_music.PrewarmPlaybackAsync(_lifetime.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
        finally { Busy = false; }
    }

    public void SetWindowVisible(bool visible) { _visible = visible; UpdateTimer(); }
    public (double Width, double Height) GetWindowSize() => (_library.State.Settings.WindowWidth, _library.State.Settings.WindowHeight);
    public void SetWindowSize(double width, double height) { _library.State.Settings.WindowWidth = width; _library.State.Settings.WindowHeight = height; }

    private void SetPage(string kind, string title, string subtitle)
    {
        _pageRequest?.Cancel();
        if (Busy) Status = "";
        Busy = false;
        ClearArtistProfile();
        _pageKind = kind;
        _continuation = null;
        _localPlaylistId = null;
        SelectedTrack = null;
        PageTitle = title;
        PageSubtitle = subtitle;
        foreach (var property in new[] { nameof(ShowTracks), nameof(ShowSettings), nameof(ShowBrowse), nameof(ShowCreatePlaylist), nameof(ShowLyrics), nameof(IsQueue), nameof(HasMore) }) OnPropertyChanged(property);
        RefreshDesignPage();
    }

    private CancellationToken BeginRequest(string message)
    {
        _pageRequest?.Cancel();
        _pageRequest?.Dispose();
        _pageRequest = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Status = message;
        Busy = true;
        return _pageRequest.Token;
    }

    private bool IsCurrentRequest(CancellationToken token) => !_disposed && _pageRequest?.Token == token;
    private void EndRequest(CancellationToken token) { if (IsCurrentRequest(token)) Busy = false; }

    public async Task SearchAsync()
    {
        var query = Query.Trim();
        if (query.Length == 0) return;
        if (Uri.TryCreate(query, UriKind.Absolute, out var uri) && (uri.Host == "youtu.be" || uri.Host == "youtube.com" || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)))
        {
            var parameters = ParseQuery(uri.Query);
            if (parameters.TryGetValue("list", out var list)) { await OpenOnlineBrowseAsync(new(list, "Playlist", "", null, "playlist")); return; }
            var id = parameters.GetValueOrDefault("v") ?? (uri.Host == "youtu.be" ? uri.AbsolutePath.Trim('/') : null);
            if (!string.IsNullOrWhiteSpace(id)) { await Player.PlayTrackAsync(new(id, id, "YouTube Music"), _lifetime.Token); return; }
        }
        SetPage("search", "Search results", query);
        var token = BeginRequest("Searching YouTube Music…");
        try { ApplyPage(await _music.SearchAsync(query, token), token); Status = Tracks.Count == 0 ? "No songs found." : $"{Tracks.Count} songs"; }
        finally { EndRequest(token); }
    }

    private async Task LoadHomeAsync()
    {
        SetPage("home", "Home", "Your YouTube Music recommendations");
        var token = BeginRequest("Loading recommendations…");
        try { ApplyPage(await _music.HomeAsync(token), token); Status = ""; }
        finally { EndRequest(token); }
    }

    private void ApplyPage(MusicPage page, CancellationToken token, bool append = false)
    {
        token.ThrowIfCancellationRequested();
        if (!append) Tracks.Clear();
        foreach (var track in page.Tracks) Tracks.Add(new(track));
        _continuation = page.Continuation;
        OnPropertyChanged(nameof(HasMore));
        if (!IsArtistPage && Tracks.Count == 0 && page.Items is { Count: > 0 })
        {
            _pageKind = "browse-items";
            BrowseItems.Clear();
            foreach (var item in page.Items) BrowseItems.Add(item);
            OnPropertyChanged(nameof(ShowTracks)); OnPropertyChanged(nameof(ShowBrowse));
        }
        SelectedTrack ??= Tracks.FirstOrDefault();
        RefreshHomeCards();
        RefreshDesignPage();
    }

    private async Task LoadMoreAsync()
    {
        if (_continuation is null) return;
        var token = BeginRequest("Loading more…");
        try
        {
            var page = _pageKind == "search" ? await _music.SearchContinuationAsync(_continuation, token) : await _music.BrowseContinuationAsync(_continuation, token);
            ApplyPage(page, token, true); Status = $"{Tracks.Count} songs";
        }
        finally { EndRequest(token); }
    }

    private void ShowLocal(string kind)
    {
        SetPage(kind, kind switch { "likes" => "Liked songs", "history" => "Listening history", "queue" => "Playback queue", _ => "Downloads" }, "Click a song to play. Right-click for more actions.");
        ShowTracksFor(kind switch
        {
            "likes" => _library.State.LikedTracks,
            "history" => _library.State.History.Select(h => h.Track),
            "queue" => Player.Queue,
            _ => _downloads.ListDownloads().Select(d => d.Track with { LocalPath = d.FilePath })
        });
    }

    private void ShowTracksFor(IEnumerable<Track> tracks)
    {
        Tracks.Clear();
        foreach (var track in tracks) Tracks.Add(new(track));
        SelectedTrack = Tracks.FirstOrDefault();
        RefreshDesignPage();
    }

    private void RefreshPlaylists()
    {
        var selected = SelectedPlaylist;
        Playlists.Clear();
        foreach (var playlist in _library.State.Playlists) Playlists.Add(playlist);
        if (IsSignedIn) foreach (var playlist in _cloud.Playlists) Playlists.Add(new()
        { Id = playlist.Id, Name = playlist.Title, IsRemote = true, CanEdit = playlist.CanEdit });
        SelectedPlaylist = Playlists.FirstOrDefault(p => selected is not null && p.Id == selected.Id && p.IsRemote == selected.IsRemote) ?? PlaylistTargets.FirstOrDefault();
        OnPropertyChanged(nameof(HasPlaylists));
        OnPropertyChanged(nameof(PlaylistTargets));
    }

    private void ShowPlaylists()
    {
        SetPage("playlists", "Playlists", "Double-click to open a local playlist. Paste a YouTube playlist URL into Search to browse online.");
        BrowseItems.Clear();
        foreach (var playlist in _library.State.Playlists) BrowseItems.Add(new(playlist.Id, playlist.Name, $"{playlist.Tracks.Count} songs · local playlist", null, "local"));
        if (IsSignedIn) foreach (var playlist in _cloud.Playlists) BrowseItems.Add(playlist with { Subtitle = playlist.Subtitle + (playlist.CanEdit ? " · YouTube Music" : " · YouTube Music · saved playlist") });
        SelectedBrowseItem = BrowseItems.FirstOrDefault();
    }

    private void OpenLocalPlaylist(LocalPlaylist playlist)
    {
        SetPage("local-playlist", playlist.Name, $"{playlist.Tracks.Count} songs · local playlist");
        _localPlaylistId = playlist.Id;
        SelectedPlaylist = playlist;
        ShowTracksFor(playlist.Tracks);
    }

    public async Task OpenSelectedBrowseAsync()
    {
        if (SelectedBrowseItem is not { } item) return;
        if (item.Kind == "local") { var playlist = _library.State.Playlists.Find(p => p.Id == item.Id); if (playlist is not null) OpenLocalPlaylist(playlist); }
        else await OpenOnlineBrowseAsync(item);
    }

    private async Task OpenOnlineBrowseAsync(BrowseItem item)
    {
        if (item.Kind == "remote-artist" || item.Kind.Contains("ARTIST", StringComparison.Ordinal) || item.Id.StartsWith("UC", StringComparison.Ordinal))
        { await OpenArtistAsync(new(item.Title, item.Id)); return; }
        SetPage(item.Kind == "remote-artist" || item.Kind.Contains("ARTIST", StringComparison.Ordinal) ? "artist" : "online-playlist", item.Title, item.Subtitle);
        _browseId = item.Id;
        var token = BeginRequest("Loading music…");
        try { ApplyPage(await _music.BrowseAsync(item.Id, token), token); Status = ""; }
        finally { EndRequest(token); }
    }

    private async Task CreatePlaylistAsync()
    {
        if (IsSignedIn)
        {
            var title = NewPlaylistName.Trim();
            if (title.Length is < 1 or > 150) { Status = "Enter a playlist name of 1–150 characters."; return; }
            await MutateCloudAsync(async token =>
            {
                var id = await _music.CreateAccountPlaylistAsync(title, token); token.ThrowIfCancellationRequested();
                _cloud.Playlists.Insert(0, new(id.StartsWith("VL", StringComparison.Ordinal) ? id : "VL" + id, title, "Private playlist", null, "remote-playlist", CanEdit: true));
                NewPlaylistName = ""; Status = "Private playlist created on YouTube Music.";
            });
            ShowPlaylists(); return;
        }
        var playlist = await _library.CreatePlaylistAsync(NewPlaylistName, _lifetime.Token);
        NewPlaylistName = "";
        RefreshPlaylists();
        SelectedPlaylist = playlist;
        ShowPlaylists();
        Status = "Playlist created.";
    }

    private async Task RenamePlaylistAsync()
    {
        if (SelectedBrowseItem is { Kind: "remote-playlist" } remote)
        {
            if (!remote.CanEdit) { Status = "You can rename only playlists you own."; return; }
            var title = NewPlaylistName.Trim();
            if (title.Length is < 1 or > 150) { Status = "Enter a playlist name of 1–150 characters."; return; }
            await MutateCloudAsync(async token =>
            {
                await _music.EditAccountPlaylistAsync(remote.Id, "rename", title, token); token.ThrowIfCancellationRequested();
                int index = _cloud.Playlists.FindIndex(p => p.Id == remote.Id);
                if (index >= 0) _cloud.Playlists[index] = remote with { Title = title };
                NewPlaylistName = ""; Status = "Playlist renamed on YouTube Music.";
            }); return;
        }
        if (SelectedBrowseItem is not { Kind: "local" } item) { Status = "Select a local playlist first."; return; }
        await _library.RenamePlaylistAsync(item.Id, NewPlaylistName, _lifetime.Token);
        NewPlaylistName = "";
        RefreshPlaylists();
        ShowPlaylists();
        Status = "Playlist renamed.";
    }

    private async Task DeletePlaylistAsync()
    {
        if (SelectedBrowseItem is { Kind: "remote-playlist" } remote)
        {
            await MutateCloudAsync(async token =>
            {
                await _music.DeleteAccountPlaylistAsync(remote.Id, remote.CanEdit, token); token.ThrowIfCancellationRequested();
                _cloud.Playlists.RemoveAll(p => p.Id == remote.Id);
                Status = remote.CanEdit ? "Playlist deleted from YouTube Music." : "Playlist removed from your YouTube Music library.";
            }); return;
        }
        if (SelectedBrowseItem is not { Kind: "local" } item) { Status = "Select a local playlist first."; return; }
        await _library.DeletePlaylistAsync(item.Id, _lifetime.Token);
        if (SelectedPlaylist?.Id == item.Id) SelectedPlaylist = null;
        RefreshPlaylists(); ShowPlaylists();
        Status = "Local playlist removed. Audio files were retained.";
    }

    private async Task AddToPlaylistAsync()
    {
        if (SelectedPlaylist is not { } playlist || SelectedTrack is not { } row) { Status = "Select a song and a playlist first."; return; }
        if (playlist.IsRemote)
        {
            if (!playlist.CanEdit) { Status = "You can add songs only to playlists you own."; return; }
            if (!YouTubeMusicClient.IsVideoId(row.Track.Id)) { Status = "Local audio files cannot be added to a YouTube Music playlist."; return; }
            await MutateCloudAsync(async token => { await _music.EditAccountPlaylistAsync(playlist.Id, "add", row.Track.Id, token); Status = $"Added to {playlist.Name} on YouTube Music."; }); return;
        }
        await _library.AddToPlaylistAsync(playlist.Id, row.Track, _lifetime.Token);
        Status = $"Added to {playlist.Name}.";
    }

    public async Task PlaySelectedAsync()
    {
        if (SelectedTrack is not { } selected) return;
        _restoreSeekPending = false;
        await Player.SetQueueAsync(Tracks.Select(t => t.Track), Tracks.IndexOf(selected), _lifetime.Token);
    }

    public async Task ShuffleCollectionAsync()
    {
        if (Tracks.Count == 0) return;
        var rows = Tracks.ToArray();
        int index = Random.Shared.Next(rows.Length);
        SelectedTrack = rows[index];
        _restoreSeekPending = false;
        await Player.SetQueueAsync(rows.Select(row => row.Track), index, _lifetime.Token);
    }

    private Task LikeAsync() => LikeTrackAsync(SelectedTrack?.Track ?? Player.CurrentTrack);
    private async Task LikeTrackAsync(Track? track)
    {
        if (track is null) return;
        if (IsSignedIn && YouTubeMusicClient.IsVideoId(track.Id)) { await ToggleCloudLikeAsync(track); return; }
        bool liked = await _library.ToggleLikeAsync(track, _lifetime.Token);
        OnPropertyChanged(nameof(IsCurrentLiked));
        Status = liked ? "Added to liked songs." : "Removed from liked songs.";
        if (_pageKind == "likes") ShowLocal("likes");
    }

    private async Task RadioAsync()
    {
        var track = SelectedTrack?.Track ?? Player.CurrentTrack;
        if (track is null || track.LocalPath is not null) return;
        SetPage("radio", "Radio", "Based on " + track.Title);
        var token = BeginRequest("Loading related songs…");
        try { ApplyPage(await _music.RadioAsync(track, token), token); Status = ""; }
        finally { EndRequest(token); }
    }

    private async Task DownloadSelectedAsync()
    {
        var track = SelectedTrack?.Track ?? Player.CurrentTrack;
        if (track is null) return;
        if (track.LocalPath is not null) { Status = "This song is already a local file."; return; }
        var token = BeginRequest("Resolving download…");
        try
        {
            var stream = await _music.GetStreamAsync(track, token);
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (IsCurrentRequest(token) && !token.IsCancellationRequested)
                    Status = $"Downloading {track.Title} · {p.BytesReceived / (1024 * 1024)} MiB";
            });
            await _downloads.DownloadAsync(track, stream.Url, progress, token);
            if (IsCurrentRequest(token) && !token.IsCancellationRequested) Status = "Saved for offline playback.";
        }
        finally { EndRequest(token); }
    }

    private async Task RemoveAsync()
    {
        if (SelectedTrack is not { } row) return;
        int index = Tracks.IndexOf(row);
        if (_pageKind == "queue") { await Player.RemoveAtAsync(index, _lifetime.Token); ShowLocal("queue"); }
        else if (_pageKind == "likes") { if (IsSignedIn) await ToggleCloudLikeAsync(row.Track, false); else { await _library.ToggleLikeAsync(row.Track, _lifetime.Token); ShowLocal("likes"); } }
        else if (_pageKind == "local-playlist" && _localPlaylistId is { } id) { await _library.RemoveFromPlaylistAsync(id, index, _lifetime.Token); Tracks.RemoveAt(index); }
        else if (_pageKind == "online-playlist" && IsSignedIn && _browseId is { } remoteId && _cloud.Playlists.Any(p => p.CanEdit && p.Id == remoteId))
        {
            if (row.Track.PlaylistSetVideoId is null) { Status = "Refresh this playlist before removing the song; its playlist entry identifier is missing."; return; }
            await MutateCloudAsync(async token =>
            {
                await _music.EditAccountPlaylistAsync(remoteId, "remove", row.Track.Id, token, row.Track.PlaylistSetVideoId);
                token.ThrowIfCancellationRequested(); Tracks.Remove(row); Status = "Song removed from the YouTube Music playlist.";
            });
        }
        else if (_pageKind == "downloads") { await _downloads.DeleteAsync(row.Track.Id, _lifetime.Token); ShowLocal("downloads"); }
        else Status = "Remove is available in the queue, liked songs, local playlists, and downloads.";
    }

    private void MoveQueue(int direction)
    {
        if (!IsQueue || SelectedTrack is not { } row) return;
        int from = Tracks.IndexOf(row), to = from + direction;
        if (to < 0 || to >= Tracks.Count) return;
        Player.Move(from, direction);
        Tracks.Move(from, to);
        SelectedTrack = row;
    }

    private async Task<string> ResolveAsync(Track track, CancellationToken token)
    {
        if (track.LocalPath is { } local)
        {
            if (!File.Exists(local)) throw new FileNotFoundException("The local audio file is no longer available.", local);
            return local;
        }
        if (_downloads.FindDownloaded(track.Id) is { } offline) return offline;
        return (await _music.GetStreamAsync(track, token)).Url.AbsoluteUri;
    }

    public async Task OpenLocalFilesAsync(IEnumerable<string> paths)
    {
        var tracks = new List<Track>();
        foreach (string path in paths)
        {
            string id = "local:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
            tracks.Add(new(id, Path.GetFileNameWithoutExtension(path), "Local file", LocalPath: path));
        }
        if (tracks.Count == 0) return;
        SetPage("local", "Local audio", "Native Windows audio playback");
        ShowTracksFor(tracks);
        _restoreSeekPending = false;
        await Player.SetQueueAsync(tracks, 0, _lifetime.Token);
    }

    private async Task FetchLyricsCoreAsync()
    {
        _lyricDocument = null; ActiveLyric = "";
        _lyricTrackId = Player.CurrentTrack?.Id;
        UpdateOrbitLyrics();
        if (Player.CurrentTrack is not { } track) { LyricsText = "Play a song to look up lyrics."; return; }
        if (!FetchLyrics) { LyricsText = "Lyrics lookup is disabled in Settings."; return; }
        _lyricsRequest?.Cancel(); _lyricsRequest?.Dispose();
        _lyricsRequest = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var request = _lyricsRequest;
        var token = request.Token;
        LyricsText = "Finding lyrics…";
        try
        {
            var document = await _lyrics.GetAsync(track, token);
            token.ThrowIfCancellationRequested();
            if (_disposed || _lyricsRequest != request || Player.CurrentTrack?.Id != track.Id) return;
            _lyricDocument = document;
            LyricsText = _lyricDocument is { IsSynced: true } synced ? string.Join(Environment.NewLine, synced.Lines.Select(l => l.Text)) : _lyricDocument?.Text ?? "No lyrics found for this track.";
            UpdatePosition();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_disposed && _lyricsRequest == request) LyricsText = "Lyrics unavailable. " + error.Message; }
        finally { UpdateOrbitLyrics(); }
    }

    private async Task SaveAccountAsync()
    {
        string cookie = CookieInput.Trim();
        if (cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) cookie = cookie[7..].Trim();
        if (cookie.Length == 0) { Status = "Paste a YouTube Music Cookie header first."; return; }
        if (cookie.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException("Paste only the Cookie request header value, on one line.");
        _accountGeneration++;
        _signInRequest?.Cancel();
        await _accountOperations.WaitAsync(_lifetime.Token);
        try
        {
            await _credentials.SaveCookieAsync(cookie, _lifetime.Token);
            ApplyAccountSession(new(cookie));
            CookieInput = "";
            Status = "Account saved. Select Home to load account recommendations.";
        }
        finally { _accountOperations.Release(); }
    }

    private async Task SignOutAsync()
    {
        _accountGeneration++;
        _signInRequest?.Cancel();
        await _accountOperations.WaitAsync(_lifetime.Token);
        try
        {
            await _credentials.SaveAccountAsync(null, _lifetime.Token);
            ApplyAccountSession(null);
            CookieInput = ""; Status = "Signed out.";
        }
        finally { _accountOperations.Release(); }
    }

    private async Task SaveSettingsAsync()
    {
        var settings = _library.State.Settings;
        settings.Volume = Player.Volume; settings.Shuffle = Player.Shuffle; settings.RepeatMode = Player.Repeat.ToString();
        if (Country.Length != 2 || !Country.All(char.IsAsciiLetter)) throw new ArgumentException("Country must be a two-letter code.");
        settings.Country = Country.ToUpperInvariant();
        _music.Country = settings.Country;
        _music.Language = settings.Language;
        await _library.SaveAsync(_lifetime.Token);
        _downloads.BudgetBytes = settings.DownloadBudgetBytes;
        Status = "Settings saved.";
    }

    private void OnPlayerFailed(object? sender, Exception error) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        if (Player.CurrentTrack is { } track) _music.InvalidateStream(track.Id);
        ReportError(error);
    });
    private void OnPlayerChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        foreach (var property in new[] { nameof(PlayButtonText), nameof(PlayIconKind), nameof(NowTitle), nameof(NowArtist), nameof(NowArtistLinks), nameof(DurationSeconds), nameof(DurationText), nameof(RepeatText), nameof(Shuffle), nameof(Volume) }) OnPropertyChanged(property);
        if (Player.State != PlayerState.Loading) _sourcePrefetch.Prepare(Player.State == PlayerState.Playing ? Player.NextTrack : null);
        UpdatePosition(); UpdateTimer();
        RefreshQueue();
        if (_highlightedTrackId != Player.CurrentTrack?.Id || _highlightedQueueIndex != Player.CurrentIndex)
        {
            _highlightedTrackId = Player.CurrentTrack?.Id;
            _highlightedQueueIndex = Player.CurrentIndex;
            RefreshDesignPage();
        }
        var track = Player.CurrentTrack;
        OnPropertyChanged(nameof(HasCurrentTrack)); OnPropertyChanged(nameof(IsCurrentLiked)); OnPropertyChanged(nameof(NowAlbum));
        OnPropertyChanged(nameof(OrbitRepeatOn)); OnPropertyChanged(nameof(OrbitRepeatLabel));
        if (_lyricTrackId != track?.Id)
        {
            _lyricsRequest?.Cancel(); _lyricDocument = null; _lyricTrackId = track?.Id;
            LyricsText = track is null ? "Choose a song in Sway to begin." : "Open lyrics to look up this track.";
            UpdateOrbitLyrics();
            if (IsOrbitOpen || ShowLyrics || IsLyricsPanel) TrackBackground(FetchLyricsAsync());
        }
        if (_artworkTrackId != track?.Id || _artworkUrl != track?.ThumbnailUrl)
        {
            _artworkTrackId = track?.Id; _artworkUrl = track?.ThumbnailUrl;
            var previous = Artwork; Artwork = null; previous?.Dispose();
            UpdateOrbitPalette();
            TrackBackground(LoadArtworkAsync(track));
        }
        if (track is not null && _lastTrackId != track.Id && Player.State == PlayerState.Playing)
        {
            _lastTrackId = track.Id;
            TrackBackground(RecordTrackAsync(track));
            if (ShowLyrics || IsLyricsPanel || IsOrbitOpen) TrackBackground(FetchLyricsAsync());
        }
        if (_restoreSeekPending && Player.State == PlayerState.Playing)
        {
            _restoreSeekPending = false;
            TrackBackground(SeekSafelyAsync(_restoredPosition));
        }
    });

    private async Task RecordTrackAsync(Track track)
    {
        try { await _library.AddHistoryAsync(track, _lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportError(error); }
    }

    private void TrackBackground(Task task)
    {
        _backgroundTasks.Add(task);
        _ = ObserveBackgroundAsync(task);
    }

    private async Task ObserveBackgroundAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_disposed) ReportError(error); }
        finally { _backgroundTasks.Remove(task); }
    }

    private async Task LoadArtworkAsync(Track? track)
    {
        _artworkRequest?.Cancel(); _artworkRequest?.Dispose();
        _artworkRequest = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _artworkRequest.Token;
        Bitmap? bitmap = null;
        try
        {
            if (track is not null) bitmap = await ArtworkLoader.LoadAsync(_http, track, 384, token);
            token.ThrowIfCancellationRequested();
            var old = Artwork; Artwork = bitmap; bitmap = null; old?.Dispose();
            UpdateOrbitPalette();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException) { }
        finally { bitmap?.Dispose(); }
    }

    public Task<Bitmap?> LoadTrackCoverAsync(Track track, CancellationToken cancellationToken)
    {
        var loading = LoadTrackCoverCoreAsync(track, cancellationToken);
        TrackBackground(loading);
        return loading;
    }

    private async Task<Bitmap?> LoadTrackCoverCoreAsync(Track track, CancellationToken cancellationToken)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _rowArtworkOperations.WaitAsync(request.Token);
        try { return await ArtworkLoader.LoadAsync(_http, track, 96, request.Token); }
        finally { _rowArtworkOperations.Release(); }
    }

    private void UpdateTimer() { if ((_visible || IsOrbitOpen) && Player.State == PlayerState.Playing) _positionTimer.Start(); else _positionTimer.Stop(); }
    private void UpdatePosition()
    {
        if (!_isSeeking) OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(PositionText));
        if ((ShowLyrics || IsLyricsPanel || IsOrbitOpen) && _lyricDocument is { IsSynced: true } lyrics)
        {
            int line = lyrics.GetActiveLine(Player.Position);
            ActiveLyric = line >= 0 ? lyrics.Lines[line].Text : "";
        }
        if (IsOrbitOpen) UpdateOrbitLyrics();
    }
    public void BeginSeek() => _isSeeking = true;
    public async Task EndSeekAsync(TimeSpan position)
    {
        try { await SeekSafelyAsync(position); }
        finally { _isSeeking = false; UpdatePosition(); }
    }
    public async Task SeekSafelyAsync(TimeSpan position)
    {
        try { await Player.SeekAsync(position, _lifetime.Token); UpdatePosition(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
    }
    public void ReportError(Exception error) { if (error is not OperationCanceledException) Status = error is HttpRequestException ? "Network request failed. " + error.Message : error.Message; }
    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int split = pair.IndexOf('=');
            if (split > 0) result[Uri.UnescapeDataString(pair[..split])] = Uri.UnescapeDataString(pair[(split + 1)..]);
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _positionTimer.Stop();
        _lifetime.Cancel(); _pageRequest?.Cancel(); _artworkRequest?.Cancel(); _lyricsRequest?.Cancel();
        _accountLibraryLifetime.Cancel();
        if (_initializationTask is { } initialization) await initialization;
        if (_sessionRestored)
        {
            _library.State.Settings.Volume = Player.Volume;
            _library.State.Settings.Shuffle = Player.Shuffle;
            _library.State.Settings.RepeatMode = Player.Repeat.ToString();
            _library.State.Session = new() { Queue = Player.Queue.ToList(), CurrentIndex = Player.CurrentIndex, Position = Player.Position };
        }
        Player.Changed -= OnPlayerChanged; Player.PlaybackFailed -= OnPlayerFailed;
        await Player.DisposeAsync();
        await _sourcePrefetch.DisposeAsync();
        var pending = _commands.Select(command => command.RunningTask).Concat(_backgroundTasks.ToArray()).ToArray();
        try { await Task.WhenAll(pending); }
        catch (Exception) { /* Command failures are already surfaced by their owners. */ }
        try { if (_initialized) await _library.SaveAsync(); }
        finally
        {
            Artwork?.Dispose();
            OrbitBackdrop?.Dispose();
            ClearArtistProfile();
            ClearHomeCards();
            _lyrics.Dispose(); _downloads.Dispose(); _credentials.Dispose(); _library.Dispose(); _music.Dispose(); _http.Dispose();
            _pageRequest?.Dispose(); _artworkRequest?.Dispose(); _lyricsRequest?.Dispose(); _accountOperations.Dispose(); _lifetime.Dispose();
            _accountLibraryLifetime.Dispose(); _cloudOperations.Dispose(); _rowArtworkOperations.Dispose();
        }
    }
}
