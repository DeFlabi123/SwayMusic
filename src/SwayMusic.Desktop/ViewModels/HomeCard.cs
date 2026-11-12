using Avalonia.Media.Imaging;
using SwayMusic.Core;

namespace SwayMusic.Desktop.ViewModels;

public sealed class HomeCard(Track track) : ObservableObject, IDisposable
{
    private Bitmap? _artwork;
    private bool _disposed;
    private readonly CancellationTokenSource _lifetime = new();
    public Track Track { get; } = track;
    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public Bitmap? Artwork { get => _artwork; private set => Set(ref _artwork, value); }
    public async Task LoadAsync(HttpClient http, CancellationToken ct)
    {
        if (_disposed) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        Bitmap? bitmap = null;
        try
        {
            bitmap = await ArtworkLoader.LoadAsync(http, Track, 256, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (!_disposed) { var old = Artwork; Artwork = bitmap; bitmap = null; old?.Dispose(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException or NotSupportedException) { }
        finally { bitmap?.Dispose(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _lifetime.Dispose();
        Artwork?.Dispose(); Artwork = null;
    }
}
