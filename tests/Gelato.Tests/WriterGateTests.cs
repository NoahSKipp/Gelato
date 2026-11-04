using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class WriterGateTests
{
    private static WriterGate Gate(Func<string?>? heavy = null, int maxDeferMs = 5000) =>
        new(heavy ?? (() => null), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(maxDeferMs));

    [Fact]
    public async Task OnlyOneBackgroundWriterAtATime()
    {
        var gate = Gate();
        var running = 0;
        var peak = 0;
        async Task Work()
        {
            using var lease = await gate.EnterAsync(null, CancellationToken.None);
            var now = Interlocked.Increment(ref running);
            peak = Math.Max(peak, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
        }
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Work()));
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task WaitsWhileTheGuideImportRuns()
    {
        var guideRunning = true;
        var gate = Gate(() => guideRunning ? "RefreshGuide" : null);
        var enter = gate.EnterAsync(null, CancellationToken.None);
        await Task.Delay(60);
        Assert.False(enter.IsCompleted);
        guideRunning = false;
        using var lease = await enter;
        Assert.Equal("RefreshGuide", lease.DeferredFor);
        Assert.False(lease.Promoted);
    }

    [Fact]
    public async Task GivesUpWaitingForTheGuideAfterTheLimit()
    {
        var gate = Gate(() => "RefreshGuide", maxDeferMs: 50);
        using var lease = await gate.EnterAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("RefreshGuide", lease.DeferredFor);
    }

    [Fact]
    public async Task AViewerJoiningSkipsTheQueue()
    {
        var gate = Gate();
        var holder = await gate.EnterAsync(null, CancellationToken.None);
        var promote = new TaskCompletionSource();
        var waiting = gate.EnterAsync(promote.Task, CancellationToken.None);
        await Task.Delay(30);
        Assert.False(waiting.IsCompleted);
        promote.SetResult();
        using var lease = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(lease.Promoted);

        // The promoted writer took no slot, so the gate still works after it.
        holder.Dispose();
        using var next = await gate.EnterAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(next.Promoted);
    }

    [Fact]
    public async Task AViewerJoiningEndsTheGuideWait()
    {
        var gate = Gate(() => "RefreshGuide");
        var promote = new TaskCompletionSource();
        var waiting = gate.EnterAsync(promote.Task, CancellationToken.None);
        await Task.Delay(30);
        promote.SetResult();
        using var lease = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("RefreshGuide", lease.DeferredFor);
    }

    [Fact]
    public async Task DisposingTwiceReleasesOnce()
    {
        var gate = Gate();
        var lease = await gate.EnterAsync(null, CancellationToken.None);
        lease.Dispose();
        lease.Dispose();
        using var a = await gate.EnterAsync(null, CancellationToken.None);
        var b = gate.EnterAsync(null, CancellationToken.None);
        await Task.Delay(30);
        Assert.False(b.IsCompleted);
        a.Dispose();
        (await b).Dispose();
    }

    [Fact]
    public async Task SmallWritesDoNotWaitForTheGuide()
    {
        var gate = Gate(() => "RefreshGuide");
        using var lease = await gate.EnterAsync(null, CancellationToken.None, heavy: false).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(lease.DeferredFor);
    }

    [Fact]
    public async Task SmallWritesStillQueueBehindOtherWriters()
    {
        var gate = Gate();
        var holder = await gate.EnterAsync(null, CancellationToken.None);
        var small = gate.EnterAsync(null, CancellationToken.None, heavy: false);
        await Task.Delay(30);
        Assert.False(small.IsCompleted);
        holder.Dispose();
        (await small.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }
}
