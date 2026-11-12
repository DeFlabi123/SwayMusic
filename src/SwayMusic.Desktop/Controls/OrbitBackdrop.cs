using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SwayMusic.Desktop.Controls;

public static class OrbitBackdrop
{
    // Blur a small texture once per cover instead of applying a fullscreen blur on
    // every animation frame. The texture retains the cover's hue and saturation.
    public static Bitmap? Create(Bitmap? artwork, Color dominant)
    {
        if (artwork is null) return null;
        const int width = 256, height = 144, stride = width * 4;
        using var scaled = artwork.CreateScaledBitmap(new PixelSize(width, height), BitmapInterpolationMode.MediumQuality);
        using var source = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        byte[] data = new byte[stride * height];
        using (var pixels = source.Lock()) { scaled.CopyPixels(pixels); for (int y = 0; y < height; y++) Marshal.Copy(pixels.Address + y * pixels.RowBytes, data, y * stride, stride); }
        byte[] scratch = new byte[data.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            Blur(data, scratch, width, height, horizontal: true);
            Blur(scratch, data, width, height, horizontal: false);
        }
        double dominantMax = Math.Max(dominant.R, Math.Max(dominant.G, dominant.B));
        double tint = dominantMax > 0 ? 0.72 * (dominantMax - Math.Min(dominant.R, Math.Min(dominant.G, dominant.B))) / dominantMax : 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            double b = data[i], g = data[i + 1], r = data[i + 2];
            if (tint > 0)
            {
                double value = Math.Max(tint > 0.4 ? 70 : 0, Math.Max(r, Math.Max(g, b)));
                r = r * (1 - tint) + value * dominant.R / dominantMax * tint;
                g = g * (1 - tint) + value * dominant.G / dominantMax * tint;
                b = b * (1 - tint) + value * dominant.B / dominantMax * tint;
            }
            double mean = (r + g + b) / 3;
            r = Math.Clamp(mean + (r - mean) * 1.18, 0, 255); g = Math.Clamp(mean + (g - mean) * 1.18, 0, 255); b = Math.Clamp(mean + (b - mean) * 1.18, 0, 255);
            double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            double dim = Math.Min(0.6, 78 / Math.Max(1, luminance));
            // Very low amplitude, deterministic dithering removes visible color contours.
            double noise = ((i / 4 * 73 + i / stride * 151) % 17 - 8) / 10d;
            data[i] = (byte)Math.Clamp(b * dim + noise, 0, 255); data[i + 1] = (byte)Math.Clamp(g * dim + noise, 0, 255); data[i + 2] = (byte)Math.Clamp(r * dim + noise, 0, 255); data[i + 3] = 255;
        }
        var result = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var pixels = result.Lock()) for (int y = 0; y < height; y++) Marshal.Copy(data, y * stride, pixels.Address + y * pixels.RowBytes, stride);
        return result;
    }
    private static void Blur(byte[] source, byte[] target, int width, int height, bool horizontal)
    {
        const int radius = 12, count = radius * 2 + 1;
        int outer = horizontal ? height : width, inner = horizontal ? width : height;
        for (int line = 0; line < outer; line++)
        {
            int Index(int pos) => horizontal ? (line * width + Math.Clamp(pos, 0, inner - 1)) * 4 : (Math.Clamp(pos, 0, inner - 1) * width + line) * 4;
            int b = 0, g = 0, r = 0;
            for (int pos = -radius; pos <= radius; pos++) { int i = Index(pos); b += source[i]; g += source[i + 1]; r += source[i + 2]; }
            for (int pos = 0; pos < inner; pos++)
            {
                int i = Index(pos); target[i] = (byte)(b / count); target[i + 1] = (byte)(g / count); target[i + 2] = (byte)(r / count); target[i + 3] = 255;
                int add = Index(pos + radius + 1), remove = Index(pos - radius);
                b += source[add] - source[remove]; g += source[add + 1] - source[remove + 1]; r += source[add + 2] - source[remove + 2];
            }
        }
    }
}
