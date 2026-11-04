using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class ResyncThrottleTests
{
    [Fact]
    public void EntersOncePerWindowPerKey()
    {
        var now = new DateTime(2026, 11, 4, 17, 8, 0, DateTimeKind.Utc);
        var throttle = new ResyncThrottle(TimeSpan.FromSeconds(60), () => now);

        Assert.True(throttle.TryEnter("u:a"));
        Assert.False(throttle.TryEnter("u:a"));
        Assert.True(throttle.TryEnter("u:b"));

        now = now.AddSeconds(59);
        Assert.False(throttle.TryEnter("u:a"));

        now = now.AddSeconds(1);
        Assert.True(throttle.TryEnter("u:a"));
        Assert.False(throttle.TryEnter("u:a"));
    }
}
