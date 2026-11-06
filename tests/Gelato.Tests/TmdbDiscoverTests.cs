using System.Net;
using Gelato.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gelato.Tests;

public class TmdbDiscoverTests
{
    private sealed class Fake : HttpMessageHandler
    {
        public Func<HttpRequestMessage, (HttpStatusCode, string)> Answer = _ => (HttpStatusCode.NotFound, "");
        public List<string> RequestedUrls = [];
        public int ExternalIdCalls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();
            lock (RequestedUrls) RequestedUrls.Add(url);
            if (url.Contains("/external_ids")) Interlocked.Increment(ref ExternalIdCalls);
            await Task.Yield();
            var (code, body) = Answer(r);
            return new HttpResponseMessage(code) { Content = new StringContent(body) };
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, false);
    }

    private static string Page(string kind, params int[] ids) =>
        "{\"results\":["
        + string.Join(",", ids.Select(i => kind == "movie"
            ? $"{{\"id\":{i},\"title\":\"Film {i}\",\"overview\":\"Description {i}\",\"poster_path\":\"/p{i}.jpg\",\"release_date\":\"2024-05-10\",\"vote_average\":7.8,\"popularity\":{100 - i}}}"
            : $"{{\"id\":{i},\"name\":\"Show {i}\",\"overview\":\"Series Description {i}\",\"first_air_date\":\"2024-01-01\",\"vote_average\":8.1,\"popularity\":{50 - i}}}"))
        + "]}";

    private static (HttpStatusCode, string) DiscoverHandler(HttpRequestMessage r, int offset)
    {
        var path = r.RequestUri!.AbsolutePath;
        if (path.EndsWith("/discover/movie"))
        {
            return (HttpStatusCode.OK, Page("movie", offset + 1, offset + 2));
        }
        if (path.EndsWith("/discover/tv"))
        {
            return (HttpStatusCode.OK, Page("tv", offset + 10));
        }
        if (path.EndsWith("/external_ids"))
        {
            var id = int.Parse(path.Split('/')[^2]);
            return (HttpStatusCode.OK, $"{{\"imdb_id\":\"tt{id:D7}\"}}");
        }
        return (HttpStatusCode.NotFound, "");
    }

    private static TmdbSearch Make(Fake fake) =>
        new(new Factory(fake), NullLogger<TmdbSearch>.Instance);

    [Fact]
    public async Task DiscoverMapsMovieAndTvGenresCorrectly()
    {
        TmdbSearch.ApiBase = "http://tmdb.test/3";
        var fake = new Fake { Answer = r => DiscoverHandler(r, 100) };
        var search = Make(fake);

        var r = await search.DiscoverAsync("Action", true, true, "en-US", 1, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains(fake.RequestedUrls, u => u.Contains("/discover/movie") && u.Contains("with_genres=28"));
        Assert.Contains(fake.RequestedUrls, u => u.Contains("/discover/tv") && u.Contains("with_genres=10759"));

        fake.RequestedUrls.Clear();
        var rSciFi = await search.DiscoverAsync("Sci-Fi", true, true, "en-US", 1, CancellationToken.None);
        Assert.True(rSciFi.Ok);
        Assert.Contains(fake.RequestedUrls, u => u.Contains("/discover/movie") && u.Contains("with_genres=878"));
        Assert.Contains(fake.RequestedUrls, u => u.Contains("/discover/tv") && u.Contains("with_genres=10765"));

        fake.RequestedUrls.Clear();
        var rHorror = await search.DiscoverAsync("Horror", true, true, "en-US", 1, CancellationToken.None);
        Assert.True(rHorror.Ok);
        Assert.Contains(fake.RequestedUrls, u => u.Contains("/discover/movie") && u.Contains("with_genres=27"));
        Assert.DoesNotContain(fake.RequestedUrls, u => u.Contains("/discover/tv"));
    }

    [Fact]
    public void GenreIdLookupMapsAllSpecifiedGenres()
    {
        // Movie genres
        Assert.Equal(28, TmdbSearch.GetMovieGenreId("Action"));
        Assert.Equal(12, TmdbSearch.GetMovieGenreId("Adventure"));
        Assert.Equal(16, TmdbSearch.GetMovieGenreId("Animation"));
        Assert.Equal(35, TmdbSearch.GetMovieGenreId("Comedy"));
        Assert.Equal(80, TmdbSearch.GetMovieGenreId("Crime"));
        Assert.Equal(99, TmdbSearch.GetMovieGenreId("Documentary"));
        Assert.Equal(18, TmdbSearch.GetMovieGenreId("Drama"));
        Assert.Equal(10751, TmdbSearch.GetMovieGenreId("Family"));
        Assert.Equal(14, TmdbSearch.GetMovieGenreId("Fantasy"));
        Assert.Equal(36, TmdbSearch.GetMovieGenreId("History"));
        Assert.Equal(27, TmdbSearch.GetMovieGenreId("Horror"));
        Assert.Equal(10402, TmdbSearch.GetMovieGenreId("Music"));
        Assert.Equal(9648, TmdbSearch.GetMovieGenreId("Mystery"));
        Assert.Equal(10749, TmdbSearch.GetMovieGenreId("Romance"));
        Assert.Equal(878, TmdbSearch.GetMovieGenreId("Sci-Fi"));
        Assert.Equal(878, TmdbSearch.GetMovieGenreId("Science Fiction"));
        Assert.Equal(53, TmdbSearch.GetMovieGenreId("Thriller"));
        Assert.Equal(10752, TmdbSearch.GetMovieGenreId("War"));
        Assert.Equal(37, TmdbSearch.GetMovieGenreId("Western"));

        // TV genres
        Assert.Equal(10759, TmdbSearch.GetTvGenreId("Action"));
        Assert.Equal(10759, TmdbSearch.GetTvGenreId("Action & Adventure"));
        Assert.Equal(16, TmdbSearch.GetTvGenreId("Animation"));
        Assert.Equal(35, TmdbSearch.GetTvGenreId("Comedy"));
        Assert.Equal(80, TmdbSearch.GetTvGenreId("Crime"));
        Assert.Equal(99, TmdbSearch.GetTvGenreId("Documentary"));
        Assert.Equal(18, TmdbSearch.GetTvGenreId("Drama"));
        Assert.Equal(10751, TmdbSearch.GetTvGenreId("Family"));
        Assert.Equal(10765, TmdbSearch.GetTvGenreId("Fantasy"));
        Assert.Equal(10765, TmdbSearch.GetTvGenreId("Sci-Fi & Fantasy"));
        Assert.Equal(10765, TmdbSearch.GetTvGenreId("Sci-Fi"));
        Assert.Equal(9648, TmdbSearch.GetTvGenreId("Mystery"));
        Assert.Equal(10768, TmdbSearch.GetTvGenreId("War"));
        Assert.Equal(10768, TmdbSearch.GetTvGenreId("War & Politics"));
        Assert.Equal(37, TmdbSearch.GetTvGenreId("Western"));
    }

    [Fact]
    public async Task DiscoverHitsAreResolvedToImdbIdsAndMetas()
    {
        TmdbSearch.ApiBase = "http://tmdb.test/3";
        var fake = new Fake { Answer = r => DiscoverHandler(r, 500) };
        var search = Make(fake);

        var r = await search.DiscoverAsync("Comedy", true, true, "en-US", 1, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(3, r.Metas.Count);
        Assert.Equal(["tt0000501", "tt0000502", "tt0000510"], r.Metas.Select(m => m.Id));

        var first = r.Metas[0];
        Assert.Equal("tt0000501", first.ImdbId);
        Assert.Equal(StremioMediaType.Movie, first.Type);
        Assert.Equal("Film 501", first.GetName());
        Assert.Equal("https://image.tmdb.org/t/p/w500/p501.jpg", first.Poster);
        Assert.Equal(2024, first.GetYear());

        var series = r.Metas[2];
        Assert.Equal("tt0000510", series.ImdbId);
        Assert.Equal(StremioMediaType.Series, series.Type);
        Assert.Equal("Show 510", series.GetName());
    }

    [Fact]
    public async Task DiscoverPaginationPassesPageParameter()
    {
        TmdbSearch.ApiBase = "http://tmdb.test/3";
        var fake = new Fake { Answer = r => DiscoverHandler(r, 600) };
        var search = Make(fake);

        var r = await search.DiscoverAsync("Action", true, false, "de-DE", 3, CancellationToken.None);
        Assert.True(r.Ok);
        var requestedUrl = Assert.Single(fake.RequestedUrls, u => u.Contains("/discover/movie"));
        Assert.Contains("page=3", requestedUrl);
        Assert.Contains("language=de-DE", requestedUrl);
        Assert.Contains("with_genres=28", requestedUrl);
        Assert.Contains("sort_by=popularity.desc", requestedUrl);
        Assert.Contains("include_adult=false", requestedUrl);
    }

    [Fact]
    public void PersistentMetadataCacheSurvivesMemoryPressureAndCompaction()
    {
        var memCache = new MemoryCache(new MemoryCacheOptions());
        var manager = new GelatoManager(
            NullLoggerFactory.Instance,
            null!,
            null!,
            null!,
            null!,
            memCache,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!
        );

        var guid = Guid.NewGuid();
        var meta = new StremioMeta
        {
            Id = "tt1234567",
            ImdbId = "tt1234567",
            Name = "Virtual Test Title",
            Type = StremioMediaType.Movie
        };

        manager.SaveStremioMeta(guid, meta);

        // Verify retrieval from memory cache
        var cached = manager.GetStremioMeta(guid);
        Assert.NotNull(cached);
        Assert.Equal("Virtual Test Title", cached.GetName());

        // Simulate memory pressure / cache compaction
        memCache.Compact(1.0);

        // Verify that memoryCache is empty for this key
        Assert.False(memCache.TryGetValue($"meta:{guid}", out _));

        // GetStremioMeta should retrieve it from _persistentMeta and rehydrate memoryCache
        var recovered = manager.GetStremioMeta(guid);
        Assert.NotNull(recovered);
        Assert.Equal("Virtual Test Title", recovered.GetName());
        Assert.Equal("tt1234567", recovered.Id);

        // Subsequent check: memoryCache was repopulated
        Assert.True(memCache.TryGetValue($"meta:{guid}", out var rehydrated));
        Assert.NotNull(rehydrated);

        // Verify RemoveStremioMeta removes from both
        manager.RemoveStremioMeta(guid);
        Assert.Null(manager.GetStremioMeta(guid));
    }
}
