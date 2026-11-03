using System.Collections.Concurrent;

namespace Gelato.Services;

// Play had nothing usable to start with: the client should ask again shortly.
public sealed class ProbeUnavailableException(string reason) : Exception(reason)
{
    public const int RetryAfterSeconds = 2;
}

// One probe per stream row at a time. A warm-up and a Play arriving together
// used to probe the same row twice, through the same temp file and the same
// shared item, and Play could come back with a source that had no streams.
public sealed class ProbeGate
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, Lazy<Task<bool>>> _inflight = new();

    public int InFlight => _inflight.Count;

    // The first caller runs probe; callers arriving meanwhile share its task.
    // True when the probe itself reported success.
    public Task<bool> RunAsync(Guid ownerId, Func<Task<bool>> probe)
    {
        Lazy<Task<bool>>? entry = null;
        entry = _inflight.GetOrAdd(
            ownerId,
            _ => new Lazy<Task<bool>>(
                () => Run(ownerId, probe, () => entry!),
                LazyThreadSafetyMode.ExecutionAndPublication
            )
        );
        return entry.Value;
    }

    private async Task<bool> Run(Guid ownerId, Func<Task<bool>> probe, Func<Lazy<Task<bool>>> self)
    {
        try
        {
            await Task.Yield();
            return await probe().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<Guid, Lazy<Task<bool>>>(ownerId, self()));
        }
    }

    // A source that still needs its probe is probed (or the probe already
    // running is awaited) and read again; retried once, then given up on with
    // ProbeUnavailableException rather than handing back a half-empty source.
    // A source that needs no probe comes straight back, without the gate.
    public async Task<T> EnsureProbedAsync<T>(
        Guid ownerId,
        T current,
        Func<T, bool> needsProbe,
        Func<Task<bool>> probe,
        Func<T?> reread,
        TimeSpan maxWait,
        CancellationToken ct
    )
        where T : class
    {
        if (!needsProbe(current))
            return current;

        var deadline = DateTime.UtcNow + maxWait;
        var reason = "probe failed";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var task = RunAsync(ownerId, probe);
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
                throw new ProbeUnavailableException("probe still running after " + maxWait.TotalSeconds + " s");
            var finished = await Task.WhenAny(task, Task.Delay(left, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (finished != task)
                throw new ProbeUnavailableException("probe still running after " + maxWait.TotalSeconds + " s");

            var ok = await task.ConfigureAwait(false);
            var fresh = reread() ?? current;
            if (!needsProbe(fresh))
                return fresh;
            current = fresh;
            reason = ok ? "probe found no playable streams" : "probe failed";
        }
        throw new ProbeUnavailableException(reason);
    }
}

// Every probe gets a folder of its own, so two probes of releases with the
// same file name never share, or delete, each other's .strm.
public static class ProbeTempFile
{
    public static string Create(string baseName, string contents)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gelato-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, baseName + ".strm");
        File.WriteAllText(path, contents);
        return path;
    }

    // Removes only the folder Create made for this path.
    public static void Delete(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || Path.GetFileName(Path.GetDirectoryName(dir)) != "gelato-probe")
            return;
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        { /* best effort */
        }
    }
}
