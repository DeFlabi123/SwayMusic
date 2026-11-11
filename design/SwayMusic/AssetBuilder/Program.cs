using System.Globalization;
using SkiaSharp;

const string ribbon = "M83 29C66 13 39 18 32 32C19 59 81 44 68 70C60 86 36 86 21 71";
string root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
string assets = Path.Combine(root, "src", "SwayMusic.Desktop", "Assets");
string design = Path.Combine(root, "design", "SwayMusic");
Directory.CreateDirectory(assets);
Directory.CreateDirectory(design);
int[] sizes = [16, 20, 24, 32, 48, 64, 128, 256];
var frames = new List<byte[]>();
foreach (int size in sizes)
{
    using var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / 112f);
    using var background = new SKPaint { Color = SKColor.Parse("#D6F68A"), IsAntialias = true };
    canvas.DrawRoundRect(new SKRect(0, 0, 112, 112), 29, 29, background);
    using var path = SKPath.ParseSvgPathData(ribbon);
    using var stroke = new SKPaint { Color = SKColor.Parse("#152019"), Style = SKPaintStyle.Stroke, StrokeWidth = 13, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
    canvas.DrawPath(path, stroke);
    using var image = SKImage.FromBitmap(bitmap);
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    var bytes = png.ToArray();
    frames.Add(bytes);
    File.WriteAllBytes(Path.Combine(design, $"sway-{size}.png"), bytes);
    if (size == 256) File.WriteAllBytes(Path.Combine(assets, "sway.png"), bytes);
}
using (var output = new BinaryWriter(File.Create(Path.Combine(assets, "sway.ico"))))
{
    output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        output.Write((byte)(sizes[i] % 256)); output.Write((byte)(sizes[i] % 256));
        output.Write((byte)0); output.Write((byte)0); output.Write((ushort)1); output.Write((ushort)32);
        output.Write(frames[i].Length); output.Write(offset); offset += frames[i].Length;
    }
    foreach (var frame in frames) output.Write(frame);
}
string symbol = $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 112 112\" role=\"img\" aria-label=\"SwayMusic ribbon logo\"><rect width=\"112\" height=\"112\" rx=\"29\" fill=\"#D6F68A\"/><path d=\"{ribbon}\" fill=\"none\" stroke=\"#152019\" stroke-width=\"13\" stroke-linecap=\"round\"/></svg>";
File.WriteAllText(Path.Combine(assets, "sway-symbol.svg"), symbol);
File.WriteAllText(Path.Combine(design, "sway-symbol.svg"), symbol);
foreach (var variant in new[] { ("mono", "#18221A"), ("reversed", "#F3F4EB") })
{
    string svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 112 112\" role=\"img\" aria-label=\"SwayMusic {variant.Item1} ribbon\"><path d=\"{ribbon}\" fill=\"none\" stroke=\"{variant.Item2}\" stroke-width=\"13\" stroke-linecap=\"round\"/></svg>";
    File.WriteAllText(Path.Combine(assets, $"sway-symbol-{variant.Item1}.svg"), svg);
    File.WriteAllText(Path.Combine(design, $"sway-symbol-{variant.Item1}.svg"), svg);
}

using var typeface = SKTypeface.FromFile(Path.Combine(assets, "Fonts", "Inter-SemiBold.ttf"));
using var mediumTypeface = SKTypeface.FromFile(Path.Combine(assets, "Fonts", "Inter-Medium.ttf"));
Console.WriteLine($"Bundled font: {typeface.FamilyName}, weight={typeface.FontWeight}");
using var font = new SKFont(typeface, 78);
using var word = TrackedText(font, "sway", new SKPoint(137, 75), -4);
using var musicFont = new SKFont(mediumTypeface, 12);
using var music = TrackedText(musicFont, "MUSIC", new SKPoint(142, 101), 5);
string wordPath = word.ToSvgPathData(), musicPath = music.ToSvgPathData();
foreach (var variant in new[] { ("light", "#F3F4EB", "#A5AEA5"), ("dark", "#18221A", "#586552") })
{
    string svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 390 112\" role=\"img\" aria-label=\"SwayMusic outlined wordmark\"><rect width=\"112\" height=\"112\" rx=\"29\" fill=\"#D6F68A\"/><path d=\"{ribbon}\" fill=\"none\" stroke=\"#152019\" stroke-width=\"13\" stroke-linecap=\"round\"/><path d=\"{wordPath}\" fill=\"{variant.Item2}\"/><path d=\"{musicPath}\" fill=\"{variant.Item3}\"/></svg>";
    File.WriteAllText(Path.Combine(design, $"sway-wordmark-outlined-{variant.Item1}.svg"), svg);
    File.WriteAllText(Path.Combine(assets, $"sway-wordmark-outlined-{variant.Item1}.svg"), svg);
}

using (var preview = new SKBitmap(720, 210))
using (var canvas = new SKCanvas(preview))
using (var labelFont = new SKFont(mediumTypeface, 12))
using (var labelPaint = new SKPaint { Color = SKColor.Parse("#A5AEA5"), IsAntialias = true })
{
    canvas.Clear(SKColor.Parse("#101412"));
    for (int i = 0; i < sizes.Length; i++)
    {
        using var icon = SKBitmap.Decode(frames[i]);
        float x = 16 + i * 88, y = 20;
        var visibleSize = Math.Min(sizes[i], 64);
        canvas.DrawBitmap(icon, new SKRect(x, y, x + visibleSize, y + visibleSize));
        canvas.DrawText(sizes[i] + " px", x, 112, SKTextAlign.Left, labelFont, labelPaint);
    }
    using var iconLarge = SKBitmap.Decode(frames[^1]);
    canvas.DrawBitmap(iconLarge, new SKRect(18, 137, 74, 193));
    using var lockupFont = new SKFont(typeface, 46);
    using var ink = new SKPaint { Color = SKColor.Parse("#F3F4EB"), IsAntialias = true };
    canvas.DrawText("sway", 91, 178, SKTextAlign.Left, lockupFont, ink);
    using var image = SKImage.FromBitmap(preview);
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(Path.Combine(design, "brand-size-preview.png"), encoded.ToArray());
}
Console.WriteLine($"Generated {sizes.Length} icon sizes, native ICO, symbols and outlined wordmarks in {assets} and {design}.");

static SKPath TrackedText(SKFont font, string text, SKPoint origin, float tracking)
{
    var output = new SKPath();
    float x = origin.X;
    foreach (char character in text)
    {
        string letter = character.ToString();
        using var path = font.GetTextPath(letter, new SKPoint(x, origin.Y));
        output.AddPath(path);
        x += font.MeasureText(letter) + tracking;
    }
    return output;
}
