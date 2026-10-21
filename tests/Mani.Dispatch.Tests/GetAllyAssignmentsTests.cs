using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using Mani.Dispatch.Controllers;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Exceptions;
using ManiDispatch.Infrastructure.Repositories;
using ManiDispatch.Tests.Fixtures;
using ManiDispatch.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>Asignaciones del aliado autenticado (SCRUM-853): la consulta que hace Core para las reglas del sitio.</summary>
[Collection("JwtAuthEnvironment")]
public class GetAllyAssignmentsTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _usuarioId = Guid.NewGuid();
    private readonly Guid _aliadoId = Guid.NewGuid();
    private readonly InMemorySolicitudAssignmentRepository _assignments = new();
    private readonly FakeAllyProfiles _allyProfiles = new();

    public GetAllyAssignmentsTests()
    {
        Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", TestTokenHelper.DefaultTestSecret);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        _allyProfiles.RegisterAlly(new AllyProfile(_aliadoId, _tenantId, _usuarioId, "VERIFICADO"));
    }

    private AssignmentsController Controller(IAllyRepository? allies = null, string? token = null)
    {
        var controller = new AssignmentsController(new GetAllyAssignmentsUseCase(_assignments, allies ?? _allyProfiles))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Request.Headers["X-Correlation-ID"] = "corr-asignaciones";
        if (token != null)
        {
            controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {token}";
        }
        return controller;
    }

    private static List<string> RequestIds(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.SerializeToElement(ok.Value);
        return json.GetProperty("requestIds").EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    [Fact]
    public async Task UseCase_ReturnsOnlyTheRequestsWonByTheAlly()
    {
        var mia = Guid.NewGuid();
        var ajena = Guid.NewGuid();
        await _assignments.AcceptRequestAsync(mia, _tenantId, _aliadoId);
        await _assignments.AcceptRequestAsync(ajena, _tenantId, Guid.NewGuid());

        var ids = await new GetAllyAssignmentsUseCase(_assignments, _allyProfiles).ExecuteAsync(_tenantId, _usuarioId);

        Assert.Equal(new[] { mia }, ids);
    }

    [Fact]
    public async Task UseCase_WithoutAllyProfile_IsUnauthorized()
    {
        var useCase = new GetAllyAssignmentsUseCase(_assignments, _allyProfiles);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => useCase.ExecuteAsync(_tenantId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Get_WithAllyToken_Returns200WithItsAssignments()
    {
        var mia = Guid.NewGuid();
        await _assignments.AcceptRequestAsync(mia, _tenantId, _aliadoId);
        var token = TestTokenHelper.CreateTestToken(_tenantId, _usuarioId.ToString(), "aliado");

        var result = await Controller(token: token).GetMyAssignments();

        Assert.Equal(new[] { mia.ToString() }, RequestIds(result));
    }

    [Fact]
    public async Task Get_WithAllyWithoutAssignments_Returns200Empty()
    {
        var token = TestTokenHelper.CreateTestToken(_tenantId, _usuarioId.ToString(), "aliado");

        var result = await Controller(token: token).GetMyAssignments();

        Assert.Empty(RequestIds(result));
    }

    [Fact]
    public async Task Get_WithoutToken_Returns401()
    {
        var result = await Controller().GetMyAssignments();

        Assert.Equal(401, Assert.IsType<UnauthorizedObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task Get_WithClientToken_Returns403()
    {
        var token = TestTokenHelper.CreateTestToken(_tenantId, _usuarioId.ToString(), "cliente");

        var result = await Controller(token: token).GetMyAssignments();

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task Get_WhenCoreDoesNotKnowTheAlly_Returns403()
    {
        var token = TestTokenHelper.CreateTestToken(_tenantId, Guid.NewGuid().ToString(), "aliado");

        var result = await Controller(token: token).GetMyAssignments();

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task Get_WhenCoreIsDown_Returns503()
    {
        var core = new Mock<IAllyRepository>();
        core.Setup(c => c.GetAllyProfileAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ThrowsAsync(new CoreServiceUnavailableException("Core no responde"));
        var token = TestTokenHelper.CreateTestToken(_tenantId, _usuarioId.ToString(), "aliado");

        var result = await Controller(core.Object, token).GetMyAssignments();

        Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
    }
}

/// <summary>La consulta de asignaciones contra PostgreSQL real con el esquema oficial.</summary>
[Collection("Postgres")]
public class PostgresAllyAssignmentsIntegrationTests
{
    private readonly PostgresFixture _db;

    public PostgresAllyAssignmentsIntegrationTests(PostgresFixture db) => _db = db;

    private PostgresSolicitudAssignmentRepository CreateRepository() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString })
                .Build(),
            NullLogger<PostgresSolicitudAssignmentRepository>.Instance);

    [Fact]
    public async Task ReturnsTheAllyRequests_InItsTenant_AndSkipsCancelledOnes()
    {
        var seed = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var other = await DispatchSeed.CreateAsync(_db.ConnectionString);
        var (aliado, _) = await seed.AddAliadoAsync("Ana");
        var (otroAliado, _) = await seed.AddAliadoAsync("Beto");
        var repo = CreateRepository();

        var asignada = await seed.AddSolicitudAsync();
        await repo.AcceptRequestAsync(asignada, seed.TenantId, aliado);
        var deOtro = await seed.AddSolicitudAsync();
        await repo.AcceptRequestAsync(deOtro, seed.TenantId, otroAliado);
        var cancelada = await seed.AddSolicitudAsync();
        await repo.AcceptRequestAsync(cancelada, seed.TenantId, aliado);
        await using (var c = seed.Open())
        {
            await c.OpenAsync();
            await c.ExecuteAsync("UPDATE solicitud SET estado = 'CANCELADA' WHERE id = @cancelada", new { cancelada });
        }
        await seed.AddSolicitudAsync(); // pendiente, sin aliado

        var ids = await repo.GetAssignedRequestIdsAsync(seed.TenantId, aliado);
        var otroTenant = await repo.GetAssignedRequestIdsAsync(other.TenantId, aliado);

        Assert.Equal(new[] { asignada }, ids);
        Assert.Empty(otroTenant);
    }
}
