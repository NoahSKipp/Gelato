using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

// Finds the titles carrying any of the given provider ids with one index
// lookup. Jellyfin's own HasAnyProviderId filter matches on
// ProviderId || ':' || ProviderValue, which no index serves, so on a large
// library every check scanned all titles of the kind: a few hundred ms each,
// and nearly half the CPU when several new titles were opened at once.
public sealed class ProviderIdLookup(IDbContextFactory<JellyfinDbContext> db, ILogger<ProviderIdLookup> log)
{
    private int _failed;

    // Ids of items of that type (stream rows left out) carrying one of the
    // ids, or null when the database can't be asked this way.
    public IReadOnlyList<Guid>? Find(IReadOnlyDictionary<string, string> providerIds, string typeName)
    {
        if (Volatile.Read(ref _failed) != 0)
            return null;
        var pairs = providerIds.Where(p => !string.IsNullOrEmpty(p.Value)).ToList();
        if (pairs.Count == 0)
            return [];
        var names = pairs.Select(p => p.Key).Distinct().ToList();
        var values = pairs.Select(p => p.Value).Distinct().ToList();
        try
        {
            using var context = db.CreateDbContext();
            // Two steps: joined in one query SQLite starts from the item
            // type and scans every title of it.
            var ids = context
                .BaseItemProviders.AsNoTracking()
                .Where(p => names.Contains(p.ProviderId) && values.Contains(p.ProviderValue))
                .Select(p => new { p.ItemId, p.ProviderId, p.ProviderValue })
                .Take(5000)
                .ToList()
                .Where(r => pairs.Any(p => p.Key == r.ProviderId && p.Value == r.ProviderValue))
                .Select(r => r.ItemId)
                .Distinct()
                .ToList();
            if (ids.Count == 0)
                return [];
            return context
                .BaseItems.AsNoTracking()
                .Where(b =>
                    ids.Contains(b.Id)
                    && b.Type == typeName
                    && (b.Path == null || !b.Path.StartsWith("http"))
                )
                .Select(b => b.Id)
                .Take(50)
                .ToList();
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _failed, 1) == 0)
                log.LogWarning(ex, "Provider id lookup is not available, using Jellyfin's item query");
            return null;
        }
    }
}
