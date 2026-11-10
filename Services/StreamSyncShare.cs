using System.Security.Cryptography;
using System.Text;
using Gelato.Config;

namespace Gelato.Services;

// One fetch of a title's streams from the addon.
public readonly record struct StreamFetch(Guid UserId, string Setup, DateTime At);

// When a fresh fetch by one user can stand in for another user's: both use
// the same addon (same address, so the same addon settings) and the same
// library folders, so the rows would come out identical.
public static class StreamSyncShare
{
    // What decides a user's stream rows, hashed so no address is kept.
    // Null when the addon address is missing or unreadable.
    public static string? SetupOf(PluginConfiguration? cfg)
    {
        if (cfg is null)
            return null;
        string baseUrl;
        try
        {
            baseUrl = cfg.GetBaseUrl();
        }
        catch (Exception)
        {
            return null;
        }
        var key = string.Join('\n', baseUrl, cfg.MoviePath ?? "", cfg.SeriesPath ?? "", cfg.P2PEnabled);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    // The newest fetch by another user with this setup, still within the
    // TTL and still marked synced for that user (a fetch whose links were
    // found dead has lost its mark).
    public static StreamFetch? PickSource(
        Guid userId,
        string? setup,
        IEnumerable<StreamFetch> fetches,
        DateTime now,
        TimeSpan ttl,
        Func<Guid, bool> stillSynced
    )
    {
        if (setup is null)
            return null;
        StreamFetch? best = null;
        foreach (var f in fetches)
        {
            if (f.UserId == userId || f.Setup != setup)
                continue;
            if (now - f.At >= ttl || f.At > now)
                continue;
            if (best is { } b && b.At >= f.At)
                continue;
            if (stillSynced(f.UserId))
                best = f;
        }
        return best;
    }

    // The rows the user joins: every row the source user has. Null when the
    // source has none, or when the user is on a row the source is not on
    // (an older list of the user's that only a real sync clears up).
    public static List<T>? RowsToJoin<T>(
        IEnumerable<T> rows,
        Func<T, IReadOnlyCollection<Guid>> usersOf,
        Guid source,
        Guid userId
    )
    {
        var join = new List<T>();
        foreach (var row in rows)
        {
            var users = usersOf(row);
            if (users.Contains(source))
                join.Add(row);
            else if (users.Contains(userId))
                return null;
        }
        return join.Count > 0 ? join : null;
    }
}
