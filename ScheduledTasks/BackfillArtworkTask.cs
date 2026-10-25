using Gelato.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.ScheduledTasks;

/// <summary>
/// Fills in backdrops and logos for Gelato titles that have none, a few
/// hundred per run, one at a time with a pause between, so it never competes
/// with the library scan or the series tree sync for the database.
/// </summary>
public sealed class BackfillArtworkTask(ArtworkBackfillService backfill, ILogger<BackfillArtworkTask> log)
    : IScheduledTask
{
    private const int PerRun = 500;
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(1500);

    public string Name => "Backfill artwork";
    public string Key => "GelatoBackfillArtwork";

    public string Description =>
        "Gives Gelato movies and series without a backdrop one from their addon or TMDB, up to 500 per run. Titles with no art anywhere are retried after 30 days.";

    public string Category => "Gelato";

    // 09:10, clear of the library scan (02:00), the series tree sync (04:15)
    // and the media segment scan (05:00, about two and a half hours).
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = new TimeSpan(9, 10, 0).Ticks,
            },
        ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var items = backfill.FindCandidates(PerRun);
        var filled = 0;
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await backfill.FillAsync(items[i], cancellationToken).ConfigureAwait(false))
                    filled++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Backfill artwork: failed for {Name}", items[i].Name);
            }
            progress.Report(100.0 * (i + 1) / items.Count);
            await Task.Delay(Pause, cancellationToken).ConfigureAwait(false);
        }
        log.LogInformation("Backfill artwork: {Filled} of {Count} titles got a backdrop", filled, items.Count);
        progress.Report(100);
    }
}
