namespace Gelato.Services;

// Every warm-up adds a release to nzbdav (import, article check, Usenet
// connections), so the whole server runs at most two at once.
public sealed class WarmupGate
{
    public const int MaxConcurrent = 2;
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);

    public async Task RunAsync(Func<Task> work, CancellationToken ct = default)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }
}
