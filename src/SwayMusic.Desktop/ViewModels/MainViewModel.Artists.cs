using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using SwayMusic.Core;

namespace SwayMusic.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private HomeCard? _artistImage;
    private string _artistDescription = "", _artistStats = "";
    private bool _artistCatalogImagesRequested;
    private int _artistSectionIndex;
    private CancellationToken _artistCatalogToken;
    public Bitmap? ArtistArtwork => _artistImage?.Artwork;
    public string ArtistDescription { get => _artistDescription; private set { if (Set(ref _artistDescription, value)) OnPropertyChanged(nameof(HasArtistDescription)); } }
    public string ArtistStats { get => _artistStats; private set => Set(ref _artistStats, value); }
    public bool HasArtistDescription => !string.IsNullOrWhiteSpace(ArtistDescription);
    public bool ShowStandardTracks => ShowTracks && !IsArtistPage;
    public int ArtistSectionIndex
    {
        get => _artistSectionIndex;
        set { if (Set(ref _artistSectionIndex, value) && value == 1) LoadArtistCatalogArtwork(); }
    }
    public ObservableCollection<BrowseCard> ArtistCatalog { get; } = [];
    public bool HasArtistCatalog => IsArtistPage && ArtistCatalog.Count > 0;
    private void LoadArtistCatalogArtwork()
    {
        if (_artistCatalogImagesRequested) return;
        _artistCatalogImagesRequested = true;
        foreach (var card in ArtistCatalog) TrackBackground(card.LoadAsync(_http, _artistCatalogToken));
    }
    public IReadOnlyList<ArtistReference> NowArtistLinks => Player.CurrentTrack is { } track ? new TrackRow(track).ArtistLinks : [];

    private void ClearArtistProfile()
    {
        _artistImage?.Dispose(); _artistImage = null;
        ArtistDescription = ""; ArtistStats = "";
        foreach (var card in ArtistCatalog) card.Dispose();
        ArtistCatalog.Clear();
        _artistCatalogImagesRequested = false;
        _artistSectionIndex = 0; OnPropertyChanged(nameof(ArtistSectionIndex));
        OnPropertyChanged(nameof(ArtistArtwork)); OnPropertyChanged(nameof(HasArtistCatalog));
    }

    public Task OpenArtistAsync(ArtistReference artist)
    {
        var navigation = LoadArtistAsync(artist);
        TrackBackground(navigation);
        return navigation;
    }

    private async Task LoadArtistAsync(ArtistReference artist)
    {
        if (string.IsNullOrWhiteSpace(artist.Name) || artist.Name == "Unknown artist") return;
        SetPage("artist", artist.Name, "Artist · YouTube Music");
        Tracks.Clear(); _browseId = artist.Id;
        var token = BeginRequest("Loading artist…");
        try
        {
            var id = artist.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                // Older saved songs have names only. Resolve with the artist-only search filter.
                var results = await _music.SearchArtistsAsync(artist.Name, token);
                token.ThrowIfCancellationRequested();
                var candidates = (results.Items ?? []).Where(item => item.Id.StartsWith("UC", StringComparison.Ordinal) || item.Kind.Contains("ARTIST", StringComparison.Ordinal)).ToArray();
                var exact = candidates.Where(item => item.Title.Trim().Equals(artist.Name.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (exact.Length == 1) id = exact[0].Id;
                else
                {
                    SetPage("browse-items", "Artists matching “" + artist.Name + "”", "Choose an artist to open their page.");
                    BrowseItems.Clear(); foreach (var item in candidates) BrowseItems.Add(item);
                    SelectedBrowseItem = BrowseItems.FirstOrDefault();
                    Status = candidates.Length == 0 ? "No matching artist was found on YouTube Music." : "";
                    return;
                }
            }
            var profile = await _music.GetArtistAsync(id!, token);
            token.ThrowIfCancellationRequested();
            _browseId = profile.Id; PageTitle = profile.Name;
            ArtistStats = profile.Stats; ArtistDescription = profile.Description;
            ApplyPage(profile.Music, token);
            foreach (var item in profile.Music.Items ?? [])
            {
                var card = new BrowseCard(item); ArtistCatalog.Add(card);
            }
            _artistCatalogToken = token;
            OnPropertyChanged(nameof(HasArtistCatalog));
            OnPropertyChanged(nameof(ArtistSubscriptionText)); OnPropertyChanged(nameof(CanChangeArtistSubscription));
            if (profile.ThumbnailUrl is { } url)
            {
                var image = new HomeCard(new Track(profile.Id, profile.Name, "", ThumbnailUrl: url));
                _artistImage = image;
                TrackBackground(LoadArtistImageAsync(image, token));
            }
            Status = "";
        }
        finally { EndRequest(token); }
    }

    private async Task LoadArtistImageAsync(HomeCard image, CancellationToken token)
    {
        await image.LoadAsync(_http, token);
        if (!token.IsCancellationRequested && ReferenceEquals(_artistImage, image)) OnPropertyChanged(nameof(ArtistArtwork));
    }

    public Task OpenArtistCatalogItemAsync(BrowseItem item) => OpenOnlineBrowseAsync(item);
}
