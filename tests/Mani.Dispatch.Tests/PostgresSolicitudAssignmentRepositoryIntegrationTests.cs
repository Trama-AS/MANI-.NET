using ManiDispatch.Domain.Exceptions;
using ManiDispatch.Infrastructure.Repositories;
using ManiDispatch.Tests.Fixtures;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// PostgresSolicitudAssignmentRepository contra PostgreSQL real con el esquema oficial. Desde la decisión 6A Dispatch
/// solo escribe rechazos; la asignación la registra Core y aquí se siembra directo. Cada operación debe fijar el
/// contexto de la sesión (D-2): el doble de app.set_context del fixture lo registra.
/// </summary>
[Collection("Postgres")]
public class PostgresSolicitudAssignmentRepositoryIntegrationTests
{
    private static readonly string[] SoloFueraDeZona = { "FUERA_DE_ZONA" };

    private readonly PostgresFixture _db;

    public PostgresSolicitudAssignmentRepositoryIntegrationTests(PostgresFixture db) => _db = db;

    private PostgresSolicitudAssignmentRepository CreateRepository() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString })
                .Build(),
            NullLogger<PostgresSolicitudAssignmentRepository>.Instance);

    [Fact]
    public async Task Reject_PersistsTheRejection_WithTheAllyContext_AndRepeatingItIsIdempotent()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (aliado, usuario) = await seed.AddAliadoAsync("Ana");
        var solicitud = await seed.AddSolicitudAsync();
        var repo = CreateRepository();

        Assert.True(await repo.RejectRequestAsync(solicitud, seed.TenantId, usuario, aliado, "FUERA_DE_ZONA"));
        Assert.False(await repo.RejectRequestAsync(solicitud, seed.TenantId, usuario, aliado, "OTRO"));

        await using var c = seed.Open();
        await c.OpenAsync();
        var motivos = (await c.QueryAsync<string>(
            "SELECT motivo FROM solicitud_rechazo WHERE solicitud_id = @solicitud AND aliado_id = @aliado", new { solicitud, aliado })).ToList();
        Assert.Equal(SoloFueraDeZona, motivos);
        Assert.All(await seed.ContextosAsync(), ctx => Assert.Equal((usuario, "aliado"), (ctx.UserId!.Value, ctx.Role)));
    }

    [Fact]
    public async Task Reject_WithoutReason_StoresNull_WhichTheCheckAllows()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (aliado, usuario) = await seed.AddAliadoAsync("Ana");
        var solicitud = await seed.AddSolicitudAsync();

        Assert.True(await CreateRepository().RejectRequestAsync(solicitud, seed.TenantId, usuario, aliado, null));

        await using var c = seed.Open();
        await c.OpenAsync();
        Assert.Null(await c.QuerySingleAsync<string?>(
            "SELECT motivo FROM solicitud_rechazo WHERE solicitud_id = @solicitud AND aliado_id = @aliado", new { solicitud, aliado }));
    }

    [Fact]
    public async Task Reject_WithTheOldDefaultReason_FailsTheCheck_AndNothingIsStored()
    {
        // N-1: el antiguo motivo por defecto NO_DISPONIBLE no está en el CHECK; el caso de uso ya no lo envía.
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (aliado, usuario) = await seed.AddAliadoAsync("Ana");
        var solicitud = await seed.AddSolicitudAsync();

        var error = await Assert.ThrowsAsync<DispatchDatabaseUnavailableException>(() =>
            CreateRepository().RejectRequestAsync(solicitud, seed.TenantId, usuario, aliado, "NO_DISPONIBLE"));

        Assert.IsType<PostgresException>(error.InnerException);
        await using var c = seed.Open();
        await c.OpenAsync();
        Assert.Equal(0, await c.QuerySingleAsync<int>("SELECT count(*) FROM solicitud_rechazo WHERE solicitud_id = @solicitud", new { solicitud }));
    }

    [Fact]
    public async Task RejectionQueries_ReturnOnlyTheRowsOfTheAllyAndTheRequest()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (ana, usuarioAna) = await seed.AddAliadoAsync("Ana");
        var (beto, usuarioBeto) = await seed.AddAliadoAsync("Beto");
        var primera = await seed.AddSolicitudAsync();
        var segunda = await seed.AddSolicitudAsync();
        var repo = CreateRepository();
        await repo.RejectRequestAsync(primera, seed.TenantId, usuarioAna, ana, "OTRO");
        await repo.RejectRequestAsync(segunda, seed.TenantId, usuarioAna, ana, null);
        await repo.RejectRequestAsync(primera, seed.TenantId, usuarioBeto, beto, "SIN_DISPONIBILIDAD");

        Assert.True(await repo.HasRejectedAsync(primera, seed.TenantId, usuarioAna, ana));
        Assert.False(await repo.HasRejectedAsync(segunda, seed.TenantId, usuarioBeto, beto));
        Assert.Equal(new[] { primera, segunda }.OrderBy(x => x), (await repo.GetRejectedRequestIdsAsync(seed.TenantId, usuarioAna, ana)).OrderBy(x => x));

        var cliente = Guid.NewGuid();
        Assert.Equal(new[] { ana, beto }.OrderBy(x => x), (await repo.GetRejectedAllyIdsAsync(primera, seed.TenantId, cliente, "cliente")).OrderBy(x => x));

        var otro = await DispatchSeed.CreateAsync(_db.ConnectionString);
        Assert.Empty(await repo.GetRejectedAllyIdsAsync(primera, otro.TenantId, cliente, "cliente"));
        Assert.Contains((cliente, "cliente"), (await seed.ContextosAsync()).Select(c => (c.UserId!.Value, c.Role)));
    }

    [Fact]
    public async Task AssignedRequestIds_AreReadWithTheAllyContext()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (aliado, usuario) = await seed.AddAliadoAsync("Ana");
        var asignada = await seed.AddSolicitudAsync();
        await seed.AssignAsync(asignada, aliado);

        var ids = await CreateRepository().GetAssignedRequestIdsAsync(seed.TenantId, usuario, aliado);

        Assert.Equal(new[] { asignada }, ids);
        Assert.Equal(new[] { (usuario, "aliado") }, (await seed.ContextosAsync()).Select(c => (c.UserId!.Value, c.Role)));
    }

    [Fact]
    public async Task WithoutConnectionString_Throws_SoNothingIsConfirmedWithoutDatabase()
    {
        var repo = new PostgresSolicitudAssignmentRepository(
            new ConfigurationBuilder().Build(), NullLogger<PostgresSolicitudAssignmentRepository>.Instance);

        await Assert.ThrowsAsync<DispatchDatabaseUnavailableException>(() => repo.RejectRequestAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "OTRO"));
        await Assert.ThrowsAsync<DispatchDatabaseUnavailableException>(() => repo.HasRejectedAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
    }
}
