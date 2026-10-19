using Dapper;
using ManiDispatch.Application.DTOs;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Infrastructure.Dispatch;
using ManiDispatch.Infrastructure.Repositories;
using ManiDispatch.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// OrchestrateDispatchUseCase con los repositorios reales sobre PostgreSQL real: las ofertas
/// se guardan una sola vez por aliado aunque el despacho se oriente varias veces (RNF-03).
/// </summary>
[Collection("Postgres")]
public class OrchestrateDispatchIntegrationTests
{
    private readonly PostgresFixture _db;

    public OrchestrateDispatchIntegrationTests(PostgresFixture db) => _db = db;

    private OrchestrateDispatchUseCase CreateUseCase(string? connectionString = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connectionString ?? _db.ConnectionString })
            .Build();
        return new OrchestrateDispatchUseCase(
            new PostgresAllyRepository(config, NullLogger<PostgresAllyRepository>.Instance),
            new PostgresDispatchPublisher(config, NullLogger<PostgresDispatchPublisher>.Instance));
    }

    private static async Task<int> CountOffers(DispatchSeed seed, Guid solicitud)
    {
        await using var c = seed.Open();
        await c.OpenAsync();
        return await c.QuerySingleAsync<int>(
            "SELECT count(*) FROM notificacion WHERE tenant_id = @TenantId AND tipo = 'OFERTA_DESPACHO' AND payload->>'requestId' = @id",
            new { seed.TenantId, id = solicitud.ToString() });
    }

    [Fact]
    public async Task Orchestrate_TwiceForTheSameRequest_DoesNotDuplicateOffers()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        await seed.AddAliadoAsync("Ana");
        await seed.AddAliadoAsync("Beto");
        await seed.AddAliadoAsync("Sin verificar", estadoVerificacion: "PENDIENTE");
        var solicitud = await seed.AddSolicitudAsync();
        var useCase = CreateUseCase();
        var request = new OrchestrateRequestDto(solicitud.ToString());

        var first = await useCase.ExecuteAsync(request, "corr-1", seed.TenantId);
        var second = await useCase.ExecuteAsync(request, "corr-2", seed.TenantId);

        Assert.Equal("DISPATCHED", first.Status);
        Assert.Equal(2, first.CandidatesCount);
        Assert.Equal("DISPATCHED", second.Status);
        Assert.Equal(2, second.CandidatesCount);
        Assert.Equal(2, await CountOffers(seed, solicitud));
    }

    [Fact]
    public async Task Orchestrate_WithoutEligibleAllies_ReportsNoCandidates_AndStoresNothing()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var solicitud = await seed.AddSolicitudAsync();

        var result = await CreateUseCase().ExecuteAsync(new OrchestrateRequestDto(solicitud.ToString()), "corr", seed.TenantId);

        Assert.Equal("NO_ELIGIBLE_ALLIES", result.Status);
        Assert.Equal(0, await CountOffers(seed, solicitud));
    }

    [Fact]
    public async Task Orchestrate_RequestOfAnotherTenant_ThrowsNotFound()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var solicitud = await seed.AddSolicitudAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateUseCase().ExecuteAsync(new OrchestrateRequestDto(solicitud.ToString()), "corr", Guid.NewGuid()));
    }

    [Fact]
    public async Task Orchestrate_AlreadyAssignedRequest_ThrowsInvalidOperation()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var solicitud = await seed.AddSolicitudAsync(estado: "ASIGNADA");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateUseCase().ExecuteAsync(new OrchestrateRequestDto(solicitud.ToString()), "corr", seed.TenantId));
    }

    [Fact]
    public async Task Orchestrate_WhenSavingOffersFails_TheErrorPropagatesAndIsNotHidden()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        await seed.AddAliadoAsync("Ana");
        var solicitud = await seed.AddSolicitudAsync();

        // Lee con la base real, pero el publicador apunta a una base inalcanzable.
        var reader = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString }).Build();
        var broken = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2"
            }).Build();
        var useCase = new OrchestrateDispatchUseCase(
            new PostgresAllyRepository(reader, NullLogger<PostgresAllyRepository>.Instance),
            new PostgresDispatchPublisher(broken, NullLogger<PostgresDispatchPublisher>.Instance));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            useCase.ExecuteAsync(new OrchestrateRequestDto(solicitud.ToString()), "corr", seed.TenantId));
    }
}
