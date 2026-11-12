using System.Buffers.Binary;
using NAudio.Dsp;
using NAudio.Wave;

namespace SwayMusic.Desktop.Playback;

public sealed class AudioSpectrum
{
    public const int BandCount = 64;
    private const int Size = 1024;
    private readonly Complex[] _fft = new Complex[Size];
    private readonly float[] _window = Enumerable.Range(0, Size).Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Size - 1)))).ToArray();
    private readonly float[] _bands = new float[BandCount];
    private readonly object _sync = new();
    private int _enabled, _reset, _count;
    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set { if (Interlocked.Exchange(ref _enabled, value ? 1 : 0) != (value ? 1 : 0)) Clear(); }
    }
    public void Clear() { Interlocked.Exchange(ref _reset, 1); lock (_sync) Array.Clear(_bands); }
    public void CopyTo(Span<float> destination)
    {
        destination.Clear();
        if (Enabled) lock (_sync) _bands.AsSpan(0, Math.Min(destination.Length, BandCount)).CopyTo(destination);
    }
    public void Capture(ReadOnlySpan<byte> data, WaveFormat format)
    {
        if (!Enabled) return;
        if (Interlocked.Exchange(ref _reset, 0) != 0) _count = 0;
        int bytes = format.BitsPerSample / 8, align = format.BlockAlign;
        if (bytes is < 1 or > 4 || align <= 0 || format.Channels <= 0) return;
        bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible extended && extended.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        for (int frame = 0; frame + align <= data.Length; frame += align)
        {
            float mono = 0;
            for (int channel = 0; channel < format.Channels; channel++)
            {
                var sample = data.Slice(frame + channel * bytes, bytes);
                float value = bytes switch
                {
                    1 => (sample[0] - 128) / 128f,
                    2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                    3 => ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608f,
                    4 when floating => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample)),
                    4 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f,
                    _ => 0
                };
                if (float.IsFinite(value)) mono += value / format.Channels;
            }
            _fft[_count].X = mono * _window[_count]; _fft[_count].Y = 0;
            if (++_count != Size) continue;
            _count = 0; FastFourierTransform.FFT(true, 10, _fft);
            lock (_sync)
            {
                for (int band = 0; band < BandCount; band++)
                {
                    double ceiling = Math.Min(16000, format.SampleRate * 0.48);
                    int low = Math.Clamp((int)(45 * Math.Pow(ceiling / 45, band / (double)BandCount) * Size / format.SampleRate), 1, Size / 2 - 1);
                    int high = Math.Clamp((int)(45 * Math.Pow(ceiling / 45, (band + 1d) / BandCount) * Size / format.SampleRate), low + 1, Size / 2);
                    double magnitude = 0;
                    for (int bin = low; bin < high; bin++) magnitude = Math.Max(magnitude, Math.Sqrt(_fft[bin].X * _fft[bin].X + _fft[bin].Y * _fft[bin].Y));
                    _bands[band] = (float)Math.Clamp((20 * Math.Log10(magnitude + 1e-7) + 65) / 65, 0, 1);
                }
            }
        }
    }
}

public sealed class SpectrumWaveProvider(IWaveProvider source, AudioSpectrum spectrum) : IWaveProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;
    public int Read(Span<byte> buffer)
    {
        int count = source.Read(buffer);
        spectrum.Capture(buffer[..count], WaveFormat);
        return count;
    }
}
