using Gelato.Filters;
using Gelato.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Gelato.Tests;

public class ProbeGateTests
{
    private sealed record Source(string Container, bool HasVideo);

    private static bool NeedsProbe(Source s) => !s.HasVideo || string.IsNullOrEmpty(s.Container);

    private static readonly Source Unprobed = new("", false);
    private static readonly Source Probed = new("mkv", true);

    [Fact]
    public async Task ConcurrentCallersShareOneProbeAndAllGetTheProbedSource()
    {
        var gate = new ProbeGate();
        var stored = Unprobed;
        var probes = 0;
        async Task<bool> Probe()
        {
            Interlocked.Increment(ref probes);
            await Task.Delay(300);
            stored = Probed;
            return true;
        }

        var callers = Enumerable.Range(0, 5).Select(_ =>
            Task.Run(() => gate.EnsureProbedAsync(Guid.Empty, Unprobed, NeedsProbe, Probe, () => stored, ProbeGate.MaxWait, CancellationToken.None))
        );
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, probes);
        Assert.All(results, r =>
        {
            Assert.True(r.HasVideo);
            Assert.False(string.IsNullOrEmpty(r.Container));
        });
        Assert.Equal(0, gate.InFlight);
    }

    [Fact]
    public async Task AFailingProbeIsRetriedOnceThenGivesUpInsteadOfAnUnprobedSource()
    {
        var gate = new ProbeGate();
        var probes = 0;
        Task<bool> Probe()
        {
            Interlocked.Increment(ref probes);
            throw new InvalidOperationException("ffprobe died");
        }

        var error = await Assert.ThrowsAsync<ProbeUnavailableException>(() =>
            gate.EnsureProbedAsync(Guid.NewGuid(), Unprobed, NeedsProbe, Probe, () => Unprobed, ProbeGate.MaxWait, CancellationToken.None)
        );
        Assert.Equal(2, probes);
        Assert.Equal("probe failed", error.Message);
    }

    [Fact]
    public async Task ASecondTryThatWorksIsUsed()
    {
        var gate = new ProbeGate();
        var stored = Unprobed;
        var probes = 0;
        Task<bool> Probe()
        {
            if (Interlocked.Increment(ref probes) == 2)
                stored = Probed;
            return Task.FromResult(probes == 2);
        }

        var result = await gate.EnsureProbedAsync(Guid.NewGuid(), Unprobed, NeedsProbe, Probe, () => stored, ProbeGate.MaxWait, CancellationToken.None);
        Assert.Equal(Probed, result);
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task AnAlreadyProbedSourceComesBackWithoutTheGate()
    {
        var gate = new ProbeGate();
        var probes = 0;
        var result = await gate.EnsureProbedAsync(
            Guid.NewGuid(),
            Probed,
            NeedsProbe,
            () =>
            {
                probes++;
                return Task.FromResult(true);
            },
            () => throw new InvalidOperationException("must not re-read"),
            ProbeGate.MaxWait,
            CancellationToken.None
        );
        Assert.Same(Probed, result);
        Assert.Equal(0, probes);
        Assert.Equal(0, gate.InFlight);
    }

    [Fact]
    public async Task PlayWaitsForAStuckProbeOnlyUpToTheLimit()
    {
        var gate = new ProbeGate();
        var never = new TaskCompletionSource<bool>();
        var started = DateTime.UtcNow;
        var error = await Assert.ThrowsAsync<ProbeUnavailableException>(() =>
            gate.EnsureProbedAsync(Guid.NewGuid(), Unprobed, NeedsProbe, () => never.Task, () => Unprobed, TimeSpan.FromMilliseconds(200), CancellationToken.None)
        );
        Assert.Contains("still running", error.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ProbesOfReleasesWithTheSameNameNeverShareOrDeleteEachOthersFile()
    {
        var first = ProbeTempFile.Create("Movie.2009.1080p.BluRay.x264-GRP", "a");
        var second = ProbeTempFile.Create("Movie.2009.1080p.BluRay.x264-GRP", "b");
        try
        {
            Assert.NotEqual(first, second);
            Assert.Equal(Path.GetFileName(first), Path.GetFileName(second));
            ProbeTempFile.Delete(first);
            Assert.False(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.Equal("b", File.ReadAllText(second));
        }
        finally
        {
            ProbeTempFile.Delete(second);
        }
    }

    [Fact]
    public void DeleteLeavesPathsItDidNotCreateAlone()
    {
        var dir = Directory.CreateTempSubdirectory("gelato-other");
        var stranger = Path.Combine(dir.FullName, "keep.strm");
        File.WriteAllText(stranger, "x");
        ProbeTempFile.Delete(stranger);
        Assert.True(File.Exists(stranger));
        dir.Delete(true);
    }

    [Fact]
    public void ANotReadySourceBecomesA503WithRetryAfter()
    {
        var http = new DefaultHttpContext();
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var executed = new ActionExecutedContext(action, new List<IFilterMetadata>(), new object())
        {
            Exception = new AggregateException(new ProbeUnavailableException("probe failed")),
        };

        Assert.True(PlaybackInfoFilter.HandleProbeUnavailable(executed));
        Assert.True(executed.ExceptionHandled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(executed.Result).StatusCode);
        Assert.Equal("2", http.Response.Headers["Retry-After"].ToString());
    }

    [Fact]
    public void OtherErrorsAreLeftToJellyfin()
    {
        var action = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var executed = new ActionExecutedContext(action, new List<IFilterMetadata>(), new object())
        {
            Exception = new InvalidOperationException("other"),
        };
        Assert.False(PlaybackInfoFilter.HandleProbeUnavailable(executed));
        Assert.False(executed.ExceptionHandled);
    }
}
