using System.Collections.Concurrent;
using System.Globalization;
using Gelato.Decorators;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// Gives Gelato movies and series a backdrop and logo when they have none.
/// Catalog sync only stores the poster, so most stubs open with an empty
/// header. Art comes from the addon's full meta first, then TMDB, and is
/// downloaded so the first view is fast. Titles with nothing anywhere are
/// marked and not tried again for <see cref="RetryAfter"/>.
/// </summary>
public sealed class ArtworkBackfillService(
    ILibraryManager libraryManager,
    Lazy<ProviderManagerDecorator> providerManager,
    ILogger<ArtworkBackfillService> log
)
{
    public const string TriedProviderId = "GelatoArtTried";
    public static readonly TimeSpan RetryAfter = TimeSpan.FromDays(30);

    private static readonly ConcurrentDictionary<Guid, byte> InFlight = new();
    private static readonly SemaphoreSlim OnOpenSlot = new(1);
    private static DateTime _lastOnOpen = DateTime.MinValue;

    public static bool NeedsArt(BaseItem item) =>
        item is Movie or Series
        && item.IsGelato()
        && !item.IsLocked
        && !item.HasImage(ImageType.Backdrop)
        && !TriedRecently(item);

    private static bool TriedRecently(BaseItem item)
    {
        var tried = item.GetProviderId(TriedProviderId);
        return DateTime.TryParse(tried, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            && DateTime.UtcNow - at < RetryAfter;
    }

    /// <summary>The stubs still without a backdrop, oldest first, at most <paramref name="limit"/>.</summary>
    public List<BaseItem> FindCandidates(int limit) =>
        libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                    Recursive = true,
                }
            )
            .Where(NeedsArt)
            .OrderBy(item => item.DateCreated)
            .Take(limit)
            .ToList();

    /// <summary>Fetches and stores missing art for one title. True when a backdrop was stored.</summary>
    public async Task<bool> FillAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (!NeedsArt(item) || !InFlight.TryAdd(item.Id, 0))
            return false;
        try
        {
            var stremio = GelatoPlugin.Instance?.Configuration.Stremio;
            string? backdrop = null, logo = null, thumb = null;
            if (stremio is not null)
            {
                try
                {
                    var meta = await stremio.GetMetaAsync(item).ConfigureAwait(false);
                    backdrop = meta?.Background;
                    logo = meta?.Logo;
                    thumb = meta?.LandscapePoster;
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "ArtworkBackfill: addon meta failed for {Name}", item.Name);
                }

                if (backdrop is null || logo is null)
                {
                    var tmdb = await stremio
                        .GetTmdbArtworkAsync(
                            item.GetProviderId(MetadataProvider.Tmdb),
                            item.GetProviderId(MetadataProvider.Imdb),
                            item is Movie,
                            item.GetPreferredMetadataLanguage(),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    backdrop ??= tmdb.Backdrop;
                    logo ??= tmdb.Logo;
                }
            }

            var stored = false;
            stored |= await SaveIfMissing(item, ImageType.Backdrop, backdrop, cancellationToken).ConfigureAwait(false);
            await SaveIfMissing(item, ImageType.Logo, logo, cancellationToken).ConfigureAwait(false);
            await SaveIfMissing(item, ImageType.Thumb, thumb, cancellationToken).ConfigureAwait(false);

            if (stored)
            {
                await libraryManager.UpdateImagesAsync(item).ConfigureAwait(false);
            }
            else
            {
                item.SetProviderId(TriedProviderId, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }
            return stored;
        }
        finally
        {
            InFlight.TryRemove(item.Id, out _);
        }
    }

    private async Task<bool> SaveIfMissing(BaseItem item, ImageType type, string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || item.HasImage(type))
            return false;
        try
        {
            await providerManager.Value.SaveImageDirect(item, url, type, null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "ArtworkBackfill: could not store {Type} for {Name}", type, item.Name);
            return false;
        }
    }

    /// <summary>
    /// A title opened without a backdrop fetches its own in the background:
    /// one at a time, at most one every two seconds, never twice at once.
    /// </summary>
    public void FillOnOpen(BaseItem item)
    {
        if (!NeedsArt(item) || InFlight.ContainsKey(item.Id))
            return;
        _ = Task.Run(async () =>
        {
            if (!await OnOpenSlot.WaitAsync(0).ConfigureAwait(false))
                return;
            try
            {
                var wait = _lastOnOpen.AddSeconds(2) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait).ConfigureAwait(false);
                _lastOnOpen = DateTime.UtcNow;
                await FillAsync(item, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "ArtworkBackfill: on-open fill failed for {Name}", item.Name);
            }
            finally
            {
                OnOpenSlot.Release();
            }
        });
    }
}
