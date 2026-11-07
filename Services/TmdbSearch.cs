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

        var metas = await ResolveCandidatesAsync(candidates, key, sw, timings, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        log.LogDebug("TMDB search took {Ms} ms", sw.ElapsedMilliseconds);
        return new Result(true, metas, timings.ToList());
    }

    private static readonly Dictionary<string, int> MovieGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Action"] = 28,
        ["Adventure"] = 12,
        ["Animation"] = 16,
        ["Anime"] = 16,
        ["Comedy"] = 35,
        ["Crime"] = 80,
        ["Documentary"] = 99,
        ["Drama"] = 18,
        ["Family"] = 10751,
        ["Fantasy"] = 14,
        ["History"] = 36,
        ["Horror"] = 27,
        ["Music"] = 10402,
        ["Mystery"] = 9648,
        ["Romance"] = 10749,
        ["Sci-Fi"] = 878,
        ["Science Fiction"] = 878,
        ["Science-Fiction"] = 878,
        ["Sci Fi"] = 878,
        ["SciFi"] = 878,
        ["Thriller"] = 53,
        ["War"] = 10752,
        ["Western"] = 37,
        ["TV Movie"] = 10770,
        ["Action & Adventure"] = 28,
        ["Action and Adventure"] = 28,
        ["Sci-Fi & Fantasy"] = 878,
        ["Sci Fi & Fantasy"] = 878,
        ["War & Politics"] = 10752,
        // Common German localized genre names
        ["Abenteuer"] = 12,
        ["Komödie"] = 35,
        ["Krimi"] = 80,
        ["Dokumentation"] = 99,
        ["Familie"] = 10751,
        ["Liebesfilm"] = 10749,
        ["Krieg"] = 10752,
        ["Musik"] = 10402,
        ["Historie"] = 36,
        ["Zeichentrick"] = 16,
        // Common French localized genre names
        ["Aventure"] = 12,
        ["Comédie"] = 35,
        ["Documentaire"] = 99,
        ["Drame"] = 18,
        ["Familial"] = 10751,
        ["Fantastique"] = 14,
        ["Histoire"] = 36,
        ["Horreur"] = 27,
        ["Musique"] = 10402,
        ["Mystère"] = 9648,
        ["Guerre"] = 10752,
        // Common Spanish localized genre names
        ["Acción"] = 28,
        ["Aventura"] = 12,
        ["Animación"] = 16,
        ["Comedia"] = 35,
        ["Crimen"] = 80,
        ["Documental"] = 99,
        ["Familia"] = 10751,
        ["Fantasía"] = 14,
        ["Historia"] = 36,
        ["Misterio"] = 9648,
        ["Película de TV"] = 10770,
        ["Suspense"] = 53,
        ["Bélica"] = 10752,
        ["Guerra"] = 10752,
        ["Ciencia ficción"] = 878
    };

    private static readonly Dictionary<string, int> TvGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Action"] = 10759,
        ["Action & Adventure"] = 10759,
        ["Action and Adventure"] = 10759,
        ["Adventure"] = 10759,
        ["Animation"] = 16,
        ["Anime"] = 16,
        ["Comedy"] = 35,
        ["Crime"] = 80,
        ["Documentary"] = 99,
        ["Drama"] = 18,
        ["Family"] = 10751,
        ["Fantasy"] = 10765,
        ["Kids"] = 10762,
        ["News"] = 10763,
        ["Reality"] = 10764,
        ["Sci-Fi & Fantasy"] = 10765,
        ["Sci-Fi"] = 10765,
        ["Science Fiction"] = 10765,
        ["Science-Fiction"] = 10765,
        ["Sci Fi"] = 10765,
        ["SciFi"] = 10765,
        ["Soap"] = 10766,
        ["Talk"] = 10767,
        ["Mystery"] = 9648,
        ["War"] = 10768,
        ["War & Politics"] = 10768,
        ["Western"] = 37,
        // Common German localized genre names
        ["Abenteuer"] = 10759,
        ["Action & Abenteuer"] = 10759,
        ["Komödie"] = 35,
        ["Krimi"] = 80,
        ["Dokumentation"] = 99,
        ["Familie"] = 10751,
        ["Krieg"] = 10768,
        ["Musik"] = 10402,
        ["Zeichentrick"] = 16,
        // Common French localized genre names
        ["Action et aventure"] = 10759,
        ["Aventure"] = 10759,
        ["Comédie"] = 35,
        ["Documentaire"] = 99,
        ["Drame"] = 18,
        ["Familial"] = 10751,
        ["Fantastique"] = 10765,
        ["Guerre"] = 10768,
        ["Mystère"] = 9648,
        // Common Spanish localized genre names
        ["Acción"] = 10759,
        ["Acción y aventura"] = 10759,
        ["Aventura"] = 10759,
        ["Animación"] = 16,
        ["Comedia"] = 35,
        ["Crimen"] = 80,
        ["Documental"] = 99,
        ["Familia"] = 10751,
        ["Fantasía"] = 10765,
        ["Ciencia ficción"] = 10765,
        ["Misterio"] = 9648,
        ["Guerra"] = 10768,
        ["Bélica"] = 10768
    };

    public static string? GetMovieGenreId(string genre) => ResolveGenreIds(genre, MovieGenres);

    public static string? GetTvGenreId(string genre) => ResolveGenreIds(genre, TvGenres);

    private static string? ResolveGenreIds(string genre, Dictionary<string, int> map)
    {
        if (string.IsNullOrWhiteSpace(genre)) return null;
        var trimmed = genre.Trim();
        if (map.TryGetValue(trimmed, out var singleId)) return singleId.ToString(CultureInfo.InvariantCulture);
        if (int.TryParse(trimmed, out var parsedSingle)) return parsedSingle.ToString(CultureInfo.InvariantCulture);

        if (!trimmed.Contains(',')) return null;

        var ids = new List<string>();
        foreach (var part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (map.TryGetValue(part, out var id))
            {
                var s = id.ToString(CultureInfo.InvariantCulture);
                if (!ids.Contains(s)) ids.Add(s);
            }
            else if (int.TryParse(part, out var parsed))
            {
                var s = parsed.ToString(CultureInfo.InvariantCulture);
                if (!ids.Contains(s)) ids.Add(s);
            }
        }

        return ids.Count > 0 ? string.Join(",", ids) : null;
    }

    public async Task<Result> DiscoverAsync(
        string genre,
        bool movies,
        bool series,
        string language,
        int page,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();
        var timings = new ConcurrentQueue<string>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(SearchBudget);
        var key = GelatoStremioProvider.GetTmdbApiKey();
        var targetPage = Math.Max(1, page);

        var filterByGenre = !string.IsNullOrWhiteSpace(genre) && !genre.Equals("all", StringComparison.OrdinalIgnoreCase);
        var movieGenreId = filterByGenre ? GetMovieGenreId(genre) : null;
        var tvGenreId = filterByGenre ? GetTvGenreId(genre) : null;

        var queryMovies = movies && (!filterByGenre || movieGenreId is not null);
        var queryTv = series && (!filterByGenre || tvGenreId is not null);

        if (!queryMovies && !queryTv)
        {
            return new Result(true, [], timings.ToList());
        }

        async Task<(bool Ok, List<Hit> Hits)> DiscoverPage(string kind, string? genreId)
        {
            var started = sw.ElapsedMilliseconds;
            var genreParam = !string.IsNullOrEmpty(genreId) ? $"with_genres={genreId}&" : "";
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var dateParam = kind == "movie" ? $"primary_release_date.lte={today}&" : $"first_air_date.lte={today}&";
            var url = $"{ApiBase}/discover/{kind}?{genreParam}{dateParam}language={language}&include_adult=false&sort_by=popularity.desc&page={targetPage}&api_key={key}";
            try
            {
                var body = await Client().GetStringAsync(url, budget.Token).ConfigureAwait(false);
                var hits = JsonSerializer.Deserialize<SearchPage>(body, Json)?.Results ?? [];
                timings.Enqueue($"{kind} discover page {targetPage} {sw.ElapsedMilliseconds - started} ms ({hits.Count})");
                return (true, hits);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                timings.Enqueue($"{kind} discover page {targetPage} failed after {sw.ElapsedMilliseconds - started} ms ({ex.GetType().Name})");
                return (false, []);
            }
        }

        var movieTask = queryMovies ? DiscoverPage("movie", movieGenreId) : Task.FromResult((Ok: true, Hits: new List<Hit>()));
        var tvTask = queryTv ? DiscoverPage("tv", tvGenreId) : Task.FromResult((Ok: true, Hits: new List<Hit>()));

        var (movieRes, tvRes) = (await movieTask.ConfigureAwait(false), await tvTask.ConfigureAwait(false));
        ct.ThrowIfCancellationRequested();

        if ((queryMovies && !movieRes.Ok) || (queryTv && !tvRes.Ok))
        {
            return new Result(false, [], timings.ToList());
        }

        var candidates = movieRes.Hits.Select(h => (Kind: "movie", Hit: h))
            .Concat(tvRes.Hits.Select(h => (Kind: "tv", Hit: h)))
            .GroupBy(c => c.Kind + ":" + c.Hit.Id)
            .Select(g => g.First())
            .ToList();

        if (candidates.Any(c => c.Hit.Popularity > 0))
        {
            candidates = candidates.OrderByDescending(c => c.Hit.Popularity ?? 0).ToList();
        }

        var metas = await ResolveCandidatesAsync(candidates, key, sw, timings, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        log.LogDebug("TMDB discover took {Ms} ms", sw.ElapsedMilliseconds);
        return new Result(true, metas, timings.ToList());
    }

    private async Task<List<StremioMeta>> ResolveCandidatesAsync(
        List<(string Kind, Hit Hit)> candidates,
        string key,
        Stopwatch sw,
        ConcurrentQueue<string> timings,
        CancellationToken ct
    )
    {
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
            var acquired = false;
            try
            {
                await gate.WaitAsync(idBudget.Token).ConfigureAwait(false);
                acquired = true;
                return await FetchImdbIdAsync(c.Kind, c.Hit.Id, key, cacheKey, idBudget.Token, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            finally
            {
                if (acquired)
                {
                    gate.Release();
                }
            }
        })).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var metas = new List<StremioMeta>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var id = resolved[i] ?? ("tmdb:" + candidates[i].Hit.Id);
            metas.Add(ToMeta(candidates[i].Kind, candidates[i].Hit, id));
        }
        timings.Enqueue(
            $"imdb ids {sw.ElapsedMilliseconds - idStarted} ms ({metas.Count} of {candidates.Count}, {cached} cached)"
        );
        return metas;
    }

    public async Task<string?> GetImdbIdAsync(string kind, int tmdbId, CancellationToken ct = default)
    {
        var cacheKey = kind + ":" + tmdbId;
        if (ImdbIds.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < IdTtl)
        {
            return hit.Imdb;
        }

        var key = GelatoStremioProvider.GetTmdbApiKey();
        using var idBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idBudget.CancelAfter(IdBudget);
        return await FetchImdbIdAsync(kind, tmdbId, key, cacheKey, idBudget.Token, ct).ConfigureAwait(false);
    }

    private async Task<string?> FetchImdbIdAsync(
        string kind,
        int tmdbId,
        string key,
        string cacheKey,
        CancellationToken requestCt,
        CancellationToken callerCt
    )
    {
        try
        {
            var body = await Client()
                .GetStringAsync($"{ApiBase}/{kind}/{tmdbId}/external_ids?api_key={key}", requestCt)
                .ConfigureAwait(false);
            var imdb = JsonSerializer.Deserialize<ExternalIds>(body, Json)?.ImdbId;
            imdb = string.IsNullOrWhiteSpace(imdb) ? null : imdb;
            Remember(cacheKey, imdb);
            return imdb;
        }
        catch (Exception) when (!callerCt.IsCancellationRequested)
        {
            return null;
        }
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

    public static StremioMeta ToMeta(string kind, Hit hit, string id)
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
            Id = id,
            ImdbId = id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? id : null,
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

        [JsonPropertyName("popularity")]
        public double? Popularity { get; set; }
    }

    private sealed class ExternalIds
    {
        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }
    }
}
