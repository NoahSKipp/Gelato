using System.Net;
using Gelato.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gelato.Tests;

public class TmdbSearchTests
{
    private sealed class Fake : HttpMessageHandler
    {
        public Func<string, (HttpStatusCode, string)> Answer = _ => (HttpStatusCode.NotFound, "");
        public int Delay;
        public int InFlight, MaxInFlight, ExternalIdCalls;
        public List<string> Paths = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath;
            lock (Paths) Paths.Add(path);
            if (path.EndsWith("/external_ids")) Interlocked.Increment(ref ExternalIdCalls);
            var now = Interlocked.Increment(ref InFlight);
            lock (Paths) MaxInFlight = Math.Max(MaxInFlight, now);
            try
            {
                await Task.Delay(Delay, ct);
                var (code, body) = Answer(path);
                return new HttpResponseMessage(code) { Content = new StringContent(body) };
            }
            finally
            {
                Interlocked.Decrement(ref InFlight);
            }
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, false);
    }

    private static string Page(string kind, params int[] ids) =>
        "{\"results\":["
        + string.Join(",", ids.Select(i => kind == "movie"
            ? $"{{\"id\":{i},\"title\":\"Heat {i}\",\"overview\":\"o\",\"poster_path\":\"/p{i}.jpg\",\"release_date\":\"1995-12-15\",\"vote_average\":8.3}}"
            : $"{{\"id\":{i},\"name\":\"Heat Show {i}\",\"first_air_date\":\"2010-01-01\"}}"))
        + "]}";

    private static (HttpStatusCode, string) Tmdb(string path, int offset)
    {
        if (path.EndsWith("/search/movie")) return (HttpStatusCode.OK, Page("movie", offset + 1, offset + 2, offset + 3));
        if (path.EndsWith("/search/tv")) return (HttpStatusCode.OK, Page("tv", offset + 7));
        if (path.EndsWith("/external_ids"))
        {
            var id = int.Parse(path.Split('/')[^2]);
            return (HttpStatusCode.OK, id % 10 == 3 ? "{\"imdb_id\":null}" : $"{{\"imdb_id\":\"tt{id:D7}\"}}");
        }
        return (HttpStatusCode.NotFound, "");
    }

    private static TmdbSearch Make(Fake fake) =>
        new(new Factory(fake), NullLogger<TmdbSearch>.Instance) { MoviePages = 1 };

    [Fact]
    public async Task HitsBecomeTheImdbIdsGelatoUses()
    {
        TmdbSearch.ApiBase = "http://tmdb.test/3";
        var fake = new Fake { Answer = p => Tmdb(p, 1000) };
        var r = await Make(fake).SearchAsync("Heat", true, true, "de-DE", CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(["tt0001001", "tt0001002", "tt0001007"], r.Metas.Select(m => m.Id));
        var heat = r.Metas[0];
        Assert.Equal(StremioMediaType.Movie, heat.Type);
        Assert.Equal("tt0001001", heat.ImdbId);
        Assert.Equal("Heat 1001", heat.GetName());
        Assert.Equal("https://image.tmdb.org/t/p/w500/p1001.jpg", heat.Poster);
        Assert.Equal(1995, heat.GetYear());
        Assert.Equal(StremioMediaType.Series, r.Metas[2].Type);
        Assert.Equal("Heat Show 1007", r.Metas[2].GetName());
    }

    [Fact]
    public async Task ImdbIdsAreLookedUpOnce()
    {
        TmdbSearch.ApiBase = "http://tmdb.test/3";
        var fake = new Fake { Answer = p => Tmdb(p, 2000) };
        var search = Make(fake);
        await search.SearchAsync("Heat", true, true, "en-US", CancellationToken.None);
        var first = fake.ExternalIdCalls;
        var r = await search.SearchAsync("Heat", true, true, "en-US", CancellationToken.None);
        Assert.Equal(4, first);
        Assert.Equal(first, fake.ExternalIdCalls);
        Assert.Contains(r.Timings, t => t.Contains("4 cached"));
    }

    [Fact]
    public async Task MovieAndTvAreAskedAtTheSameTime()
    {
        TmdbSearch.ApiBase = "http://tmdb.test/3";
        var fake = new Fake { Answer = p => Tmdb(p, 3000), Delay = 200 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await new TmdbSearch(new Factory(fake), NullLogger<TmdbSearch>.Instance) { MoviePages = 2 }
            .SearchAsync("Heat", true, true, "en-US", CancellationToken.None);
        Assert.True(fake.MaxInFlight >= 3, $"at most {fake.MaxInFlight} requests at once");
        // three searches and four id lookups, 200 ms each, would be 1.4 s one after another
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task AFailingSearchSendsTheCallerBackToTheAddon()
    {
        TmdbSearch.ApiBase = "http://tmdb.test/3";
        var fake = new Fake { Answer = p => p.EndsWith("/search/tv") ? (HttpStatusCode.ServiceUnavailable, "") : Tmdb(p, 4000) };
        var r = await Make(fake).SearchAsync("Heat", true, true, "en-US", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains(r.Timings, t => t.StartsWith("tv page 1 failed"));
    }

    [Fact]
    public async Task ASlowTmdbIsGivenUpOn()
    {
        TmdbSearch.ApiBase = "http://tmdb.test/3";
        var fake = new Fake { Answer = p => Tmdb(p, 5000), Delay = 3000 };
        var search = new TmdbSearch(new Factory(fake), NullLogger<TmdbSearch>.Instance) { SearchBudget = TimeSpan.FromMilliseconds(300) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await search.SearchAsync("Heat", true, false, "en-US", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"took {sw.ElapsedMilliseconds} ms");
    }
}
