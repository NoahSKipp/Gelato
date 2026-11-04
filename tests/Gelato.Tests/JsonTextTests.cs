using System.Text.Json;
using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class JsonTextTests
{
    [Fact]
    public void RawLineBreaksInsideStringsAreEscaped()
    {
        var raw = "{\"description\":\"Line one\r\nLine two\",\"n\":1}";
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(raw));
        var fixedJson = JsonText.EscapeControlCharsInStrings(raw);
        using var doc = JsonDocument.Parse(fixedJson);
        Assert.Equal("Line one\r\nLine two", doc.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public void WhitespaceBetweenValuesAndEscapedQuotesAreLeftAlone()
    {
        var raw = "{\r\n  \"a\": \"say \\\"hi\\\"\tnow\",\r\n  \"b\": \"x\\\\\"\r\n}";
        using var doc = JsonDocument.Parse(JsonText.EscapeControlCharsInStrings(raw));
        Assert.Equal("say \"hi\"\tnow", doc.RootElement.GetProperty("a").GetString());
        Assert.Equal("x\\", doc.RootElement.GetProperty("b").GetString());
    }

    [Fact]
    public void CleanJsonComesBackAsTheSameString()
    {
        var raw = "{\"a\":\"b\"}";
        Assert.Same(raw, JsonText.EscapeControlCharsInStrings(raw));
    }

    [Fact]
    public void OtherControlCharactersBecomeUnicodeEscapes()
    {
        var raw = "{\"a\":\"x\u0001y\"}";
        Assert.Equal("{\"a\":\"x\\u0001y\"}", JsonText.EscapeControlCharsInStrings(raw));
    }

    [Fact]
    public void AddonUrlsLoseTheirConfiguration()
    {
        var baseUrl = "https://aio.example/stremio/abc123secretconfig";
        var shown = JsonText.RedactAddonUrl(baseUrl + "/stream/movie/tt1.json", baseUrl);
        Assert.Equal("aio.example/…/stream/movie/tt1.json", shown);
        Assert.DoesNotContain("secret", shown);
        Assert.Equal("other.example/…", JsonText.RedactAddonUrl("https://other.example/k/x", baseUrl));
    }
}
