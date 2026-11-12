using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SwayMusic.Desktop.Playback;

namespace SwayMusic.Desktop.Controls;

public sealed class OrbitHalo : Control
{
    private readonly float[] _levels = new float[AudioSpectrum.BandCount];
    private readonly float[] _previous = new float[AudioSpectrum.BandCount];
    public float Energy { get; private set; }
    public IBrush Primary { get; set; } = Brushes.LightGreen;
    public IBrush Secondary { get; set; } = Brushes.PeachPuff;
    public void Update(PlaybackService player)
    {
        Span<float> next = stackalloc float[AudioSpectrum.BandCount]; player.CopySpectrum(next);
        float energy = 0;
        for (int i = 0; i < next.Length; i++)
        {
            float target = MathF.Pow(Math.Clamp((next[i] - 0.12f) * 1.8f, 0, 1), 1.35f);
            target = Math.Clamp(target + Math.Max(0, next[i] - _previous[i]) * 3.2f, 0, 1);
            _previous[i] = next[i];
            _levels[i] += (target - _levels[i]) * (target > _levels[i] ? 0.95f : 0.3f);
            energy += _levels[i];
        }
        Energy = energy / next.Length;
        InvalidateVisual();
    }
    public void Reset() { Array.Clear(_levels); Array.Clear(_previous); Energy = 0; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height), radius = size * (0.38 + Energy * 0.006);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        for (int i = 0; i < _levels.Length; i++)
        {
            double angle = i * 2 * Math.PI / _levels.Length - Math.PI / 2;
            double length = size * (0.009 + _levels[i] * 0.105);
            var from = center + new Vector(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
            var to = center + new Vector(Math.Cos(angle) * (radius + length), Math.Sin(angle) * (radius + length));
            context.DrawLine(new Pen(i < _levels.Length / 2 ? Primary : Secondary, Math.Max(2, size * 0.006), lineCap: PenLineCap.Round), from, to);
        }
    }
}
