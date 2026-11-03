using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class WarmTrackerTests
{
    private static (WarmTracker Tracker, Func<DateTime> Clock, Action<TimeSpan> Advance, DirectoryInfo Dir) Make()
    {
        var dir = Directory.CreateTempSubdirectory("gelato-warm");
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var store = new JsonFileStore<WarmRecord>(() => Path.Combine(dir.FullName, "warm.json"));
        DateTime Clock() => now;
        var tracker = new WarmTracker(store, Clock);
        return (tracker, Clock, by => now += by, dir);
    }

    [Fact]
    public void WarmExpiresAfterSixHours()
    {
        var (tracker, _, advance, dir) = Make();
        tracker.MarkWarm("row", "Movie.2009.1080p.mkv");
        Assert.NotNull(tracker.WarmedAt("row"));
        advance(TimeSpan.FromHours(5.9));
        Assert.NotNull(tracker.WarmedAt("row"));
        advance(TimeSpan.FromHours(0.2));
        Assert.Null(tracker.WarmedAt("row"));
        dir.Delete(true);
    }

    [Fact]
    public void APlaceholderIsNeverWarm()
    {
        var (tracker, _, _, dir) = Make();
        tracker.MarkWarm("row", null);
        tracker.MarkCold("row");
        Assert.Null(tracker.WarmedAt("row"));
        dir.Delete(true);
    }

    [Fact]
    public void GoneFromNzbdavIsNotWarmButUnknownIs()
    {
        var (tracker, _, _, dir) = Make();
        tracker.MarkWarm("row", "Movie.2009.1080p.mkv");
        Assert.Null(tracker.WarmedAt("row", _ => false));
        Assert.NotNull(tracker.WarmedAt("row", _ => true));
        Assert.NotNull(tracker.WarmedAt("row", _ => null));
        dir.Delete(true);
    }
}

public class PrefetchBatchTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(),
        D = Guid.NewGuid(), E = Guid.NewGuid(), F = Guid.NewGuid();

    [Fact]
    public void KeepsOrderCapsAtFiveAndProbesOnlyTheFirst()
    {
        var (steps, warm) = PrefetchBatch.Plan([A, B, A, C, D, E, F], probe: true, (_, _) => false);
        Assert.Equal([A, B, C, D, E], steps.Select(s => s.ItemId));
        Assert.Equal([true, false, false, false, false], steps.Select(s => s.Probe));
        Assert.Empty(warm);
    }

    [Fact]
    public void NeverWarmsAWarmTitleTwice()
    {
        var (steps, warm) = PrefetchBatch.Plan([A, B, C], probe: false, (id, _) => id == B);
        Assert.Equal([A, C], steps.Select(s => s.ItemId));
        Assert.Equal([B], warm);
    }

    [Fact]
    public void NoProbeUnlessAsked()
    {
        var (steps, _) = PrefetchBatch.Plan([A, B], probe: false, (_, _) => false);
        Assert.All(steps, s => Assert.False(s.Probe));
    }
}

public class SlidingWindowLimiterTests
{
    [Fact]
    public void TwentyCallsAMinuteThenRefusedUntilTheWindowMoves()
    {
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new SlidingWindowLimiter(20, TimeSpan.FromMinutes(1), () => now);
        var user = Guid.NewGuid();
        for (var i = 0; i < 20; i++)
            Assert.True(limiter.TryAcquire(user));
        Assert.False(limiter.TryAcquire(user));
        Assert.True(limiter.TryAcquire(Guid.NewGuid()));
        now = now.AddSeconds(61);
        Assert.True(limiter.TryAcquire(user));
    }
}

public class QualityTierTests
{
    [Theory]
    [InlineData(null, "🚀 4K DV HDR10", "2160p-dv")]
    [InlineData(null, "Movie.2009.2160p.UHD.BluRay.HDR10.mkv", "2160p-hdr")]
    [InlineData(null, "🚀 FHD", "1080p-sdr")]
    [InlineData(null, "Show.S01E01.1080p.WEB-DL.DDP5.1.H.264", "1080p-sdr")]
    [InlineData(null, "720p WEB", "720p-sdr")]
    [InlineData(2160, "Some release", "2160p-sdr")]
    [InlineData(1080, "4K in the name but probed 1080", "1080p-sdr")]
    [InlineData(null, "Unlabelled", "unknown")]
    public void NamesResolutionAndHdrClass(int? height, string label, string expected)
    {
        Assert.Equal(expected, QualityTier.Of(height, null, label));
    }

    [Fact]
    public void Hdr10PlusAndHdrAreTheSameClass()
    {
        Assert.Equal(QualityTier.Of(null, null, "2160p HDR10+"), QualityTier.Of(null, null, "2160p HDR"));
    }
}

public class NzbdavPresenceTests
{
    [Fact]
    public void ReadsFolderNamesFromAMultistatusListing()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/content/movies/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
              <d:response><d:href>/content/movies/Movie.2009.1080p.BluRay.x264-GRP/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
              <d:response><d:href>/content/movies/Other%20Release/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
              <d:response><d:href>/content/movies/readme.txt</d:href><d:propstat><d:prop><d:resourcetype/></d:prop></d:propstat></d:response>
            </d:multistatus>
            """;
        Assert.Equal(["Movie.2009.1080p.BluRay.x264-GRP", "Other Release"], NzbdavPresence.ParseFolders(xml, "/content/movies/"));
    }

    [Fact]
    public void MatchesAFilenameToItsReleaseFolder()
    {
        Assert.Equal(
            NzbdavPresence.NormalizeRelease("Movie.2009.1080p.BluRay.x264-GRP"),
            NzbdavPresence.NormalizeRelease("Movie.2009.1080p.BluRay.x264-GRP.mkv")
        );
    }
}
