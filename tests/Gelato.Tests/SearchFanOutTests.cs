using System.Diagnostics;
using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class SearchFanOutTests
{
    private static Func<string, CancellationToken, Task<IReadOnlyList<string>>> Answer(
        Dictionary<string, (int Ms, string[] Items)> answers
    ) =>
        async (term, ct) =>
        {
            var (ms, items) = answers[term];
            await Task.Delay(ms, ct);
            return items;
        };

    private static string[] Hits(string prefix, int n) => Enumerable.Range(0, n).Select(i => $"{prefix}{i}").ToArray();

    [Fact]
    public async Task EnoughDirectHitsDoNotWaitForTheTranslatedSearches()
    {
        var fan = new SearchFanOut<string>();
        var sw = Stopwatch.StartNew();
        var r = await fan.RunAsync(
            "Heat",
            Answer(new() { ["Heat"] = (300, Hits("h", 40)), ["Heat 1995"] = (3000, Hits("x", 5)) }),
            async (_, ct) => { await Task.Delay(200, ct); return ["Heat 1995"]; },
            s => s,
            CancellationToken.None
        );
        Assert.True(sw.ElapsedMilliseconds < 1200, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(40, r.Items.Count);
        Assert.Contains(r.Timings, t => t.StartsWith("direct "));
        Assert.Contains("translated titles not waited for", r.Timings);
    }

    [Fact]
    public async Task AForeignTermWaitsForTheTitleItTranslatesTo()
    {
        var fan = new SearchFanOut<string>();
        var r = await fan.RunAsync(
            "Die Verurteilten",
            Answer(new() { ["Die Verurteilten"] = (100, []), ["The Shawshank Redemption"] = (400, ["tt0111161"]) }),
            async (_, ct) => { await Task.Delay(100, ct); return ["The Shawshank Redemption"]; },
            s => s,
            CancellationToken.None
        );
        Assert.Equal(["tt0111161"], r.Items);
    }

    [Fact]
    public async Task ASlowRequestOnlyCostsItsOwnResults()
    {
        var fan = new SearchFanOut<string> { RequestBudget = TimeSpan.FromMilliseconds(300), EnoughDirect = 100 };
        var sw = Stopwatch.StartNew();
        var r = await fan.RunAsync(
            "Alien",
            Answer(new() { ["Alien"] = (50, Hits("a", 3)), ["Aliens"] = (5000, Hits("b", 3)), ["Alien 3"] = (100, ["c"]) }),
            async (_, ct) => { await Task.Delay(20, ct); return ["Aliens", "Alien 3"]; },
            s => s,
            CancellationToken.None
        );
        Assert.True(sw.ElapsedMilliseconds < 1500, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(["a0", "a1", "a2", "c"], r.Items);
        Assert.Contains(r.Timings, t => t.StartsWith("title 1 timed out"));
    }

    [Fact]
    public async Task ATmdbLookupThatHangsDoesNotHoldTheDirectAnswer()
    {
        var fan = new SearchFanOut<string> { TitleBudget = TimeSpan.FromMilliseconds(200), EnoughDirect = 100 };
        var sw = Stopwatch.StartNew();
        var r = await fan.RunAsync(
            "Heat",
            Answer(new() { ["Heat"] = (50, Hits("h", 3)) }),
            async (_, ct) => { await Task.Delay(10000, ct); return []; },
            s => s,
            CancellationToken.None
        );
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(3, r.Items.Count);
        Assert.Contains(r.Timings, t => t.StartsWith("tmdb titles timed out"));
    }

    [Fact]
    public async Task TranslatedHitsComeAfterTheTermsOwnAndAreNotRepeated()
    {
        var fan = new SearchFanOut<string> { EnoughDirect = 100 };
        var r = await fan.RunAsync(
            "Alien",
            Answer(new() { ["Alien"] = (10, ["a", "b"]), ["Aliens"] = (10, ["b", "c"]) }),
            async (_, _) => { await Task.Yield(); return ["Aliens"]; },
            s => s,
            CancellationToken.None
        );
        Assert.Equal(["a", "b", "c"], r.Items);
    }

    [Fact]
    public async Task ASlowAddonStillAnswersTheSearchItself()
    {
        var fan = new SearchFanOut<string> { RequestBudget = TimeSpan.FromMilliseconds(200), SlowAfter = TimeSpan.FromMilliseconds(200) };
        var r = await fan.RunAsync(
            "Heat",
            Answer(new() { ["Heat"] = (600, Hits("h", 10)), ["Heat 1995"] = (600, ["x"]) }),
            async (_, ct) => { await Task.Delay(10, ct); return ["Heat 1995"]; },
            s => s,
            CancellationToken.None
        );
        Assert.Equal(10, r.Items.Count);
        Assert.Contains(r.Timings, t => t.StartsWith("direct ") && t.EndsWith(" slow"));
        Assert.Contains(r.Timings, t => t.StartsWith("title 1 timed out"));
    }
}
