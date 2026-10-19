using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ManiDispatch.Infrastructure.Core;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

public class ForwardAuthorizationHandlerTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (HttpClient Client, CaptureHandler Capture) Build(HttpContext? context)
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(context);
        var capture = new CaptureHandler();
        var handler = new ForwardAuthorizationHandler(accessor.Object) { InnerHandler = capture };
        return (new HttpClient(handler), capture);
    }

    [Fact]
    public async Task ForwardsTheIncomingAuthorizationHeaderToCore()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer token-del-usuario";
        var (client, capture) = Build(context);

        await client.GetAsync("http://core.test/x");

        Assert.Equal("Bearer", capture.Seen!.Headers.Authorization!.Scheme);
        Assert.Equal("token-del-usuario", capture.Seen.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task WithoutAnIncomingRequest_SendsNoAuthorization()
    {
        var (client, capture) = Build(null);

        await client.GetAsync("http://core.test/x");

        Assert.Null(capture.Seen!.Headers.Authorization);
    }

    [Fact]
    public async Task WithoutTheHeader_SendsNoAuthorization()
    {
        var (client, capture) = Build(new DefaultHttpContext());

        await client.GetAsync("http://core.test/x");

        Assert.Null(capture.Seen!.Headers.Authorization);
    }

    [Fact]
    public async Task DoesNotOverrideAnAuthorizationAlreadySetOnTheRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer del-usuario";
        var (client, capture) = Build(context);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://core.test/x");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "explicito");

        await client.SendAsync(request);

        Assert.Equal("explicito", capture.Seen!.Headers.Authorization!.Parameter);
    }
}
