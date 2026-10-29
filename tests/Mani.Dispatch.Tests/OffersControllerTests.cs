using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Mani.Dispatch.Controllers;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Entities;
using ManiDispatch.Domain.Exceptions;
using ManiDispatch.Infrastructure.Repositories;
using ManiDispatch.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>GET /api/v1/dispatch/offers/me: la bandeja del aliado por el Gateway (SCRUM-1402/1404, TEC-18).</summary>
[Collection("JwtAuthEnvironment")]
public class OffersControllerTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _usuarioId = Guid.NewGuid();
    private readonly Guid _aliadoId = Guid.NewGuid();
    private readonly FakeAllyProfiles _profiles = new();
    private readonly InMemorySolicitudAssignmentRepository _repo = new();
    private readonly InMemoryCoreAssignments _core;

    public OffersControllerTests()
    {
        Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", TestTokenHelper.DefaultTestSecret);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        _profiles.RegisterAlly(new AllyProfile(_aliadoId, _tenantId, _usuarioId, "VERIFICADO"));
        _core = new InMemoryCoreAssignments(_profiles, () => _usuarioId);
    }

    private OffersController Controller(string? token, ISolicitudAssignmentRepository? repo = null)
    {
        var controller = new OffersController(new GetAllyOffersUseCase(_core, repo ?? _repo, _profiles))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (token != null)
        {
            controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";
        }
        return controller;
    }

    private string AllyToken() => TestTokenHelper.CreateTestToken(_tenantId, _usuarioId.ToString(), "aliado");

    private static JsonElement Body(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<ObjectResult>(result).Value);

    [Fact]
    public async Task Get_WithAllyToken_ReturnsTheInboxWithoutRejectedOffers()
    {
        var abierta = Guid.NewGuid();
        var rechazada = Guid.NewGuid();
        _core.Offers.Add(new AllyOffer { Id = abierta, Estado = "PENDIENTE" });
        _core.Offers.Add(new AllyOffer { Id = rechazada, Estado = "PENDIENTE" });
        await _repo.RejectRequestAsync(rechazada, _tenantId, _usuarioId, _aliadoId, "FUERA_DE_ZONA", null);

        var result = await Controller(AllyToken()).GetMyOffers("corr-1");

        var body = Body(Assert.IsType<OkObjectResult>(result));
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal(abierta, body.GetProperty("items").EnumerateArray().Single().GetProperty("Id").GetGuid());
        Assert.Equal("corr-1", body.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Get_WithoutToken_Returns401_AndWithClientRole_Returns403()
    {
        var unauth = await Controller(null).GetMyOffers(null);
        var client = await Controller(TestTokenHelper.CreateTestToken(_tenantId, _usuarioId.ToString(), "cliente")).GetMyOffers(null);

        Assert.Equal(401, Assert.IsType<UnauthorizedObjectResult>(unauth).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(client).StatusCode);
        Assert.Equal("MANI-DSP-403", Body(client).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_WithANonUuidUser_Returns401_AndWithoutAllyProfile_Returns403V()
    {
        var notUuid = await Controller(TestTokenHelper.CreateTestToken(_tenantId, "no-uuid", "aliado")).GetMyOffers(null);
        var unknown = await Controller(TestTokenHelper.CreateTestToken(_tenantId, Guid.NewGuid().ToString(), "aliado")).GetMyOffers(null);

        Assert.IsType<UnauthorizedObjectResult>(notUuid);
        Assert.Equal(403, Assert.IsType<ObjectResult>(unknown).StatusCode);
        Assert.Equal("MANI-SOL-403V", Body(unknown).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_MapsCoreAndDatabaseFailures()
    {
        _core.Offers.Add(new AllyOffer { Id = Guid.NewGuid(), Estado = "PENDIENTE" });
        var noDb = new PostgresSolicitudAssignmentRepository(new ConfigurationBuilder().Build(), NullLogger<PostgresSolicitudAssignmentRepository>.Instance);
        var database = await Controller(AllyToken(), noDb).GetMyOffers(null);

        _core.Failure = new CoreAccessDeniedException("Core negó.", "MANI-DSP-403");
        var denied = await Controller(AllyToken()).GetMyOffers(null);
        _core.Failure = new CoreServiceUnavailableException("Core caído.");
        var unavailable = await Controller(AllyToken()).GetMyOffers(null);

        Assert.Equal("DATABASE_UNAVAILABLE", Body(database).GetProperty("code").GetString());
        Assert.Equal(403, Assert.IsType<ObjectResult>(denied).StatusCode);
        Assert.Equal(503, Assert.IsType<ObjectResult>(unavailable).StatusCode);
        Assert.Equal("CORE_SERVICE_UNAVAILABLE", Body(unavailable).GetProperty("code").GetString());
    }
}
