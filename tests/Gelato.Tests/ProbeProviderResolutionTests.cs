using System.Reflection;
using Gelato.Decorators;
using Gelato.Providers;
using Gelato.Services;
using Jellyfin.Data.Events;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gelato.Tests;

public class ProbeProviderResolutionTests
{
    public class NullProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType == typeof(LibraryOptions))
                return new LibraryOptions();
            return targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    private sealed class FakeProviderManager : IProviderManager
    {
        public IReadOnlyList<IMetadataProvider> Providers { get; set; } = [];
        public int GetMetadataProvidersCalls { get; private set; }

        public IEnumerable<IMetadataProvider<T>> GetMetadataProviders<T>(
            BaseItem item,
            LibraryOptions libraryOptions
        )
            where T : BaseItem
        {
            GetMetadataProvidersCalls++;
            return Providers.OfType<IMetadataProvider<T>>();
        }

        public IEnumerable<IMetadataProvider<T>> GetMetadataProviders<T>(
            BaseItem item,
            LibraryOptions libraryOptions,
            bool includeDisabled
        )
            where T : BaseItem
        {
            GetMetadataProvidersCalls++;
            return Providers.OfType<IMetadataProvider<T>>();
        }

        public event EventHandler<GenericEventArgs<BaseItem>> RefreshStarted { add { } remove { } }
        public event EventHandler<GenericEventArgs<BaseItem>> RefreshCompleted { add { } remove { } }
        public event EventHandler<GenericEventArgs<Tuple<BaseItem, double>>> RefreshProgress { add { } remove { } }

        public Task SaveImage(BaseItem item, string url, ImageType type, int? imageIndex, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveImage(BaseItem item, Stream source, string mimeType, ImageType type, int? imageIndex, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveImage(BaseItem item, string source, string mimeType, ImageType type, int? imageIndex, bool? saveLocallyWithMedia, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveImage(Stream source, string mimeType, string path) => Task.CompletedTask;
        public void QueueRefresh(Guid itemId, MetadataRefreshOptions options, RefreshPriority priority) { }
        public Task RefreshFullItem(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ItemUpdateType> RefreshSingleItem(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken) => Task.FromResult(ItemUpdateType.None);
        public void AddParts(IEnumerable<IImageProvider> imageProviders, IEnumerable<IMetadataService> metadataServices, IEnumerable<IMetadataProvider> metadataProviders, IEnumerable<IMetadataSaver> metadataSavers, IEnumerable<IExternalId> externalIds, IEnumerable<IExternalUrlProvider> externalUrlProviders) { }
        public Task<IEnumerable<RemoteImageInfo>> GetAvailableRemoteImages(BaseItem item, RemoteImageQuery query, CancellationToken cancellationToken) => Task.FromResult(Enumerable.Empty<RemoteImageInfo>());
        public IEnumerable<ImageProviderInfo> GetRemoteImageProviderInfo(BaseItem item) => [];
        public IEnumerable<IImageProvider> GetImageProviders(BaseItem item, ImageRefreshOptions refreshOptions) => [];
        public IEnumerable<IMetadataSaver> GetMetadataSavers(BaseItem item, LibraryOptions libraryOptions) => [];
        public MetadataPluginSummary[] GetAllMetadataPlugins() => [];
        public IEnumerable<ExternalUrl> GetExternalUrls(BaseItem item) => [];
        public IEnumerable<ExternalIdInfo> GetExternalIdInfos(IHasProviderIds item) => [];
        public Task SaveMetadataAsync(BaseItem item, ItemUpdateType updateType) => Task.CompletedTask;
        public Task SaveMetadataAsync(BaseItem item, ItemUpdateType updateType, IEnumerable<string> savers) => Task.CompletedTask;
        public MetadataOptions GetMetadataOptions(BaseItem item) => new();
        public Task<IEnumerable<RemoteSearchResult>> GetRemoteSearchResults<TItemType, TLookupType>(RemoteSearchQuery<TLookupType> searchInfo, CancellationToken cancellationToken)
            where TItemType : BaseItem, new()
            where TLookupType : ItemLookupInfo => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());
        public HashSet<Guid> GetRefreshQueue() => [];
        public void OnRefreshStart(BaseItem item) { }
        public void OnRefreshProgress(BaseItem item, double progress) { }
        public void OnRefreshComplete(BaseItem item) { }
        public double? GetRefreshProgress(Guid id) => null;
    }

    // Mirrors Jellyfin's native ProbeProvider, which implements ICustomMetadataProvider
    // for Video, Movie, and Episode (IMetadataProvider<T> is invariant in T).
    private sealed class FakeProbeProvider(string name)
        : ICustomMetadataProvider<Video>,
            ICustomMetadataProvider<Movie>,
            ICustomMetadataProvider<Episode>
    {
        public string Name { get; } = name;

        public Task<ItemUpdateType> FetchAsync(
            Video item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken
        ) => Task.FromResult(ItemUpdateType.None);

        public Task<ItemUpdateType> FetchAsync(
            Movie item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken
        ) => Task.FromResult(ItemUpdateType.None);

        public Task<ItemUpdateType> FetchAsync(
            Episode item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken
        ) => Task.FromResult(ItemUpdateType.None);
    }

    private static (
        MediaSourceManagerDecorator Decorator,
        ProviderManagerDecorator ProviderManager,
        FakeProviderManager InnerProviderManager
    ) CreateSut()
    {
        var innerProviderManager = new FakeProviderManager();
        var libraryManager = DispatchProxy.Create<ILibraryManager, NullProxy>();
        var appPaths = DispatchProxy.Create<IApplicationPaths, NullProxy>();

        var providerManager = new ProviderManagerDecorator(
            innerProviderManager,
            appPaths,
            new Lazy<ILibraryManager>(() => libraryManager),
            NullLogger<ProviderManagerDecorator>.Instance
        );

        var innerMediaSourceManager = DispatchProxy.Create<IMediaSourceManager, NullProxy>();
        var httpContextAccessor = new HttpContextAccessor();
        var itemRepo = new GelatoItemRepository(
            DispatchProxy.Create<IItemRepository, NullProxy>(),
            httpContextAccessor
        );
        var directoryService = DispatchProxy.Create<IDirectoryService, NullProxy>();
        var config = DispatchProxy.Create<IServerConfigurationManager, NullProxy>();
        var segmentManager = DispatchProxy.Create<IMediaSegmentManager, NullProxy>();

        var decorator = new MediaSourceManagerDecorator(
            innerMediaSourceManager,
            libraryManager,
            NullLogger<MediaSourceManagerDecorator>.Instance,
            httpContextAccessor,
            itemRepo,
            directoryService,
            config,
            new Lazy<GelatoManager>(() => throw new InvalidOperationException("Not used in test")),
            new Lazy<SubtitleProvider>(() => throw new InvalidOperationException("Not used in test")),
            segmentManager,
            new Lazy<ProviderManagerDecorator>(() => providerManager),
            new BadStreamStore(new JsonFileStore<DateTime>(() => Path.GetTempFileName()), () => DateTime.UtcNow),
            new WarmTracker(new JsonFileStore<WarmRecord>(() => Path.GetTempFileName()), () => DateTime.UtcNow),
            new ProbeGate()
        );

        return (decorator, providerManager, innerProviderManager);
    }

    [Fact]
    public void GetProbeProviderDoesNotLatchNullAndResolvesOnceProviderBecomesAvailable()
    {
        var (decorator, _, innerProviderManager) = CreateSut();
        var streamMovie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Heat",
            Tags = [GelatoManager.StreamTag],
        };
        streamMovie.SetProviderId("Stremio", "tt0113277");

        // Initially no providers registered — must return null without permanently latching null.
        Assert.Null(decorator.GetProbeProvider(streamMovie));
        Assert.Equal(1, innerProviderManager.GetMetadataProvidersCalls);

        // Once Jellyfin finishes registering its native Probe Provider, the next call picks it up.
        var probeProvider = new FakeProbeProvider("Probe Provider");
        innerProviderManager.Providers =
        [
            new FakeProbeProvider("Other Provider"),
            probeProvider,
        ];

        var resolved = decorator.GetProbeProvider(streamMovie);
        Assert.Same(probeProvider, resolved);
        Assert.Equal(2, innerProviderManager.GetMetadataProvidersCalls);

        // Subsequent calls reuse the cached non-null provider without re-querying.
        Assert.Same(probeProvider, decorator.GetProbeProvider(streamMovie));
        Assert.Equal(2, innerProviderManager.GetMetadataProvidersCalls);
    }

    [Fact]
    public void GetProbeProviderResolvesForStreamRowsWhileMetadataPipelineRemainsExcluded()
    {
        var (decorator, providerManager, innerProviderManager) = CreateSut();
        var probeProvider = new FakeProbeProvider("Probe Provider");
        innerProviderManager.Providers = [probeProvider];

        var streamEpisode = new Episode
        {
            Id = Guid.NewGuid(),
            Name = "Pilot",
            Tags = [GelatoManager.StreamTag],
        };
        streamEpisode.SetProviderId("Stremio", "tt0306414:1:1");

        // General metadata provider lookup must still exclude stream rows.
        Assert.Empty(
            providerManager.GetMetadataProviders<Episode>(streamEpisode, new LibraryOptions(), true)
        );

        // Direct probe provider lookup must bypass the stream-row filter and resolve Probe Provider.
        Assert.Same(probeProvider, decorator.GetProbeProvider(streamEpisode));
    }
}
