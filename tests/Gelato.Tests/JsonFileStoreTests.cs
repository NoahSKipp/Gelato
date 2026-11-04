using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class JsonFileStoreTests
{
    [Fact]
    public async Task ClearingEveryEntryAlsoEmptiesTheFile()
    {
        var dir = Directory.CreateTempSubdirectory("gelato-store");
        var path = Path.Combine(dir.FullName, "stream-sync.json");
        try
        {
            var store = new JsonFileStore<DateTime>(() => path, TimeSpan.FromMilliseconds(10));
            store.Set("user:title", DateTime.UtcNow);
            store.Set("user:other", DateTime.UtcNow);
            store.RemoveWhere((_, _) => true);
            Assert.False(store.TryGet("user:title", out _));
            await Task.Delay(200);

            var reloaded = new JsonFileStore<DateTime>(() => path);
            Assert.False(reloaded.TryGet("user:title", out _));
            Assert.False(reloaded.TryGet("user:other", out _));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
