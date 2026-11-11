using SwayMusic.Core;
using SwayMusic.Desktop.Playback;

namespace SwayMusic.Tests;

public static class PlaybackTests
{
    public static async Task AacStreamSmokeAsync()
    {
        var stem = Path.Combine(Path.GetTempPath(), $"metrolist-aac-{Guid.NewGuid():N}");
        var wavePath = stem + ".wav";
        var aacPath = stem + ".mp4";
        try
        {
            WriteSilence(wavePath, 8);
            NAudio.MediaFoundation.MediaFoundationApi.Startup();
            using (var input = new NAudio.Wave.WaveFileReader(wavePath))
                NAudio.Wave.MediaFoundationEncoder.EncodeToAac(input, aacPath, 128000);
            CancellationToken transportToken = default;
            FileStream? transport = null;
            await using var player = new PlaybackService((_, _) => Task.FromResult("https://audio.invalid/test.m4a"),
                (_, ct) => { transportToken = ct; transport = File.OpenRead(aacPath); return Task.FromResult<Stream>(transport); }) { Volume = 0 };
            Exception? failure = null;
            player.PlaybackFailed += (_, error) => failure = error;
            await player.PlayTrackAsync(new Track("aac-smoke", "AAC stream verification", "Test"));
            await Task.Delay(700);
            if (failure is not null) throw new InvalidOperationException("AAC byte-stream decoding failed.", failure);
            Check(player.State == PlayerState.Playing && player.Position > TimeSpan.Zero,
                $"AAC byte stream did not advance: state={player.State}, position={player.Position}, duration={player.Duration}.");
            Check(!transportToken.IsCancellationRequested, "A completed load must not cancel the active stream transport.");
            player.Pause();
            await player.SeekAsync(TimeSpan.FromSeconds(2));
            player.Play();
            await Task.Delay(250);
            Check(player.State == PlayerState.Playing && player.Position.TotalSeconds >= 2, "AAC byte stream did not seek and resume.");
            await player.StopAsync();
            Check(transportToken.IsCancellationRequested && transport is { CanRead: false }, "Stopping must cancel and dispose the compressed transport.");
        }
        finally { File.Delete(wavePath); File.Delete(aacPath); }
    }

