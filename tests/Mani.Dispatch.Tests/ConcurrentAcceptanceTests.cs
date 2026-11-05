using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Exceptions;
using ManiDispatch.Infrastructure.Core;
using ManiDispatch.Infrastructure.Repositories;
using ManiDispatch.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// Exclusión concurrente de la aceptación (RNF-05 / ADR-0016 / SCRUM-1403). Con la decisión 6A la decide Core; aquí
/// se comprueba que Dispatch traduce bien el resultado y que, contra un Core por HTTP, solo uno gana.
/// </summary>
public class ConcurrentAcceptanceTests
{
    private static (List<(Guid UserId, Guid AllyId)> Allies, FakeAllyProfiles Profiles) CreateAllies(Guid tenantId, int count)
    {
        var profiles = new FakeAllyProfiles();
        var allies = Enumerable.Range(1, count).Select(_ =>
        {
            var userId = Guid.NewGuid();
            var allyId = Guid.NewGuid();
            profiles.RegisterAlly(new AllyProfile(allyId, tenantId, userId, "VERIFICADO"));
            return (userId, allyId);
        }).ToList();
        return (allies, profiles);
    }

    [Fact]
    public async Task AcceptRequest_With50SimultaneousAllies_OnlyOneWins_AndOthersReceive409WithoutTheWinner()
    {
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid().ToString();
        var (allies, profiles) = CreateAllies(tenantId, 50);
        var current = new AsyncLocal<Guid>();
        var core = new InMemoryCoreAssignments(profiles, () => current.Value);
        var useCase = new AcceptDispatchOfferUseCase(new InMemorySolicitudAssignmentRepository(), profiles, core);

        var results = new ConcurrentBag<AssignmentResult>();
        await Task.WhenAll(allies.Select(ally => Task.Run(async () =>
        {
            current.Value = ally.UserId;
            results.Add(await useCase.ExecuteAsync(requestId, tenantId, ally.UserId));
        })));

        var winner = Assert.Single(results, r => r.Status == AssignmentStatus.Assigned);
        var conflicts = results.Where(r => r.Status == AssignmentStatus.Conflict).ToList();
        Assert.Equal(49, conflicts.Count);
        Assert.All(conflicts, c => Assert.Null(c.AssignedAllyId));
        Assert.All(conflicts, c => Assert.Equal("MANI-SOL-409", c.Code));

        // El reintento del ganador es idempotente.
        var winningAlly = allies.First(a => a.AllyId == winner.AssignedAllyId);
        current.Value = winningAlly.UserId;
        var retry = await useCase.ExecuteAsync(requestId, tenantId, winningAlly.UserId);
        Assert.Equal(AssignmentStatus.AlreadyAssigned, retry.Status);
    }

