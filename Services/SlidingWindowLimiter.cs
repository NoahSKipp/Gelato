using System.Collections.Concurrent;

namespace Gelato.Services;

// At most `max` calls per key within `window`.
public sealed class SlidingWindowLimiter(int max, TimeSpan window, Func<DateTime>? now = null)
{
    private readonly ConcurrentDictionary<Guid, Queue<DateTime>> _calls = new();
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

    public bool TryAcquire(Guid key)
    {
        var queue = _calls.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (queue)
        {
            var now = _now();
            while (queue.Count > 0 && now - queue.Peek() >= window)
                queue.Dequeue();
            if (queue.Count >= max)
                return false;
            queue.Enqueue(now);
            return true;
        }
    }
}
