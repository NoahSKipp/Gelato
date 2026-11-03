using System.Globalization;
using System.Text.RegularExpressions;

namespace Gelato.Services;

// AIOStreams answers an unresolvable release with a short placeholder clip
// from the same proxy address instead of an error: mp4, H.264 720p, 1 fps,
// about 1 kb/s, exactly 2 minutes, no audio.
public static partial class PlaceholderFingerprint
{
    public static string? Detect(
        double? probedSeconds,
        double? expectedSeconds,
        double? averageFrameRate,
        bool hasAudio,
        long? bitrate
    )
    {
        var expected = expectedSeconds ?? 0;
        if (probedSeconds is double probed && probed > 0)
        {
            if (probed < 600 && expected >= 1200)
                return "runs well short of the title";
            if (probed <= 180 && expected >= 600)
                return "short clip";
        }

        if (
            averageFrameRate is > 0 and <= 2
            && !hasAudio
            && bitrate is > 0 and < 50_000
        )
        {
            return "silent slideshow";
        }

        return null;
    }

    // A runtime in a stream's label, only where the label marks it as one:
    // "⏱ 59m", "⏱️ 1h 23m", "⏱ 59:00", "Duration: 1:23:45".
    public static double? ParseLabelRuntime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = LabelRuntime().Match(text);
        if (!match.Success)
            return null;

        var value = match.Groups["value"].Value.Trim();
        var clock = ClockRuntime().Match(value);
        if (clock.Success)
        {
            var parts = value.Split(':').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            var seconds = parts.Length == 3
                ? parts[0] * 3600 + parts[1] * 60 + parts[2]
                : parts[0] * 60 + parts[1];
            return seconds > 0 ? seconds : null;
        }

        var hm = HoursMinutes().Match(value);
        if (!hm.Success)
            return null;
        var hours = hm.Groups["h"].Success ? int.Parse(hm.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = hm.Groups["m"].Success ? int.Parse(hm.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var total = hours * 3600 + minutes * 60;
        return total > 0 ? total : null;
    }

    [GeneratedRegex(@"(?:⏱️?|⌚|🕒|\bDuration\b:?|\bRuntime\b:?)\s*(?<value>\d{1,2}:\d{2}(?::\d{2})?|(?:\d{1,2}\s*h)?\s*(?:\d{1,3}\s*m(?:in)?)?)", RegexOptions.IgnoreCase)]
    private static partial Regex LabelRuntime();

    [GeneratedRegex(@"^\d{1,2}:\d{2}(?::\d{2})?$")]
    private static partial Regex ClockRuntime();

    [GeneratedRegex(@"^(?:(?<h>\d{1,2})\s*h)?\s*(?:(?<m>\d{1,3})\s*m(?:in)?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex HoursMinutes();
}
