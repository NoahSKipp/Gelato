using System.Collections.Concurrent;

namespace Gelato.Services;

// Recent addon search results, per user and term, for a minute: typing and
// re-opening the search screen repeat the same request.
public sealed class SearchCache<T>
{
    private readonly ConcurrentDictionary<string, (DateTime At, T Value)> _entries = new();
    private readonly TimeSpan _ttl;
    private readonly int _capacity;
    private readonly Func<DateTime> _now;

    public SearchCache(TimeSpan ttl, int capacity = 256, Func<DateTime>? now = null)
    {
        _ttl = ttl;
        _capacity = capacity;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public static string Key(Guid userId, string term, IEnumerable<string> parts) =>
        userId.ToString("N")
        + "|"
        + term.Trim().ToLowerInvariant()
        + "|"
        + string.Join(",", parts.OrderBy(p => p, StringComparer.Ordinal));

    public bool TryGet(string key, out T value)
    {
        if (_entries.TryGetValue(key, out var entry) && _now() - entry.At < _ttl)
        {
            value = entry.Value;
            return true;
        }
        _entries.TryRemove(key, out _);
        value = default!;
        return false;
    }

    public void Set(string key, T value)
    {
        var now = _now();
        if (_entries.Count >= _capacity)
        {
            foreach (var stale in _entries.Where(e => now - e.Value.At >= _ttl).Select(e => e.Key).ToList())
                _entries.TryRemove(stale, out _);
            while (_entries.Count >= _capacity)
            {
                var oldest = _entries.OrderBy(e => e.Value.At).First().Key;
                _entries.TryRemove(oldest, out _);
            }
        }
        _entries[key] = (now, value);
    }
}
