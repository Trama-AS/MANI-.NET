using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

public class ConcurrentAcceptanceTests
{
    [Fact]
    public async Task AcceptRequest_With50SimultaneousAllies_OnlyOneWins_AndOthersReceive409Conflict()
    {
        // Arrange (RNF-05 / ADR-0016 / SCRUM-1074)
        var mockConfig = new Mock<IConfiguration>();
        // Sin connection string para usar el motor de exclusión concurrente en memoria atómica
        var repo = new PostgresSolicitudAssignmentRepository(mockConfig.Object, NullLogger<PostgresSolicitudAssignmentRepository>.Instance);
        var useCase = new AcceptDispatchOfferUseCase(repo);

        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid().ToString();

        // Creamos 50 aliados verificados únicos
        var allies = Enumerable.Range(1, 50).Select(i =>
        {
            var userId = Guid.NewGuid();
            var allyId = Guid.NewGuid();
            var profile = new AllyProfile(allyId, tenantId, userId, "VERIFICADO");
            repo.RegisterMemoryAlly(profile);
            return (UserId: userId, AllyId: allyId);
        }).ToList();

        var results = new ConcurrentBag<AssignmentResult>();

        // Act: 50 tareas compitiendo en paralelo exactamente al mismo tiempo
        var tasks = allies.Select(ally => Task.Run(async () =>
        {
            var res = await useCase.ExecuteAsync(requestId, tenantId, ally.UserId);
            results.Add(res);
        }));

        await Task.WhenAll(tasks);

        // Assert (Exclusión Concurrente RNF-05)
        var assignedList = results.Where(r => r.Status == AssignmentStatus.Assigned).ToList();
        var conflictList = results.Where(r => r.Status == AssignmentStatus.Conflict).ToList();

        Assert.Single(assignedList); // Exactamente 1 ganador
        Assert.Equal(49, conflictList.Count); // Exactamente 49 rechazados con conflicto

        var winningAllyId = assignedList.First().AssignedAllyId;
        Assert.NotNull(winningAllyId);

        // Todos los 49 conflictos deben reportar que la solicitud fue asignada al ganador
        foreach (var conflict in conflictList)
        {
            Assert.Equal(winningAllyId, conflict.AssignedAllyId);
        }

        // Paridad RPC 005: Un reintento por el aliado ganador DEBE ser idempotente (200 OK)
        var winningAlly = allies.First(a => a.AllyId == winningAllyId);
        var retryResult = await useCase.ExecuteAsync(requestId, tenantId, winningAlly.UserId);

        Assert.Equal(AssignmentStatus.AlreadyAssigned, retryResult.Status);
        Assert.Equal(winningAllyId, retryResult.AssignedAllyId);
    }

    [Fact]
    public async Task AcceptRequest_WhenAllyNotVerified_ThrowsUnauthorizedAccessException_WithManiSol403V()
    {
        // Arrange
        var mockConfig = new Mock<IConfiguration>();
        var repo = new PostgresSolicitudAssignmentRepository(mockConfig.Object, NullLogger<PostgresSolicitudAssignmentRepository>.Instance);
        var useCase = new AcceptDispatchOfferUseCase(repo);

        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid().ToString();
        var unverifiedUserId = Guid.NewGuid();

        // Aliado con estado pendiente (no verificado)
        var profile = new AllyProfile(Guid.NewGuid(), tenantId, unverifiedUserId, "PENDIENTE");
        repo.RegisterMemoryAlly(profile);

        // Act & Assert (DoD §9.3)
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            useCase.ExecuteAsync(requestId, tenantId, unverifiedUserId));

        Assert.Contains("MANI-SOL-403V", ex.Message);
    }
}
