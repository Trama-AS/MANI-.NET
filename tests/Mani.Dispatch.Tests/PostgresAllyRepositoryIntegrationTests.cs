using ManiDispatch.Domain.Entities;
using ManiDispatch.Infrastructure.Repositories;
using ManiDispatch.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>Consultas de PostgresAllyRepository contra PostgreSQL real con el esquema oficial.</summary>
[Collection("Postgres")]
public class PostgresAllyRepositoryIntegrationTests
{
    private readonly PostgresFixture _db;

    public PostgresAllyRepositoryIntegrationTests(PostgresFixture db) => _db = db;

    private PostgresAllyRepository CreateRepository()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString })
            .Build();
        return new PostgresAllyRepository(config, NullLogger<PostgresAllyRepository>.Instance);
    }

    private static MatchCriteria Criteria(DispatchSeed seed, Guid? requestId = null, int page = 1, int pageSize = 20) => new()
    {
        TenantId = seed.TenantId,
        ZonaId = seed.ZonaId,
        CategoriaId = seed.CategoriaId,
        RequestId = requestId,
        Page = page,
        PageSize = pageSize
    };

    [Fact]
    public async Task GetEligibleAllies_ReturnsVerifiedAlliesCoveringZoneAndCategory()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (ana, _) = await seed.AddAliadoAsync("Ana");
        await seed.AddAliadoAsync("Beto");

        var (allies, total) = await CreateRepository().GetEligibleAlliesAsync(Criteria(seed));

        Assert.Equal(2, total);
        Assert.Contains(allies, a => a.Id == ana && a.NombreRazonSocial == "Ana");
        Assert.Equal(new[] { "Ana", "Beto" }, allies.Select(a => a.NombreRazonSocial).ToArray());
    }

    [Fact]
    public async Task GetEligibleAllies_ExcludesNotVerifiedAllies()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        await seed.AddAliadoAsync("Verificada");
        await seed.AddAliadoAsync("Pendiente", estadoVerificacion: "PENDIENTE");

        var (allies, total) = await CreateRepository().GetEligibleAlliesAsync(Criteria(seed));

        Assert.Equal(1, total);
        Assert.Equal("Verificada", Assert.Single(allies).NombreRazonSocial);
    }

    [Fact]
    public async Task GetEligibleAllies_ExcludesAlliesWithCurrentAbsence_ButKeepsPastAndFutureOnes()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (vacaciones, _) = await seed.AddAliadoAsync("De vacaciones");
        var (pasada, _) = await seed.AddAliadoAsync("Ausencia pasada");
        var (futura, _) = await seed.AddAliadoAsync("Ausencia futura");
        await seed.AddAusenciaAsync(vacaciones, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(2));
        await seed.AddAusenciaAsync(pasada, DateTime.UtcNow.AddDays(-10), DateTime.UtcNow.AddDays(-5));
        await seed.AddAusenciaAsync(futura, DateTime.UtcNow.AddDays(5), DateTime.UtcNow.AddDays(9));

        var (allies, total) = await CreateRepository().GetEligibleAlliesAsync(Criteria(seed));

        Assert.Equal(2, total);
        Assert.DoesNotContain(allies, a => a.Id == vacaciones);
        Assert.Contains(allies, a => a.Id == pasada);
        Assert.Contains(allies, a => a.Id == futura);
    }

    [Fact]
    public async Task GetEligibleAllies_NeverReturnsAlliesOfAnotherTenant()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var other = await DispatchSeed.CreateAsync(_db.ConnectionString);
        await seed.AddAliadoAsync("Propia");
        await other.AddAliadoAsync("Ajena");

        var (allies, total) = await CreateRepository().GetEligibleAlliesAsync(Criteria(seed));

        Assert.Equal(1, total);
        Assert.Equal("Propia", Assert.Single(allies).NombreRazonSocial);
    }

    [Fact]
    public async Task GetEligibleAllies_ExcludesAlliesThatAlreadyRejectedTheRequest()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (rechaza, _) = await seed.AddAliadoAsync("Rechaza");
        await seed.AddAliadoAsync("Acepta");
        var solicitud = await seed.AddSolicitudAsync();
        await using (var c = seed.Open())
        {
            await c.OpenAsync();
            await Dapper.SqlMapper.ExecuteAsync(c,
                "INSERT INTO solicitud_rechazo (tenant_id, solicitud_id, aliado_id, motivo) VALUES (@TenantId, @solicitud, @rechaza, 'OTRO')",
                new { seed.TenantId, solicitud, rechaza });
        }

        var (allies, total) = await CreateRepository().GetEligibleAlliesAsync(Criteria(seed, requestId: solicitud));

        Assert.Equal(1, total);
        Assert.Equal("Acepta", Assert.Single(allies).NombreRazonSocial);
    }

    [Fact]
    public async Task GetEligibleAllies_IncludesAlliesCoveringTheParentZone()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (cubrePadre, _) = await seed.AddAliadoAsync("Cubre la ciudad", zonaId: seed.ZonaPadreId);

        var (allies, _) = await CreateRepository().GetEligibleAlliesAsync(Criteria(seed));

        Assert.Contains(allies, a => a.Id == cubrePadre);
    }

    [Fact]
    public async Task GetEligibleAllies_PaginatesResults()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        foreach (var nombre in new[] { "A", "B", "C" })
        {
            await seed.AddAliadoAsync(nombre);
        }

        var (page2, total) = await CreateRepository().GetEligibleAlliesAsync(Criteria(seed, page: 2, pageSize: 2));

        Assert.Equal(3, total);
        Assert.Equal("C", Assert.Single(page2).NombreRazonSocial);
    }

    [Fact]
    public async Task GetSolicitudContext_ReturnsTheRequestOfTheTenant_AndNullForAnotherTenant()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var solicitud = await seed.AddSolicitudAsync();
        var repo = CreateRepository();

        var context = await repo.GetSolicitudContextAsync(solicitud, seed.TenantId);
        var crossTenant = await repo.GetSolicitudContextAsync(solicitud, Guid.NewGuid());

        Assert.NotNull(context);
        Assert.Equal(seed.ZonaId, context!.ZonaId);
        Assert.Equal(seed.CategoriaId, context.CategoriaId);
        Assert.Equal("PENDIENTE", context.Estado);
        Assert.Null(crossTenant);
    }
}
