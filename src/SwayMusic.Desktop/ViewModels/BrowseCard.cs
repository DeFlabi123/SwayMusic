using Avalonia.Media.Imaging;
using SwayMusic.Core;

namespace SwayMusic.Desktop.ViewModels;

public sealed class BrowseCard(BrowseItem item) : ObservableObject, IDisposable
{
    private readonly HomeCard _image = new(new Track(item.Id, item.Title, "", ThumbnailUrl: item.ThumbnailUrl));
    public BrowseItem Item { get; } = item;
    public string Title => Item.Title;
    public string Subtitle => Item.Subtitle;
    public Bitmap? Artwork => _image.Artwork;
    public async Task LoadAsync(HttpClient http, CancellationToken token)
    { await _image.LoadAsync(http, token); if (!token.IsCancellationRequested) OnPropertyChanged(nameof(Artwork)); }
    public void Dispose() => _image.Dispose();
}
