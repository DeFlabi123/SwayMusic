using SwayMusic.Core;

namespace SwayMusic.Desktop.Playback;

public enum RepeatMode { Off, All, One }
public enum PlayerState { Stopped, Loading, Playing, Paused, Failed }

public sealed class PlaybackQueue
{
    private readonly List<Track> _tracks = [];
    private readonly List<int> _order = [];
    private int _cursor = -1;
    private bool _shuffle;

    public Track? Current => _cursor >= 0 && _cursor < _order.Count ? _tracks[_order[_cursor]] : null;
    public int CurrentIndex => _cursor >= 0 && _cursor < _order.Count ? _order[_cursor] : -1;
    public int Count => _tracks.Count;
    public RepeatMode Repeat { get; set; }
    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value) return;
            var index = CurrentIndex;
            _shuffle = value;
            RebuildOrder(index);
        }
    }

    public Track[] Snapshot() => _tracks.ToArray();

    public void Set(IEnumerable<Track> tracks, int startIndex)
    {
        var copy = tracks.ToArray();
        if (copy.Length > 0 && (startIndex < 0 || startIndex >= copy.Length))
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        _tracks.Clear();
        _tracks.AddRange(copy);
        RebuildOrder(copy.Length == 0 ? -1 : startIndex);
    }

    public void Add(Track track)
    {
        _tracks.Add(track);
        _order.Add(_tracks.Count - 1);
        if (_cursor < 0) _cursor = 0;
    }

    public bool RemoveAt(int index)
    {
        if (index < 0 || index >= _tracks.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var currentRemoved = CurrentIndex == index;
        var orderIndex = _order.IndexOf(index);
        _tracks.RemoveAt(index);
        _order.RemoveAt(orderIndex);
        for (var i = 0; i < _order.Count; i++) if (_order[i] > index) _order[i]--;
        if (orderIndex < _cursor) _cursor--;
        if (_cursor >= _order.Count) _cursor = _order.Count - 1;
        return currentRemoved;
    }

    public void Move(int index, int direction)
    {
        if (index < 0 || index >= _tracks.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var destination = index + direction;
        if (destination < 0 || destination >= _tracks.Count) return;
        (_tracks[index], _tracks[destination]) = (_tracks[destination], _tracks[index]);
        for (var i = 0; i < _order.Count; i++)
        {
            if (_order[i] == index) _order[i] = destination;
            else if (_order[i] == destination) _order[i] = index;
        }
        if (!_shuffle)
        {
            var selected = CurrentIndex;
            for (var i = 0; i < _order.Count; i++) _order[i] = i;
            _cursor = selected;
        }
    }

    public bool MoveNext(bool automatic)
    {
        if (_tracks.Count == 0) return false;
        if (automatic && Repeat == RepeatMode.One) return true;
        if (_cursor + 1 < _order.Count) { _cursor++; return true; }
        if (Repeat != RepeatMode.All) return false;
        _cursor = 0;
        return true;
    }

    public bool MovePrevious()
    {
        if (_tracks.Count == 0) return false;
        if (_cursor > 0) { _cursor--; return true; }
        if (Repeat != RepeatMode.All) return true;
        _cursor = _order.Count - 1;
        return true;
    }

    private void RebuildOrder(int currentIndex)
    {
        _order.Clear();
        for (var i = 0; i < _tracks.Count; i++) _order.Add(i);
        if (_shuffle && _order.Count > 1)
        {
            // Fisher-Yates retains each queue entry exactly once, including duplicate songs.
            for (var i = _order.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
            var selected = _order.IndexOf(currentIndex);
            if (selected >= 0) (_order[0], _order[selected]) = (_order[selected], _order[0]);
            _cursor = _order.Count == 0 ? -1 : 0;
        }
        else _cursor = currentIndex;
    }
}
