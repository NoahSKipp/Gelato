using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

// ffmpeg cannot read gelato:// stubs or remote stream rows, so trickplay and
// chapter image extraction only produced a one minute failure per title.
public static class RemoteVideo
{
    public static bool Skip(BaseItem item) =>
        item.IsGelato()
        && (
            item.HasStreamTag()
            || string.IsNullOrEmpty(item.Path)
            || item.Path.Contains("://", StringComparison.Ordinal)
        );
}

public sealed class TrickplayManagerDecorator(
    ITrickplayManager inner,
    ILogger<TrickplayManagerDecorator> log
) : ITrickplayManager
{
    public Task RefreshTrickplayDataAsync(
        Video video,
        bool replace,
        LibraryOptions libraryOptions,
        CancellationToken cancellationToken
    )
    {
        if (RemoteVideo.Skip(video))
        {
            log.LogTrace("Skipping trickplay for Gelato item {Id}", video.Id);
            return Task.CompletedTask;
        }
        return inner.RefreshTrickplayDataAsync(video, replace, libraryOptions, cancellationToken);
    }

    public Task MoveGeneratedTrickplayDataAsync(
        Video video,
        LibraryOptions libraryOptions,
        CancellationToken cancellationToken
    ) =>
        RemoteVideo.Skip(video)
            ? Task.CompletedTask
            : inner.MoveGeneratedTrickplayDataAsync(video, libraryOptions, cancellationToken);

    public TrickplayInfo CreateTiles(
        IReadOnlyList<string> images,
        int width,
        TrickplayOptions options,
        string outputDir
    ) => inner.CreateTiles(images, width, options, outputDir);

    public Task<Dictionary<int, TrickplayInfo>> GetTrickplayResolutions(Guid itemId) =>
        inner.GetTrickplayResolutions(itemId);

    public Task<IReadOnlyList<TrickplayInfo>> GetTrickplayItemsAsync(int limit, int offset) =>
        inner.GetTrickplayItemsAsync(limit, offset);

    public Task SaveTrickplayInfo(TrickplayInfo info) => inner.SaveTrickplayInfo(info);

    public Task DeleteTrickplayDataAsync(Guid itemId, CancellationToken cancellationToken) =>
        inner.DeleteTrickplayDataAsync(itemId, cancellationToken);

    public Task<Dictionary<string, Dictionary<int, TrickplayInfo>>> GetTrickplayManifest(
        BaseItem item
    ) => inner.GetTrickplayManifest(item);

    public Task<string> GetTrickplayTilePathAsync(
        BaseItem item,
        int width,
        int index,
        bool saveWithMedia
    ) => inner.GetTrickplayTilePathAsync(item, width, index, saveWithMedia);

    public string GetTrickplayDirectory(
        BaseItem item,
        int tileWidth,
        int tileHeight,
        int width,
        bool saveWithMedia = false
    ) => inner.GetTrickplayDirectory(item, tileWidth, tileHeight, width, saveWithMedia);

    public Task<string?> GetHlsPlaylist(Guid itemId, int width, string? apiKey) =>
        inner.GetHlsPlaylist(itemId, width, apiKey);
}

public sealed class ChapterManagerDecorator(IChapterManager inner) : IChapterManager
{
    public bool Supports(BaseItem item) => inner.Supports(item);

    public void SaveChapters(BaseItem item, IReadOnlyList<ChapterInfo> chapters) =>
        inner.SaveChapters(item, chapters);

    public ChapterInfo? GetChapter(Guid baseItemId, int index) =>
        inner.GetChapter(baseItemId, index);

    public IReadOnlyList<ChapterInfo> GetChapters(Guid baseItemId) =>
        inner.GetChapters(baseItemId);

    public Task<bool> RefreshChapterImages(
        Video video,
        IDirectoryService directoryService,
        IReadOnlyList<ChapterInfo> chapters,
        bool extractImages,
        bool saveChapters,
        CancellationToken cancellationToken
    ) =>
        inner.RefreshChapterImages(
            video,
            directoryService,
            chapters,
            extractImages && !RemoteVideo.Skip(video),
            saveChapters,
            cancellationToken
        );

    public Task DeleteChapterDataAsync(Guid itemId, CancellationToken cancellationToken) =>
        inner.DeleteChapterDataAsync(itemId, cancellationToken);
}
