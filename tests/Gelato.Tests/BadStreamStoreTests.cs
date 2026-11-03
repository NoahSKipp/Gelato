using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class BadStreamStoreTests
{
    [Fact]
    public void MarkedSourcesExpireAndSurviveARestart()
    {
        var dir = Directory.CreateTempSubdirectory("gelato-bad");
        try
        {
            var path = Path.Combine(dir.FullName, "bad-streams.json");
            var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
            var file = new JsonFileStore<DateTime>(() => path, TimeSpan.FromMilliseconds(1));
            var store = new BadStreamStore(file, () => now);

            store.MarkBad("abc");
            Assert.True(store.IsBad("abc"));
            Assert.False(store.IsBad("other"));
            file.Flush();

            var reloaded = new BadStreamStore(new JsonFileStore<DateTime>(() => path), () => now.AddHours(5));
            Assert.True(reloaded.IsBad("abc"));

            var later = new BadStreamStore(new JsonFileStore<DateTime>(() => path), () => now.AddHours(6).AddMinutes(1));
            Assert.False(later.IsBad("abc"));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
