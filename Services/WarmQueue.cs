namespace Gelato.Services;

// Lower runs first.
public enum WarmPriority
{
    // The title page or Play: someone is looking at it now.
    Play = 0,

    // Continue watching, next episode: likely the next thing played.
    Resume = 1,

    // Any other row or result list.
    Browse = 2,
}

public enum WarmOutcome
{
    Ran,
    Dropped,
    Expired,
    Cancelled,
}

public sealed class WarmJob
{
    internal WarmJob(Guid userId, Guid itemId, bool probe, WarmPriority priority, Func<Task> work, DateTime at, long seq)
    {
        UserId = userId;
        ItemId = itemId;
        Probe = probe;
        Priority = priority;
        Work = work;
        QueuedAt = at;
        Seq = seq;
    }

    public Guid UserId { get; }
    public Guid ItemId { get; }
    public bool Probe { get; }
    public WarmPriority Priority { get; internal set; }
    public Func<Task> Work { get; internal set; }
    public DateTime QueuedAt { get; internal set; }
    internal long Seq { get; set; }

    internal TaskCompletionSource<WarmOutcome> Done { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<WarmOutcome> Completion => Done.Task;

    internal bool Matches(Guid userId, Guid itemId, bool probe) =>
        UserId == userId && ItemId == itemId && Probe == probe;
}

// Which warm-up runs next. Not thread safe: WarmupGate holds a lock around it.
public sealed class WarmQueue
{
    // Every warm-up adds a release to nzbdav (import, article check, Usenet
    // connections), so the whole server runs at most two at once, and one of
    // them is kept for a title page so it never waits behind a list.
    public const int MaxConcurrent = 2;
    public const int ReservedForPlay = 1;
    public const int MaxQueuedPerUser = 8;

    // By then the focus that asked for it is long gone.
    public static readonly TimeSpan MaxQueueAge = TimeSpan.FromMinutes(3);

    private readonly List<WarmJob> _queued = [];
    private readonly List<WarmJob> _running = [];
    private long _seq;

    public int QueuedCount => _queued.Count;
    public int RunningCount => _running.Count;

    // A repeat of a queued or running job (same user, item and probe) joins
    // it; a queued one takes the higher of the two priorities.
    public WarmJob Enqueue(Guid userId, Guid itemId, bool probe, WarmPriority priority, Func<Task> work, DateTime now)
    {
        if (_running.Find(j => j.Matches(userId, itemId, probe)) is { } running)
            return running;

        if (_queued.Find(j => j.Matches(userId, itemId, probe)) is { } queued)
        {
            if (priority < queued.Priority)
            {
                queued.Priority = priority;
                queued.Work = work;
                queued.Seq = ++_seq;
            }
            queued.QueuedAt = now;
            return queued;
        }

        var job = new WarmJob(userId, itemId, probe, priority, work, now, ++_seq);
        _queued.Add(job);
        TrimUser(userId);
        return job;
    }

    // The next job to run, already counted as running, or null when none
    // may start now.
    public WarmJob? TryStart(DateTime now)
    {
        Expire(now);
        if (_running.Count >= MaxConcurrent)
            return null;
        var othersRunning = _running.Count(j => j.Priority != WarmPriority.Play);
        var othersMayStart = othersRunning < MaxConcurrent - ReservedForPlay;

        WarmJob? next = null;
        foreach (var job in _queued)
        {
            if (job.Priority != WarmPriority.Play && !othersMayStart)
                continue;
            if (next is null || job.Priority < next.Priority || (job.Priority == next.Priority && job.Seq < next.Seq))
                next = job;
        }
        if (next is null)
            return null;
        _queued.Remove(next);
        _running.Add(next);
        return next;
    }

    public void Finish(WarmJob job)
    {
        if (_running.Remove(job))
            job.Done.TrySetResult(WarmOutcome.Ran);
    }

    // Only queued jobs: one already running finishes.
    public int Cancel(Guid userId, Guid itemId) =>
        RemoveWhere(j => j.UserId == userId && j.ItemId == itemId, WarmOutcome.Cancelled);

    public int Expire(DateTime now) => RemoveWhere(j => now - j.QueuedAt > MaxQueueAge, WarmOutcome.Expired);

    // Over the cap the user's oldest job of the lowest priority goes.
    private void TrimUser(Guid userId)
    {
        while (_queued.Count(j => j.UserId == userId) > MaxQueuedPerUser)
        {
            var victim = _queued
                .Where(j => j.UserId == userId)
                .OrderByDescending(j => j.Priority)
                .ThenBy(j => j.Seq)
                .First();
            _queued.Remove(victim);
            victim.Done.TrySetResult(WarmOutcome.Dropped);
        }
    }

    private int RemoveWhere(Predicate<WarmJob> match, WarmOutcome outcome)
    {
        var gone = _queued.FindAll(match);
        foreach (var job in gone)
        {
            _queued.Remove(job);
            job.Done.TrySetResult(outcome);
        }
        return gone.Count;
    }
}
