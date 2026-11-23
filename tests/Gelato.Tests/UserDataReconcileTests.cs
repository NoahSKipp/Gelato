using Gelato.Decorators;
using Gelato.Services;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gelato.Tests;

public class UserDataReconcileTests
{
    private const string Stub = "gelato://stub/tt0765400:1:2";
    private static readonly string[] WithTvdb = ["74608001002", "tt0765400001002", "ep-guid"];
    private static readonly string[] WithoutTvdb = ["tt0765400001002", "ep-guid"];
    private static readonly DateTime Watched = new(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Rewatched = new(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc);
    private const long FifteenMinutes = 9_000_000_000;

    // An episode whose user data keys the test moves around, the way a series
    // provider id appearing or vanishing moves the real ones.
    private sealed class KeyedEpisode : Episode
    {
        public IReadOnlyList<string> Keys { get; set; } = WithTvdb;

        public override List<string> GetUserDataKeys() => [.. Keys];
    }

    // The user data table: detached copies in, detached copies out, like the
    // rows Jellyfin hands to BaseItem.UserData.
    private sealed class Table : IUserDataRows
    {
        public List<UserData> Rows { get; } = [];
        public bool RefuseOverwrites { get; set; }

        public IReadOnlyList<UserData> Load(Guid itemId)
        {
            lock (Rows)
                return Rows.Where(r => r.ItemId == itemId).Select(StubUserData.Clone).ToList();
        }

        public bool TryOverwrite(UserData expected, UserData state)
        {
            lock (Rows)
            {
                var row = Rows.FirstOrDefault(r =>
                    r.ItemId == expected.ItemId
                    && r.UserId == expected.UserId
                    && r.CustomDataKey == expected.CustomDataKey
                );
                if (RefuseOverwrites || row is null || !StubUserData.SameState(row, expected))
                    return false;
                StubUserData.CopyState(state, row);
                return true;
            }
        }

        public void Put(
            Guid itemId,
            Guid userId,
            string key,
            bool played,
            long position,
            int plays,
            DateTime? at
        )
        {
            lock (Rows)
            {
                Rows.RemoveAll(r =>
                    r.ItemId == itemId && r.UserId == userId && r.CustomDataKey == key
                );
                Rows.Add(
                    new UserData
                    {
                        ItemId = itemId,
                        UserId = userId,
                        CustomDataKey = key,
                        Item = null,
                        User = null,
                        Played = played,
                        PlaybackPositionTicks = position,
                        PlayCount = plays,
                        LastPlayedDate = at,
                    }
                );
            }
        }
    }

    // Jellyfin's UserDataManager as it behaves on 12.0: a save writes the rows
    // under the item's current keys, a read takes the first current key that
    // has a row. 12.4 also copies the saved state onto rows under other keys.
    private sealed class JellyfinUserData(Table table, bool updatesStaleKeys) : IUserDataManager
    {
        public event EventHandler<UserDataSaveEventArgs>? UserDataSaved
        {
            add { }
            remove { }
        }

        public void SaveUserData(
            User user,
            BaseItem item,
            UserItemData data,
            UserDataSaveReason reason,
            CancellationToken ct
        )
        {
            var keys = item.GetUserDataKeys();
            foreach (var key in keys)
                table.Put(
                    item.Id,
                    user.Id,
                    key,
                    data.Played,
                    data.PlaybackPositionTicks,
                    data.PlayCount,
                    data.LastPlayedDate
                );
            if (updatesStaleKeys)
            {
                foreach (
                    var row in table
                        .Rows.Where(r =>
                            r.ItemId == item.Id
                            && r.UserId == user.Id
                            && !keys.Contains(r.CustomDataKey)
                        )
                        .ToList()
                )
                    table.Put(
                        item.Id,
                        user.Id,
                        row.CustomDataKey,
                        data.Played,
                        data.PlaybackPositionTicks,
                        data.PlayCount,
                        data.LastPlayedDate
                    );
            }
            item.UserData = table.Load(item.Id).ToArray();
        }

        public void SaveUserData(
            User user,
            BaseItem item,
            UpdateUserItemDataDto dto,
            UserDataSaveReason reason
        )
        {
            var data = GetUserData(user, item)!;
            if (dto.PlaybackPositionTicks.HasValue)
                data.PlaybackPositionTicks = dto.PlaybackPositionTicks.Value;
            if (dto.Played.HasValue)
                data.Played = dto.Played.Value;
            if (dto.LastPlayedDate.HasValue)
                data.LastPlayedDate = dto.LastPlayedDate.Value;
            SaveUserData(user, item, data, reason, CancellationToken.None);
        }

        public UserItemData? GetUserData(User user, BaseItem item)
        {
            var rows = item.UserData?.Where(r => r.UserId == user.Id).ToList() ?? [];
            var keys = item.GetUserDataKeys();
            var row =
                keys.Select(k => rows.Find(r => r.CustomDataKey == k))
                    .FirstOrDefault(r => r is not null)
                ?? rows.FirstOrDefault();
            return row is null
                ? new UserItemData { Key = keys[0] }
                : StubUserData.ToItemData(row, row.CustomDataKey);
        }

        public UserItemDataDto? GetUserDataDto(BaseItem item, User user) =>
            GetUserDataDto(item, null, user, new DtoOptions());

        public UserItemDataDto? GetUserDataDto(
            BaseItem item,
            BaseItemDto? itemDto,
            User user,
            DtoOptions options
        )
        {
            var data = GetUserData(user, item)!;
            return new UserItemDataDto
            {
                ItemId = item.Id,
                Key = data.Key,
                Played = data.Played,
                PlaybackPositionTicks = data.PlaybackPositionTicks,
                PlayCount = data.PlayCount,
                LastPlayedDate = data.LastPlayedDate,
            };
        }

        public Dictionary<Guid, UserItemData> GetUserDataBatch(
            IReadOnlyList<BaseItem> items,
            User user
        ) => items.ToDictionary(i => i.Id, i => GetUserData(user, i)!);

        public VersionResumeData? GetResumeUserData(User user, BaseItem item) => null;

        public IReadOnlyDictionary<Guid, VersionResumeData> GetResumeUserDataBatch(
            IReadOnlyList<BaseItem> items,
            User user
        ) => new Dictionary<Guid, VersionResumeData>();

        public bool UpdatePlayState(BaseItem item, UserItemData data, long? reportedPositionTicks)
        {
            data.PlaybackPositionTicks = reportedPositionTicks ?? 0;
            return false;
        }

        public void ResetPlaybackStreamSelections(User user, BaseItem item) { }
    }

    private static User NewUser() => new("viewer", "default", "default");

    private static KeyedEpisode NewEpisode(string path = Stub) =>
        new()
        {
            Id = Guid.NewGuid(),
            Path = path,
            IndexNumber = 2,
            ParentIndexNumber = 1,
        };

    private static UserDataManagerDecorator Decorate(Table table, bool updatesStaleKeys = false) =>
        new(
            new JellyfinUserData(table, updatesStaleKeys),
            table,
            NullLogger<UserDataManagerDecorator>.Instance
        );

    // Watched to the end with the tvdb key, then the key goes away and a
    // rewatch is stopped at 15 minutes, then the key comes back.
    private static (
        UserItemData Read,
        Table Table,
        KeyedEpisode Episode,
        User User
    ) PlayThroughKeyChange(Func<Table, IUserDataManager> manager)
    {
        var table = new Table();
        var user = NewUser();
        var episode = NewEpisode();
        var users = manager(table);

        episode.Keys = WithTvdb;
        users.SaveUserData(
            user,
            episode,
            new UserItemData
            {
                Key = WithTvdb[0],
                Played = true,
                PlayCount = 1,
                LastPlayedDate = Watched,
            },
            UserDataSaveReason.TogglePlayed,
            CancellationToken.None
        );

        episode.Keys = WithoutTvdb;
        var rewatch = users.GetUserData(user, episode)!;
        rewatch.PlaybackPositionTicks = FifteenMinutes;
        rewatch.PlayCount = 2;
        rewatch.LastPlayedDate = Rewatched;
        users.SaveUserData(
            user,
            episode,
            rewatch,
            UserDataSaveReason.PlaybackFinished,
            CancellationToken.None
        );

        episode.Keys = WithTvdb;
        return (users.GetUserData(user, episode)!, table, episode, user);
    }

    [Fact]
    public void JellyfinAloneLosesTheRewatchPositionWhenTheKeyReturns()
    {
        var (read, _, _, _) = PlayThroughKeyChange(t => new JellyfinUserData(
            t,
            updatesStaleKeys: false
        ));

        Assert.Equal(0, read.PlaybackPositionTicks);
        Assert.True(read.Played);
    }

    [Fact]
    public void ProgressWrittenWhileTheKeyWasGoneIsReadBackWhenItReturns()
    {
        var (read, table, episode, user) = PlayThroughKeyChange(t => Decorate(t));

        Assert.Equal(FifteenMinutes, read.PlaybackPositionTicks);
        Assert.Equal(2, read.PlayCount);
        Assert.Equal(Rewatched, read.LastPlayedDate);
        Assert.All(
            table.Load(episode.Id).Where(r => r.UserId == user.Id),
            r => Assert.Equal(FifteenMinutes, r.PlaybackPositionTicks)
        );
        Assert.Equal(3, table.Rows.Count);
    }

    [Fact]
    public void TheSameHoldsOnAJellyfinThatUpdatesStaleKeysItself()
    {
        var (read, table, _, _) = PlayThroughKeyChange(t => Decorate(t, updatesStaleKeys: true));

        Assert.Equal(FifteenMinutes, read.PlaybackPositionTicks);
        Assert.True(StubUserData.AllSame(table.Rows));
    }

    [Fact]
    public void PlayedDoesNotHideANewerPartialPositionInAnotherRow()
    {
        var table = new Table();
        var user = NewUser();
        var episode = NewEpisode();
        table.Put(
            episode.Id,
            user.Id,
            WithTvdb[0],
            played: true,
            position: 0,
            plays: 1,
            at: Watched
        );
        table.Put(
            episode.Id,
            user.Id,
            WithoutTvdb[0],
            played: false,
            position: FifteenMinutes,
            plays: 1,
            at: Rewatched
        );
        table.Put(
            episode.Id,
            user.Id,
            WithoutTvdb[1],
            played: false,
            position: FifteenMinutes,
            plays: 1,
            at: Rewatched
        );
        episode.UserData = table.Load(episode.Id).ToArray();
        var users = Decorate(table);

        var read = users.GetUserData(user, episode)!;
        var dto = users.GetUserDataDto(episode, user)!;
        var batch = users.GetUserDataBatch([episode], user)[episode.Id];

        Assert.Equal(FifteenMinutes, read.PlaybackPositionTicks);
        Assert.False(read.Played);
        Assert.Equal(FifteenMinutes, dto.PlaybackPositionTicks);
        Assert.Equal(FifteenMinutes, batch.PlaybackPositionTicks);
    }

    [Fact]
    public void BatchReadIgnoresWhatJellyfinResolvedEarlier()
    {
        var table = new Table();
        var user = NewUser();
        var episode = NewEpisode();
        table.Put(episode.Id, user.Id, WithTvdb[0], true, 0, 1, Watched);
        table.Put(episode.Id, user.Id, WithoutTvdb[0], true, FifteenMinutes, 2, Rewatched);
        episode.UserData = table.Load(episode.Id).ToArray();

        var batch = Decorate(table).GetUserDataBatch([episode], user);

        Assert.Equal(FifteenMinutes, batch[episode.Id].PlaybackPositionTicks);
        Assert.Equal(WithTvdb[0], batch[episode.Id].Key);
    }

    [Fact]
    public void TwoRowsOfOneEpisodeConvergeInTheTable()
    {
        var table = new Table();
        var user = NewUser();
        var episode = NewEpisode();
        table.Put(episode.Id, user.Id, WithTvdb[0], true, 0, 1, Watched);
        table.Put(episode.Id, user.Id, WithoutTvdb[0], true, FifteenMinutes, 2, Rewatched);
        table.Put(episode.Id, user.Id, WithoutTvdb[1], true, FifteenMinutes, 2, Rewatched);
        episode.UserData = table.Load(episode.Id).ToArray();

        Decorate(table).Heal(episode.Id, user.Id, episode.GetUserDataKeys());

        Assert.True(StubUserData.AllSame(table.Rows));
        Assert.All(table.Rows, r => Assert.Equal(FifteenMinutes, r.PlaybackPositionTicks));
        Assert.Equal(3, table.Rows.Count);
    }

    [Fact]
    public async Task ReadingASplitEpisodeHealsItInTheBackground()
    {
        var table = new Table();
        var user = NewUser();
        var episode = NewEpisode();
        table.Put(episode.Id, user.Id, WithTvdb[0], true, 0, 1, Watched);
        table.Put(episode.Id, user.Id, WithoutTvdb[0], true, FifteenMinutes, 2, Rewatched);
        episode.UserData = table.Load(episode.Id).ToArray();

        Decorate(table).GetUserData(user, episode);

        for (var i = 0; i < 100 && !StubUserData.AllSame(table.Rows); i++)
            await Task.Delay(20);
        Assert.True(StubUserData.AllSame(table.Rows));
        Assert.All(table.Rows, r => Assert.Equal(FifteenMinutes, r.PlaybackPositionTicks));
    }

    [Fact]
    public void HealLeavesRowsAloneThatChangedUnderneathIt()
    {
        var table = new Table { RefuseOverwrites = true };
        var user = NewUser();
        var episode = NewEpisode();
        table.Put(episode.Id, user.Id, WithTvdb[0], true, 0, 1, Watched);
        table.Put(episode.Id, user.Id, WithoutTvdb[0], true, FifteenMinutes, 2, Rewatched);

        Decorate(table).Heal(episode.Id, user.Id, WithoutTvdb);

        Assert.Equal(
            0,
            table.Rows.Single(r => r.CustomDataKey == WithTvdb[0]).PlaybackPositionTicks
        );
    }

    [Fact]
    public void SavingThroughTheDtoOverloadLeavesNoRowBehind()
    {
        var table = new Table();
        var user = NewUser();
        var episode = NewEpisode();
        table.Put(episode.Id, user.Id, WithTvdb[0], true, 0, 1, Watched);
        table.Put(episode.Id, user.Id, WithoutTvdb[0], true, 0, 1, Watched);
        table.Put(episode.Id, user.Id, WithoutTvdb[1], true, 0, 1, Watched);
        episode.Keys = WithoutTvdb;
        episode.UserData = table.Load(episode.Id).ToArray();

        Decorate(table)
            .SaveUserData(
                user,
                episode,
                new UpdateUserItemDataDto
                {
                    PlaybackPositionTicks = FifteenMinutes,
                    LastPlayedDate = Rewatched,
                },
                UserDataSaveReason.UpdateUserData
            );

        Assert.True(StubUserData.AllSame(table.Rows));
        Assert.All(table.Rows, r => Assert.Equal(FifteenMinutes, r.PlaybackPositionTicks));
    }

    [Fact]
    public void ItemsOutsideTheStubTreeAreLeftToJellyfin()
    {
        var table = new Table();
        var user = NewUser();
        var episode = NewEpisode("/media/tv/Show/S01E02.mkv");
        table.Put(episode.Id, user.Id, WithTvdb[0], true, 0, 1, Watched);
        table.Put(episode.Id, user.Id, WithoutTvdb[0], true, FifteenMinutes, 2, Rewatched);
        episode.UserData = table.Load(episode.Id).ToArray();

        var read = Decorate(table).GetUserData(user, episode)!;

        Assert.Equal(0, read.PlaybackPositionTicks);
        Assert.False(StubUserData.AllSame(table.Rows));
    }

    [Fact]
    public void RowsOfAnotherUserAreNeverMixedIn()
    {
        var table = new Table();
        var alice = NewUser();
        var bob = NewUser();
        var episode = NewEpisode();
        table.Put(episode.Id, alice.Id, WithoutTvdb[0], true, FifteenMinutes, 2, Rewatched);
        table.Put(episode.Id, bob.Id, WithTvdb[0], true, 0, 1, Watched);
        table.Put(episode.Id, bob.Id, WithoutTvdb[0], true, 0, 1, Watched);
        episode.UserData = table.Load(episode.Id).ToArray();
        var users = Decorate(table);

        Assert.Equal(0, users.GetUserData(bob, episode)!.PlaybackPositionTicks);
        Assert.Equal(FifteenMinutes, users.GetUserData(alice, episode)!.PlaybackPositionTicks);
    }

    [Fact]
    public void PickPrefersTheNewestPlayThenPlayCountThenPosition()
    {
        var keys = new[] { "a", "b" };
        UserData Row(string key, int plays, long pos, DateTime? at) =>
            new()
            {
                ItemId = Guid.Empty,
                UserId = Guid.Empty,
                CustomDataKey = key,
                Item = null,
                User = null,
                PlayCount = plays,
                PlaybackPositionTicks = pos,
                LastPlayedDate = at,
            };

        Assert.Equal(
            "b",
            StubUserData
                .Pick([Row("a", 5, 9, Watched), Row("b", 1, 0, Rewatched)], keys)!
                .CustomDataKey
        );
        Assert.Equal(
            "b",
            StubUserData
                .Pick([Row("a", 1, 9, Watched), Row("b", 2, 0, Watched)], keys)!
                .CustomDataKey
        );
        Assert.Equal(
            "b",
            StubUserData.Pick([Row("a", 1, 0, null), Row("b", 1, 5, null)], keys)!.CustomDataKey
        );
        Assert.Equal(
            "a",
            StubUserData
                .Pick([Row("b", 1, 5, Watched), Row("a", 1, 5, Watched)], keys)!
                .CustomDataKey
        );
    }

    [Theory]
    [InlineData("gelato://stub/tt0765400", true)]
    [InlineData("gelato://stub/tt0765400:1:2", true)]
    [InlineData("http://127.0.0.1:8096/gelato/stream?ih=abc", false)]
    [InlineData("/media/tv/S01E02.mkv", false)]
    [InlineData(null, false)]
    public void OnlyStubPathsAreInScope(string? path, bool expected)
    {
        Assert.Equal(expected, StubUserData.Applies(new KeyedEpisode { Path = path }));
    }
}
