using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ManiDispatch.Domain.Entities;
using ManiDispatch.Domain.Exceptions;
using ManiDispatch.Infrastructure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ManiDispatch.Tests;

public class CoreAllyClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }

    private static CoreAllyClient CreateClient(StubHandler handler, string? coreUrl = "http://core.test")
    {
        var settings = new Dictionary<string, string?>();
        if (coreUrl != null)
        {
            settings["CORE_SERVICE_URL"] = coreUrl;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new CoreAllyClient(new HttpClient(handler), config, NullLogger<CoreAllyClient>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static MatchCriteria Criteria(Guid? requestId = null) => new()
    {
        TenantId = Guid.NewGuid(),
        ZonaId = Guid.NewGuid(),
        CategoriaId = Guid.NewGuid(),
        RequestId = requestId,
        Page = 2,
        PageSize = 5,
        CorrelationId = "corr-1"
    };

    [Fact]
    public async Task GetEligibleAlliesAsync_CallsCoreWithTenantAndCorrelationHeaders_AndMapsResponse()
    {
        var allyId = Guid.NewGuid();
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            $"{{\"allies\":[{{\"id\":\"{allyId}\",\"nombreRazonSocial\":\"Ana\",\"tipo\":\"PERSONA_NATURAL\",\"estadoVerificacion\":\"VERIFICADO\"}}],\"totalCount\":7}}"));
        var client = CreateClient(handler);
        var criteria = Criteria(Guid.NewGuid());

        var (allies, total) = await client.GetEligibleAlliesAsync(criteria);

        var ally = Assert.Single(allies);
        Assert.Equal(allyId, ally.Id);
        Assert.Equal("Ana", ally.NombreRazonSocial);
        Assert.Equal(7, total);
        var request = handler.LastRequest!;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.StartsWith("http://core.test/api/v1/allies/eligible?", request.RequestUri!.ToString());
        Assert.Contains($"categoriaId={criteria.CategoriaId}", request.RequestUri.Query);
        Assert.Contains($"zonaId={criteria.ZonaId}", request.RequestUri.Query);
        Assert.Contains($"requestId={criteria.RequestId}", request.RequestUri.Query);
        Assert.Contains("page=2", request.RequestUri.Query);
        Assert.Equal(criteria.TenantId.ToString(), request.Headers.GetValues("X-Tenant-Id").Single());
        Assert.Equal("corr-1", request.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task GetEligibleAlliesAsync_WithoutRequestId_OmitsTheParameter()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"allies\":[],\"totalCount\":0}"));
        var client = CreateClient(handler);

        var (allies, total) = await client.GetEligibleAlliesAsync(Criteria());

        Assert.Empty(allies);
        Assert.Equal(0, total);
        Assert.DoesNotContain("requestId", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task GetEligibleAlliesAsync_WithoutCoreUrl_ThrowsUnavailable()
    {
        var client = CreateClient(new StubHandler(_ => Json(HttpStatusCode.OK, "{}")), coreUrl: null);

        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() => client.GetEligibleAlliesAsync(Criteria()));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task GetEligibleAlliesAsync_WhenCoreReturnsError_ThrowsUnavailable(HttpStatusCode status)
    {
        var client = CreateClient(new StubHandler(_ => Json(status, "{}")));

        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() => client.GetEligibleAlliesAsync(Criteria()));
    }

    [Fact]
    public async Task GetEligibleAlliesAsync_WhenCoreIsUnreachable_ThrowsUnavailable()
    {
        var client = CreateClient(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() => client.GetEligibleAlliesAsync(Criteria()));
    }

    [Fact]
    public async Task GetEligibleAlliesAsync_WhenCoreTimesOut_ThrowsUnavailable()
    {
        var client = CreateClient(new StubHandler(_ => throw new TaskCanceledException("timeout")));

        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() => client.GetEligibleAlliesAsync(Criteria()));
    }

    [Fact]
    public async Task GetEligibleAlliesAsync_WhenBodyIsNotJson_ThrowsUnavailable()
    {
        var client = CreateClient(new StubHandler(_ => Json(HttpStatusCode.OK, "<html>no</html>")));

        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() => client.GetEligibleAlliesAsync(Criteria()));
    }

    [Fact]
    public async Task GetSolicitudContextAsync_MapsResponse_AndSendsTenantHeader()
    {
        var requestId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var zonaId = Guid.NewGuid();
        var categoriaId = Guid.NewGuid();
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            $"{{\"id\":\"{requestId}\",\"tenantId\":\"{tenantId}\",\"zonaId\":\"{zonaId}\",\"categoriaId\":\"{categoriaId}\",\"estado\":\"PUBLICADA\"}}"));
        var client = CreateClient(handler);

        var context = await client.GetSolicitudContextAsync(requestId, tenantId);

        Assert.NotNull(context);
        Assert.Equal(zonaId, context!.ZonaId);
        Assert.Equal(categoriaId, context.CategoriaId);
        Assert.Equal($"http://core.test/api/v1/solicitudes/{requestId}/context", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(tenantId.ToString(), handler.LastRequest.Headers.GetValues("X-Tenant-Id").Single());
    }

    [Fact]
    public async Task GetSolicitudContextAsync_WhenCoreReturns404_ReturnsNull()
    {
        var client = CreateClient(new StubHandler(_ => Json(HttpStatusCode.NotFound, "{}")));

        Assert.Null(await client.GetSolicitudContextAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task GetSolicitudContextAsync_WhenCoreFails_ThrowsUnavailable()
    {
        var client = CreateClient(new StubHandler(_ => Json(HttpStatusCode.BadGateway, "{}")));

        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() => client.GetSolicitudContextAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task GetSolicitudContextAsync_WithoutCoreUrl_ThrowsUnavailable()
    {
        var client = CreateClient(new StubHandler(_ => Json(HttpStatusCode.OK, "{}")), coreUrl: null);

        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() => client.GetSolicitudContextAsync(Guid.NewGuid(), Guid.NewGuid()));
    }
}
