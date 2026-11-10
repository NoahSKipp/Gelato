namespace Gelato.Services;

// Runs warm-ups in the order WarmQueue picks, two at most server-wide.
public sealed class WarmupGate
{
    private readonly WarmQueue _queue = new();
    private readonly object _lock = new();
    private readonly Func<DateTime> _now;

    public WarmupGate()
        : this(() => DateTime.UtcNow) { }

    public WarmupGate(Func<DateTime> now)
    {
        _now = now;
    }

    public int Queued
    {
        get
        {
            lock (_lock)
                return _queue.QueuedCount;
        }
    }

    public int Running
    {
        get
        {
            lock (_lock)
                return _queue.RunningCount;
        }
    }

    // Completes when the work has run, or with the reason it never will.
    public Task<WarmOutcome> RunAsync(
        Guid userId,
        Guid itemId,
        bool probe,
        WarmPriority priority,
        Func<Task> work
    )
    {
        WarmJob job;
        lock (_lock)
            job = _queue.Enqueue(userId, itemId, probe, priority, work, _now());
        Pump();
        return job.Completion;
    }

    public int Cancel(Guid userId, Guid itemId)
    {
        lock (_lock)
            return _queue.Cancel(userId, itemId);
    }

    private void Pump()
    {
        while (true)
        {
            WarmJob? job;
            lock (_lock)
                job = _queue.TryStart(_now());
            if (job is null)
                return;
            using (ExecutionContext.SuppressFlow())
                _ = Task.Run(() => RunJob(job));
        }
    }

    private async Task RunJob(WarmJob job)
    {
        try
        {
            await job.Work().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The work logs its own failures.
        }
        finally
        {
            lock (_lock)
                _queue.Finish(job);
            Pump();
        }
    }
}
