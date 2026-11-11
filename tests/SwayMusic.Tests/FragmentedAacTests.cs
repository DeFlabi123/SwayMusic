using System.Buffers.Binary;
using System.Text;
using SwayMusic.Desktop.Playback;

namespace SwayMusic.Tests;

public static class FragmentedAacTests
{
    public static async Task RunAsync()
    {
        var packets = new List<byte[]>();
        for (int frame = 0; frame < 40; frame++)
        {
            var packet = new byte[800 + frame];
            for (int p = 0; p < packet.Length; p++) packet[p] = (byte)((frame * 13 + p) % 251);
            packets.Add(packet);
        }
        byte[] fixture = BuildFragmentedAudio(packets);
        using var input = new CountingStream(fixture);
        using var audio = await FragmentedAacStream.TryCreateAsync(input, CancellationToken.None)
            ?? throw new InvalidOperationException("The fragmented AAC fixture was not recognized.");
        Check(input.BytesRead < fixture.Length / 2, "Indexing should read small fragment metadata rather than the whole audio payload.");
        Check(input.MaximumRead <= 4096, "Metadata reads should remain bounded.");
        Check(Math.Abs(audio.Duration.TotalSeconds - 40 * 1024d / 44100) < 0.001, "Duration must follow the exact AAC sample count.");
        byte[] expected = ExpectedAdts(packets);
        Check(audio.Length == expected.Length, "The virtual ADTS byte length should be exact.");
        var actual = new byte[expected.Length];
        audio.ReadExactly(actual);
        Check(actual.AsSpan().SequenceEqual(expected), "Every AAC payload must be preserved with its correct ADTS frame header.");
        Check(audio.Read(new byte[16]) == 0, "The virtual stream should return EOF only at the declared end.");
        foreach (int position in new[] { 0, 3, 7, 805, 16300, expected.Length - 50 })
        {
            audio.Position = position;
            byte[] sample = new byte[Math.Min(1000, expected.Length - position)];
            audio.ReadExactly(sample);
            Check(sample.AsSpan().SequenceEqual(expected.AsSpan(position, sample.Length)), "Seeking must preserve byte order across frame and fragment boundaries.");
        }
        Expect<IOException>(() => audio.Position = audio.Length + 1);
        var cancelled = new CancellationToken(true);
        await ExpectAsync<OperationCanceledException>(() => FragmentedAacStream.TryCreateAsync(new MemoryStream(fixture), cancelled));
        byte[] corrupt = (byte[])fixture.Clone();
        int firstRun = Find(corrupt, "trun");
        BinaryPrimitives.WriteUInt32BigEndian(corrupt.AsSpan(firstRun + 8), 250001);
        await ExpectAsync<InvalidDataException>(() => FragmentedAacStream.TryCreateAsync(new MemoryStream(corrupt), CancellationToken.None));
        using var ordinary = new MemoryStream("RIFFordinary-wave"u8.ToArray());
        Check(await FragmentedAacStream.TryCreateAsync(ordinary, CancellationToken.None) is null && ordinary.Position == 0,
            "Ordinary native audio must remain unchanged for the existing decoder.");
        Console.WriteLine("PASS fragmented AAC metadata indexing, exact ADTS payloads, duration, seek, EOF, cancellation, and malformed input");
    }

    private static byte[] BuildFragmentedAudio(IReadOnlyList<byte[]> packets)
    {
        byte[] descriptor = Descriptor(3, Join([0, 1, 0], Descriptor(4,
            Join([0x40, 0x15, 0, 0, 0], new byte[8], Descriptor(5, [0x12, 0x10])))));
        byte[] sample = Box("mp4a", Join(new byte[28], Box("esds", Join(new byte[4], descriptor))));
        byte[] track = Box("trak", Join(Box("tkhd", Join(new byte[12], U32(1))),
            Box("mdia", Box("minf", Box("stbl", Box("stsd", Join(new byte[4], U32(1), sample)))))));
        byte[] movie = Box("moov", track);
        var fragments = new[] { Fragment(packets.Take(20).ToArray()), Fragment(packets.Skip(20).ToArray()) };
        byte[] entries = Join(fragments.Select(fragment => Join(U32((uint)fragment.Length), U32(20 * 1024), U32(0))).ToArray());
        byte[] index = Box("sidx", Join(new byte[4], U32(1), U32(44100), U32(0), U32(0), [0, 0, 0, 2], entries));
        return Join(Box("ftyp", Join("dash"u8.ToArray(), U32(0), "iso6mp41"u8.ToArray())), movie, index, fragments[0], fragments[1]);
    }

    private static byte[] Fragment(IReadOnlyList<byte[]> packets)
    {
        byte[] entries = Join(packets.Select(packet => Join(U32(1024), U32((uint)packet.Length))).ToArray());
        byte[] MakeMovie(int dataOffset) => Box("moof", Box("traf", Join(
            Box("tfhd", Join(U32(0x020000), U32(1))),
            Box("trun", Join(U32(0x301), U32((uint)packets.Count), U32((uint)dataOffset), entries)))));
        byte[] first = MakeMovie(0);
        return Join(MakeMovie(first.Length + 8), Box("mdat", Join(packets.ToArray())));
    }

    private static byte[] ExpectedAdts(IEnumerable<byte[]> packets)
    {
        using var result = new MemoryStream();
        foreach (var packet in packets)
        {
            int size = packet.Length + 7;
            byte[] header = [0xff, 0xf1, 0x50, (byte)(0x80 | size >> 11), (byte)(size >> 3), (byte)((size & 7) << 5 | 0x1f), 0xfc];
            result.Write(header); result.Write(packet);
        }
        return result.ToArray();
    }

    private static byte[] Box(string kind, byte[] payload) => Join(U32((uint)(payload.Length + 8)), Encoding.ASCII.GetBytes(kind), payload);
    private static byte[] Descriptor(byte tag, byte[] payload) => Join([tag, (byte)payload.Length], payload);
    private static byte[] U32(uint value) { byte[] bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); return bytes; }
    private static byte[] Join(params byte[][] pieces)
    {
        int length = pieces.Sum(piece => piece.Length);
        byte[] result = new byte[length]; int p = 0;
        foreach (var piece in pieces) { piece.CopyTo(result, p); p += piece.Length; }
        return result;
    }
    private static int Find(byte[] bytes, string kind)
    {
        byte[] pattern = Encoding.ASCII.GetBytes(kind);
        int found = bytes.AsSpan().IndexOf(pattern);
        return found >= 0 ? found : throw new InvalidOperationException("Fixture box missing: " + kind);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public long BytesRead { get; private set; }
        public int MaximumRead { get; private set; }
        public override int Read(Span<byte> buffer) { MaximumRead = Math.Max(MaximumRead, buffer.Length); int count = base.Read(buffer); BytesRead += count; return count; }
    }
}