    [Fact]
    public async Task AcceptRequest_With50AlliesAgainstCoreOverHttp_ExactlyOneIsAssigned_And49Get409()
    {
        using var server = new FakeCoreServer();
        var tenantId = Guid.NewGuid();
        var requestId = server.AddSolicitud(tenantId);
        var (allies, profiles) = CreateAllies(tenantId, 50);
        foreach (var ally in allies)
        {
            server.RegisterAllyUser(ally.UserId, ally.AllyId);
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CORE_SERVICE_URL"] = server.BaseUrl })
            .Build();

        // Cada aliado llama con su propio JWT, como lo reenvía ForwardAuthorizationHandler.
        AcceptDispatchOfferUseCase UseCaseFor(Guid userId)
        {
            var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", TestTokenHelper.CreateTestToken(tenantId, userId.ToString(), "aliado"));
            var client = new CoreAssignmentClient(http, config, NullLogger<CoreAssignmentClient>.Instance);
            return new AcceptDispatchOfferUseCase(new InMemorySolicitudAssignmentRepository(), profiles, client);
        }

        using var gate = new ManualResetEventSlim(false);
        var tasks = allies.Select(a => Task.Run(async () =>
        {
            var useCase = UseCaseFor(a.UserId);
            gate.Wait();
            return (a.AllyId, Result: await useCase.ExecuteAsync(requestId.ToString(), tenantId, a.UserId, $"k-{a.UserId}", "corr-50"));
        })).ToList();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        var winner = Assert.Single(results, r => r.Result.Status == AssignmentStatus.Assigned);
        Assert.Equal(49, results.Count(r => r.Result.Status == AssignmentStatus.Conflict));
        Assert.Equal(winner.AllyId, server.AssignedTo(requestId));

        var retry = await UseCaseFor(allies.First(a => a.AllyId == winner.AllyId).UserId)
            .ExecuteAsync(requestId.ToString(), tenantId, allies.First(a => a.AllyId == winner.AllyId).UserId);
        Assert.Equal(AssignmentStatus.AlreadyAssigned, retry.Status);
    }

    [Fact]
    public async Task AcceptRequest_WhenTheAllyRejectedItBefore_Returns409R_AndDoesNotCallCore()
    {
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var (allies, profiles) = CreateAllies(tenantId, 1);
        var repo = new InMemorySolicitudAssignmentRepository();
        await repo.RejectRequestAsync(requestId, tenantId, allies[0].UserId, allies[0].AllyId, "SIN_DISPONIBILIDAD", null);
        var core = new InMemoryCoreAssignments(profiles, () => allies[0].UserId);

        var result = await new AcceptDispatchOfferUseCase(repo, profiles, core).ExecuteAsync(requestId.ToString(), tenantId, allies[0].UserId);

        Assert.Equal(AssignmentStatus.RejectedBefore, result.Status);
        Assert.Equal("MANI-DSP-409R", result.Code);
        Assert.Equal(0, core.Calls);
    }

    [Fact]
    public async Task AcceptRequest_PassesTheIdempotencyKeyAndCorrelationIdToCore()
    {
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var (allies, profiles) = CreateAllies(tenantId, 1);
        var core = new InMemoryCoreAssignments(profiles, () => allies[0].UserId);

        await new AcceptDispatchOfferUseCase(new InMemorySolicitudAssignmentRepository(), profiles, core)
            .ExecuteAsync(requestId.ToString(), tenantId, allies[0].UserId, "clave-1", "corr-1");

        Assert.Equal((requestId, "clave-1", "corr-1"), core.LastCommand);
    }

    [Fact]
    public async Task AcceptRequest_WhenAllyNotVerified_ThrowsUnauthorizedAccessException_WithManiSol403V()
    {
        var tenantId = Guid.NewGuid();
        var profiles = new FakeAllyProfiles();
        var unverifiedUserId = Guid.NewGuid();
        profiles.RegisterAlly(new AllyProfile(Guid.NewGuid(), tenantId, unverifiedUserId, "PENDIENTE"));
        var core = new InMemoryCoreAssignments(profiles, () => unverifiedUserId);
        var useCase = new AcceptDispatchOfferUseCase(new InMemorySolicitudAssignmentRepository(), profiles, core);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            useCase.ExecuteAsync(Guid.NewGuid().ToString(), tenantId, unverifiedUserId));

        Assert.Contains("MANI-SOL-403V", ex.Message);
        Assert.Equal(0, core.Calls);
    }

    [Fact]
    public async Task AcceptRequest_WhenAllyProfileDoesNotExist_ThrowsUnauthorizedAccessException_AndDoesNotFabricateProfile()
    {
        var profiles = new FakeAllyProfiles();
        var unknownUserId = Guid.NewGuid();
        var core = new InMemoryCoreAssignments(profiles, () => unknownUserId);
        var useCase = new AcceptDispatchOfferUseCase(new InMemorySolicitudAssignmentRepository(), profiles, core);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            useCase.ExecuteAsync(Guid.NewGuid().ToString(), Guid.NewGuid(), unknownUserId));

        Assert.Contains("no existe un perfil de aliado", ex.Message);
        Assert.Equal(0, core.Calls);
    }

    [Fact]
    public async Task PostgresSolicitudAssignmentRepository_WhenDatabaseUnavailable_FailsClosedAndThrows()
    {
        // Sin cadena de conexión no se puede comprobar el rechazo previo: la aceptación no sigue (ADR-0016).
        var mockConfig = new Mock<IConfiguration>();
        var repo = new PostgresSolicitudAssignmentRepository(mockConfig.Object, NullLogger<PostgresSolicitudAssignmentRepository>.Instance);

        await Assert.ThrowsAsync<DispatchDatabaseUnavailableException>(() =>
            repo.HasRejectedAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        await Assert.ThrowsAsync<DispatchDatabaseUnavailableException>(() =>
            repo.RejectRequestAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "SIN_DISPONIBILIDAD", null));
    }
}
