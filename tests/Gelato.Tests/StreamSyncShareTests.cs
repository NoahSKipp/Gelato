using Gelato.Config;
using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class StreamSyncShareTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000000");

    private static PluginConfiguration Cfg(string url, string movies = "/m", string series = "/s") =>
        new() { Url = url, MoviePath = movies, SeriesPath = series };

    [Fact]
    public void SameAddonAndFoldersGiveTheSameSetup()
    {
        var a = StreamSyncShare.SetupOf(Cfg("https://addon.example/abc/manifest.json"));
        var b = StreamSyncShare.SetupOf(Cfg("https://addon.example/abc/"));
        Assert.NotNull(a);
        Assert.Equal(a, b);
        Assert.DoesNotContain("addon.example", a);
    }

    [Fact]
    public void AnythingThatChangesTheRowsChangesTheSetup()
    {
        var baseline = StreamSyncShare.SetupOf(Cfg("https://addon.example/abc"));
        Assert.NotEqual(baseline, StreamSyncShare.SetupOf(Cfg("https://addon.example/ABC")));
        Assert.NotEqual(baseline, StreamSyncShare.SetupOf(Cfg("https://addon.example/abc", movies: "/other")));
        Assert.NotEqual(baseline, StreamSyncShare.SetupOf(Cfg("https://addon.example/abc", series: "/other")));
        var p2p = Cfg("https://addon.example/abc");
        p2p.P2PEnabled = true;
        Assert.NotEqual(baseline, StreamSyncShare.SetupOf(p2p));
    }

    [Fact]
    public void NoAddonAddressMeansNoSetup()
    {
        Assert.Null(StreamSyncShare.SetupOf(Cfg("")));
        Assert.Null(StreamSyncShare.SetupOf(null));
    }

    [Fact]
    public void PicksTheNewestFreshFetchByAnotherUserWithTheSameSetup()
    {
        var fetches = new[]
        {
            new StreamFetch(A, "s1", Now.AddMinutes(-30)),
            new StreamFetch(C, "s1", Now.AddMinutes(-5)),
            new StreamFetch(B, "s1", Now.AddMinutes(-1)),
        };
        var pick = StreamSyncShare.PickSource(B, "s1", fetches, Now, Ttl, _ => true);
        Assert.Equal(C, pick?.UserId);
    }

    [Fact]
    public void NeverSharesAcrossSetups()
    {
        var fetches = new[] { new StreamFetch(A, "s1", Now.AddMinutes(-1)) };
        Assert.Null(StreamSyncShare.PickSource(B, "s2", fetches, Now, Ttl, _ => true));
        Assert.Null(StreamSyncShare.PickSource(B, null, fetches, Now, Ttl, _ => true));
    }

    [Fact]
    public void NeverSharesAFetchAtOrPastTheTtl()
    {
        var fetches = new[] { new StreamFetch(A, "s1", Now - Ttl) };
        Assert.Null(StreamSyncShare.PickSource(B, "s1", fetches, Now, Ttl, _ => true));
    }

    [Fact]
    public void NeverSharesAFetchWhoseMarkIsGone()
    {
        var fetches = new[]
        {
            new StreamFetch(A, "s1", Now.AddMinutes(-1)),
            new StreamFetch(C, "s1", Now.AddMinutes(-10)),
        };
        var pick = StreamSyncShare.PickSource(B, "s1", fetches, Now, Ttl, u => u != A);
        Assert.Equal(C, pick?.UserId);
        Assert.Null(StreamSyncShare.PickSource(B, "s1", fetches, Now, Ttl, _ => false));
    }

    [Fact]
    public void JoinsEveryRowOfTheSource()
    {
        var rows = new List<Guid[]> { new[] { A }, new[] { A, B }, new[] { C } };
        var join = StreamSyncShare.RowsToJoin(rows, r => r, A, B);
        Assert.NotNull(join);
        Assert.Equal(2, join.Count);
    }

    [Fact]
    public void FallsBackWhenTheUserHasRowsTheSourceDoesNot()
    {
        var rows = new List<Guid[]> { new[] { A }, new[] { B } };
        Assert.Null(StreamSyncShare.RowsToJoin(rows, r => r, A, B));
    }

    [Fact]
    public void FallsBackWhenTheSourceHasNoRows()
    {
        var rows = new List<Guid[]> { new[] { C } };
        Assert.Null(StreamSyncShare.RowsToJoin(rows, r => r, A, B));
        Assert.Null(StreamSyncShare.RowsToJoin(new List<Guid[]>(), r => r, A, B));
    }
}
