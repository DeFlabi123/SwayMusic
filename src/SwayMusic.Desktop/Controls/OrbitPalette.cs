using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SwayMusic.Desktop.Controls;

public sealed record OrbitPalette(Color Background, Color Primary, Color Secondary)
{
    public Color Dominant { get; init; }
    public static OrbitPalette FromBitmap(Bitmap? bitmap)
    {
        var fallback = new OrbitPalette(Color.Parse("#101412"), Color.Parse("#D6F68A"), Color.Parse("#E9BDA8"));
        if (bitmap is null) return fallback;
        try
        {
            using var scaled = bitmap.CreateScaledBitmap(new PixelSize(24, 24), BitmapInterpolationMode.LowQuality);
            using var converted = new WriteableBitmap(new PixelSize(24, 24), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var pixels = converted.Lock(); scaled.CopyPixels(pixels);
            byte[] data = new byte[pixels.RowBytes * 24]; Marshal.Copy(pixels.Address, data, 0, data.Length);
            var buckets = new Dictionary<int, (long R, long G, long B, int Count)>();
            for (int y = 0; y < 24; y++) for (int x = 0; x < 24; x++)
            {
                int i = y * pixels.RowBytes + x * 4; int r = data[i + 2], g = data[i + 1], b = data[i];
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (data[i + 3] < 200 || max < 35 || min > 235) continue;
                int key = (r / 64) << 4 | (g / 64) << 2 | b / 64;
                var old = buckets.GetValueOrDefault(key); buckets[key] = (old.R + r, old.G + g, old.B + b, old.Count + 1);
            }
            var colors = buckets.Values.OrderByDescending(v => v.Count * (1 + (Math.Max(v.R, Math.Max(v.G, v.B)) - Math.Min(v.R, Math.Min(v.G, v.B))) / (double)(v.Count * 255)))
                .Select(v => Color.FromRgb((byte)(v.R / v.Count), (byte)(v.G / v.Count), (byte)(v.B / v.Count))).ToArray();
            if (colors.Length == 0) return fallback;
            var first = colors[0];
            var second = colors.Skip(1).FirstOrDefault(color => Math.Abs(color.R - first.R) + Math.Abs(color.G - first.G) + Math.Abs(color.B - first.B) > 100);
            if (second == default) second = fallback.Secondary;
            Color Light(Color color) => Color.FromRgb((byte)(color.R * 0.65 + 255 * 0.35), (byte)(color.G * 0.65 + 255 * 0.35), (byte)(color.B * 0.65 + 255 * 0.35));
            return new(Color.FromRgb((byte)(first.R * 0.34), (byte)(first.G * 0.34), (byte)(first.B * 0.34)), Light(first), Light(second)) { Dominant = first };
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or InvalidOperationException) { return fallback; }
    }
}
