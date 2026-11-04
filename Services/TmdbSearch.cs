using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

// Search straight on TMDB instead of through the addon's search catalog
// (on the live server a tmdb-addon behind AIOStreams, 2 to 10 s a search).
// Hits are resolved to their IMDb ids, the ids Gelato's items and stream
// lookups use, so a result is the same item it was from the addon.
public sealed class TmdbSearch(IHttpClientFactory http, ILogger<TmdbSearch> log)
{
    // For the bench server's mock; production uses TMDB.
    public static string ApiBase { get; set; } =
        Environment.GetEnvironmentVariable("GELATO_TMDB_API") ?? "https://api.themoviedb.org/3";

    public TimeSpan SearchBudget { get; init; } = TimeSpan.FromSeconds(2.5);
    public TimeSpan IdBudget { get; init; } = TimeSpan.FromSeconds(2);
    public int MoviePages { get; init; } = 2;
    public int TvPages { get; init; } = 1;
    public int Parallel { get; init; } = 16;

    private static readonly TimeSpan IdTtl = TimeSpan.FromDays(7);
    private const int MaxIds = 20000;
    private static readonly ConcurrentDictionary<string, (DateTime At, string? Imdb)> ImdbIds = new();

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public sealed record Result(bool Ok, List<StremioMeta> Metas, IReadOnlyList<string> Timings);

    public async Task<Result> SearchAsync(
        string term,
        bool movies,
        bool series,
        string language,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();
        var timings = new ConcurrentQueue<string>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(SearchBudget);
        var key = GelatoStremioProvider.GetTmdbApiKey();
        var q = Uri.EscapeDataString(term.Trim());

        async Task<(bool Ok, List<Hit> Hits)> Page(string kind, int page)
        {
            var started = sw.ElapsedMilliseconds;
            var url = $"{ApiBase}/search/{kind}?query={q}&language={language}&include_adult=false&page={page}&api_key={key}";
            try
            {
                var body = await Client().GetStringAsync(url, budget.Token).ConfigureAwait(false);
                var hits = JsonSerializer.Deserialize<SearchPage>(body, Json)?.Results ?? [];
                timings.Enqueue($"{kind} page {page} {sw.ElapsedMilliseconds - started} ms ({hits.Count})");
                return (true, hits);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                timings.Enqueue($"{kind} page {page} failed after {sw.ElapsedMilliseconds - started} ms ({ex.GetType().Name})");
                return (false, []);
            }
        }

        var movieTasks = movies ? Enumerable.Range(1, MoviePages).Select(p => Page("movie", p)).ToList() : [];
        var tvTasks = series ? Enumerable.Range(1, TvPages).Select(p => Page("tv", p)).ToList() : [];
        var moviePages = await Task.WhenAll(movieTasks).ConfigureAwait(false);
        var tvPages = await Task.WhenAll(tvTasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // The first page of each requested kind has to answer; a later page
        // only adds results.
        if ((movies && !moviePages[0].Ok) || (series && !tvPages[0].Ok))
            return new Result(false, [], timings.ToList());

        var candidates = moviePages.SelectMany(p => p.Hits).Select(h => (Kind: "movie", Hit: h))
            .Concat(tvPages.SelectMany(p => p.Hits).Select(h => (Kind: "tv", Hit: h)))
            .GroupBy(c => c.Kind + ":" + c.Hit.Id)
            .Select(g => g.First())
            .ToList();

        var idStarted = sw.ElapsedMilliseconds;
        var cached = 0;
        using var gate = new SemaphoreSlim(Parallel);
        using var idBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idBudget.CancelAfter(IdBudget);
        var resolved = await Task.WhenAll(candidates.Select(async c =>
        {
            var cacheKey = c.Kind + ":" + c.Hit.Id;
            if (ImdbIds.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < IdTtl)
            {
                Interlocked.Increment(ref cached);
                return hit.Imdb;
            }
            await gate.WaitAsync(idBudget.Token).ConfigureAwait(false);
            try
            {
                var body = await Client()
                    .GetStringAsync($"{ApiBase}/{c.Kind}/{c.Hit.Id}/external_ids?api_key={key}", idBudget.Token)
                    .ConfigureAwait(false);
                var imdb = JsonSerializer.Deserialize<ExternalIds>(body, Json)?.ImdbId;
                imdb = string.IsNullOrWhiteSpace(imdb) ? null : imdb;
                Remember(cacheKey, imdb);
                return imdb;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            finally
            {
                gate.Release();
            }
        }).Select(t => t.ContinueWith(r => r.IsCompletedSuccessfully ? r.Result : null, TaskScheduler.Default))).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var metas = new List<StremioMeta>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (resolved[i] is { } imdb)
                metas.Add(ToMeta(candidates[i].Kind, candidates[i].Hit, imdb));
        }
        timings.Enqueue(
            $"imdb ids {sw.ElapsedMilliseconds - idStarted} ms ({metas.Count} of {candidates.Count}, {cached} cached)"
        );
        log.LogDebug("TMDB search took {Ms} ms", sw.ElapsedMilliseconds);
        return new Result(true, metas, timings.ToList());
    }

    private HttpClient Client()
    {
        var c = http.CreateClient(nameof(TmdbSearch));
        c.Timeout = TimeSpan.FromSeconds(10);
        return c;
    }

    private static void Remember(string key, string? imdb)
    {
        if (ImdbIds.Count >= MaxIds)
            ImdbIds.Clear();
        ImdbIds[key] = (DateTime.UtcNow, imdb);
    }

    public static StremioMeta ToMeta(string kind, Hit hit, string imdb)
    {
        var date = kind == "movie" ? hit.ReleaseDate : hit.FirstAirDate;
        DateTime? released = DateTime.TryParseExact(
            date,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var d
        )
            ? d
            : null;
        return new StremioMeta
        {
            Id = imdb,
            ImdbId = imdb,
            Type = kind == "movie" ? StremioMediaType.Movie : StremioMediaType.Series,
            Name = kind == "movie" ? hit.Title : hit.Name,
            Description = string.IsNullOrWhiteSpace(hit.Overview) ? null : hit.Overview,
            Poster = hit.PosterPath is { Length: > 0 } p ? "https://image.tmdb.org/t/p/w500" + p : null,
            Background = hit.BackdropPath is { Length: > 0 } b ? "https://image.tmdb.org/t/p/w1280" + b : null,
            Released = released,
            ReleaseInfo = released?.Year.ToString(CultureInfo.InvariantCulture),
            ImdbRating = hit.VoteAverage is > 0 ? (float)Math.Round(hit.VoteAverage.Value, 1) : null,
        };
    }

    public sealed class SearchPage
    {
        public List<Hit>? Results { get; set; }
    }

    public sealed class Hit
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Name { get; set; }
        public string? Overview { get; set; }

        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }

        [JsonPropertyName("backdrop_path")]
        public string? BackdropPath { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }

        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; set; }
    }

    private sealed class ExternalIds
    {
        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }
    }
}
