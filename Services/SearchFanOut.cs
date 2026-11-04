using System.Collections.Concurrent;
using System.Diagnostics;

namespace Gelato.Services;

// One search term against the addon, plus the titles TMDB knows it by
// (a German term finds the English title the addon indexes). The direct
// search and the TMDB lookup start together, each translated search
// starts the moment TMDB answers, and every request has its own budget:
// a slow one costs its own results, never the others'.
public sealed class SearchFanOut<T>
{
    // The extra searches for translated titles; a slow one is dropped.
    public TimeSpan RequestBudget { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan TitleBudget { get; init; } = TimeSpan.FromSeconds(2);

    // The term's own search is the answer, so it is not cut at the short
    // budget (an addon that always needs 6 s would otherwise return
    // nothing at all); past SlowAfter it is only marked slow in the log.
    public TimeSpan DirectBudget { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan SlowAfter { get; init; } = TimeSpan.FromSeconds(4);

    // With this many direct hits the translated ones only get a short grace
    // after the direct answer; with fewer they get their whole budget.
    public int EnoughDirect { get; init; } = 5;
    public TimeSpan Grace { get; init; } = TimeSpan.FromMilliseconds(250);
    public int MaxTitles { get; init; } = 2;
    public int PerTitle { get; init; } = 5;

    public sealed record Result(List<T> Items, IReadOnlyList<string> Timings);

    public async Task<Result> RunAsync(
        string term,
        Func<string, CancellationToken, Task<IReadOnlyList<T>>> search,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? titles,
        Func<T, string> id,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();
        var timings = new ConcurrentQueue<(long At, string Line)>();

        async Task<IReadOnlyList<TR>> Timed<TR>(string label, TimeSpan budget, Func<CancellationToken, Task<IReadOnlyList<TR>>> work)
        {
            var started = sw.ElapsedMilliseconds;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(budget);
            try
            {
                var r = await work(cts.Token).ConfigureAwait(false);
                var took = sw.ElapsedMilliseconds - started;
                timings.Enqueue((started, $"{label} {took} ms ({r.Count}){(took >= SlowAfter.TotalMilliseconds ? " slow" : "")}"));
                return r;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timings.Enqueue((started, $"{label} timed out after {sw.ElapsedMilliseconds - started} ms"));
                return [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                timings.Enqueue((started, $"{label} failed after {sw.ElapsedMilliseconds - started} ms"));
                return [];
            }
        }

        var direct = Timed("direct", DirectBudget, c => search(term, c));
        var translated = titles is null || term.Trim().Length < 3
            ? Task.FromResult<IReadOnlyList<IReadOnlyList<T>>>([])
            : TranslatedAsync();

        async Task<IReadOnlyList<IReadOnlyList<T>>> TranslatedAsync()
        {
            var found = await Timed("tmdb titles", TitleBudget, c => titles!(term, c)).ConfigureAwait(false);
            var searches = found
                .Take(MaxTitles)
                .Select((t, i) => Timed($"title {i + 1}", RequestBudget, c => search(t, c)))
                .ToList();
            return await Task.WhenAll(searches).ConfigureAwait(false);
        }

        var directItems = await direct.ConfigureAwait(false);
        IReadOnlyList<IReadOnlyList<T>> extra = [];
        if (directItems.Count >= EnoughDirect)
        {
            if (await Task.WhenAny(translated, Task.Delay(Grace, ct)).ConfigureAwait(false) == translated)
                extra = await translated.ConfigureAwait(false);
            else
                timings.Enqueue((sw.ElapsedMilliseconds, "translated titles not waited for"));
        }
        else
        {
            extra = await translated.ConfigureAwait(false);
        }

        var merged = new List<T>(directItems);
        var seen = new HashSet<string>(merged.Select(id), StringComparer.OrdinalIgnoreCase);
        foreach (var batch in extra)
        {
            foreach (var item in batch.Take(PerTitle))
            {
                if (seen.Add(id(item)))
                    merged.Add(item);
            }
        }

        return new Result(merged, timings.OrderBy(t => t.At).Select(t => t.Line).ToList());
    }
}
