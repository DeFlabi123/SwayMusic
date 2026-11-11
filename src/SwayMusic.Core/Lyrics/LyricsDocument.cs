using System.Globalization;
using System.Text.RegularExpressions;

namespace SwayMusic.Core.Lyrics;

public sealed record LyricLine(TimeSpan Time, string Text);

public sealed record LyricsDocument(string Text, IReadOnlyList<LyricLine> Lines, string Provider = "LRCLIB")
{
    public bool IsSynced => Lines.Count > 0;

    public int GetActiveLine(TimeSpan position)
    {
        int low = 0, high = Lines.Count - 1, result = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (Lines[middle].Time <= position) { result = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return result;
    }
}

public static partial class LrcParser
{
    public static LyricsDocument Parse(string text, string provider = "LRCLIB")
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<LyricLine>();
        int offset = 0;
        var offsetMatch = OffsetPattern().Match(text);
        if (offsetMatch.Success) int.TryParse(offsetMatch.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out offset);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var timestamps = TimestampPattern().Matches(line);
            if (timestamps.Count == 0) continue;
            var last = timestamps[^1];
            string content = WordTimestampPattern().Replace(line[(last.Index + last.Length)..], "").Trim();
            foreach (Match timestamp in timestamps)
            {
                int minutes = int.Parse(timestamp.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
                int seconds = int.Parse(timestamp.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
                var fraction = timestamp.Groups[3].ValueSpan;
                int milliseconds = fraction.Length > 0 ? int.Parse(fraction, CultureInfo.InvariantCulture) : 0;
                if (fraction.Length == 1) milliseconds *= 100;
                else if (fraction.Length == 2) milliseconds *= 10;
                long total = minutes * 60000L + seconds * 1000L + milliseconds + offset;
                lines.Add(new(TimeSpan.FromMilliseconds(Math.Max(0, total)), content));
            }
        }
        // OrderBy is stable for simultaneous lines and costs only once per fetched lyric document.
        return new(text, lines.OrderBy(l => l.Time).ToArray(), provider);
    }

    [GeneratedRegex(@"\[(\d{1,3}):([0-5]\d)(?:[.:](\d{1,3}))?\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();
    [GeneratedRegex(@"\[offset:([+-]?\d+)\]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OffsetPattern();
    [GeneratedRegex(@"<\d{1,3}:[0-5]\d(?:\.\d{1,3})?>", RegexOptions.CultureInvariant)]
    private static partial Regex WordTimestampPattern();
}
