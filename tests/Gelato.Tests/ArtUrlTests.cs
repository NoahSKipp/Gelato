using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class ArtUrlTests
{
    [Theory]
    [InlineData("https://image.tmdb.org/t/p/w500/abc123.jpg")]
    [InlineData("https://image.tmdb.org/t/p/original/xyz.png")]
    [InlineData("http://img.example.com/poster?id=5")]
    public void KeepsFetchableAddresses(string url) => Assert.Equal(url, ArtUrl.Clean(url));

    [Theory]
    [InlineData("https://image.tmdb.org/t/p/w500null")]
    [InlineData("https://image.tmdb.org/t/p/originalnull")]
    [InlineData("https://image.tmdb.org/t/p/w500/null")]
    [InlineData("https://image.tmdb.org/t/p/w500/undefined")]
    [InlineData("https://image.tmdb.org/t/p/w500")]
    [InlineData("https://image.tmdb.org/t/p/original/")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://host/poster.jpg")]
    public void DropsAddressesThatCannotBeFetched(string url) => Assert.Null(ArtUrl.Clean(url));

    [Fact]
    public void NullStaysNull() => Assert.Null(ArtUrl.Clean(null));
}
