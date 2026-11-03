namespace Gelato.Services;

// Stream rows found to be a placeholder clip, set aside for a while so they
// are neither listed nor probed again. Keyed by the stream row's item id.
public sealed class BadStreamStore
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(6);

    private readonly JsonFileStore<DateTime> _store;
    private readonly Func<DateTime> _now;

    public BadStreamStore()
        : this(
            new JsonFileStore<DateTime>(() =>
                Path.Combine(GelatoPlugin.Instance!.DataFolderPath, "bad-streams.json")
            ),
            () => DateTime.UtcNow
        )
    { }

    public BadStreamStore(JsonFileStore<DateTime> store, Func<DateTime> now)
    {
        _store = store;
        _now = now;
    }

    public bool IsBad(string sourceId)
    {
        if (!_store.TryGet(sourceId, out var until))
            return false;
        if (until > _now())
            return true;
        _store.Remove(sourceId);
        return false;
    }

    public void MarkBad(string sourceId, TimeSpan? ttl = null)
    {
        var now = _now();
        _store.RemoveWhere((_, until) => until <= now);
        _store.Set(sourceId, now + (ttl ?? DefaultTtl));
    }
}
