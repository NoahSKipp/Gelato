namespace Gelato.Services;

public sealed class WarmRecord
{
    public DateTime WarmedAt { get; set; }
    public bool ProbeOk { get; set; }
    public string? Release { get; set; }
}

// Which stream rows start fast right now: probed without tripping the
// placeholder fingerprint, within the last six hours, and (when nzbdav can
// be asked) still imported there. Keyed by the stream row's item id.
public sealed class WarmTracker
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

    private readonly JsonFileStore<WarmRecord> _store;
    private readonly Func<DateTime> _now;

    public WarmTracker()
        : this(
            new JsonFileStore<WarmRecord>(() =>
                Path.Combine(GelatoPlugin.Instance!.DataFolderPath, "warm-streams.json")
            ),
            () => DateTime.UtcNow
        )
    { }

    public WarmTracker(JsonFileStore<WarmRecord> store, Func<DateTime> now)
    {
        _store = store;
        _now = now;
    }

    public void MarkWarm(string sourceId, string? release)
    {
        var now = _now();
        _store.RemoveWhere((_, r) => now - r.WarmedAt > Ttl);
        _store.Set(sourceId, new WarmRecord { WarmedAt = now, ProbeOk = true, Release = release });
    }

    public void MarkCold(string sourceId)
    {
        _store.Set(sourceId, new WarmRecord { WarmedAt = _now(), ProbeOk = false });
    }

    // releasePresent answers true, false, or null when nzbdav can't tell.
    public DateTime? WarmedAt(string sourceId, Func<string, bool?>? releasePresent = null)
    {
        if (!_store.TryGet(sourceId, out var record) || !record.ProbeOk)
            return null;
        if (_now() - record.WarmedAt > Ttl)
            return null;
        if (
            releasePresent is not null
            && !string.IsNullOrEmpty(record.Release)
            && releasePresent(record.Release) == false
        )
        {
            return null;
        }
        return record.WarmedAt;
    }

    public string? ReleaseOf(string sourceId) =>
        _store.TryGet(sourceId, out var record) ? record.Release : null;
}
