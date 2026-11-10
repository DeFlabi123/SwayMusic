using System.Buffers.Binary;
using System.Text;
using Metrolist.Core.Networking;

namespace Metrolist.Desktop.Playback;

// Windows' MP4 source does not decode YouTube's DASH fragments. Reframe their unchanged AAC
// packets as ADTS, which the native AAC source accepts, without buffering or decoding the song.
internal sealed class FragmentedAacStream : Stream
{
    private readonly Stream _source;
    private readonly Fragment[] _fragments;
    private readonly int _profile, _frequencyIndex, _channels;
    private readonly object _sync = new();
    private long _position;
    private bool _disposed;

    private FragmentedAacStream(Stream source, Fragment[] fragments, AudioConfiguration audio)
    {
        _source = source;
        _fragments = fragments;
        _profile = audio.Profile;
        _frequencyIndex = audio.FrequencyIndex;
        _channels = audio.Channels;
        SampleRate = audio.SampleRate;
        long length = 0, frames = 0;
        foreach (var fragment in fragments)
        {
            fragment.VirtualStart = length;
            length += fragment.Offsets[^1];
            frames += fragment.Offsets.Length - 1;
        }
        Length = length;
        Duration = TimeSpan.FromSeconds(frames * 1024d / SampleRate);
    }

    public int SampleRate { get; }
    public TimeSpan Duration { get; }
    public override long Length { get; }
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Position { get { lock (_sync) return _position; } set => Seek(value, SeekOrigin.Begin); }

