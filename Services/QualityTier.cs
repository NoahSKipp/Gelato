using System.Text.RegularExpressions;

namespace Gelato.Services;

// A source's resolution and HDR class as one stable string, for example
// "2160p-dv", "1080p-hdr" or "720p-sdr", so clients can tell whether two
// sources are the same quality without reading their names.
public static partial class QualityTier
{
    public const string Unknown = "unknown";

    public static string Of(int? height, string? hdrHint, params string?[] labels)
    {
        var text = string.Join(' ', labels.Where(l => !string.IsNullOrEmpty(l)));
        var resolution = height is > 0 ? FromHeight(height.Value) : FromLabel(text);
        if (resolution is null)
            return Unknown;
        return resolution + "-" + HdrClass(hdrHint + " " + text);
    }

    private static string FromHeight(int height) =>
        height switch
        {
            >= 1800 => "2160p",
            >= 1300 => "1440p",
            >= 900 => "1080p",
            >= 650 => "720p",
            _ => "sd",
        };

    private static string? FromLabel(string text)
    {
        if (Uhd().IsMatch(text))
            return "2160p";
        if (Qhd().IsMatch(text))
            return "1440p";
        if (Fhd().IsMatch(text))
            return "1080p";
        if (Hd().IsMatch(text))
            return "720p";
        if (Sd().IsMatch(text))
            return "sd";
        return null;
    }

    private static string HdrClass(string text)
    {
        if (DolbyVision().IsMatch(text))
            return "dv";
        if (Hdr().IsMatch(text))
            return "hdr";
        return "sdr";
    }

    [GeneratedRegex(@"(?<![a-z0-9])(2160p|4k|uhd)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Uhd();

    [GeneratedRegex(@"(?<![a-z0-9])(1440p|2k|qhd)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Qhd();

    [GeneratedRegex(@"(?<![a-z0-9])(1080[pi]|fhd)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Fhd();

    [GeneratedRegex(@"(?<![a-z0-9])(720p|hd)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Hd();

    [GeneratedRegex(@"(?<![a-z0-9])(576p|480p|360p|sd|dvdrip)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Sd();

    [GeneratedRegex(@"(?<![a-z0-9])(dv|dovi|dolby[ ._-]?vision)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex DolbyVision();

    [GeneratedRegex(@"(?<![a-z0-9])(hdr10\+?|hdr|hlg|pq|smpte2084|bt2020)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Hdr();
}