    public static async Task ProfileAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"metrolist-profile-{Guid.NewGuid():N}.wav");
        try
        {
            WriteSilence(path, 30);
            var player = new PlaybackService((_, _) => Task.FromResult(path));
            player.Volume = 0;
            try
            {
                await MeasureAsync("engine idle", TimeSpan.FromSeconds(3));
                await player.PlayTrackAsync(new Track("profile", "Resource measurement", "Test", LocalPath: path));
                await Task.Delay(250);
                await MeasureAsync("local WAV playing", TimeSpan.FromSeconds(3));
                player.Pause();
                await Task.Delay(250);
                await MeasureAsync("local WAV paused", TimeSpan.FromSeconds(3));
            }
            finally { await player.DisposeAsync(); }
            await MeasureAsync("engine disposed", TimeSpan.FromSeconds(3));
        }
        finally { File.Delete(path); }
    }

    private static async Task MeasureAsync(string state, TimeSpan interval)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(interval);
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds / timer.Elapsed.TotalSeconds * 100;
        Console.WriteLine($"PROFILE {state}: CPU={cpu:F3}% of one core ({cpu / Environment.ProcessorCount:F3}% host), private={process.PrivateMemorySize64 / 1048576d:F2} MiB, working={process.WorkingSet64 / 1048576d:F2} MiB, GC={GC.GetTotalMemory(false) / 1048576d:F2} MiB, threads={process.Threads.Count}, interval={timer.Elapsed.TotalSeconds:F2}s");
    }

    public static async Task RunAsync()
    {
        var first = new Track("a", "A", "Artist");
        var second = new Track("b", "B", "Artist");
        var queue = new PlaybackQueue();
        queue.Set([first, second, first], 1);
        Check(queue.CurrentIndex == 1, "Queue must retain selected index.");
        queue.Repeat = RepeatMode.One;
        Check(queue.MoveNext(true) && queue.CurrentIndex == 1, "Natural end repeats current track.");
        Check(queue.MoveNext(false) && queue.CurrentIndex == 2, "Manual next bypasses repeat-one.");
        queue.Repeat = RepeatMode.Off;
        Check(!queue.MoveNext(true), "Repeat-off must stop at the last track.");
        queue.Repeat = RepeatMode.All;
        Check(queue.MoveNext(true) && queue.CurrentIndex == 0, "Repeat-all wraps to the first track.");
        Check(queue.MovePrevious() && queue.CurrentIndex == 2, "Previous wraps under repeat-all.");
        queue.Shuffle = true;
        Check(queue.CurrentIndex == 2, "Enabling shuffle must preserve the selected duplicate entry.");
        queue.Repeat = RepeatMode.Off;
        var visited = new HashSet<int> { queue.CurrentIndex };
        while (queue.MoveNext(false)) Check(visited.Add(queue.CurrentIndex), "Shuffle must not repeat queue entries.");
        Check(visited.Count == 3, "Shuffle must include each entry exactly once.");
        queue.Shuffle = false;
        Check(queue.Snapshot().Length == 3, "Queue must preserve duplicate songs.");
        queue.Set([first, second, first], 1);
        queue.Move(1, -1);
        Check(queue.CurrentIndex == 0 && queue.Current?.Id == "b", "Reordering preserves the current song.");
        Check(!queue.RemoveAt(2) && queue.Current?.Id == "b", "Removing a different duplicate preserves the current song.");
        Check(queue.RemoveAt(0) && queue.Current?.Id == "a", "Removing the current song selects the following entry.");
        queue.RemoveAt(0);
        Check(queue.Current is null, "Removing the final entry empties the queue safely.");
        queue.Set([], 0);
        Check(queue.Current is null && !queue.MoveNext(true) && !queue.MovePrevious(), "Empty queues must be safe.");
        var resolverEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var player = new PlaybackService(async (_, cancellationToken) =>
        {
            calls++;
            resolverEntered.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "unreachable";
        });
        player.RestoreQueue([first, second], 1);
        Check(calls == 0 && player.State == PlayerState.Stopped && player.CurrentIndex == 1,
            "Restoring a queue must not resolve or autoplay audio.");
        var pending = player.PlayTrackAsync(first);
        await resolverEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await player.SetQueueAsync([]).WaitAsync(TimeSpan.FromSeconds(2));
        try { await pending; throw new InvalidOperationException("Replacing a pending queue should cancel its resolver."); }
        catch (OperationCanceledException) { }
        Check(player.State == PlayerState.Stopped && player.CurrentTrack is null,
            "A superseded asynchronous load must not restore stale state.");
        var loadingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var loadingPlayer = new PlaybackService(async (_, token) =>
        {
            loadingEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        });
        var loading = loadingPlayer.PlayTrackAsync(first);
        await loadingEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        loadingPlayer.Toggle();
        try { await loading; throw new InvalidOperationException("Toggling a loading player should cancel resolution."); }
        catch (OperationCanceledException) { }
        Check(loadingPlayer.State == PlayerState.Stopped, "Cancelling a loading player should leave it stopped.");
    }

    public static async Task SmokeAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"metrolist-audio-{Guid.NewGuid():N}.wav");
        try
        {
            WriteSilence(path, 5);
            await using var player = new PlaybackService((_, _) => Task.FromResult(path));
            player.Volume = 0;
            Exception? failure = null;
            player.PlaybackFailed += (_, error) => failure = error;
            await player.PlayTrackAsync(new Track("local-smoke", "Audio smoke", "Test", LocalPath: path));
            Check(player.State == PlayerState.Playing, "WASAPI did not start playing.");
            await Task.Delay(350);
            Check(player.Position > TimeSpan.Zero, "Decoder position did not advance.");
            player.Pause();
            Check(player.State == PlayerState.Paused, "WASAPI did not pause.");
            var pausedPosition = player.Position;
            await Task.Delay(200);
            Check((player.Position - pausedPosition).Duration().TotalMilliseconds < 100, "Paused playback kept decoding.");
            await player.SeekAsync(TimeSpan.FromSeconds(2));
            Check(Math.Abs(player.Position.TotalSeconds - 2) < 0.15, "Seek did not reach expected position.");
            player.Play();
            await Task.Delay(250);
            Check(player.Position.TotalSeconds > 2, "Resume did not advance decoder.");
            await player.StopAsync();
            Check(player.State == PlayerState.Stopped && failure is null, $"Audio failed: {failure}");
            var advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Changed += (_, _) =>
            {
                if (player.CurrentTrack?.Id == "second" && player.State == PlayerState.Playing) advanced.TrySetResult();
            };
            await player.SetQueueAsync([
                new Track("first", "First", "Test", LocalPath: path),
                new Track("second", "Second", "Test", LocalPath: path)]);
            await player.SeekAsync(TimeSpan.FromSeconds(4.7));
            await advanced.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(player.CurrentIndex == 1, "Natural completion did not advance the queue.");
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Changed += (_, _) =>
            {
                if (player.State == PlayerState.Stopped) ended.TrySetResult();
            };
            await player.SeekAsync(TimeSpan.FromSeconds(4.7));
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(failure is null, $"Completion failed: {failure}");
        }
        finally { File.Delete(path); }
    }

    private static void WriteSilence(string path, int seconds)
    {
        const int rate = 44100, channels = 2, bits = 16;
        var length = rate * channels * (bits / 8) * seconds;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(length + 36); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)channels);
        writer.Write(rate); writer.Write(rate * channels * (bits / 8));
        writer.Write((short)(channels * bits / 8)); writer.Write((short)bits);
        writer.Write("data"u8); writer.Write(length);
        stream.SetLength(44L + length);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
