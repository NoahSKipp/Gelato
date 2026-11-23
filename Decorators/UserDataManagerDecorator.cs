using System.Collections.Concurrent;
using Gelato.Services;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

// Keeps one watched state per Gelato stub item and user, see StubUserData for
// why Jellyfin splits it. Reads settle the rows an item carries into one state
// before Jellyfin picks from them, saves copy the saved state onto the rows
// Jellyfin skipped, and rows that already disagree are made equal in the
// background. Nothing is deleted, only copied over. Every other item goes
// straight to Jellyfin.
public sealed class UserDataManagerDecorator : IUserDataManager
{
    private readonly IUserDataManager _inner;
    private readonly IUserDataRows _rows;
    private readonly ILogger<UserDataManagerDecorator> _log;

    private readonly ConcurrentDictionary<(Guid Item, Guid User), IReadOnlyList<string>> _pending =
        new();
    private int _draining;

    public UserDataManagerDecorator(
        IUserDataManager inner,
        IUserDataRows rows,
        ILogger<UserDataManagerDecorator> log
    )
    {
        _inner = inner;
        _rows = rows;
        _log = log;
    }

    public event EventHandler<UserDataSaveEventArgs>? UserDataSaved
    {
        add => _inner.UserDataSaved += value;
        remove => _inner.UserDataSaved -= value;
    }

    public void SaveUserData(
        User user,
        BaseItem item,
        UserItemData userData,
        UserDataSaveReason reason,
        CancellationToken cancellationToken
    )
    {
        _inner.SaveUserData(user, item, userData, reason, cancellationToken);
        Align(user, item);
    }

    public void SaveUserData(
        User user,
        BaseItem item,
        UpdateUserItemDataDto userDataDto,
        UserDataSaveReason reason
    )
    {
        Settle(user, item);
        _inner.SaveUserData(user, item, userDataDto, reason);
        Align(user, item);
    }

    public UserItemData? GetUserData(User user, BaseItem item)
    {
        Settle(user, item);
        return _inner.GetUserData(user, item);
    }

    public UserItemDataDto? GetUserDataDto(BaseItem item, User user)
    {
        Settle(user, item);
        return _inner.GetUserDataDto(item, user);
    }

    public UserItemDataDto? GetUserDataDto(
        BaseItem item,
        BaseItemDto? itemDto,
        User user,
        DtoOptions options
    )
    {
        Settle(user, item);
        return _inner.GetUserDataDto(item, itemDto, user, options);
    }

    public Dictionary<Guid, UserItemData> GetUserDataBatch(IReadOnlyList<BaseItem> items, User user)
    {
        List<(BaseItem Item, UserData Winner)>? settled = null;
        foreach (var item in items)
        {
            if (Settle(user, item) is { } winner)
                (settled ??= []).Add((item, winner));
        }

        var result = _inner.GetUserDataBatch(items, user);

        // Jellyfin keeps what it resolved earlier per item, which may be the
        // row that just lost.
        if (settled is not null)
        {
            foreach (var (item, winner) in settled)
                result[item.Id] = StubUserData.ToItemData(winner, item.GetUserDataKeys()[0]);
        }

        return result;
    }

    public VersionResumeData? GetResumeUserData(User user, BaseItem item)
    {
        Settle(user, item);
        return _inner.GetResumeUserData(user, item);
    }

    public IReadOnlyDictionary<Guid, VersionResumeData> GetResumeUserDataBatch(
        IReadOnlyList<BaseItem> items,
        User user
    )
    {
        foreach (var item in items)
            Settle(user, item);
        return _inner.GetResumeUserDataBatch(items, user);
    }

    public bool UpdatePlayState(BaseItem item, UserItemData data, long? reportedPositionTicks) =>
        _inner.UpdatePlayState(item, data, reportedPositionTicks);

    public void ResetPlaybackStreamSelections(User user, BaseItem item) =>
        _inner.ResetPlaybackStreamSelections(user, item);

