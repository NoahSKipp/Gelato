using Gelato.Config;

namespace Gelato.Services;

public sealed record WarmPolicy(
    int Version,
    int HoverDwellMs,
    int PlayFocusDwellMs,
    int SearchSettleMs,
    int SearchFocusDwellMs,
    int HomeWarmCount,
    int SecondSourceDelayMs,
    double NextEpisodeAtFraction
)
{
    public const int CurrentVersion = 1;

    public static WarmPolicy From(PluginConfiguration c) =>
        new(
            CurrentVersion,
            c.WarmHoverDwellMs,
            c.WarmPlayFocusDwellMs,
            c.WarmSearchSettleMs,
            c.WarmSearchFocusDwellMs,
            c.WarmHomeCount,
            c.WarmSecondSourceDelayMs,
            c.WarmNextEpisodeAtFraction
        );
}
