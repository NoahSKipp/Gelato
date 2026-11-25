using System.Reflection;
using Gelato.Providers;
using Gelato.ScheduledTasks;
using Gelato.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gelato.Tests;

public class ArtworkBackfillTests
{
    public class DummyLibraryManagerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }

    [Fact]
    public void NeedsArtReturnsTrueForPrimaryGelatoMovie()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Heat" };
        movie.SetProviderId("Stremio", "tt0113277");

        Assert.True(ArtworkBackfillService.NeedsArt(movie));
    }

    [Fact]
    public void NeedsArtReturnsTrueForSeriesWithBackdropButNoPoster()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Invincible" };
        series.SetProviderId("Stremio", "tt6741278");
        series.SetImage(new ItemImageInfo { Path = "https://image.tmdb.org/t/p/original/b.jpg", Type = ImageType.Backdrop }, 0);

        Assert.True(ArtworkBackfillService.NeedsArt(series));
    }

    [Fact]
    public void NeedsArtReturnsFalseWhenPosterAndBackdropExist()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Invincible" };
        series.SetProviderId("Stremio", "tt6741278");
        series.SetImage(new ItemImageInfo { Path = "https://image.tmdb.org/t/p/original/b.jpg", Type = ImageType.Backdrop }, 0);
        series.SetImage(new ItemImageInfo { Path = "https://image.tmdb.org/t/p/w500/p.jpg", Type = ImageType.Primary }, 0);

        Assert.False(ArtworkBackfillService.NeedsArt(series));
    }

    [Fact]
    public void NeedsArtReturnsFalseForStreamTaggedMovie()
    {
        var stream = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Heat",
            Tags = [GelatoManager.StreamTag],
        };
        stream.SetProviderId("Stremio", "tt0113277");

        Assert.False(ArtworkBackfillService.NeedsArt(stream));
    }

    [Fact]
    public void NeedsArtReturnsFalseForLegacyStreamRowAndVirtualItem()
    {
        var legacyStream = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Heat",
            PrimaryVersionId = Guid.NewGuid(),
        };
        legacyStream.SetProviderId("Stremio", "tt0113277");

        var virtualItem = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Heat",
            IsVirtualItem = true,
        };
        virtualItem.SetProviderId("Stremio", "tt0113277");

        Assert.False(ArtworkBackfillService.NeedsArt(legacyStream));
        Assert.False(ArtworkBackfillService.NeedsArt(virtualItem));
    }

    [Fact]
    public void ImageProviderSupportsPrimaryItemsAndRejectsStreamRows()
    {
        var provider = new GelatoImageProvider(NullLogger<GelatoImageProvider>.Instance);

        var primaryMovie = new Movie { Id = Guid.NewGuid(), Name = "Heat" };
        primaryMovie.SetProviderId("Stremio", "tt0113277");

        var primarySeries = new Series { Id = Guid.NewGuid(), Name = "The Wire" };
        primarySeries.SetProviderId("Stremio", "tt0306414");

        var streamMovie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Heat",
            Tags = [GelatoManager.StreamTag],
        };
        streamMovie.SetProviderId("Stremio", "tt0113277");

        Assert.True(provider.Supports(primaryMovie));
        Assert.True(provider.Supports(primarySeries));
        Assert.False(provider.Supports(streamMovie));
    }

    [Fact]
    public async Task IntroDbSegmentProviderSupportsPrimaryEpisodeAndRejectsStreamRow()
    {
        var libraryManager = DispatchProxy.Create<ILibraryManager, DummyLibraryManagerProxy>();
        using var httpClient = new HttpClient();
        var client = new IntroDbClient(httpClient, NullLogger<IntroDbClient>.Instance);
        var provider = new IntroDbSegmentProvider(
            libraryManager,
            client,
            NullLogger<IntroDbSegmentProvider>.Instance
        );

        var primaryEpisode = new Episode { Id = Guid.NewGuid(), Name = "Pilot" };
        primaryEpisode.SetProviderId("Stremio", "tt0306414:1:1");

        var streamEpisode = new Episode
        {
            Id = Guid.NewGuid(),
            Name = "Pilot",
            Tags = [GelatoManager.StreamTag],
        };
        streamEpisode.SetProviderId("Stremio", "tt0306414:1:1");

        Assert.True(await provider.Supports(primaryEpisode));
        Assert.False(await provider.Supports(streamEpisode));
    }

    [Fact]
    public void IsSafeStreamArtworkPathValidatesStreamOwnershipAndSafety()
    {
        var streamId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var streamIdFolder = streamId.ToString("N");
        var otherIdFolder = otherId.ToString("N");

        var streamMovie = new Movie
        {
            Id = streamId,
            Name = "Heat",
            Tags = [GelatoManager.StreamTag],
        };
        streamMovie.SetProviderId("Stremio", "tt0113277");

        var primaryMovie = new Movie { Id = streamId, Name = "Heat" };
        primaryMovie.SetProviderId("Stremio", "tt0113277");

        var ownMetadataPath = Path.Combine(
            Path.GetTempPath(),
            "metadata",
            "library",
            streamIdFolder[..2],
            streamIdFolder,
            "poster.jpg"
        );
        var otherMetadataPath = Path.Combine(
            Path.GetTempPath(),
            "metadata",
            "library",
            otherIdFolder[..2],
            otherIdFolder,
            "poster.jpg"
        );

        var emptyPrimaryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conflictingPrimaryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ownMetadataPath,
        };

        Assert.True(
            PurgeStreamArtworkTask.IsSafeStreamArtworkPath(
                streamMovie,
                ownMetadataPath,
                emptyPrimaryPaths
            )
        );
        Assert.False(
            PurgeStreamArtworkTask.IsSafeStreamArtworkPath(
                streamMovie,
                otherMetadataPath,
                emptyPrimaryPaths
            )
        );
        Assert.False(
            PurgeStreamArtworkTask.IsSafeStreamArtworkPath(
                streamMovie,
                ownMetadataPath,
                conflictingPrimaryPaths
            )
        );
        Assert.False(
            PurgeStreamArtworkTask.IsSafeStreamArtworkPath(
                streamMovie,
                "https://image.tmdb.org/t/p/original/abc.jpg",
                emptyPrimaryPaths
            )
        );
        Assert.False(
            PurgeStreamArtworkTask.IsSafeStreamArtworkPath(
                primaryMovie,
                ownMetadataPath,
                emptyPrimaryPaths
            )
        );
    }
}
