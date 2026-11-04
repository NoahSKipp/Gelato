using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

// One heavy Gelato background writer at a time, and none while Jellyfin runs
// a task that writes a lot itself (the guide import, the database optimise).
// SQLite has a single writer; several of ours on top of a guide import turned
// a one minute refresh into 46 and made reads fail with "database is locked".
// Play, PlaybackInfo and the detail page never come through here.
public sealed class WriterGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string?> _heavyTask;
    private readonly TimeSpan _poll;
    private readonly TimeSpan _maxDefer;

    public WriterGate(Func<string?> heavyTask, TimeSpan poll, TimeSpan maxDefer)
    {
        _heavyTask = heavyTask;
        _poll = poll;
        _maxDefer = maxDefer;
    }

    public sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _held;

        internal Lease(SemaphoreSlim? held, long waitedMs, string? deferredFor, bool promoted)
        {
            _held = held;
            WaitedMs = waitedMs;
            DeferredFor = deferredFor;
            Promoted = promoted;
        }

        public long WaitedMs { get; }

        // The Jellyfin task this writer waited out, if any.
        public string? DeferredFor { get; }

        // An interactive caller joined while this one waited, so it went ahead.
        public bool Promoted { get; }

        public void Dispose() => Interlocked.Exchange(ref _held, null)?.Release();
    }

    // heavy: also wait out Jellyfin's own heavy tasks. Small writes (one
    // title's streams) only queue behind the other background writers.
    public async Task<Lease> EnterAsync(Task? promoted, CancellationToken ct, bool heavy = true)
    {
        var started = Environment.TickCount64;
        string? deferredFor = null;
        while (heavy && _heavyTask() is { } task && promoted?.IsCompleted != true)
        {
            deferredFor = task;
            if (Environment.TickCount64 - started >= _maxDefer.TotalMilliseconds)
                break;
            if (promoted is null)
                await Task.Delay(_poll, ct).ConfigureAwait(false);
            else
                await Task.WhenAny(Task.Delay(_poll, ct), promoted).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        if (promoted is null)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            return new Lease(_gate, Environment.TickCount64 - started, deferredFor, false);
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var wait = _gate.WaitAsync(stop.Token);
        if (await Task.WhenAny(wait, promoted).ConfigureAwait(false) == wait)
        {
            await wait.ConfigureAwait(false);
            return new Lease(_gate, Environment.TickCount64 - started, deferredFor, false);
        }

        stop.Cancel();
        try
        {
            await wait.ConfigureAwait(false);
            // Got it in the same instant it was given up: hand it back.
            _gate.Release();
        }
        catch (OperationCanceledException) { }
        ct.ThrowIfCancellationRequested();
        return new Lease(null, Environment.TickCount64 - started, deferredFor, true);
    }
}

public sealed class BackgroundWriteGate
{
    // Jellyfin's own heavy writers, by scheduled task key.
    public static readonly string[] HeavyTaskKeys = ["RefreshGuide", "OptimizeDatabaseTask"];

    private readonly WriterGate _gate;
    private readonly ILogger<BackgroundWriteGate> _log;

    public BackgroundWriteGate(ITaskManager taskManager, ILogger<BackgroundWriteGate> log)
    {
        _log = log;
        _gate = new WriterGate(
            () => RunningHeavyTask(taskManager),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(30)
        );
    }

    private static string? RunningHeavyTask(ITaskManager taskManager)
    {
        foreach (var worker in taskManager.ScheduledTasks)
        {
            if (
                worker.State != TaskState.Idle
                && HeavyTaskKeys.Contains(worker.ScheduledTask.Key, StringComparer.OrdinalIgnoreCase)
            )
            {
                return worker.ScheduledTask.Key;
            }
        }
        return null;
    }

    public async Task<WriterGate.Lease> EnterAsync(
        string who,
        CancellationToken ct,
        Task? promoted = null,
        bool heavy = true
    )
    {
        var lease = await _gate.EnterAsync(promoted, ct, heavy).ConfigureAwait(false);
        if (lease.WaitedMs >= 1000 || lease.Promoted)
        {
            _log.LogInformation(
                "{Who} waited {Ms} ms for the database{Deferred}{Promoted}",
                who,
                lease.WaitedMs,
                lease.DeferredFor is { } t ? $" ({t} was running)" : "",
                lease.Promoted ? ", then went ahead for a viewer" : ""
            );
        }
        return lease;
    }

    public async Task RunAsync(string who, Func<Task> work, CancellationToken ct)
    {
        using var lease = await EnterAsync(who, ct).ConfigureAwait(false);
        await work().ConfigureAwait(false);
    }

    public async Task<T> RunAsync<T>(string who, Func<Task<T>> work, CancellationToken ct)
    {
        using var lease = await EnterAsync(who, ct).ConfigureAwait(false);
        return await work().ConfigureAwait(false);
    }
}
