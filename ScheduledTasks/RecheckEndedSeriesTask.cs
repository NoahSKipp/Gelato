using MediaBrowser.Model.Tasks;

namespace Gelato.ScheduledTasks;

public sealed class RecheckEndedSeriesTask(GelatoManager manager) : IScheduledTask
{
    public string Name => "Re-check ended series";
    public string Key => "GelatoRecheckEndedSeries";

    public string Description =>
        "Re-checks series marked as ended for new seasons, so a show that comes back is picked up. The daily series sync only checks continuing series.";

    public string Category => "Gelato";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
            },
        ];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await manager
            .SyncSeriesTrees(Guid.Empty, cancellationToken, progress, includeEnded: true)
            .ConfigureAwait(false);
        progress.Report(100);
    }
}
