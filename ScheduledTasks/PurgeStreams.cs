using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.ScheduledTasks;

public sealed class PurgeGelatoStreamsTask(
    ILibraryManager libraryManager,
    ILogger<PurgeGelatoStreamsTask> log,
    GelatoManager manager,
    Gelato.Services.BackgroundWriteGate writeGate
) : IScheduledTask
{
    public string Name => "Purge streams";
    public string Key => "PurgeGelatoStreamsTask";
    public string Description => "Removes all stremio streams";
    public string Category => "Gelato Maintenance";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromDays(7).Ticks,
            },
        ];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        log.LogInformation("purging streams");

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true,
            HasAnyProviderId = new Dictionary<string, string>
            {
                { "Stremio", string.Empty },
                { "stremio", string.Empty },
            },
            IsDeadPerson = true,
        };

        var streams = libraryManager
            .GetItemList(query)
            .OfType<Video>()
            .Where(v => v.IsStream())
            .ToArray();

        var total = streams.Length;

        // Stream rows have no files behind them; a few hundred per delete
        // instead of a transaction each, one batch at a time.
        const int batchSize = 200;
        for (var start = 0; start < total; start += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = streams.Skip(start).Take(batchSize).Cast<BaseItem>().ToList();
            using (await writeGate.EnterAsync("Purge streams", cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    libraryManager.DeleteItemsUnsafeFast(batch);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Failed to delete {Count} streams", batch.Count);
                }
            }
            progress?.Report(Math.Min(100.0, 100.0 * (start + batch.Count) / total));
        }

        progress?.Report(100.0);
        manager.ClearCache();

        log.LogInformation("stream purge completed");
    }
}
