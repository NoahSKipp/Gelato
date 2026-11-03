using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class PlaceholderFingerprintTests
{
    [Fact]
    public void FlagsTheAioStreamsPlaceholder()
    {
        // mp4, H.264 720p, 1 fps, about 1 kb/s, 00:02:00.00, no audio, on a 59 minute episode.
        var reason = PlaceholderFingerprint.Detect(120, 59 * 60, 1, hasAudio: false, bitrate: 1_000);
        Assert.NotNull(reason);
    }

    [Fact]
    public void FlagsTheSilentSlideshowEvenWhenTheLengthLooksRight()
    {
        Assert.NotNull(PlaceholderFingerprint.Detect(59 * 60, 59 * 60, 1, hasAudio: false, bitrate: 1_000));
    }

    [Fact]
    public void LeavesARealEpisodeAlone()
    {
        Assert.Null(PlaceholderFingerprint.Detect(57 * 60, 59 * 60, 23.976, hasAudio: true, bitrate: 6_500_000));
    }

    [Fact]
    public void LeavesAShortMusicVideoAlone()
    {
        Assert.Null(PlaceholderFingerprint.Detect(4 * 60, 4 * 60, 25, hasAudio: true, bitrate: 3_000_000));
    }

    [Fact]
    public void ShortAgainstAMovieButNotAgainstAShortTitle()
    {
        Assert.NotNull(PlaceholderFingerprint.Detect(500, 2 * 3600, 24, hasAudio: true, bitrate: 2_000_000));
        Assert.Null(PlaceholderFingerprint.Detect(500, 15 * 60, 24, hasAudio: true, bitrate: 2_000_000));
        Assert.Null(PlaceholderFingerprint.Detect(120, null, 24, hasAudio: true, bitrate: 2_000_000));
    }

    [Fact]
    public void SilentLowFrameRateNeedsAKnownLowBitrate()
    {
        Assert.Null(PlaceholderFingerprint.Detect(59 * 60, 59 * 60, 1, hasAudio: false, bitrate: null));
        Assert.Null(PlaceholderFingerprint.Detect(59 * 60, 59 * 60, 1, hasAudio: false, bitrate: 800_000));
        Assert.Null(PlaceholderFingerprint.Detect(59 * 60, 59 * 60, 1, hasAudio: true, bitrate: 1_000));
    }

    [Theory]
    [InlineData("4K HDR\n⏱ 59m", 59 * 60)]
    [InlineData("⏱️ 1h 23m · 12 GB", 83 * 60)]
    [InlineData("⏱ 59:00", 59 * 60)]
    [InlineData("Duration: 1:23:45", 5025)]
    public void ReadsTheRuntimeFromTheLabel(string label, double expected)
    {
        Assert.Equal(expected, PlaceholderFingerprint.ParseLabelRuntime(label));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Show.S01E01.1080p.WEB-DL 12:30 release")]
    [InlineData("5.1 · 2.4 GB")]
    public void IgnoresLabelsWithoutAMarkedRuntime(string? label)
    {
        Assert.Null(PlaceholderFingerprint.ParseLabelRuntime(label));
    }
}
