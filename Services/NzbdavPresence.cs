using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

// Whether nzbdav still holds a release: one PROPFIND (depth 1) of its
// content folder and of each category in it, cached for a minute. With no
// nzbdav configured nothing can be told and callers get null.
public sealed class NzbdavPresence(ILogger<NzbdavPresence> log)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private const int MaxCategories = 8;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private HashSet<string>? _releases;
    private DateTime _fetchedAt;
    private bool _warnedOnce;

    public async Task<Func<string, bool?>> SnapshotAsync(CancellationToken ct)
    {
        var releases = await ReleasesAsync(ct).ConfigureAwait(false);
        if (releases is null)
            return _ => null;
        return release => releases.Contains(NormalizeRelease(release));
    }

    public static string NormalizeRelease(string name)
    {
        var trimmed = name.Trim().TrimEnd('/');
        var ext = Path.GetExtension(trimmed);
        if (ext.Length is >= 3 and <= 5 && !ext.Contains(' '))
            trimmed = trimmed[..^ext.Length];
        return trimmed.ToLowerInvariant();
    }

    // Folder names in a WebDAV multistatus answer, the requested folder left out.
    public static List<string> ParseFolders(string xml, string requestedPath)
    {
        XNamespace dav = "DAV:";
        var doc = XDocument.Parse(xml);
        var self = requestedPath.TrimEnd('/');
        var folders = new List<string>();
        foreach (var response in doc.Descendants(dav + "response"))
        {
            var href = response.Element(dav + "href")?.Value;
            if (string.IsNullOrEmpty(href))
                continue;
            var path = Uri.UnescapeDataString(
                href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(href, UriKind.Absolute, out var abs)
                    ? abs.AbsolutePath
                    : href
            ).TrimEnd('/');
            if (path.Length == 0 || string.Equals(path, self, StringComparison.Ordinal))
                continue;
            var isFolder = response.Descendants(dav + "collection").Any();
            if (!isFolder)
                continue;
            folders.Add(path[(path.LastIndexOf('/') + 1)..]);
        }
        return folders;
    }

    private async Task<HashSet<string>?> ReleasesAsync(CancellationToken ct)
    {
        var cfg = GelatoPlugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(cfg.NzbdavUrl))
            return null;
        if (_releases is not null && DateTime.UtcNow - _fetchedAt < CacheFor)
            return _releases;

        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_releases is not null && DateTime.UtcNow - _fetchedAt < CacheFor)
                return _releases;

            var root = (cfg.NzbdavContentPath ?? "/content").TrimEnd('/') + "/";
            var releases = new HashSet<string>(StringComparer.Ordinal);
            var categories = await ListAsync(cfg, root, ct).ConfigureAwait(false);
            foreach (var category in categories.Take(MaxCategories))
            {
                var path = root + Uri.EscapeDataString(category) + "/";
                foreach (var release in await ListAsync(cfg, path, ct).ConfigureAwait(false))
                    releases.Add(NormalizeRelease(release));
            }
            _releases = releases;
            _fetchedAt = DateTime.UtcNow;
            return releases;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (!_warnedOnce)
            {
                _warnedOnce = true;
                // The address carries credentials in some setups: never logged.
                log.LogWarning("nzbdav could not be listed ({Error}); warm state skips the release check", ex.GetType().Name);
            }
            return null;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private static async Task<List<string>> ListAsync(
        Config.PluginConfiguration cfg,
        string path,
        CancellationToken ct
    )
    {
        var url = cfg.NzbdavUrl!.TrimEnd('/') + path;
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
        request.Headers.Add("Depth", "1");
        if (!string.IsNullOrEmpty(cfg.NzbdavUser))
        {
            var token = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(cfg.NzbdavUser + ":" + (cfg.NzbdavPassword ?? ""))
            );
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseFolders(xml, path);
    }
}
