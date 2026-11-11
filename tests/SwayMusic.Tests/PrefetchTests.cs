using SwayMusic.Core;
using SwayMusic.Desktop.Playback;

namespace SwayMusic.Tests;

internal static class PrefetchTests
{
    public static async Task RunAsync()
    {
        var a = new Track("a", "A", "Fixture"); var b = new Track("b", "B", "Fixture");
        var queue = new PlaybackQueue(); queue.Set([a, b, a], 0);
        Check(queue.PeekNext() == b && queue.CurrentIndex == 0, "peek must not advance");
        queue.Shuffle = true;
        var expected = queue.PeekNext(); queue.MoveNext(false);
        Check(queue.Current == expected, "peek follows the real shuffle order");
        queue.Set([a], 0); Check(queue.PeekNext() is null, "end of queue");
        queue.Repeat = RepeatMode.All; Check(queue.PeekNext() == a, "repeat-all wrap");

        int calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken warmToken = default;
        await using (var prefetch = new TrackSourcePrefetcher((track, token) =>
        { Interlocked.Increment(ref calls); warmToken = token; started.TrySetResult(); return ready.Task.WaitAsync(token); }))
        {
            prefetch.Prepare(b); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            prefetch.Prepare(b);
            var foreground = prefetch.ResolveAsync(b, CancellationToken.None);
            Check(calls == 1, "Next joins the pending preparation");
            ready.SetResult("url-b"); Check(await foreground == "url-b", "shared result");
        }
        calls = 0;
        started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var prefetch = new TrackSourcePrefetcher(async (track, token) =>
        {
            Interlocked.Increment(ref calls);
            if (track.Id == "b") { warmToken = token; started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return "url-a";
        }))
        {
            prefetch.Prepare(b); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(await prefetch.ResolveAsync(a, CancellationToken.None) == "url-a" && warmToken.IsCancellationRequested,
                "a different foreground selection cancels speculative work");
            prefetch.Prepare(a); prefetch.Prepare(null);
        }
        calls = 0;
        started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var prefetch = new TrackSourcePrefetcher((_, _) =>
        { if (Interlocked.Increment(ref calls) == 1) { started.TrySetResult(); return failure.Task; } return Task.FromResult("retry-url"); }))
        {
            prefetch.Prepare(b);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var foreground = prefetch.ResolveAsync(b, CancellationToken.None);
            failure.SetException(new IOException("fixture"));
            Check(await foreground == "retry-url" && calls == 2, "speculative failures retry through foreground resolution");
        }
        started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var prefetch = new TrackSourcePrefetcher(async (_, token) =>
        { warmToken = token; started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return "unreachable"; }))
        {
            prefetch.Prepare(b); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var cancel = new CancellationTokenSource();
            var foreground = prefetch.ResolveAsync(b, cancel.Token);
            cancel.Cancel();
            try { await foreground; throw new InvalidOperationException("Cancelled playback continued."); }
            catch (OperationCanceledException) { }
            Check(warmToken.IsCancellationRequested, "foreground cancellation stops the joined preparation");
        }
        Console.WriteLine("PASS bounded next-source preparation, shuffle/repeat peek, shared work and foreground priority");
    }
    private static void Check(bool valid, string message)
    { if (!valid) throw new InvalidOperationException("Prefetch: " + message); }
}
