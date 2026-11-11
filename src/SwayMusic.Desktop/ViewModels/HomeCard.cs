using Avalonia.Media.Imaging;
using SwayMusic.Core;

namespace SwayMusic.Desktop.ViewModels;

public sealed class HomeCard(Track track) : ObservableObject, IDisposable
{
    private Bitmap? _artwork;
    private bool _disposed;
    public Track Track { get; } = track;
    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public Bitmap? Artwork { get => _artwork; private set => Set(ref _artwork, value); }
    public async Task LoadAsync(HttpClient http, CancellationToken ct)
    {
        if (!Uri.TryCreate(Track.ThumbnailUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https") return;
        Bitmap? bitmap = null;
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 2 * 1024 * 1024) return;
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var data = new MemoryStream();
            byte[] buffer = new byte[16384]; int count;
            while ((count = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (data.Length + count > 2 * 1024 * 1024) return;
                await data.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            data.Position = 0;
            bitmap = await Task.Run(() => Bitmap.DecodeToWidth(data, 256), ct);
            ct.ThrowIfCancellationRequested();
            if (!_disposed) { Artwork = bitmap; bitmap = null; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException or NotSupportedException) { }
        finally { bitmap?.Dispose(); }
    }
    public void Dispose() { _disposed = true; Artwork?.Dispose(); Artwork = null; }
}
