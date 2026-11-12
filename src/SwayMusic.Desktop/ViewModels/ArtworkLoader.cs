using Avalonia.Media.Imaging;
using SwayMusic.Core;
using SwayMusic.Core.Networking;

namespace SwayMusic.Desktop.ViewModels;

public static class ArtworkLoader
{
    private const int MaximumBytes = 2 * 1024 * 1024;

    public static IEnumerable<Uri> Sources(Track track)
    {
        Uri? supplied = null;
        if (Uri.TryCreate(track.ThumbnailUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https")
        { supplied = uri; yield return uri; }
        // Old library entries may have no artwork URL, or a stale Google image URL.
        if (YouTubeMusicClient.IsVideoId(track.Id))
        {
            var fallback = new Uri("https://i.ytimg.com/vi/" + track.Id + "/hqdefault.jpg");
            if (fallback != supplied) yield return fallback;
        }
    }

    public static async Task<Bitmap?> LoadAsync(HttpClient http, Track track, int width, CancellationToken ct)
    {
        foreach (var uri in Sources(track))
        {
            ct.ThrowIfCancellationRequested();
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
            request.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, request.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumBytes) continue;
                using var source = await response.Content.ReadAsStreamAsync(request.Token);
                using var memory = new MemoryStream();
                byte[] buffer = new byte[16384]; int count;
                while ((count = await source.ReadAsync(buffer, request.Token)) > 0)
                {
                    if (memory.Length + count > MaximumBytes) break;
                    await memory.WriteAsync(buffer.AsMemory(0, count), request.Token);
                }
                if (count > 0 || memory.Length == 0) continue;
                memory.Position = 0;
                var bitmap = await Task.Run(() => Bitmap.DecodeToWidth(memory, width), request.Token);
                if (request.IsCancellationRequested) { bitmap.Dispose(); request.Token.ThrowIfCancellationRequested(); }
                return bitmap;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception error) when (error is HttpRequestException or IOException or ArgumentException or NotSupportedException) { }
        }
        return null;
    }
}
