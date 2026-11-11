using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Styling;
using SwayMusic.Core;

namespace SwayMusic.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly bool _migrateLegacy;
    private bool _canDockPanel = true;
    private string _panelKind = "queue", _libraryFilter = "";
    private int _librarySortIndex;
    private TrackRow? _selectedQueueTrack;
    private readonly List<Track> _librarySnapshot = [];
    public ObservableCollection<TrackRow> QueueTracks { get; } = [];
    public ObservableCollection<HomeCard> HomeCards { get; } = [];
    public ICommand LibraryCommand { get; private set; } = null!;
    public ICommand RetryStartupCommand { get; private set; } = null!;
    public ICommand DismissStatusCommand { get; private set; } = null!;
    public ICommand SelectQueuePanelCommand { get; private set; } = null!;
    public ICommand SelectLyricsPanelCommand { get; private set; } = null!;
    public ICommand ClosePanelCommand { get; private set; } = null!;
    public ICommand QueueUpCommand { get; private set; } = null!;
    public ICommand QueueDownCommand { get; private set; } = null!;
    public ICommand QueueRemoveCommand { get; private set; } = null!;
    public bool IsHome => _pageKind == "home";
    public bool IsSearch => _pageKind == "search";
    public bool IsHistory => _pageKind == "history";
    public bool IsLibrary => _pageKind == "library";
    public bool IsLikes => _pageKind == "likes";
    public bool IsDownloads => _pageKind == "downloads";
    public bool IsPlaylists => _pageKind is "playlists" or "local-playlist";
    public string HeroTitle => IsHome ? "In your rhythm." : PageTitle;
    private bool _isReady;
    public bool IsReady { get => _isReady; private set { if (Set(ref _isReady, value)) { OnPropertyChanged(nameof(CanRetryStartup)); OnPropertyChanged(nameof(CanSignIn)); } } }
    public bool CanRetryStartup => !IsReady && !Busy;
    public string PlayIconKind => Player.State == Playback.PlayerState.Loading ? "close" : Player.State == Playback.PlayerState.Playing ? "pause" : "play";
    public string TrackCount => $"{Tracks.Count:N0} songs";
    public string HomeSectionTitle => _library.State.History.Count > 0 ? "Continue listening" : "Discover something new";
    public bool ShowHomeCards => IsHome && HomeCards.Count > 0;
    public bool ShowEmpty => !Busy && ShowTracks && Tracks.Count == 0;
    public string EmptyTitle => IsHome ? "Find your rhythm." : "Nothing here yet";
    public string EmptyMessage => IsHome ? "Search for a song or open your audio files to start listening." : IsLibrary || IsLikes ? "Your liked songs, playlists and listening history will make this space yours." : "Try another search, or add music to this collection.";
    public string[] AppearanceOptions { get; } = ["Dark", "Light", "System"];
    public string[] LibrarySortOptions { get; } = ["Recently added", "Title", "Artist"];
    public string Appearance
    {
        get => _library.State.Settings.Appearance;
        set { _library.State.Settings.Appearance = value; ApplyAppearance(); OnPropertyChanged(); }
    }
    public bool CompactRows
    {
        get => _library.State.Settings.CompactRows;
        set { _library.State.Settings.CompactRows = value; OnPropertyChanged(); OnPropertyChanged(nameof(TrackRowHeight)); }
    }
    public double TrackRowHeight => CompactRows ? 44 : 60;
    public bool SidePanelOpen
    {
        get => _library.State.Settings.SidePanelOpen;
        set { _library.State.Settings.SidePanelOpen = value; NotifyPanel(); }
    }
    public bool IsSidePanelVisible => SidePanelOpen && _canDockPanel;
    public bool IsQueuePanel => IsSidePanelVisible && _panelKind == "queue";
    public bool IsLyricsPanel => IsSidePanelVisible && _panelKind == "lyrics";
    public TrackRow? SelectedQueueTrack { get => _selectedQueueTrack; set => Set(ref _selectedQueueTrack, value); }
    public string LibraryFilter { get => _libraryFilter; set { if (Set(ref _libraryFilter, value) && IsLibrary) FilterLibrary(); } }
    public int LibrarySortIndex { get => _librarySortIndex; set { if (Set(ref _librarySortIndex, value) && IsLibrary) FilterLibrary(); } }

    private void InitializeDesign()
    {
        LibraryCommand = new RelayCommand(ShowLibrary);
        RetryStartupCommand = Async(async () => { if (IsReady || Busy) return; _initializationTask = null; await InitializeAsync(); });
        DismissStatusCommand = new RelayCommand(() => Status = "");
        SelectQueuePanelCommand = new RelayCommand(() => { _panelKind = "queue"; SidePanelOpen = true; });
        SelectLyricsPanelCommand = Async(async () => { _panelKind = "lyrics"; SidePanelOpen = true; await FetchLyricsAsync(); });
        ClosePanelCommand = new RelayCommand(() => SidePanelOpen = false);
        QueueUpCommand = new RelayCommand(() => MovePanelQueue(-1));
        QueueDownCommand = new RelayCommand(() => MovePanelQueue(1));
        QueueRemoveCommand = Async(async () =>
        {
            if (SelectedQueueTrack is not { } row) return;
            await Player.RemoveAtAsync(QueueTracks.IndexOf(row), _lifetime.Token);
            RefreshQueue();
        });
        Tracks.CollectionChanged += OnTracksChanged;
    }
    private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs args)
    { OnPropertyChanged(nameof(ShowEmpty)); OnPropertyChanged(nameof(TrackCount)); }
    private void ApplyAppearance()
    {
        if (Application.Current is { } application)
            application.RequestedThemeVariant = Appearance switch { "Light" => ThemeVariant.Light, "System" => ThemeVariant.Default, _ => ThemeVariant.Dark };
    }
    private void RestoreDesignPreferences()
    {
        ApplyAppearance();
        foreach (var property in new[] { nameof(Appearance), nameof(CompactRows), nameof(TrackRowHeight) }) OnPropertyChanged(property);
        NotifyPanel();
    }
    public void SetAvailableWidth(double width)
    {
        bool canDock = width >= 1120;
        if (_canDockPanel == canDock) return;
        _canDockPanel = canDock;
        NotifyPanel();
    }
    private void NotifyPanel()
    {
        foreach (var property in new[] { nameof(SidePanelOpen), nameof(IsSidePanelVisible), nameof(IsQueuePanel), nameof(IsLyricsPanel) }) OnPropertyChanged(property);
        RefreshQueue();
    }
    private void ToggleQueuePanel()
    {
        if (!_canDockPanel) { ShowLocal("queue"); return; }
        bool close = IsQueuePanel;
        _panelKind = "queue";
        SidePanelOpen = !close;
    }
    private async Task ToggleLyricsPanelAsync()
    {
        if (_canDockPanel)
        {
            bool close = IsLyricsPanel;
            _panelKind = "lyrics";
            SidePanelOpen = !close;
            if (close) return;
        }
        else SetPage("lyrics", "Lyrics", Player.CurrentTrack?.Title ?? "Nothing playing");
        await FetchLyricsAsync();
    }
    private void RefreshQueue()
    {
        if (!IsQueuePanel && !IsQueue) return;
        var queue = Player.Queue;
        if (IsQueue && !Tracks.Select(row => row.Track).SequenceEqual(queue))
        {
            int selectedIndex = SelectedTrack is { } selected ? Tracks.IndexOf(selected) : 0;
            ShowTracksFor(queue);
            SelectedTrack = Tracks.ElementAtOrDefault(Math.Min(selectedIndex, Tracks.Count - 1));
        }
        if (!IsQueuePanel) return;
        bool same = queue.Count == QueueTracks.Count;
        if (same) for (int i = 0; i < queue.Count; i++) if (!Equals(queue[i], QueueTracks[i].Track)) { same = false; break; }
        if (!same)
        {
            QueueTracks.Clear();
            foreach (var track in queue) QueueTracks.Add(new(track));
        }
        for (int i = 0; i < QueueTracks.Count; i++) QueueTracks[i].IsCurrent = i == Player.CurrentIndex;
    }
    public async Task PlayQueueSelectedAsync()
    {
        if (SelectedQueueTrack is not { } row) return;
        _restoreSeekPending = false;
        await Player.SetQueueAsync(Player.Queue, QueueTracks.IndexOf(row), _lifetime.Token);
    }
    private void MovePanelQueue(int direction)
    {
        if (SelectedQueueTrack is not { } row) return;
        int index = QueueTracks.IndexOf(row), destination = index + direction;
        if (destination < 0 || destination >= QueueTracks.Count) return;
        Player.Move(index, direction); RefreshQueue(); SelectedQueueTrack = QueueTracks[destination];
    }
    private void ShowLibrary()
    {
        SetPage("library", "Your library", "The songs you keep coming back to.");
        _librarySnapshot.Clear();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Add(Track track) { if (ids.Add(track.Id)) _librarySnapshot.Add(track); }
        foreach (var track in _library.State.LikedTracks) Add(track);
        foreach (var item in _library.State.History) Add(item.Track);
        foreach (var playlist in _library.State.Playlists) foreach (var track in playlist.Tracks) Add(track);
        foreach (var download in _downloads.ListDownloads()) Add(download.Track with { LocalPath = download.FilePath });
        FilterLibrary();
    }
    private void FilterLibrary()
    {
        IEnumerable<Track> tracks = _librarySnapshot;
        if (!string.IsNullOrWhiteSpace(LibraryFilter)) tracks = tracks.Where(t => t.Title.Contains(LibraryFilter, StringComparison.OrdinalIgnoreCase) || t.Artist.Contains(LibraryFilter, StringComparison.OrdinalIgnoreCase) || (t.Album?.Contains(LibraryFilter, StringComparison.OrdinalIgnoreCase) ?? false));
        tracks = LibrarySortIndex switch { 1 => tracks.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase), 2 => tracks.OrderBy(t => t.Artist, StringComparer.CurrentCultureIgnoreCase), _ => tracks };
        ShowTracksFor(tracks);
    }
    private void RefreshDesignPage()
    {
        if (_pageKind == "queue") for (int i = 0; i < Tracks.Count; i++) Tracks[i].IsCurrent = i == Player.CurrentIndex;
        else foreach (var row in Tracks) row.IsCurrent = row.Track.Id == Player.CurrentTrack?.Id;
        foreach (var property in new[] { nameof(IsHome), nameof(IsSearch), nameof(IsHistory), nameof(IsLibrary), nameof(IsLikes), nameof(IsDownloads), nameof(IsPlaylists), nameof(HeroTitle), nameof(ShowEmpty), nameof(EmptyTitle), nameof(EmptyMessage), nameof(ShowHomeCards), nameof(TrackCount) }) OnPropertyChanged(property);
    }
    private void ClearHomeCards()
    {
        foreach (var card in HomeCards) card.Dispose();
        HomeCards.Clear();
    }
    private void RefreshHomeCards()
    {
        ClearHomeCards();
        if (!IsHome) { OnPropertyChanged(nameof(ShowHomeCards)); return; }
        IEnumerable<Track> source = _library.State.History.Count > 0 ? _library.State.History.Select(h => h.Track) : Tracks.Select(r => r.Track);
        foreach (var track in source.Take(3))
        {
            var card = new HomeCard(track);
            HomeCards.Add(card);
            // Covers are fetched only after an explicit Home request, not during session restore.
            if (_pageRequest is { IsCancellationRequested: false } request) TrackBackground(card.LoadAsync(_http, request.Token));
        }
        OnPropertyChanged(nameof(ShowHomeCards)); OnPropertyChanged(nameof(HomeSectionTitle));
    }
    public async Task PlayHomeCardAsync(HomeCard card)
    {
        _restoreSeekPending = false;
        var row = Tracks.FirstOrDefault(r => r.Track.Id == card.Track.Id);
        if (row is not null) { SelectedTrack = row; await PlaySelectedAsync(); }
        else await Player.PlayTrackAsync(card.Track, _lifetime.Token);
    }
}
