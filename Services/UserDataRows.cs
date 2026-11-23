using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gelato.Services;

// The user data table as Gelato needs it: every row of one item, and a
// conditional overwrite. A seam, so the reconcile logic can run on a plain
// list in tests.
public interface IUserDataRows
{
    IReadOnlyList<UserData> Load(Guid itemId);

    // Gives the row the state of `state`, but only while it still holds the
    // state `expected` was read with, so a play that landed in between is
    // never overwritten. False when the row changed or is gone.
    bool TryOverwrite(UserData expected, UserData state);
}

public sealed class EfUserDataRows(IDbContextFactory<JellyfinDbContext> db) : IUserDataRows
{
    public IReadOnlyList<UserData> Load(Guid itemId)
    {
        using var context = db.CreateDbContext();
        return context.UserData.AsNoTracking().Where(e => e.ItemId == itemId).ToList();
    }

    public bool TryOverwrite(UserData expected, UserData state)
    {
        var itemId = expected.ItemId;
        var userId = expected.UserId;
        var key = expected.CustomDataKey;
        var played = expected.Played;
        var position = expected.PlaybackPositionTicks;
        var count = expected.PlayCount;
        var lastPlayed = expected.LastPlayedDate;
        var favorite = expected.IsFavorite;
        var likes = expected.Likes;
        var rating = expected.Rating;
        var audio = expected.AudioStreamIndex;
        var subtitle = expected.SubtitleStreamIndex;

        var nPlayed = state.Played;
        var nPosition = state.PlaybackPositionTicks;
        var nCount = state.PlayCount;
        var nLastPlayed = state.LastPlayedDate;
        var nFavorite = state.IsFavorite;
        var nLikes = state.Likes;
        var nRating = state.Rating;
        var nAudio = state.AudioStreamIndex;
        var nSubtitle = state.SubtitleStreamIndex;

        using var context = db.CreateDbContext();
        return context
                .UserData.Where(e =>
                    e.ItemId == itemId
                    && e.UserId == userId
                    && e.CustomDataKey == key
                    && e.Played == played
                    && e.PlaybackPositionTicks == position
                    && e.PlayCount == count
                    && e.LastPlayedDate == lastPlayed
                    && e.IsFavorite == favorite
                    && e.Likes == likes
                    && e.Rating == rating
                    && e.AudioStreamIndex == audio
                    && e.SubtitleStreamIndex == subtitle
                )
                .ExecuteUpdate(s =>
                    s.SetProperty(e => e.Played, nPlayed)
                        .SetProperty(e => e.PlaybackPositionTicks, nPosition)
                        .SetProperty(e => e.PlayCount, nCount)
                        .SetProperty(e => e.LastPlayedDate, nLastPlayed)
                        .SetProperty(e => e.IsFavorite, nFavorite)
                        .SetProperty(e => e.Likes, nLikes)
                        .SetProperty(e => e.Rating, nRating)
                        .SetProperty(e => e.AudioStreamIndex, nAudio)
                        .SetProperty(e => e.SubtitleStreamIndex, nSubtitle)
                ) > 0;
    }
}
