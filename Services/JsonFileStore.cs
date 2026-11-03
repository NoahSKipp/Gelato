using System.Collections.Concurrent;
using System.Text.Json;

namespace Gelato.Services;

// A small key/value map kept in a JSON file so it survives a Jellyfin
// restart. Writes are coalesced; a lost write only costs a re-warm.
public sealed class JsonFileStore<T>
{
    private readonly Func<string> _path;
    private readonly ConcurrentDictionary<string, T> _items = new();
    private readonly object _gate = new();
    private readonly TimeSpan _saveDelay;
    private bool _loaded;
    private Timer? _saveTimer;

    public JsonFileStore(Func<string> path, TimeSpan? saveDelay = null)
    {
        _path = path;
        _saveDelay = saveDelay ?? TimeSpan.FromSeconds(2);
    }

    public bool TryGet(string key, out T value)
    {
        EnsureLoaded();
        return _items.TryGetValue(key, out value!);
    }

    public void Set(string key, T value)
    {
        EnsureLoaded();
        _items[key] = value;
        ScheduleSave();
    }

    public bool Remove(string key)
    {
        EnsureLoaded();
        var removed = _items.TryRemove(key, out _);
        if (removed)
            ScheduleSave();
        return removed;
    }

    public void RemoveWhere(Func<string, T, bool> predicate)
    {
        EnsureLoaded();
        var any = false;
        foreach (var pair in _items)
        {
            if (predicate(pair.Key, pair.Value) && _items.TryRemove(pair.Key, out _))
                any = true;
        }
        if (any)
            ScheduleSave();
    }

    public IReadOnlyDictionary<string, T> Snapshot()
    {
        EnsureLoaded();
        return new Dictionary<string, T>(_items);
    }

    public void Flush()
    {
        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
            Save();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;
        lock (_gate)
        {
            if (_loaded)
                return;
            try
            {
                var path = _path();
                if (File.Exists(path))
                {
                    var data = JsonSerializer.Deserialize<Dictionary<string, T>>(
                        File.ReadAllText(path)
                    );
                    if (data is not null)
                    {
                        foreach (var pair in data)
                            _items[pair.Key] = pair.Value;
                    }
                }
            }
            catch (Exception)
            {
                // A damaged file starts empty; it is only a cache.
            }
            _loaded = true;
        }
    }

    private void ScheduleSave()
    {
        lock (_gate)
        {
            if (_saveTimer is not null)
                return;
            _saveTimer = new Timer(
                _ =>
                {
                    lock (_gate)
                    {
                        _saveTimer?.Dispose();
                        _saveTimer = null;
                        Save();
                    }
                },
                null,
                _saveDelay,
                Timeout.InfiniteTimeSpan
            );
        }
    }

    private void Save()
    {
        try
        {
            var path = _path();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Dictionary<string, T>(_items)));
            File.Move(tmp, path, true);
        }
        catch (Exception)
        {
            // Best effort: the next change tries again.
        }
    }
}