    // Makes every row the item carries in memory say what the winning row says,
    // so whichever one Jellyfin resolves is the right one. Returns the winner
    // when the rows disagreed, null when there was nothing to settle.
    private UserData? Settle(User user, BaseItem item)
    {
        if (!StubUserData.Applies(item))
            return null;

        var all = item.UserData;
        if (all is null || all.Count < 2)
            return null;

        var mine = all.Where(r => r.UserId == user.Id).ToList();
        if (mine.Count < 2 || StubUserData.AllSame(mine))
            return null;

        var keys = item.GetUserDataKeys();
        var winner = StubUserData.Pick(mine, keys)!;
        item.UserData = all.Where(r => r.UserId != user.Id)
            .Concat(
                mine.Select(r =>
                {
                    var copy = StubUserData.Clone(r);
                    StubUserData.CopyState(winner, copy);
                    return copy;
                })
            )
            .ToArray();

        Enqueue(item.Id, user.Id, keys);
        return winner;
    }

    // After a save every row of the item holds the saved state. Jellyfin 12.0
    // leaves the rows under keys the item no longer reports as they were.
    private void Align(User user, BaseItem item)
    {
        if (!StubUserData.Applies(item))
            return;

        try
        {
            var all = _rows.Load(item.Id);
            var mine = all.Where(r => r.UserId == user.Id).ToList();
            if (mine.Count < 2 || StubUserData.AllSame(mine))
                return;

            var saved = mine.FirstOrDefault(r =>
                string.Equals(r.CustomDataKey, item.GetUserDataKeys()[0], StringComparison.Ordinal)
            );
            if (saved is null)
                return;

            var aligned = 0;
            foreach (var row in mine)
            {
                if (ReferenceEquals(row, saved) || StubUserData.SameState(row, saved))
                    continue;
                if (_rows.TryOverwrite(row, saved))
                    aligned++;
            }

            if (aligned > 0)
            {
                _log.LogDebug(
                    "Aligned {Count} user data row(s) of {Item} with its saved state",
                    aligned,
                    item.Id
                );
                item.UserData = _rows.Load(item.Id).ToArray();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not align the user data rows of {Item}", item.Id);
        }
    }

    private void Enqueue(Guid itemId, Guid userId, IReadOnlyList<string> keys)
    {
        _pending[(itemId, userId)] = keys;
        if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
            _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        try
        {
            while (true)
            {
                foreach (var job in _pending.Keys.ToList())
                {
                    if (!_pending.TryRemove(job, out var keys))
                        continue;
                    Heal(job.Item, job.User, keys);
                    // One small write at a time, so a playing viewer's
                    // saves get the lock in between.
                    await Task.Delay(25).ConfigureAwait(false);
                }

                Volatile.Write(ref _draining, 0);
                if (_pending.IsEmpty || Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
                    return;
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _draining, 0);
            _log.LogWarning(ex, "Settling user data rows stopped");
        }
    }

    // Reads the rows afresh and copies the winner over the others, each only
    // while it still holds what was read, so a newer play is never undone.
    internal void Heal(Guid itemId, Guid userId, IReadOnlyList<string> keys)
    {
        try
        {
            var mine = _rows.Load(itemId).Where(r => r.UserId == userId).ToList();
            if (mine.Count < 2 || StubUserData.AllSame(mine))
                return;

            var winner = StubUserData.Pick(mine, keys)!;
            var healed = 0;
            foreach (var row in mine)
            {
                if (ReferenceEquals(row, winner) || StubUserData.SameState(row, winner))
                    continue;
                if (_rows.TryOverwrite(row, winner))
                    healed++;
            }

            if (healed > 0)
            {
                _log.LogInformation(
                    "Made {Count} split user data row(s) of {Item} agree (position {Position}, played {Played})",
                    healed,
                    itemId,
                    winner.PlaybackPositionTicks,
                    winner.Played
                );
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not settle the user data rows of {Item}", itemId);
        }
    }
}
