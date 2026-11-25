namespace Gelato.Services;

/// <summary>
/// Guards image addresses before they are stored or downloaded. Some addons
/// build a poster from a missing TMDB path and return an address like
/// ".../w500null", which is not blank but can never be fetched.
/// </summary>
public static class ArtUrl
{
    /// <summary>The address when it can be fetched, otherwise null.</summary>
    public static string? Clean(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("null", StringComparison.OrdinalIgnoreCase) || path.EndsWith("undefined", StringComparison.OrdinalIgnoreCase))
            return null;
        // A TMDB size folder with no file after it.
        var last = path[(path.LastIndexOf('/') + 1)..];
        if (last.Length == 0 || last == "original" || (last.Length > 1 && last[0] == 'w' && last[1..].All(char.IsAsciiDigit)))
            return null;
        return trimmed;
    }
}
