using System.Text;

namespace Gelato.Services;

public static class JsonText
{
    // Some addons put raw carriage returns and line breaks inside JSON
    // strings ("'0x0D' is invalid within a JSON string"). Escapes control
    // characters inside strings only; returns the input when there are none.
    public static string EscapeControlCharsInStrings(string json)
    {
        StringBuilder? sb = null;
        var inString = false;
        var escaped = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (inString && !escaped && c < 0x20)
            {
                sb ??= new StringBuilder(json.Length + 16).Append(json, 0, i);
                sb.Append(
                    c switch
                    {
                        '\r' => "\\r",
                        '\n' => "\\n",
                        '\t' => "\\t",
                        _ => "\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
                    }
                );
                continue;
            }

            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (c == '\\')
                    escaped = true;
                else if (c == '"')
                    inString = false;
            }
            else if (c == '"')
            {
                inString = true;
            }
            sb?.Append(c);
        }
        return sb?.ToString() ?? json;
    }

    // An addon address carries its configuration (debrid keys included) in
    // the path: logs only get the host and the resource after it.
    public static string RedactAddonUrl(string url, string baseUrl)
    {
        if (!string.IsNullOrEmpty(baseUrl) && url.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase))
        {
            var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.Host : "addon";
            return host + "/…" + url[baseUrl.TrimEnd('/').Length..];
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var v) ? v.Host + "/…" : "addon";
    }
}
