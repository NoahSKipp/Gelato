using System.Collections.Concurrent;

namespace Gelato.Services;

// At most one forced stream refresh per user and title in a window: a
// title whose links keep failing (or that has no streams at all) must not
// make every Play ask the addon again.
public sealed class ResyncThrottle(TimeSpan window, Func<DateTime>? now = null)
{
    private readonly ConcurrentDictionary<string, DateTime> _at = new();
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

    public bool TryEnter(string key)
    {
        var now = _now();
        if (_at.Count > 10000)
        {
            foreach (var old in _at.Where(e => now - e.Value >= window).Select(e => e.Key).ToList())
                _at.TryRemove(old, out _);
        }
        while (true)
        {
            if (_at.TryGetValue(key, out var at))
            {
                if (now - at < window)
                    return false;
                if (_at.TryUpdate(key, now, at))
                    return true;
            }
            else if (_at.TryAdd(key, now))
            {
                return true;
            }
        }
    }
}
