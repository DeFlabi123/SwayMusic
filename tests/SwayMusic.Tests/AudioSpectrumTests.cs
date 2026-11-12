using System.Buffers.Binary;
using NAudio.Wave;
using SwayMusic.Core;
using SwayMusic.Desktop.Playback;

namespace SwayMusic.Tests;

internal static class AudioSpectrumTests
{
    public static void Run()
    {
        foreach (int bits in new[] { 8, 16, 24, 32, -32 })
        {
            var format = bits == -32 ? WaveFormat.CreateIeeeFloatWaveFormat(48000, 2) : new WaveFormat(48000, bits, 2);
            byte[] data = Tone(format, 8192);
            var spectrum = new AudioSpectrum { Enabled = true };
            var tap = new SpectrumWaveProvider(new Provider(format, data), spectrum);
            byte[] output = new byte[data.Length];
            Check(tap.Read(output) == data.Length && output.SequenceEqual(data), "the visualization tap must preserve all PCM bytes");
            var bands = new float[AudioSpectrum.BandCount]; spectrum.CopyTo(bands);
            Check(bands.Max() > 0.4 && bands.All(float.IsFinite), "a real tone produces a finite audio spectrum");
            int peak = Array.IndexOf(bands, bands.Max());
            double frequency = 45 * Math.Pow(16000d / 45, (peak + 0.5) / AudioSpectrum.BandCount);
            Check(frequency is > 650 and < 1500, "spectrum energy follows the decoded tone frequency");
            spectrum.Enabled = false; spectrum.CopyTo(bands);
            Check(bands.All(value => value == 0), "closed/calm visualization clears all spectrum activity");
        }
        var silent = new AudioSpectrum { Enabled = true };
        silent.Capture(new byte[8192], new WaveFormat(48000, 16, 2));
        var zeros = new float[AudioSpectrum.BandCount]; silent.CopyTo(zeros);
        Check(zeros.All(value => value == 0), "silence does not invent an animated beat");
        Console.WriteLine("PASS real PCM spectrum, 8/16/24/32-bit and float formats, transparent audio, frequency mapping, silence and disabled behavior");
    }
    public static async Task NativeAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), "sway-spectrum-" + Guid.NewGuid().ToString("N") + ".wav");
        WriteWave(path, 5);
        try
        {
            await using var player = new PlaybackService((_, _) => throw new InvalidOperationException("Local spectrum test must not resolve a URL")) { Volume = 0, VisualizationEnabled = true };
            await player.PlayTrackAsync(new Track("spectrum", "Spectrum test", "Fixture", LocalPath: path));
            await Task.Delay(250);
            var bands = new float[AudioSpectrum.BandCount]; player.CopySpectrum(bands);
            Check(bands.Max() > 0.4, "native decoder/output publishes its real decoded spectrum");
            player.Pause(); player.CopySpectrum(bands); Check(bands.All(value => value == 0), "pause freezes the halo");
            await player.SeekAsync(TimeSpan.FromSeconds(2)); player.Play(); await Task.Delay(200); player.CopySpectrum(bands);
            Check(bands.Max() > 0.4, "native audio resumes and analyzes after seeking");
            await player.StopAsync(); player.CopySpectrum(bands); Check(bands.All(value => value == 0), "stop clears the halo");
        }
        finally { File.Delete(path); }
        Console.WriteLine("PASS muted native playback with audio visualization, pause, seek, resume and stop");
    }
    internal static void WriteWave(string path, int seconds)
    {
        var format = new WaveFormat(48000, 16, 2);
        using var writer = new WaveFileWriter(path, format);
        writer.Write(Tone(format, 48000 * seconds));
    }
    private static byte[] Tone(WaveFormat format, int frames)
    {
        int bytes = format.BitsPerSample / 8; var result = new byte[frames * format.BlockAlign];
        for (int frame = 0; frame < frames; frame++) for (int channel = 0; channel < format.Channels; channel++)
        {
            double value = 0.5 * Math.Sin(2 * Math.PI * 1000 * frame / format.SampleRate);
            var destination = result.AsSpan(frame * format.BlockAlign + channel * bytes, bytes);
            switch (bytes)
            {
                case 1: destination[0] = (byte)(128 + value * 127); break;
                case 2: BinaryPrimitives.WriteInt16LittleEndian(destination, (short)(value * short.MaxValue)); break;
                case 3: int sample = (int)(value * 8388607); destination[0] = (byte)sample; destination[1] = (byte)(sample >> 8); destination[2] = (byte)(sample >> 16); break;
                case 4: BinaryPrimitives.WriteInt32LittleEndian(destination, format.Encoding == WaveFormatEncoding.IeeeFloat ? BitConverter.SingleToInt32Bits((float)value) : (int)(value * int.MaxValue)); break;
            }
        }
        return result;
    }
    private sealed class Provider(WaveFormat format, byte[] data) : IWaveProvider
    {
        public WaveFormat WaveFormat => format;
        public int Read(Span<byte> buffer) { data.CopyTo(buffer); return data.Length; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
