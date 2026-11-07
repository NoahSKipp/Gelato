using System.Collections.Concurrent;
using Gelato.Config;
using Gelato.Filters;
using Gelato.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Gelato;

public class GelatoPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<GelatoPlugin> _log;
    private readonly GelatoManager _manager;
    private ConcurrentDictionary<Guid, PluginConfiguration> UserConfigs { get; } = new();
    private readonly GelatoStremioProviderFactory _stremioFactory;
    public PalcoCacheService PalcoCache { get; } // Migrated Palco Cache Service
    private readonly TmdbSearch? _tmdb;
    private readonly IDtoService? _dtoService;
    private readonly IServerConfigurationManager? _serverConfig;

    public GelatoPlugin(
        IApplicationPaths applicationPaths,
        GelatoManager manager,
        IXmlSerializer xmlSerializer,
        ILogger<GelatoPlugin> log,
        GelatoStremioProviderFactory stremioFactory,
        PalcoCacheService palcoCache,
        TmdbSearch? tmdb = null,
        IDtoService? dtoService = null,
        IServerConfigurationManager? serverConfig = null
    )
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _log = log;
        _manager = manager;
        _stremioFactory = stremioFactory;
        PalcoCache = palcoCache;
        _tmdb = tmdb;
        _dtoService = dtoService;
        _serverConfig = serverConfig;
    }

    public static GelatoPlugin? Instance { get; private set; }

    // Event fired when the plugin configuration is updated via UpdateConfiguration
    public static new event Action<PluginConfiguration>? ConfigurationChanged;

    public override string Name => "Gelato";
    public override Guid Id => Guid.Parse("94EA4E14-8163-4989-96FE-0A2094BC2D6A");
    public override string Description => "on-demand MediaSources and optional image suppression.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;
        yield return new PluginPageInfo
        {
            Name = "config",
            EnableInMainMenu = true,
            EmbeddedResourcePath = prefix + ".Config.config.html",
        };
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        var cfg = (PluginConfiguration)configuration;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISABLE_P2P")))
        {
            cfg.P2PEnabled = false;
        }
        base.UpdateConfiguration(cfg);

        _manager.ClearCache();
        _stremioFactory.ClearCache();
        UserConfigs.Clear();

        // Notify subscribers that configuration changed
        try
        {
            ConfigurationChanged?.Invoke(cfg);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error while invoking ConfigurationChanged event");
        }
    }

    public PluginConfiguration GetConfig(Guid userId)
    {
        try
        {
            return UserConfigs.GetOrAdd(
                userId,
                _ =>
                {
                    var cfg = Instance?.Configuration;
                    if (userId != Guid.Empty)
                    {
                        var userConfig = Instance?.Configuration.UserConfigs.FirstOrDefault(u =>
                            u.UserId == userId
                        );
                        cfg =
                            userConfig?.ApplyOverrides(Instance?.Configuration)
                            ?? Instance?.Configuration;
                    }
                    var stremio = _stremioFactory.Create(cfg);
                    cfg.Stremio = stremio;
                    cfg.MovieFolder = _manager.TryGetMovieFolder(cfg);
                    cfg.SeriesFolder = _manager.TryGetSeriesFolder(cfg);
                    return cfg;
                }
            );
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error getting config");
            return new PluginConfiguration();
        }
    }

    public static void RegisterVirtualTitle(
        Guid guid,
        string kind,
        string tmdbId,
        string? title = null,
        int? year = null,
        string? imdbId = null,
        string? poster = null,
        IReadOnlyList<string>? genres = null
    )
    {
        if (Instance == null) return;
        var isMovie = kind.Equals("movie", StringComparison.OrdinalIgnoreCase);
        var id = !string.IsNullOrWhiteSpace(imdbId) ? imdbId : $"tmdb:{tmdbId}";
        var meta = new StremioMeta
        {
            Id = id,
            ImdbId = !string.IsNullOrWhiteSpace(imdbId) ? imdbId : null,
            Type = isMovie ? StremioMediaType.Movie : StremioMediaType.Series,
            Name = title,
            Year = year,
            Poster = poster,
            Genres = genres != null ? new List<string>(genres) : null,
        };
        Instance._manager.SaveStremioMeta(guid, meta);
    }

    public async Task<string?> ResolveImdbIdAsync(string kind, int tmdbId, CancellationToken ct = default)
    {
        if (_tmdb == null) return null;
        return await _tmdb.GetImdbIdAsync(kind, tmdbId, ct).ConfigureAwait(false);
    }

    public async Task<StremioMeta?> FetchTmdbMetaAsync(string kind, int tmdbId, CancellationToken ct = default)
    {
        if (_tmdb == null) return null;
        return await _tmdb.FetchTmdbMetaAsync(kind, tmdbId, ct).ConfigureAwait(false);
    }

    public static async Task<List<BaseItemDto>> DiscoverAsync(
        string? genre = null,
        string? types = null,
        int page = 1,
        string? language = null,
        CancellationToken ct = default
    )
    {
        if (Instance == null || Instance._tmdb == null || Instance._dtoService == null)
        {
            return new List<BaseItemDto>();
        }
        return await Instance.DiscoverInternalAsync(genre, types, page, language, ct).ConfigureAwait(false);
    }

    private string MetadataLanguage()
    {
        var c = _serverConfig?.Configuration;
        var lang = string.IsNullOrWhiteSpace(c?.PreferredMetadataLanguage) ? "en" : c.PreferredMetadataLanguage;
        return string.IsNullOrWhiteSpace(c?.MetadataCountryCode) || lang.Contains('-')
            ? lang
            : lang + "-" + c.MetadataCountryCode.ToUpperInvariant();
    }

    private async Task<List<BaseItemDto>> DiscoverInternalAsync(
        string? genre,
        string? types,
        int page,
        string? language,
        CancellationToken ct
    )
    {
        var cfg = Instance?.Configuration;
        var wantMovies = true;
        var wantSeries = true;

        if (!string.IsNullOrWhiteSpace(types))
        {
            var t = types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var hasMovie = t.Any(x => x.Equals("movie", StringComparison.OrdinalIgnoreCase) || x.Equals("movies", StringComparison.OrdinalIgnoreCase));
            var hasSeries = t.Any(x => x.Equals("series", StringComparison.OrdinalIgnoreCase) || x.Equals("tv", StringComparison.OrdinalIgnoreCase) || x.Equals("show", StringComparison.OrdinalIgnoreCase) || x.Equals("shows", StringComparison.OrdinalIgnoreCase));
            if (hasMovie || hasSeries)
            {
                wantMovies = hasMovie;
                wantSeries = hasSeries;
            }
        }

        var lang = string.IsNullOrWhiteSpace(language) ? MetadataLanguage() : language.Trim();
        var p = page < 1 ? 1 : page;
        var result = await _tmdb!.DiscoverAsync(genre ?? string.Empty, wantMovies, wantSeries, lang, p, ct).ConfigureAwait(false);

        var metas = result.Metas;
        if (cfg is { FilterUnreleased: true })
        {
            metas = metas.Where(m => m.IsReleased(cfg.FilterUnreleasedBufferDays)).ToList();
        }

        var options = new DtoOptions(false)
        {
            Fields = SearchActionFilter.ResultFields,
            EnableImages = true,
            EnableUserData = false,
        };

        var dtos = new List<BaseItemDto>(metas.Count);
        foreach (var meta in metas)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var baseItem = _manager.IntoBaseItem(meta);
            if (baseItem is null)
            {
                continue;
            }

            var dto = _dtoService!.GetBaseItemDto(baseItem, options);
            SearchActionFilter.FillSkippedFields(dto, baseItem);
            var stremioUri = StremioUri.FromBaseItem(baseItem);
            if (stremioUri is null)
            {
                continue;
            }
            dto.Id = stremioUri.ToGuid();
            _manager.SaveStremioMeta(dto.Id, meta);
            dtos.Add(dto);
        }

        return dtos;
    }
}
