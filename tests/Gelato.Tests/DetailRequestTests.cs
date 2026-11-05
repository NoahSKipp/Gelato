using Gelato;
using Gelato.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Xunit;

namespace Gelato.Tests;

public class DetailRequestTests
{
    private static HttpContext Request(string method, string action, string query = "")
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.QueryString = new QueryString(query);
        http.SetEndpoint(
            new Endpoint(
                null,
                new EndpointMetadataCollection(new ControllerActionDescriptor { ActionName = action }),
                action
            )
        );
        return http;
    }

    [Fact]
    public void TheDetailPageDoesNotWaitForStreams()
    {
        Assert.True(Request("GET", "GetItem").IsDetailWithoutSources());
        Assert.True(Request("GET", "GetItems", "?ids=abc&fields=Overview,Genres").IsDetailWithoutSources());
    }

    [Fact]
    public void ThePickerAndPlayWaitForThem()
    {
        Assert.False(Request("GET", "GetItem", "?fields=MediaSources,OriginalTitle").IsDetailWithoutSources());
        Assert.False(Request("POST", "GetPostedPlaybackInfo").IsDetailWithoutSources());
        Assert.False(Request("GET", "GetVideoStream").IsDetailWithoutSources());
        Assert.False(((HttpContext?)null).IsDetailWithoutSources());
    }

    [Fact]
    public void StepTimerSumsRepeatedStepsInOrder()
    {
        var steps = new StepTimer();
        steps.Add("lookup", 12);
        steps.Add("meta", 30);
        steps.Add("lookup", 3);
        Assert.Equal("lookup 15 ms, meta 30 ms", steps.ToString());
    }
}
