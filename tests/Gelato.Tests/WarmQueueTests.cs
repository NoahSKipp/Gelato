using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class WarmQueueTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid U1 = Guid.Parse("11111111-0000-0000-0000-000000000000");
    private static readonly Guid U2 = Guid.Parse("22222222-0000-0000-0000-000000000000");

    private static Guid Item(int n) => new(n, 0, 0, new byte[8]);

    private static Task Noop() => Task.CompletedTask;

    private static WarmJob Add(WarmQueue q, Guid user, int item, WarmPriority p, DateTime? at = null, bool probe = false) =>
        q.Enqueue(user, Item(item), probe, p, Noop, at ?? T0);

    [Fact]
    public void HigherPriorityStartsFirstThenFifo()
    {
        var q = new WarmQueue();
        Add(q, U1, 1, WarmPriority.Browse);
        Add(q, U1, 2, WarmPriority.Resume);
        Add(q, U1, 3, WarmPriority.Play);
        Add(q, U1, 4, WarmPriority.Play);

        var first = q.TryStart(T0)!;
        var second = q.TryStart(T0)!;
        Assert.Equal(Item(3), first.ItemId);
        Assert.Equal(Item(4), second.ItemId);
        Assert.Null(q.TryStart(T0));

        q.Finish(first);
        q.Finish(second);
        Assert.Equal(Item(2), q.TryStart(T0)!.ItemId);
    }

    [Fact]
    public void ListsNeverTakeTheSlotKeptForPlay()
    {
        var q = new WarmQueue();
        for (var i = 1; i <= 5; i++)
            Add(q, U1, i, WarmPriority.Browse);

        var running = q.TryStart(T0);
        Assert.NotNull(running);
        Assert.Null(q.TryStart(T0));
        Assert.Equal(1, q.RunningCount);

        var play = Add(q, U2, 9, WarmPriority.Play);
        Assert.Same(play, q.TryStart(T0));
        Assert.Equal(2, q.RunningCount);
    }

    [Fact]
    public void TwoTitlePagesMayRunTogether()
    {
        var q = new WarmQueue();
        Add(q, U1, 1, WarmPriority.Play);
        Add(q, U2, 2, WarmPriority.Play);
        Assert.NotNull(q.TryStart(T0));
        Assert.NotNull(q.TryStart(T0));
        Assert.Null(q.TryStart(T0));
    }

    [Fact]
    public void OverTheUserCapTheOldestLowestPriorityJobGoes()
    {
        var q = new WarmQueue();
        var resume = Add(q, U1, 100, WarmPriority.Resume);
        var browse = new List<WarmJob>();
        for (var i = 1; i < WarmQueue.MaxQueuedPerUser; i++)
            browse.Add(Add(q, U1, i, WarmPriority.Browse));
        var other = Add(q, U2, 50, WarmPriority.Browse);
        Assert.Equal(WarmQueue.MaxQueuedPerUser + 1, q.QueuedCount);

        Add(q, U1, 99, WarmPriority.Browse);

        Assert.Equal(WarmQueue.MaxQueuedPerUser + 1, q.QueuedCount);
        Assert.True(browse[0].Completion.IsCompleted);
        Assert.Equal(WarmOutcome.Dropped, browse[0].Completion.Result);
        Assert.False(resume.Completion.IsCompleted);
        Assert.False(browse[1].Completion.IsCompleted);
        Assert.False(other.Completion.IsCompleted);
    }

    [Fact]
    public void ARepeatJoinsTheQueuedJobAndRaisesItsPriority()
    {
        var q = new WarmQueue();
        Add(q, U1, 1, WarmPriority.Browse);
        var first = Add(q, U1, 2, WarmPriority.Browse);
        var again = Add(q, U1, 2, WarmPriority.Play);

        Assert.Same(first, again);
        Assert.Equal(2, q.QueuedCount);
        Assert.Equal(WarmPriority.Play, first.Priority);
        Assert.Same(first, q.TryStart(T0));
    }

    [Fact]
    public void ARepeatNeverLowersPriority()
    {
        var q = new WarmQueue();
        var job = Add(q, U1, 1, WarmPriority.Play);
        Add(q, U1, 1, WarmPriority.Browse);
        Assert.Equal(WarmPriority.Play, job.Priority);
    }

    [Fact]
    public void OtherUsersOrProbeAreSeparateJobs()
    {
        var q = new WarmQueue();
        Add(q, U1, 1, WarmPriority.Browse);
        Add(q, U2, 1, WarmPriority.Browse);
        Add(q, U1, 1, WarmPriority.Browse, probe: true);
        Assert.Equal(3, q.QueuedCount);
    }

    [Fact]
    public void ARepeatOfARunningJobJoinsIt()
    {
        var q = new WarmQueue();
        var job = Add(q, U1, 1, WarmPriority.Play);
        Assert.Same(job, q.TryStart(T0));
        Assert.Same(job, Add(q, U1, 1, WarmPriority.Play));
        Assert.Equal(0, q.QueuedCount);
        q.Finish(job);
        Assert.Equal(WarmOutcome.Ran, job.Completion.Result);
    }

    [Fact]
    public void OldJobsExpire()
    {
        var q = new WarmQueue();
        var old = Add(q, U1, 1, WarmPriority.Play);
        var fresh = Add(q, U1, 2, WarmPriority.Browse, T0 + TimeSpan.FromMinutes(2));

        var started = q.TryStart(T0 + WarmQueue.MaxQueueAge + TimeSpan.FromSeconds(1));

        Assert.Same(fresh, started);
        Assert.Equal(WarmOutcome.Expired, old.Completion.Result);
    }

    [Fact]
    public void ARepeatKeepsAJobFromExpiring()
    {
        var q = new WarmQueue();
        var job = Add(q, U1, 1, WarmPriority.Browse);
        Add(q, U1, 1, WarmPriority.Browse, T0 + TimeSpan.FromMinutes(2));
        Assert.Same(job, q.TryStart(T0 + WarmQueue.MaxQueueAge + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void CancelTakesBackOnlyThatUsersQueuedJobs()
    {
        var q = new WarmQueue();
        var running = Add(q, U1, 1, WarmPriority.Play);
        Assert.Same(running, q.TryStart(T0));
        var queued = Add(q, U1, 1, WarmPriority.Play, probe: true);
        var mineOther = Add(q, U1, 2, WarmPriority.Play);
        var theirs = Add(q, U2, 1, WarmPriority.Play);

        Assert.Equal(1, q.Cancel(U1, Item(1)));
        Assert.Equal(WarmOutcome.Cancelled, queued.Completion.Result);
        Assert.False(running.Completion.IsCompleted);
        Assert.False(mineOther.Completion.IsCompleted);
        Assert.False(theirs.Completion.IsCompleted);
        Assert.Equal(0, q.Cancel(U2, Item(9)));
    }

    [Fact]
    public void ContextPicksTheBatchPriority()
    {
        Assert.Equal(WarmPriority.Resume, PrefetchBatch.PriorityFor("resume"));
        Assert.Equal(WarmPriority.Resume, PrefetchBatch.PriorityFor(" Next-Up "));
        Assert.Equal(WarmPriority.Browse, PrefetchBatch.PriorityFor("search"));
        Assert.Equal(WarmPriority.Browse, PrefetchBatch.PriorityFor(null));
    }
}

public class WarmupGateTests
{
    private static readonly Guid U = Guid.NewGuid();

    [Fact]
    public async Task ATitlePageRunsWhileAListHoldsItsSlot()
    {
        var gate = new WarmupGate();
        var release = new TaskCompletionSource();
        var started = new List<int>();
        Func<Task> Blocked(int n) =>
            async () =>
            {
                lock (started)
                    started.Add(n);
                await release.Task;
            };

        var lists = Enumerable
            .Range(1, 4)
            .Select(i => gate.RunAsync(U, Guid.NewGuid(), false, WarmPriority.Browse, Blocked(i)))
            .ToList();
        var play = gate.RunAsync(U, Guid.NewGuid(), true, WarmPriority.Play, Blocked(9));

        await WaitFor(() =>
        {
            lock (started)
                return started.Count == 2;
        });
        lock (started)
            Assert.Equal([1, 9], started.Order());
        Assert.Equal(3, gate.Queued);

        release.SetResult();
        Assert.Equal(WarmOutcome.Ran, await play);
        foreach (var t in lists)
            Assert.Equal(WarmOutcome.Ran, await t);
        Assert.Equal(0, gate.Running);
    }

    [Fact]
    public async Task ACancelledJobNeverRuns()
    {
        var gate = new WarmupGate();
        var release = new TaskCompletionSource();
        var ran = false;
        var item = Guid.NewGuid();
        var blocker = gate.RunAsync(U, Guid.NewGuid(), false, WarmPriority.Browse, () => release.Task);
        var queued = gate.RunAsync(
            U,
            item,
            false,
            WarmPriority.Browse,
            () =>
            {
                ran = true;
                return Task.CompletedTask;
            }
        );

        Assert.Equal(1, gate.Cancel(U, item));
        release.SetResult();
        Assert.Equal(WarmOutcome.Cancelled, await queued);
        await blocker;
        Assert.False(ran);
    }

    [Fact]
    public async Task FailingWorkStillFreesItsSlot()
    {
        var gate = new WarmupGate();
        var outcome = await gate.RunAsync(
            U,
            Guid.NewGuid(),
            false,
            WarmPriority.Play,
            () => throw new InvalidOperationException("boom")
        );
        Assert.Equal(WarmOutcome.Ran, outcome);
        Assert.Equal(0, gate.Running);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
