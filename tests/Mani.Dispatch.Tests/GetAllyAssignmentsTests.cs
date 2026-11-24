using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Mani.Dispatch.Controllers;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Entities;
using ManiDispatch.Domain.Exceptions;
using ManiDispatch.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// Asignaciones del aliado autenticado (SCRUM-853 / SCRUM-1660, QAS-20): Dispatch obtiene las solicitudes asignadas
/// a través de Core (<c>ICoreAssignmentClient.GetOwnOffersAsync</c> filtrando <c>EsMia == true</c>) sin leer
/// directamente la tabla <c>solicitud</c>.
/// </summary>
[Collection("JwtAuthEnvironment")]
public class GetAllyAssignmentsTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _usuarioId = Guid.NewGuid();
    private readonly Guid _aliadoId = Guid.NewGuid();
    private readonly FakeAllyProfiles _allyProfiles = new();
    private readonly InMemoryCoreAssignments _coreAssignments;

    public GetAllyAssignmentsTests()
    {
        Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", TestTokenHelper.DefaultTestSecret);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        _allyProfiles.RegisterAlly(new AllyProfile(_aliadoId, _tenantId, _usuarioId, "VERIFICADO"));
        _coreAssignments = new InMemoryCoreAssignments(_allyProfiles, () => _usuarioId);
    }

    private static AllyOffer Offer(Guid id, bool esMia) => new()
    {
        Id = id,
        Estado = esMia ? "ASIGNADA" : "PENDIENTE",
        EsMia = esMia,
        Categoria = "Plomería",
        FlujoOperativo = "COTIZACION_PREVIA",
        Zona = "Chapinero",
        CreadaEn = DateTimeOffset.UtcNow
    };

    private AssignmentsController Controller(
        IAllyRepository? allies = null,
        string? token = null,
        ICoreAssignmentClient? coreAssignments = null)
    {
        var useCase = new GetAllyAssignmentsUseCase(coreAssignments ?? _coreAssignments, allies ?? _allyProfiles);
        var controller = new AssignmentsController(useCase)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (token != null)
        {
            controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";
        }
        return controller;
    }

    private static JsonElement Body(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<ObjectResult>(result).Value);

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
        var pendiente = Guid.NewGuid();
        _coreAssignments.Offers.Add(Offer(mia, esMia: true));
        _coreAssignments.Offers.Add(Offer(pendiente, esMia: false));

        var ids = await new GetAllyAssignmentsUseCase(_coreAssignments, _allyProfiles).ExecuteAsync(_tenantId, _usuarioId, "corr-1");

        Assert.Equal(new[] { mia }, ids);
    }

    [Fact]
    public async Task UseCase_WithoutAllyProfile_IsUnauthorized()
    {
        var useCase = new GetAllyAssignmentsUseCase(_coreAssignments, _allyProfiles);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => useCase.ExecuteAsync(_tenantId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Get_WithAllyToken_Returns200WithItsAssignments()
    {
        var mia = Guid.NewGuid();
        _coreAssignments.Offers.Add(Offer(mia, esMia: true));
        var token = TestTokenHelper.CreateTestToken(_tenantId, _usuarioId.ToString(), "aliado");

        var result = await Controller(token: token).GetMyAssignments(null);

        Assert.Equal(new[] { mia.ToString() }, RequestIds(result));
    }

    [Fact]
    public async Task Get_WithAllyWithoutAssignments_Returns200Empty()
    {
        var token = TestTokenHelper.CreateTestToken(_tenantId, _usuarioId.ToString(), "aliado");

        var result = await Controller(token: token).GetMyAssignments(null);

        Assert.Empty(RequestIds(result));
    }

    [Fact]
    public async Task Get_WithoutToken_Returns401()
    {
        var result = await Controller().GetMyAssignments(null);

        Assert.Equal(401, Assert.IsType<UnauthorizedObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task Get_WithClientToken_Returns403()
    {
        var token = TestTokenHelper.CreateTestToken(_tenantId, _usuarioId.ToString(), "cliente");

        var result = await Controller(token: token).GetMyAssignments(null);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task Get_WhenCoreDoesNotKnowTheAlly_Returns403()
    {
        var token = TestTokenHelper.CreateTestToken(_tenantId, Guid.NewGuid().ToString(), "aliado");

        var result = await Controller(token: token).GetMyAssignments(null);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task Get_WhenCoreDeniesAccess_Returns403()
    {
        _coreAssignments.Failure = new CoreAccessDeniedException("MANI-SOL-403V: aliado sin verificar", "MANI-SOL-403V");
        var token = TestTokenHelper.CreateTestToken(_tenantId, _usuarioId.ToString(), "aliado");

        var result = await Controller(token: token).GetMyAssignments(null);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal("MANI-SOL-403V", Body(result).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_WhenCoreIsDown_Returns503()
    {
        var core = new Mock<IAllyRepository>();
        core.Setup(c => c.GetAllyProfileAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ThrowsAsync(new CoreServiceUnavailableException("Core no responde"));
        var token = TestTokenHelper.CreateTestToken(_tenantId, _usuarioId.ToString(), "aliado");

        var result = await Controller(core.Object, token).GetMyAssignments(null);

        Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task Get_EchoesTheCorrelationId()
    {
        var token = TestTokenHelper.CreateTestToken(_tenantId, _usuarioId.ToString(), "aliado");

        var result = await Controller(token: token).GetMyAssignments("corr-asignaciones");

        Assert.Equal("corr-asignaciones", Body(result).GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Get_WithoutCorrelationId_GeneratesOne()
    {
        var token = TestTokenHelper.CreateTestToken(_tenantId, _usuarioId.ToString(), "aliado");

        var result = await Controller(token: token).GetMyAssignments(null);

        Assert.StartsWith("disp-", Body(result).GetProperty("correlationId").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_WithNonUuidUserInToken_Returns401()
    {
        var token = TestTokenHelper.CreateTestToken(_tenantId, "no-es-uuid", "aliado");

        var result = await Controller(token: token).GetMyAssignments(null);

        Assert.Equal(401, Assert.IsType<UnauthorizedObjectResult>(result).StatusCode);
    }
}
