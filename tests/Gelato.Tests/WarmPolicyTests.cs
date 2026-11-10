using System.Text.Json;
using Gelato.Config;
using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class WarmPolicyTests
{
    [Fact]
    public void DefaultsMatchWhatClientsDoToday()
    {
        var json = JsonSerializer.Serialize(WarmPolicy.From(new PluginConfiguration()));
        Assert.Equal(
            """{"Version":1,"HoverDwellMs":600,"PlayFocusDwellMs":1000,"SearchSettleMs":600,"SearchFocusDwellMs":400,"HomeWarmCount":6,"SecondSourceDelayMs":1500,"NextEpisodeAtFraction":0}""",
            json
        );
    }

    [Fact]
    public void FollowsTheConfiguration()
    {
        var cfg = new PluginConfiguration { WarmHoverDwellMs = 900, WarmHomeCount = 3 };
        var policy = WarmPolicy.From(cfg);
        Assert.Equal(900, policy.HoverDwellMs);
        Assert.Equal(3, policy.HomeWarmCount);
    }
}
