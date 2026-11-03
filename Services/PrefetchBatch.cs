namespace Gelato.Services;

public readonly record struct PrefetchStep(Guid ItemId, bool Probe);

public static class PrefetchBatch
{
    public const int MaxIds = 5;
    public const int CallsPerMinute = 20;
    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(400);

    // In the order given, without repeats, five at most. Only the first
    // item is probed when a probe was asked for; titles that are already
    // warm are reported back instead of warmed again.
    public static (List<PrefetchStep> Steps, List<Guid> AlreadyWarm) Plan(
        IEnumerable<Guid> itemIds,
        bool probe,
        Func<Guid, bool, bool> isAlreadyWarm
    )
    {
        var steps = new List<PrefetchStep>();
        var warm = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var id in itemIds)
        {
            if (id == Guid.Empty || !seen.Add(id))
                continue;
            if (seen.Count > MaxIds)
                break;
            var wantProbe = probe && seen.Count == 1;
            if (isAlreadyWarm(id, wantProbe))
            {
                warm.Add(id);
                continue;
            }
            steps.Add(new PrefetchStep(id, wantProbe));
        }
        return (steps, warm);
    }
}