    public static async Task<FragmentedAacStream?> TryCreateAsync(Stream source, CancellationToken ct)
    {
        if (!source.CanRead || !source.CanSeek || source.Length < 8) return null;
        var first = await ReadAtAsync(source, 0, 8, ct).ConfigureAwait(false);
        if (Kind(first, 4) != "ftyp") { source.Position = 0; return null; }
        byte[]? movie = null;
        Reference[]? references = null;
        long offset = 0;
        for (int boxCount = 0; boxCount < 64 && offset + 8 <= source.Length; boxCount++)
        {
            var header = await ReadAtAsync(source, offset, 16, ct).ConfigureAwait(false);
            var size = BoxSize(header, 0, source.Length - offset, out int headerSize);
            var kind = Kind(header, 4);
            if (kind == "moov")
            {
                if (size > 1024 * 1024) throw new InvalidDataException("The MP4 audio metadata is too large.");
                movie = await ReadAtAsync(source, offset, checked((int)size), ct).ConfigureAwait(false);
            }
            else if (kind == "sidx")
            {
                if (size > 1024 * 1024) throw new InvalidDataException("The MP4 segment index is too large.");
                var index = await ReadAtAsync(source, offset, checked((int)size), ct).ConfigureAwait(false);
                references = ParseIndex(index, offset + size, headerSize, source.Length);
            }
            else if (kind is "moof" or "mdat") break;
            offset = checked(offset + size);
        }
        if (references is null) { source.Position = 0; return null; }
        if (movie is null) throw new InvalidDataException("The fragmented audio has no movie metadata.");
        var audio = ParseAudio(movie);
        var fragments = new Fragment[references.Length];
        var budget = new IndexBudget();
        await Parallel.ForEachAsync(Enumerable.Range(0, references.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (index, token) =>
            {
                var reference = references[index];
                var metadata = await ReadAtAsync(source, reference.Offset, Math.Min(reference.Size, 4096), token).ConfigureAwait(false);
                var moofSize = BoxSize(metadata, 0, reference.Size, out _);
                if (Kind(metadata, 4) != "moof" || moofSize > 1024 * 1024)
                    throw new InvalidDataException("The audio segment has invalid fragment metadata.");
                if (moofSize > metadata.Length)
                    metadata = await ReadAtAsync(source, reference.Offset, checked((int)moofSize), token).ConfigureAwait(false);
                fragments[index] = ParseFragment(metadata.AsSpan(0, checked((int)moofSize)), reference.Offset, reference.Size, audio.TrackId, budget);
            }).ConfigureAwait(false);
        source.Position = 0;
        return new(source, fragments, audio);
    }

    private static async Task<byte[]> ReadAtAsync(Stream source, long offset, int count, CancellationToken ct)
    {
        if (offset < 0 || count < 0 || offset + count > source.Length) throw new InvalidDataException("MP4 metadata extends beyond the audio stream.");
        if (source is HttpRangeStream ranged) return await ranged.ReadRangeAsync(offset, count, ct).ConfigureAwait(false);
        var buffer = new byte[count];
        lock (source)
        {
            ct.ThrowIfCancellationRequested();
            source.Position = offset;
            source.ReadExactly(buffer);
        }
        return buffer;
    }

    private static Reference[] ParseIndex(byte[] bytes, long indexEnd, int headerSize, long fileLength)
    {
        var span = bytes.AsSpan();
        int p = headerSize;
        Require(span, p, 12);
        int version = span[p]; p += 12; // version/flags, reference ID and timescale
        long firstOffset;
        if (version == 0) { Require(span, p, 8); firstOffset = U32(span, p + 4); p += 8; }
        else if (version == 1) { Require(span, p, 16); firstOffset = checked((long)U64(span, p + 8)); p += 16; }
        else throw new InvalidDataException("Unsupported MP4 segment index version.");
        Require(span, p, 4);
        int count = BinaryPrimitives.ReadUInt16BigEndian(span[(p + 2)..]); p += 4;
        if (count == 0 || count > 10000) throw new InvalidDataException("The MP4 segment index is empty or too large.");
        Require(span, p, checked(count * 12));
        var references = new Reference[count];
        long offset = checked(indexEnd + firstOffset);
        for (int i = 0; i < count; i++, p += 12)
        {
            uint size = U32(span, p);
            if ((size & 0x80000000) != 0 || size < 16 || offset + size > fileLength)
                throw new InvalidDataException("The MP4 segment reference is invalid.");
            references[i] = new(offset, checked((int)size));
            offset += size;
        }
        return references;
    }

    private static AudioConfiguration ParseAudio(byte[] movie)
    {
        var span = movie.AsSpan();
        var root = Boxes(span, 0, span.Length).Single();
        foreach (var track in Boxes(span, root.Offset + root.Header, root.End).Where(box => box.Kind == "trak"))
        {
            var header = Boxes(span, track.Offset + track.Header, track.End).FirstOrDefault(box => box.Kind == "tkhd");
            if (header.Size == 0) continue;
            int h = header.Offset + header.Header;
            Require(span, h, 4);
            int idOffset = h + (span[h] == 1 ? 20 : 12);
            uint trackId = U32(span, idOffset);
            var descriptor = FindDescriptorBox(span, track.Offset + track.Header, track.End);
            if (descriptor.Size == 0) continue;
            int start = descriptor.Offset + descriptor.Header + 4;
            int asc = FindAudioSpecificConfig(span, start, descriptor.End);
            if (asc < 0) continue;
            Require(span, asc, 2);
            int bits = BinaryPrimitives.ReadUInt16BigEndian(span[asc..]);
            int profile = bits >> 11, frequency = bits >> 7 & 15, channels = bits >> 3 & 15;
            int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
            if (profile is < 1 or > 4 || frequency >= rates.Length || channels is < 1 or > 7)
                throw new InvalidDataException("The audio uses an unsupported AAC configuration.");
            return new(trackId, profile, frequency, rates[frequency], channels);
        }
        throw new InvalidDataException("The MP4 contains no supported AAC audio track.");
    }

    private static Box FindDescriptorBox(ReadOnlySpan<byte> bytes, int start, int end, int depth = 0)
    {
        if (depth > 32) throw new InvalidDataException("The AAC metadata nesting is too deep.");
        foreach (var box in Boxes(bytes, start, end))
        {
            if (box.Kind == "esds") return box;
            int skip = box.Kind switch
            {
                "mdia" or "minf" or "stbl" => 0,
                "stsd" => 8,
                "mp4a" => 28,
                _ => -1
            };
            if (skip < 0) continue;
            var found = FindDescriptorBox(bytes, box.Offset + box.Header + skip, box.End, depth + 1);
            if (found.Size > 0) return found;
        }
        return default;
    }

    private static int FindAudioSpecificConfig(ReadOnlySpan<byte> bytes, int start, int end, int depth = 0)
    {
        if (depth > 32) throw new InvalidDataException("The AAC descriptor nesting is too deep.");
        for (int p = start; p < end;)
        {
            Require(bytes, p, 2);
            int tag = bytes[p++], size = 0, lengthBytes = 0;
            byte part;
            do
            {
                Require(bytes, p, 1);
                part = bytes[p++]; size = checked((size << 7) | part & 127);
                if (++lengthBytes > 4) throw new InvalidDataException("The AAC descriptor length is invalid.");
            } while ((part & 128) != 0);
            if (size < 0 || p + size > end) throw new InvalidDataException("The AAC descriptor extends beyond its container.");
            if (tag == 5) return p;
            int childStart = -1;
            if (tag == 3)
            {
                Require(bytes, p, 3);
                int flags = bytes[p + 2]; childStart = p + 3;
                if ((flags & 128) != 0) childStart += 2;
                if ((flags & 64) != 0) { Require(bytes, childStart, 1); childStart += 1 + bytes[childStart]; }
                if ((flags & 32) != 0) childStart += 2;
            }
            else if (tag == 4) childStart = p + 13;
            if (childStart >= 0 && childStart < p + size)
            {
                int found = FindAudioSpecificConfig(bytes, childStart, p + size, depth + 1);
                if (found >= 0) return found;
            }
            p += size;
        }
        return -1;
    }

    private static Fragment ParseFragment(ReadOnlySpan<byte> bytes, long fragmentOffset, int referenceSize, uint trackId, IndexBudget budget)
    {
        var root = Boxes(bytes, 0, bytes.Length).Single();
        foreach (var traf in Boxes(bytes, root.Header, root.End).Where(box => box.Kind == "traf"))
        {
            uint defaultSize = 0;
            long baseOffset = fragmentOffset;
            var headers = Boxes(bytes, traf.Offset + traf.Header, traf.End);
            var tfhd = headers.FirstOrDefault(box => box.Kind == "tfhd");
            if (tfhd.Size == 0) continue;
            int p = tfhd.Offset + tfhd.Header;
            uint flags = U32(bytes, p) & 0xffffff; p += 4;
            if (U32(bytes, p) != trackId) continue; p += 4;
            if ((flags & 1) != 0) { baseOffset = checked((long)U64(bytes, p)); p += 8; }
            if ((flags & 2) != 0) p += 4;
            if ((flags & 8) != 0) p += 4;
            if ((flags & 16) != 0) { defaultSize = U32(bytes, p); p += 4; }
            var runs = headers.Where(box => box.Kind == "trun").ToArray();
            if (runs.Length != 1) throw new InvalidDataException("The AAC fragment must contain one contiguous audio run.");
            var run = runs[0]; p = run.Offset + run.Header;
            flags = U32(bytes, p) & 0xffffff; p += 4;
            int count = checked((int)U32(bytes, p)); p += 4;
            if (count is <= 0 or > 250000) throw new InvalidDataException("The AAC fragment sample count is invalid.");
            long dataOffset = fragmentOffset + bytes.Length + 8;
            if ((flags & 1) != 0) { dataOffset = checked(baseOffset + BinaryPrimitives.ReadInt32BigEndian(bytes[p..])); p += 4; }
            if ((flags & 4) != 0) p += 4;
            budget.Reserve(checked((count + 1) * sizeof(int)));
            var offsets = new int[count + 1];
            long payload = 0;
            for (int i = 0; i < count; i++)
            {
                if ((flags & 0x100) != 0) p += 4;
                uint size = defaultSize;
                if ((flags & 0x200) != 0) { size = U32(bytes, p); p += 4; }
                if ((flags & 0x400) != 0) p += 4;
                if ((flags & 0x800) != 0) p += 4;
                if (p > run.End || size is 0 or > 8184) throw new InvalidDataException("The AAC packet size is invalid.");
                offsets[i + 1] = checked(offsets[i] + (int)size + 7);
                payload += size;
            }
            if (dataOffset < fragmentOffset || dataOffset + payload > fragmentOffset + referenceSize)
                throw new InvalidDataException("The AAC sample data extends beyond its segment.");
            return new(dataOffset, offsets);
        }
        throw new InvalidDataException("The segment contains no samples for the AAC audio track.");
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> destination)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int written = 0;
            Span<byte> header = stackalloc byte[7];
            while (written < destination.Length && _position < Length)
            {
                int fragmentIndex = FindFragment(_position);
                var fragment = _fragments[fragmentIndex];
                int local = checked((int)(_position - fragment.VirtualStart));
                int frame = Array.BinarySearch(fragment.Offsets, local);
                if (frame < 0) frame = ~frame - 1;
                int frameOffset = local - fragment.Offsets[frame];
                int frameLength = fragment.Offsets[frame + 1] - fragment.Offsets[frame];
                if (frameOffset < 7)
                {
                    WriteHeader(header, frameLength);
                    int count = Math.Min(7 - frameOffset, destination.Length - written);
                    header.Slice(frameOffset, count).CopyTo(destination[written..]);
                    written += count; _position += count;
                }
                else
                {
                    int count = Math.Min(frameLength - frameOffset, destination.Length - written);
                    long sourcePosition = fragment.DataOffset + fragment.Offsets[frame] - 7L * frame + frameOffset - 7;
                    if (_source.Position != sourcePosition) _source.Position = sourcePosition;
                    _source.ReadExactly(destination.Slice(written, count));
                    written += count; _position += count;
                }
            }
            return written;
        }
    }

    private int FindFragment(long position)
    {
        int low = 0, high = _fragments.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (_fragments[middle].VirtualStart <= position) low = middle + 1;
            else high = middle - 1;
        }
        return Math.Max(0, high);
    }

    private void WriteHeader(Span<byte> bytes, int frameLength)
    {
        bytes[0] = 0xff; bytes[1] = 0xf1;
        bytes[2] = (byte)((_profile - 1) << 6 | _frequencyIndex << 2 | _channels >> 2);
        bytes[3] = (byte)((_channels & 3) << 6 | frameLength >> 11);
        bytes[4] = (byte)(frameLength >> 3);
        bytes[5] = (byte)((frameLength & 7) << 5 | 0x1f); bytes[6] = 0xfc;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(_position + offset), SeekOrigin.End => checked(Length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
            if (position < 0 || position > Length) throw new IOException("Cannot seek outside the AAC stream.");
            return _position = position;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (_sync) { if (!_disposed) { _disposed = true; _source.Dispose(); } }
        base.Dispose(disposing);
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private static List<Box> Boxes(ReadOnlySpan<byte> bytes, int start, int end)
    {
        var boxes = new List<Box>();
        for (int p = start; p + 8 <= end;)
        {
            long size = BoxSize(bytes, p, end - p, out int header);
            if (size > end - p) throw new InvalidDataException("The MP4 box extends beyond its container.");
            boxes.Add(new(p, checked((int)size), header, Kind(bytes, p + 4))); p += checked((int)size);
        }
        return boxes;
    }
    private static long BoxSize(ReadOnlySpan<byte> bytes, int p, long available, out int header)
    {
        Require(bytes, p, 8); long size = U32(bytes, p); header = 8;
        if (size == 1) { size = checked((long)U64(bytes, p + 8)); header = 16; }
        else if (size == 0) size = available;
        if (size < header || size > available) throw new InvalidDataException("The MP4 box size is invalid.");
        return size;
    }
    private static uint U32(ReadOnlySpan<byte> bytes, int p) { Require(bytes, p, 4); return BinaryPrimitives.ReadUInt32BigEndian(bytes[p..]); }
    private static ulong U64(ReadOnlySpan<byte> bytes, int p) { Require(bytes, p, 8); return BinaryPrimitives.ReadUInt64BigEndian(bytes[p..]); }
    private static string Kind(ReadOnlySpan<byte> bytes, int p) { Require(bytes, p, 4); return Encoding.ASCII.GetString(bytes.Slice(p, 4)); }
    private static void Require(ReadOnlySpan<byte> bytes, int p, int count) { if (p < 0 || count < 0 || p > bytes.Length - count) throw new InvalidDataException("The MP4 metadata is truncated."); }
    private readonly record struct Box(int Offset, int Size, int Header, string Kind) { public int End => Offset + Size; }
    private readonly record struct Reference(long Offset, int Size);
    private readonly record struct AudioConfiguration(uint TrackId, int Profile, int FrequencyIndex, int SampleRate, int Channels);
    private sealed class IndexBudget
    {
        private long _bytes;
        public void Reserve(int bytes)
        {
            if (Interlocked.Add(ref _bytes, bytes) > 16 * 1024 * 1024)
                throw new InvalidDataException("The AAC frame index exceeds the 16 MiB limit.");
        }
    }
    private sealed class Fragment(long dataOffset, int[] offsets)
    {
        public long DataOffset { get; } = dataOffset;
        public int[] Offsets { get; } = offsets;
        public long VirtualStart { get; set; }
    }
}
