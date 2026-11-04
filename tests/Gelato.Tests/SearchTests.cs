using Gelato.Filters;
using Gelato.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Gelato.Tests;

public class SearchTests
{
    [Fact]
    public void ASearchIsReusedForAMinutePerUser()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var cache = new SearchCache<string>(TimeSpan.FromSeconds(60), now: () => now);
        var alice = Guid.NewGuid();
        var key = SearchCache<string>.Key(alice, " Heat ", ["Series", "Movie"]);
        cache.Set(key, "results");

        Assert.True(cache.TryGet(SearchCache<string>.Key(alice, "heat", ["Movie", "Series"]), out var hit));
        Assert.Equal("results", hit);
        Assert.False(cache.TryGet(SearchCache<string>.Key(Guid.NewGuid(), "heat", ["Movie", "Series"]), out _));
        Assert.False(cache.TryGet(SearchCache<string>.Key(alice, "heat", ["Movie"]), out _));

        now = now.AddSeconds(61);
        Assert.False(cache.TryGet(key, out _));
    }

    [Fact]
    public void TheCacheStaysBounded()
    {
        var now = DateTime.UtcNow;
        var cache = new SearchCache<int>(TimeSpan.FromSeconds(60), capacity: 3, now: () => now);
        for (var i = 0; i < 10; i++)
        {
            now = now.AddMilliseconds(1);
            cache.Set("k" + i, i);
        }
        Assert.False(cache.TryGet("k0", out _));
        Assert.True(cache.TryGet("k9", out var last));
        Assert.Equal(9, last);
    }

    [Fact]
    public void AMovieResultGetsTheSamePlaceholderSourceJellyfinBuilt()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Heat", Path = "gelato://stub/tt0113277" };
        var dto = new BaseItemDto();
        SearchActionFilter.FillSkippedFields(dto, movie);

        Assert.Equal(0, dto.SpecialFeatureCount);
        Assert.Equal(0, dto.LocalTrailerCount);
        Assert.Empty(dto.Chapters);
        Assert.Empty(dto.MediaStreams);
        var source = Assert.Single(dto.MediaSources);
        Assert.Equal(movie.Id.ToString("N"), source.Id);
        Assert.Equal(movie.Path, source.Path);
        Assert.Equal(MediaSourceType.Default, source.Type);
        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.Empty(source.MediaStreams);
        Assert.True(source.SupportsDirectPlay);
    }

    [Fact]
    public void ASeriesResultGetsNoSources()
    {
        var dto = new BaseItemDto();
        SearchActionFilter.FillSkippedFields(dto, new Series { Id = Guid.NewGuid(), Name = "Heat" });
        Assert.Null(dto.MediaSources);
        Assert.Null(dto.MediaStreams);
        Assert.Equal(0, dto.SpecialFeatureCount);
    }
}
