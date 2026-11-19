using Gelato.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.ScheduledTasks;

/// <summary>
/// Read-only dry-run companion for <see cref="PurgeStreamArtworkTask"/>.
/// Scans Gelato stream rows for cached artwork and logs the exact item, file,
/// and byte counts that would be cleaned up without modifying anything.
/// </summary>
public sealed class PreviewStreamArtworkCleanupTask(
    ILibraryManager libraryManager,
    ILogger<PreviewStreamArtworkCleanupTask> log
) : IScheduledTask
{
    public string Name => "Preview Stream Artwork Cleanup (Dry Run)";
    public string Key => "GelatoPreviewStreamArtworkCleanup";

    public string Description =>
        "Scans Gelato stream rows for cached artwork and logs how many items, local files, and bytes would be freed without modifying the database or disk.";

    public string Category => "Gelato";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        PurgeStreamArtworkTask.RunAsync(
            libraryManager,
            writeGate: null,
            log,
            dryRun: true,
            progress,
            cancellationToken
        );
}

/// <summary>
/// Manual one-time cleanup task that clears artwork references from Gelato
/// stream rows and deletes their verified per-stream local image files on disk.
/// Primary items and their artwork are never touched.
/// </summary>
public sealed class PurgeStreamArtworkTask(
    ILibraryManager libraryManager,
    BackgroundWriteGate writeGate,
    ILogger<PurgeStreamArtworkTask> log
) : IScheduledTask
{
    private const int BatchSize = 50;
    private static readonly TimeSpan BatchPause = TimeSpan.FromMilliseconds(200);

    public string Name => "Purge Stream Artwork";
    public string Key => "GelatoPurgeStreamArtwork";

    public string Description =>
        "Clears artwork references from Gelato stream rows and deletes their verified per-stream local image files on disk. Primary items and their artwork are never touched.";

    public string Category => "Gelato";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        RunAsync(
            libraryManager,
            writeGate,
            log,
            dryRun: false,
            progress,
            cancellationToken
        );

    /// <summary>
    /// Verifies that <paramref name="imagePath"/> is a local file strictly inside
    /// the stream item's own metadata folder (<c>item.Id.ToString("N")</c>) and is
    /// not referenced by any primary library item.
    /// </summary>
    public static bool IsSafeStreamArtworkPath(
        BaseItem item,
        string? imagePath,
        ISet<string> primaryImagePaths
    )
    {
        if (
            item is null
            || item.Id == Guid.Empty
            || !item.IsStream()
            || item.IsPrimaryVersion()
            || string.IsNullOrWhiteSpace(imagePath)
        )
        {
            return false;
        }

        if (
            imagePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || imagePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || imagePath.Contains("://", StringComparison.Ordinal)
        )
        {
            return false;
        }

        if (!Path.IsPathRooted(imagePath))
        {
            return false;
        }

        var normalized = NormalizeLocalPath(imagePath);
        if (normalized is null)
        {
            return false;
        }

        if (primaryImagePaths.Contains(imagePath) || primaryImagePaths.Contains(normalized))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(normalized);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var idFolder = item.Id.ToString("N");
        var segments = directory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries
        );

        return segments.Any(segment =>
            string.Equals(segment, idFolder, StringComparison.OrdinalIgnoreCase)
        );
    }

    internal static async Task RunAsync(
        ILibraryManager libraryManager,
        BackgroundWriteGate? writeGate,
        ILogger log,
        bool dryRun,
        IProgress<double>? progress,
        CancellationToken cancellationToken
    )
    {
        var modeLabel = dryRun ? "dry run" : "purge";
        log.LogInformation("Stream artwork cleanup ({Mode}): scanning library", modeLabel);

        var primaryImagePaths = CollectPrimaryImagePaths(libraryManager);

        var streamQuery = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Tags = [GelatoManager.StreamTag],
            Recursive = true,
            GroupByPresentationUniqueKey = false,
            GroupBySeriesPresentationUniqueKey = false,
            CollapseBoxSetItems = false,
            // Skip Gelato's stream-hiding query filter so stream rows are returned.
            IsDeadPerson = true,
        };

        var scannedStreams = libraryManager.GetItemList(streamQuery);
        var totalScanned = scannedStreams.Count;

        var targets = scannedStreams
            .Where(item =>
                item.IsStream()
                && !item.IsPrimaryVersion()
                && item.ImageInfos is { Length: > 0 }
            )
            .ToList();

        var totalWithArtwork = targets.Count;
        var itemsUpdated = 0;
        var filesDeleted = 0;
        long bytesFreed = 0;
        var skippedUnsafePaths = 0;
        var processed = 0;

        for (var start = 0; start < totalWithArtwork; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = targets.Skip(start).Take(BatchSize).ToList();
            var filesToDelete = new List<(string Path, long Bytes)>();

            foreach (var item in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var itemFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var imageInfo in item.ImageInfos)
                {
                    var path = imageInfo?.Path;
                    if (!IsSafeStreamArtworkPath(item, path, primaryImagePaths))
                    {
                        skippedUnsafePaths++;
                        log.LogDebug(
                            "Stream artwork cleanup ({Mode}): skipping unsafe/shared path '{Path}' for stream item {ItemId}",
                            modeLabel,
                            path,
                            item.Id
                        );
                        continue;
                    }

                    var normalized = NormalizeLocalPath(path!);
                    if (normalized is null)
                    {
                        skippedUnsafePaths++;
                        continue;
                    }

                    if (itemFiles.Add(normalized))
                    {
                        TryAddExistingFile(normalized, filesToDelete);
                    }

                    var sidecar = normalized + ".url";
                    if (itemFiles.Add(sidecar))
                    {
                        TryAddExistingFile(sidecar, filesToDelete);
                    }
                }
            }

            if (dryRun)
            {
                filesDeleted += filesToDelete.Count;
                foreach (var entry in filesToDelete)
                {
                    bytesFreed += entry.Bytes;
                }
            }
            else
            {
                using (
                    writeGate is null
                        ? null
                        : await writeGate
                            .EnterAsync("Purge stream artwork", cancellationToken)
                            .ConfigureAwait(false)
                )
                {
                    foreach (var item in batch)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            item.ImageInfos = [];
                            await libraryManager
                                .UpdateItemAsync(
                                    item,
                                    item.GetParent(),
                                    ItemUpdateType.ImageUpdate,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            itemsUpdated++;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            log.LogWarning(
                                ex,
                                "Stream artwork cleanup: failed to clear ImageInfos for stream item {ItemId}",
                                item.Id
                            );
                        }
                    }
                }

                foreach (var (filePath, fileBytes) in filesToDelete)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                            filesDeleted++;
                            bytesFreed += fileBytes;
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(
                            ex,
                            "Stream artwork cleanup: failed to delete file '{Path}'",
                            filePath
                        );
                    }
                }
            }

            processed += batch.Count;
            if (totalWithArtwork > 0)
            {
                progress?.Report(Math.Min(100.0, 100.0 * processed / totalWithArtwork));
            }

            if (start + BatchSize < totalWithArtwork)
            {
                await Task.Delay(BatchPause, cancellationToken).ConfigureAwait(false);
            }
        }

        progress?.Report(100.0);

        var mb = bytesFreed / (1024.0 * 1024.0);
        if (dryRun)
        {
            log.LogInformation(
                "Stream artwork cleanup (dry run): scanned={Scanned}, withArtwork={WithArtwork}, wouldDeleteFiles={Files}, wouldFreeBytes={Bytes} ({Megabytes:F2} MB), skippedUnsafePaths={Skipped}",
                totalScanned,
                totalWithArtwork,
                filesDeleted,
                bytesFreed,
                mb,
                skippedUnsafePaths
            );
        }
        else
        {
            log.LogInformation(
                "Stream artwork cleanup (purge): scanned={Scanned}, withArtwork={WithArtwork}, updatedItems={Updated}, deletedFiles={Files}, freedBytes={Bytes} ({Megabytes:F2} MB), skippedUnsafePaths={Skipped}",
                totalScanned,
                totalWithArtwork,
                itemsUpdated,
                filesDeleted,
                bytesFreed,
                mb,
                skippedUnsafePaths
            );
        }
    }

    private static HashSet<string> CollectPrimaryImagePaths(ILibraryManager libraryManager)
    {
        var primaryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var primaryQuery = new InternalItemsQuery
        {
            IncludeItemTypes =
            [
                BaseItemKind.Movie,
                BaseItemKind.Episode,
                BaseItemKind.Series,
                BaseItemKind.Season,
                BaseItemKind.BoxSet,
            ],
            ExcludeTags = [GelatoManager.StreamTag],
            Recursive = true,
            GroupByPresentationUniqueKey = false,
            GroupBySeriesPresentationUniqueKey = false,
            CollapseBoxSetItems = false,
        };

        foreach (var item in libraryManager.GetItemList(primaryQuery))
        {
            if (item.IsStream() || item.ImageInfos is not { Length: > 0 })
            {
                continue;
            }

            foreach (var imageInfo in item.ImageInfos)
            {
                var path = imageInfo?.Path;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                primaryPaths.Add(path);
                if (Path.IsPathRooted(path))
                {
                    var normalized = NormalizeLocalPath(path);
                    if (normalized is not null)
                    {
                        primaryPaths.Add(normalized);
                    }
                }
            }
        }

        return primaryPaths;
    }

    private static void TryAddExistingFile(string path, List<(string Path, long Bytes)> files)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                files.Add((path, info.Length));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? NormalizeLocalPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }
}
