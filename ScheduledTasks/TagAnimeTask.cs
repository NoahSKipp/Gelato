using MediaBrowser.Model.Tasks;

namespace Gelato.ScheduledTasks;

public sealed class TagAnimeTask(GelatoManager manager) : IScheduledTask
{
    public string Name => "Tag anime";
    public string Key => "GelatoTagAnime";

    public string Description =>
        "Tags anime movies and series with \"anime\" so clients can keep them out of other rows. New imports are tagged as they arrive; this catches everything already in the library.";

    public string Category => "Gelato";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks,
            },
        ];
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        manager.TagAnime(cancellationToken, progress);
        progress.Report(100);
        return Task.CompletedTask;
    }
}
