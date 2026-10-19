using ManiDispatch.Application.Interfaces;
using ManiDispatch.Infrastructure.Repositories;
using ManiDispatch.Tests.Fixtures;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// Exclusión concurrente (ADR-0016 / RNF-05) de PostgresSolicitudAssignmentRepository contra
/// PostgreSQL real con el esquema oficial: el UPDATE condicional decide, no la memoria del proceso.
/// </summary>
[Collection("Postgres")]
public class PostgresSolicitudAssignmentRepositoryIntegrationTests
{
    private readonly PostgresFixture _db;

    public PostgresSolicitudAssignmentRepositoryIntegrationTests(PostgresFixture db) => _db = db;

    private PostgresSolicitudAssignmentRepository CreateRepository() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString })
                .Build(),
            NullLogger<PostgresSolicitudAssignmentRepository>.Instance);

    [Fact]
    public async Task Accept_AssignsThePendingRequest_AndPersistsIt()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (aliado, _) = await seed.AddAliadoAsync("Ana");
        var solicitud = await seed.AddSolicitudAsync();

        var result = await CreateRepository().AcceptRequestAsync(solicitud, seed.TenantId, aliado);

        Assert.Equal(AssignmentStatus.Assigned, result.Status);
        await using var c = seed.Open();
        await c.OpenAsync();
        var row = await c.QuerySingleAsync<(Guid? AliadoId, string Estado)>(
            "SELECT aliado_id, estado FROM solicitud WHERE id = @solicitud", new { solicitud });
        Assert.Equal(aliado, row.AliadoId);
        Assert.Equal("ASIGNADA", row.Estado);
    }

    [Fact]
    public async Task Accept_SameAllyTwice_IsIdempotent()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (aliado, _) = await seed.AddAliadoAsync("Ana");
        var solicitud = await seed.AddSolicitudAsync();
        var repo = CreateRepository();

        await repo.AcceptRequestAsync(solicitud, seed.TenantId, aliado);
        var second = await repo.AcceptRequestAsync(solicitud, seed.TenantId, aliado);

        Assert.Equal(AssignmentStatus.AlreadyAssigned, second.Status);
    }

    [Fact]
    public async Task Accept_AnotherAllyAfterAssignment_IsAConflictAndKeepsTheWinner()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (ganador, _) = await seed.AddAliadoAsync("Ganador");
        var (perdedor, _) = await seed.AddAliadoAsync("Perdedor");
        var solicitud = await seed.AddSolicitudAsync();
        var repo = CreateRepository();

        await repo.AcceptRequestAsync(solicitud, seed.TenantId, ganador);
        var result = await repo.AcceptRequestAsync(solicitud, seed.TenantId, perdedor);

        Assert.Equal(AssignmentStatus.Conflict, result.Status);
        Assert.Equal(ganador, result.AssignedAllyId);
    }

    [Fact]
    public async Task Accept_RequestOfAnotherTenant_IsNotFound_AndStaysPending()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var other = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (aliado, _) = await other.AddAliadoAsync("Ajena");
        var solicitud = await seed.AddSolicitudAsync();

        var result = await CreateRepository().AcceptRequestAsync(solicitud, other.TenantId, aliado);

        Assert.Equal(AssignmentStatus.NotFound, result.Status);
        await using var c = seed.Open();
        await c.OpenAsync();
        Assert.Equal("PENDIENTE", await c.QuerySingleAsync<string>("SELECT estado FROM solicitud WHERE id = @solicitud", new { solicitud }));
    }

    [Fact]
    public async Task Accept_Concurrent50Allies_ExactlyOneWins_And49GetConflict()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var solicitud = await seed.AddSolicitudAsync();
        var allies = new List<Guid>();
        for (var i = 0; i < 50; i++)
        {
            allies.Add((await seed.AddAliadoAsync($"Aliado {i}")).AliadoId);
        }

        var repo = CreateRepository();
        using var gate = new ManualResetEventSlim(false);
        var tasks = allies.Select(a => Task.Run(async () =>
        {
            gate.Wait();
            return await repo.AcceptRequestAsync(solicitud, seed.TenantId, a);
        })).ToList();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Status == AssignmentStatus.Assigned));
        Assert.Equal(49, results.Count(r => r.Status == AssignmentStatus.Conflict));

        var winner = allies.Single(a => results[allies.IndexOf(a)].Status == AssignmentStatus.Assigned);
        Assert.All(results.Where(r => r.Status == AssignmentStatus.Conflict), r => Assert.Equal(winner, r.AssignedAllyId));
        await using var c = seed.Open();
        await c.OpenAsync();
        Assert.Equal(winner, await c.QuerySingleAsync<Guid>("SELECT aliado_id FROM solicitud WHERE id = @solicitud", new { solicitud }));
    }

    [Fact]
    public async Task Reject_PersistsTheRejection_AndRepeatingItDoesNotDuplicate()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (aliado, _) = await seed.AddAliadoAsync("Ana");
        var solicitud = await seed.AddSolicitudAsync();
        var repo = CreateRepository();

        Assert.True(await repo.RejectRequestAsync(solicitud, seed.TenantId, aliado, "OTRO"));
        Assert.True(await repo.RejectRequestAsync(solicitud, seed.TenantId, aliado, "OTRO"));

        await using var c = seed.Open();
        await c.OpenAsync();
        Assert.Equal(1, await c.QuerySingleAsync<int>(
            "SELECT count(*) FROM solicitud_rechazo WHERE solicitud_id = @solicitud AND aliado_id = @aliado", new { solicitud, aliado }));
    }

    [Fact]
    public async Task GetAllyByUserId_ReturnsTheProfile_AndNullForAnotherTenant()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (aliado, usuario) = await seed.AddAliadoAsync("Ana");
        var repo = CreateRepository();

        var profile = await repo.GetAllyByUserIdAsync(usuario, seed.TenantId);
        var crossTenant = await repo.GetAllyByUserIdAsync(usuario, Guid.NewGuid());

        Assert.NotNull(profile);
        Assert.Equal(aliado, profile!.Id);
        Assert.Equal("VERIFICADO", profile.EstadoVerificacion);
        Assert.Null(crossTenant);
    }

    [Fact]
    public async Task WithoutConnectionString_Throws_SoNothingIsConfirmedWithoutDatabase()
    {
        var repo = new PostgresSolicitudAssignmentRepository(
            new ConfigurationBuilder().Build(), NullLogger<PostgresSolicitudAssignmentRepository>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.AcceptRequestAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.RejectRequestAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "OTRO"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.GetAllyByUserIdAsync(Guid.NewGuid(), Guid.NewGuid()));
    }
}
