using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;

namespace Gelato.Services;

// Jellyfin keeps a user's watched state for an item in one row per
// "user data key". An episode's keys start with the series provider ids plus
// season and episode number, so they change whenever a metadata provider adds
// or drops an id on the series, and a Gelato stub never keeps its ids still.
// Jellyfin 12.0 only writes the rows under the keys the item has right now,
// then reads the row of the first current key: a row left under an older key
// comes back as soon as that key returns, and shows an old Played with no
// resume position next to a newer partial watch. The season still counts the
// episode as played, because that query looks at every row.
//
// These helpers pick one state out of an item's rows, so every row can be
// made to say the same thing.
public static class StubUserData
{
    public const string PathPrefix = "gelato://stub/";

    public static bool Applies(BaseItem item) =>
        item.Path is { } path && path.StartsWith(PathPrefix, StringComparison.Ordinal);

    // The newest play wins, then the higher play count, then the further
    // position. A row that ties keeps Jellyfin's own pick: rows are tried in
    // the order of the item's current keys, then the rest.
    public static UserData? Pick(IReadOnlyList<UserData> rows, IReadOnlyList<string> keys)
    {
        UserData? best = null;
        foreach (var row in InKeyOrder(rows, keys))
        {
            if (best is null || Newer(row, best))
                best = row;
        }
        return best;
    }

    public static bool SameState(UserData a, UserData b) =>
        a.Played == b.Played
        && a.PlaybackPositionTicks == b.PlaybackPositionTicks
        && a.PlayCount == b.PlayCount
        && a.LastPlayedDate == b.LastPlayedDate
        && a.IsFavorite == b.IsFavorite
        && a.Likes == b.Likes
        && a.Rating == b.Rating
        && a.AudioStreamIndex == b.AudioStreamIndex
        && a.SubtitleStreamIndex == b.SubtitleStreamIndex;

    public static bool AllSame(IReadOnlyList<UserData> rows)
    {
        for (var i = 1; i < rows.Count; i++)
        {
            if (!SameState(rows[0], rows[i]))
                return false;
        }
        return true;
    }

    public static void CopyState(UserData from, UserData to)
    {
        to.Played = from.Played;
        to.PlaybackPositionTicks = from.PlaybackPositionTicks;
        to.PlayCount = from.PlayCount;
        to.LastPlayedDate = from.LastPlayedDate;
        to.IsFavorite = from.IsFavorite;
        to.Likes = from.Likes;
        to.Rating = from.Rating;
        to.AudioStreamIndex = from.AudioStreamIndex;
        to.SubtitleStreamIndex = from.SubtitleStreamIndex;
    }

    public static UserData Clone(UserData row)
    {
        var copy = new UserData
        {
            ItemId = row.ItemId,
            UserId = row.UserId,
            CustomDataKey = row.CustomDataKey,
            Item = null,
            User = null,
            RetentionDate = row.RetentionDate,
        };
        CopyState(row, copy);
        return copy;
    }

    public static UserItemData ToItemData(UserData row, string key) =>
        new()
        {
            Key = key,
            Played = row.Played,
            PlaybackPositionTicks = row.PlaybackPositionTicks,
            PlayCount = row.PlayCount,
            LastPlayedDate = row.LastPlayedDate,
            IsFavorite = row.IsFavorite,
            Likes = row.Likes,
            Rating = row.Rating,
            AudioStreamIndex = row.AudioStreamIndex,
            SubtitleStreamIndex = row.SubtitleStreamIndex,
        };

    private static bool Newer(UserData a, UserData b)
    {
        var byDate = Nullable.Compare(a.LastPlayedDate, b.LastPlayedDate);
        if (byDate != 0)
            return byDate > 0;
        if (a.PlayCount != b.PlayCount)
            return a.PlayCount > b.PlayCount;
        return a.PlaybackPositionTicks > b.PlaybackPositionTicks;
    }

    private static IEnumerable<UserData> InKeyOrder(
        IReadOnlyList<UserData> rows,
        IReadOnlyList<string> keys
    )
    {
        var seen = new HashSet<UserData>();
        foreach (var key in keys)
        {
            foreach (var row in rows)
            {
                if (
                    string.Equals(row.CustomDataKey, key, StringComparison.Ordinal) && seen.Add(row)
                )
                    yield return row;
            }
        }
        foreach (var row in rows.OrderBy(r => r.CustomDataKey, StringComparer.Ordinal))
        {
            if (seen.Add(row))
                yield return row;
        }
    }
}
