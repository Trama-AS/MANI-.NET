using System.Net;
using System.Text;
using System.Text.Json;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Exceptions;
using ManiDispatch.Infrastructure.Dispatch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// CoreDispatchPublisher contra un servidor HTTP real en localhost que hace de Core (ADR-0030).
/// </summary>
public class CoreDispatchPublisherTests
{
    private sealed class FakeCore : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string BaseUrl { get; }
        public List<(string Method, string Path, string Body, string? Tenant, string? Corr)> Requests { get; } = new();
        public int StatusCode { get; set; } = 200;
        public string ResponseBody { get; set; } = "[]";

        public FakeCore()
        {
            for (var attempt = 0; ; attempt++)
            {
                var port = new Random().Next(20000, 40000);
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    _listener.Start();
                    BaseUrl = $"http://127.0.0.1:{port}";
                    break;
                }
                catch (HttpListenerException) when (attempt < 10)
                {
                }
            }
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    using var reader = new StreamReader(ctx.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    lock (Requests)
                    {
                        Requests.Add((ctx.Request.HttpMethod, ctx.Request.Url!.PathAndQuery, body,
                            ctx.Request.Headers["X-Tenant-Id"], ctx.Request.Headers["X-Correlation-ID"]));
                    }
                    ctx.Response.StatusCode = StatusCode;
                    var bytes = Encoding.UTF8.GetBytes(ResponseBody);
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch { return; }
            }
        }

        public void Dispose() => _listener.Close();
    }

    private static CoreDispatchPublisher Publisher(string? coreUrl)
    {
        var settings = new Dictionary<string, string?>();
        if (coreUrl != null)
        {
            settings["CORE_SERVICE_URL"] = coreUrl + "/";
        }
        return new CoreDispatchPublisher(
            new HttpClient { Timeout = TimeSpan.FromSeconds(3) },
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<CoreDispatchPublisher>.Instance);
    }

    private static DispatchOffer Offer(string corr = "corr-http") =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Ana", "ENVIADA", corr, DateTime.UtcNow);

    [Fact]
    public async Task PublishOffer_PostsNotificationToCore_WithTenantAndCorrelationHeaders()
    {
        using var core = new FakeCore();
        var offer = Offer();

        await Publisher(core.BaseUrl).PublishOfferAsync(offer);

        var req = Assert.Single(core.Requests);
        Assert.Equal("POST", req.Method);
        Assert.Equal("/api/v1/notifications", req.Path);
        Assert.Equal(offer.TenantId.ToString(), req.Tenant);
        Assert.Equal("corr-http", req.Corr);
        using var json = JsonDocument.Parse(req.Body);
        Assert.Equal("OFERTA_DESPACHO", json.RootElement.GetProperty("tipo").GetString());
        Assert.Equal(offer.AllyId.ToString(), json.RootElement.GetProperty("allyId").GetString());
        Assert.Equal(offer.RequestId.ToString(), json.RootElement.GetProperty("payload").GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task PublishOffer_WhenCoreFails_ThrowsInsteadOfHidingTheError()
    {
        using var core = new FakeCore { StatusCode = 503 };

        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() => Publisher(core.BaseUrl).PublishOfferAsync(Offer()));
    }

    [Fact]
    public async Task PublishOffer_WhenCoreIsUnreachable_Throws()
    {
        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() => Publisher("http://127.0.0.1:1").PublishOfferAsync(Offer()));
    }

    [Fact]
    public async Task PublishOffer_WithoutCoreUrl_Throws()
    {
        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() => Publisher(null).PublishOfferAsync(Offer()));
    }

    [Fact]
    public async Task GetOffers_MapsCoreResponse_AndFillsMissingFields()
    {
        var requestId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var allyId = Guid.NewGuid();
        using var core = new FakeCore { ResponseBody = JsonSerializer.Serialize(new[] { new { allyId, allyNombre = "Ana" } }) };

        var offers = await Publisher(core.BaseUrl).GetOffersByRequestAsync(requestId, tenantId);

        var offer = Assert.Single(offers);
        Assert.Equal(allyId, offer.AllyId);
        Assert.Equal(requestId, offer.RequestId);
        Assert.Equal(tenantId, offer.TenantId);
        Assert.Equal("ENVIADA", offer.Estado);
        var req = Assert.Single(core.Requests);
        Assert.Contains($"requestId={requestId}", req.Path);
        Assert.Equal(tenantId.ToString(), req.Tenant);
    }

    [Fact]
    public async Task GetOffers_WhenCoreFails_Throws()
    {
        using var core = new FakeCore { StatusCode = 500 };

        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() =>
            Publisher(core.BaseUrl).GetOffersByRequestAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task GetOffers_WhenBodyIsNotJson_Throws()
    {
        using var core = new FakeCore { ResponseBody = "<html></html>" };

        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() =>
            Publisher(core.BaseUrl).GetOffersByRequestAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task GetOffers_WithoutCoreUrl_Throws()
    {
        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() =>
            Publisher(null).GetOffersByRequestAsync(Guid.NewGuid(), Guid.NewGuid()));
    }
}
