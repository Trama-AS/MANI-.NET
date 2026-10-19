using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Infrastructure.Dispatch;
using ManiDispatch.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// PostgresDispatchPublisher contra PostgreSQL real (camino SQL) y contra un servidor HTTP real
/// en localhost que hace de Core (camino ADR-0030).
/// </summary>
[Collection("Postgres")]
public class PostgresDispatchPublisherIntegrationTests
{
    private readonly PostgresFixture _db;

    public PostgresDispatchPublisherIntegrationTests(PostgresFixture db) => _db = db;

    private PostgresDispatchPublisher SqlPublisher() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString })
                .Build(),
            NullLogger<PostgresDispatchPublisher>.Instance);

    private static DispatchOffer Offer(DispatchSeed seed, Guid requestId, Guid allyId, string corr = "corr-1") =>
        new(requestId, seed.TenantId, allyId, "Ana", "ENVIADA", corr, DateTime.UtcNow);

    [Fact]
    public async Task PublishOffer_PersistsNotificationForTheAllyUser_AndItCanBeReadBack()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (aliado, usuario) = await seed.AddAliadoAsync("Ana");
        var solicitud = await seed.AddSolicitudAsync();
        var publisher = SqlPublisher();

        await publisher.PublishOfferAsync(Offer(seed, solicitud, aliado));
        var offers = await publisher.GetOffersByRequestAsync(solicitud, seed.TenantId);

        var offer = Assert.Single(offers);
        Assert.Equal(aliado, offer.AllyId);
        Assert.Equal(solicitud, offer.RequestId);
        Assert.Equal("Ana", offer.AllyNombre);
        Assert.Equal("ENVIADA", offer.Estado);
        Assert.Equal("corr-1", offer.CorrelationId);

        await using var c = seed.Open();
        await c.OpenAsync();
        var owner = await c.QuerySingleAsync<Guid>(
            "SELECT usuario_id FROM notificacion WHERE tenant_id = @TenantId AND tipo = 'OFERTA_DESPACHO'", new { seed.TenantId });
        Assert.Equal(usuario, owner);
    }

    [Fact]
    public async Task PublishOffer_WhenAllyBelongsToAnotherTenant_InsertsNothing()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var other = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (ajeno, _) = await other.AddAliadoAsync("Ajeno");
        var solicitud = await seed.AddSolicitudAsync();
        var publisher = SqlPublisher();

        await publisher.PublishOfferAsync(Offer(seed, solicitud, ajeno));

        Assert.Empty(await publisher.GetOffersByRequestAsync(solicitud, seed.TenantId));
    }

    [Fact]
    public async Task GetOffers_NeverReturnsOffersOfAnotherTenant()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (aliado, _) = await seed.AddAliadoAsync("Ana");
        var solicitud = await seed.AddSolicitudAsync();
        var publisher = SqlPublisher();
        await publisher.PublishOfferAsync(Offer(seed, solicitud, aliado));

        Assert.Empty(await publisher.GetOffersByRequestAsync(solicitud, Guid.NewGuid()));
    }

    [Fact]
    public async Task PublishOffer_ThrowsWhenTheDatabaseIsUnreachable_SoErrorsAreNotHidden()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2"
            }).Build();
        var publisher = new PostgresDispatchPublisher(config, NullLogger<PostgresDispatchPublisher>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishOfferAsync(
            new DispatchOffer(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "x", "ENVIADA", "c", DateTime.UtcNow)));
        await Assert.ThrowsAnyAsync<Exception>(() => publisher.GetOffersByRequestAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    // ---- Camino HTTP hacia Core (ADR-0030), contra un servidor real en localhost ----

    private sealed class FakeCore : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string BaseUrl { get; }
        public List<(string Method, string Path, string Body, string? Tenant, string? Corr)> Requests { get; } = new();
        public int StatusCode { get; set; } = 200;
        public string ResponseBody { get; set; } = "[]";

        public FakeCore()
        {
            var port = new Random().Next(20000, 40000);
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
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

    private static PostgresDispatchPublisher HttpPublisher(string coreUrl) =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["CORE_SERVICE_URL"] = coreUrl + "/" })
                .Build(),
            NullLogger<PostgresDispatchPublisher>.Instance);

    [Fact]
    public async Task PublishOffer_WithCoreUrl_PostsNotificationToCore_WithTenantAndCorrelationHeaders()
    {
        using var core = new FakeCore();
        var offer = new DispatchOffer(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Ana", "ENVIADA", "corr-http", DateTime.UtcNow);

        await HttpPublisher(core.BaseUrl).PublishOfferAsync(offer);

        var req = Assert.Single(core.Requests);
        Assert.Equal("POST", req.Method);
        Assert.Equal("/api/v1/notifications", req.Path);
        Assert.Equal(offer.TenantId.ToString(), req.Tenant);
        Assert.Equal("corr-http", req.Corr);
        using var json = JsonDocument.Parse(req.Body);
        Assert.Equal("OFERTA_DESPACHO", json.RootElement.GetProperty("tipo").GetString());
        Assert.Equal(offer.RequestId.ToString(), json.RootElement.GetProperty("payload").GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task PublishOffer_WhenCoreFails_ThrowsInsteadOfHidingTheError()
    {
        using var core = new FakeCore { StatusCode = 503 };
        var offer = new DispatchOffer(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Ana", "ENVIADA", "c", DateTime.UtcNow);

        await Assert.ThrowsAsync<HttpRequestException>(() => HttpPublisher(core.BaseUrl).PublishOfferAsync(offer));
    }

    [Fact]
    public async Task GetOffers_WithCoreUrl_MapsCoreResponse_AndFillsMissingFields()
    {
        var requestId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var allyId = Guid.NewGuid();
        using var core = new FakeCore { ResponseBody = JsonSerializer.Serialize(new[] { new { allyId, allyNombre = "Ana" } }) };

        var offers = await HttpPublisher(core.BaseUrl).GetOffersByRequestAsync(requestId, tenantId);

        var offer = Assert.Single(offers);
        Assert.Equal(allyId, offer.AllyId);
        Assert.Equal(requestId, offer.RequestId);
        Assert.Equal(tenantId, offer.TenantId);
        Assert.Equal("ENVIADA", offer.Estado);
        Assert.Contains($"requestId={requestId}", Assert.Single(core.Requests).Path);
    }

    [Fact]
    public async Task GetOffers_WhenCoreFails_Throws()
    {
        using var core = new FakeCore { StatusCode = 500 };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            HttpPublisher(core.BaseUrl).GetOffersByRequestAsync(Guid.NewGuid(), Guid.NewGuid()));
    }
}
