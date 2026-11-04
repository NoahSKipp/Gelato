using System.Reflection;
using Gelato.Decorators;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Gelato.Tests;

public class ItemRepositoryCompatTests
{
    public class Inner : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("not expected: " + targetMethod?.Name);
    }

    [Fact]
    public void GetTagNamesHasTheSignatureJellyfinTwelveOneAsksFor()
    {
        var method = typeof(GelatoItemRepository).GetMethod("GetTagNames", [typeof(InternalItemsQuery)]);
        Assert.NotNull(method);
        Assert.True(method!.IsPublic);
        // Only a virtual method can be mapped onto the 12.1 interface member.
        Assert.True(method.IsVirtual);
        Assert.False(method.IsFinal);
        Assert.Equal(typeof(IReadOnlyList<string>), method.ReturnType);
    }

    [Fact]
    public void OnTwelveZeroGetTagNamesAnswersWithoutCallingTheInnerRepository()
    {
        // The test builds against Jellyfin 12.0, where the interface has no GetTagNames.
        Assert.Null(typeof(IItemRepository).GetMethod("GetTagNames"));
        var inner = DispatchProxy.Create<IItemRepository, Inner>();
        var repo = new GelatoItemRepository(inner, new HttpContextAccessor());
        Assert.Empty(repo.GetTagNames(new InternalItemsQuery()));
    }
}
