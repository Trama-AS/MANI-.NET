using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Mani.Dispatch.Controllers;
using ManiDispatch.Application.DTOs;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Entities;
using ManiDispatch.Infrastructure.Auth;
using ManiDispatch.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

[Collection("JwtAuthEnvironment")]
public class DispatchControllerTests
{
    private readonly Mock<IAllyRepository> _mockRepo;
    private readonly GetEligibleAlliesUseCase _useCase;
    private readonly DispatchController _controller;
    private readonly Guid _testTenantId;
    private readonly string _validJwtToken;

    public DispatchControllerTests()
    {
        Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", TestTokenHelper.DefaultTestSecret);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        _testTenantId = Guid.NewGuid();
        _validJwtToken = TestTokenHelper.CreateTestToken(_testTenantId, "user-client-1", "cliente");

        _mockRepo = new Mock<IAllyRepository>();
        _useCase = new GetEligibleAlliesUseCase(_mockRepo.Object);
        _controller = new DispatchController(_useCase)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        _controller.HttpContext.Request.Headers["X-Correlation-ID"] = "test-corr-id";
    }

    [Fact]
    public async Task MatchProfessionals_WithoutAuthHeader_Returns401Unauthorized()
    {
        // Act: No se envía Authorization header
        var request = new MatchRequestDto
        {
            ZonaId = Guid.NewGuid().ToString(),
            CategoriaId = Guid.NewGuid().ToString()
        };

        var result = await _controller.MatchProfessionals(request);

        // Assert
        var unauthResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(401, unauthResult.StatusCode);
    }

    [Fact]
    public async Task MatchProfessionals_WithCrossTenantSpoofing_Returns403Forbidden()
    {
        // Arrange: Token del tenant A, pero payload pide tenant B
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";

        var foreignTenantId = Guid.NewGuid();
        var request = new MatchRequestDto
        {
            TenantId = foreignTenantId.ToString(),
            ZonaId = Guid.NewGuid().ToString(),
            CategoriaId = Guid.NewGuid().ToString()
        };

        // Act
        var result = await _controller.MatchProfessionals(request);

        // Assert
        var forbiddenResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbiddenResult.StatusCode);
    }

    [Fact]
    public async Task MatchProfessionals_WithValidToken_Returns200WithMatchedAllies()
    {
        // Arrange
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";

        var zonaId = Guid.NewGuid();
        var categoriaId = Guid.NewGuid();

        var allies = new List<Ally>
        {
            new()
            {
                Id = Guid.NewGuid(),
                NombreRazonSocial = "Servicios Técnicos S.A.S.",
                Tipo = "empresa",
                EstadoVerificacion = "aprobado"
            }
        };

        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ReturnsAsync((allies, 1));

        var request = new MatchRequestDto
        {
            ZonaId = zonaId.ToString(),
            CategoriaId = categoriaId.ToString()
        };

        // Act
        var actionResult = await _controller.MatchProfessionals(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<MatchResponseDto>(okResult.Value);
        Assert.Equal(_testTenantId.ToString(), response.TenantId);
        Assert.Equal(zonaId.ToString(), response.ZonaId);
        Assert.Equal(categoriaId.ToString(), response.CategoriaId);
        Assert.Equal(1, response.TotalCount);
        Assert.Single(response.MatchedAllies);
        Assert.Equal("Servicios Técnicos S.A.S.", response.MatchedAllies.First().NombreRazonSocial);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_ResolvesZonaAndCategoriaFromSolicitud_WhenParamsOmitted()
    {
        // Arrange
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";

        var requestId = Guid.NewGuid();
        var dbZonaId = Guid.NewGuid();
        var dbCategoriaId = Guid.NewGuid();

        var solicitud = new SolicitudContext
        {
            Id = requestId,
            TenantId = _testTenantId,
            ZonaId = dbZonaId,
            CategoriaId = dbCategoriaId,
            Estado = "PENDIENTE"
        };

        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, _testTenantId))
                 .ReturnsAsync(solicitud);

        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ReturnsAsync((new List<Ally>(), 0));

        // Act: Se consulta por requestId sin pasar zona ni categoría por query param
        var actionResult = await _controller.GetEligibleAlliesForRequest(
            requestId.ToString(),
            zonaId: null,
            categoriaId: null,
            page: 1,
            pageSize: 10);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<MatchResponseDto>(okResult.Value);
        Assert.Equal(requestId.ToString(), response.RequestId);
        Assert.Equal(dbZonaId.ToString(), response.ZonaId);
        Assert.Equal(dbCategoriaId.ToString(), response.CategoriaId);

        _mockRepo.Verify(r => r.GetSolicitudContextAsync(requestId, _testTenantId), Times.Once);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_FiltersByTipo_WhenTipoQueryParamIsSent()
    {
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";
        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ReturnsAsync((new List<Ally>(), 0));

        var actionResult = await _controller.GetEligibleAlliesForRequest(
            Guid.NewGuid().ToString(),
            zonaId: Guid.NewGuid().ToString(),
            categoriaId: Guid.NewGuid().ToString(),
            page: 1,
            pageSize: 10,
            tipo: "persona_juridica");

        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<MatchResponseDto>(okResult.Value);
        Assert.Equal("PERSONA_JURIDICA", response.Tipo);
        _mockRepo.Verify(r => r.GetEligibleAlliesAsync(It.Is<MatchCriteria>(c =>
            c.Tipo == "PERSONA_JURIDICA" && c.TenantId == _testTenantId)), Times.Once);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_WithInvalidTipo_Returns400BadRequest()
    {
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";

        var actionResult = await _controller.GetEligibleAlliesForRequest(
            Guid.NewGuid().ToString(),
            zonaId: Guid.NewGuid().ToString(),
            categoriaId: Guid.NewGuid().ToString(),
            page: 1,
            pageSize: 10,
            tipo: "EMPRESA");

        Assert.IsType<BadRequestObjectResult>(actionResult);
        _mockRepo.Verify(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()), Times.Never);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_WhenSolicitudNotFound_Returns404NotFound()
    {
        // Arrange
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";

        var requestId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, _testTenantId))
                 .ReturnsAsync((SolicitudContext?)null);

        // Act
        var actionResult = await _controller.GetEligibleAlliesForRequest(
            requestId.ToString(),
            zonaId: null,
            categoriaId: null,
            page: 1,
            pageSize: 10);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(actionResult);
        Assert.Equal(404, notFoundResult.StatusCode);
    }

    [Fact]
    public void AcceptRequest_ReturnsOkOnFirst_AndConflictOnSecond()
    {
        // Arrange
        string reqId = "req-unique-" + Guid.NewGuid();
        var dto1 = new AcceptRequestDto("ally-first");
        var dto2 = new AcceptRequestDto("ally-second");

        // Act 1: Primer aliado acepta
        var result1 = _controller.AcceptRequest(reqId, dto1);

        // Act 2: Segundo aliado colisiona concurrentemente sobre la misma orden
        var result2 = _controller.AcceptRequest(reqId, dto2);

        // Assert
        Assert.IsType<OkObjectResult>(result1);
        var conflictResult = Assert.IsType<ConflictObjectResult>(result2);
        Assert.Equal(409, conflictResult.StatusCode);
    }

    [Fact]
    public async Task MatchProfessionals_WithAlteredToken_Returns401Unauthorized()
    {
        // Arrange
        var token = TestTokenHelper.CreateTestToken(_testTenantId, secret: "wrong-secret-that-does-not-match-at-least-32-chars!");
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {token}";

        var prevSecret = Environment.GetEnvironmentVariable("SUPABASE_JWT_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", TestTokenHelper.DefaultTestSecret);

            var request = new MatchRequestDto
            {
                ZonaId = Guid.NewGuid().ToString(),
                CategoriaId = Guid.NewGuid().ToString()
            };

            // Act
            var result = await _controller.MatchProfessionals(request);

            // Assert
            var unauthResult = Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Equal(401, unauthResult.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", prevSecret);
        }
    }

    [Fact]
    public async Task MatchProfessionals_WithExpiredToken_Returns401Unauthorized()
    {
        // Arrange
        var expiredToken = TestTokenHelper.CreateTestToken(
            _testTenantId, 
            secret: TestTokenHelper.DefaultTestSecret, 
            expires: DateTime.UtcNow.AddMinutes(-30));
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {expiredToken}";

        var prevSecret = Environment.GetEnvironmentVariable("SUPABASE_JWT_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", TestTokenHelper.DefaultTestSecret);

            var request = new MatchRequestDto
            {
                ZonaId = Guid.NewGuid().ToString(),
                CategoriaId = Guid.NewGuid().ToString()
            };

            // Act
            var result = await _controller.MatchProfessionals(request);

            // Assert
            var unauthResult = Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Equal(401, unauthResult.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", prevSecret);
        }
    }

    [Fact]
    public async Task MatchProfessionals_WithoutSecret_InProduction_Returns401Unauthorized()
    {
        // Arrange
        var token = TestTokenHelper.CreateTestToken(_testTenantId);
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {token}";

        var prevSecret = Environment.GetEnvironmentVariable("SUPABASE_JWT_SECRET");
        var prevEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", null);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");

            var request = new MatchRequestDto
            {
                ZonaId = Guid.NewGuid().ToString(),
                CategoriaId = Guid.NewGuid().ToString()
            };

            // Act
            var result = await _controller.MatchProfessionals(request);

            // Assert
            var unauthResult = Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Equal(401, unauthResult.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", prevSecret);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", prevEnv);
        }
    }

    [Fact]
    public void HealthCheck_ReturnsOkWithStatusUp()
    {
        var result = _controller.HealthCheck();
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, okResult.StatusCode);

        // Prueba de humo de QA-06 (SCRUM-1123): estado, nombre del servicio y eco del correlation id.
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(okResult.Value));
        Assert.Equal("UP", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("MANI-Dispatch-DotNet", json.RootElement.GetProperty("service").GetString());
        Assert.Equal("test-corr-id", json.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public void HealthCheck_WithoutCorrelationId_StillReturnsOk()
    {
        _controller.HttpContext.Request.Headers.Remove("X-Correlation-ID");

        var result = _controller.HealthCheck();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_WithInvalidGuid_Returns400BadRequest()
    {
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";

        var result = await _controller.GetEligibleAlliesForRequest(
            "not-a-valid-uuid", null, null, 1, 10);

        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequestResult.StatusCode);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_WithoutAuth_Returns401Unauthorized()
    {
        _controller.HttpContext.Request.Headers.Remove("Authorization");

        var result = await _controller.GetEligibleAlliesForRequest(
            Guid.NewGuid().ToString(), null, null, 1, 10);

        var unauthResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(401, unauthResult.StatusCode);
    }

    [Fact]
    public void AcceptRequest_WithNullOrEmptyAllyId_Returns400BadRequest()
    {
        var result = _controller.AcceptRequest("req-1", new AcceptRequestDto(""));
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task MatchProfessionals_WhenUseCaseThrowsArgumentException_Returns400BadRequest()
    {
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";

        // Solicitud sin ZonaId ni CategoriaId dispara ArgumentException en ExecuteAsync
        var request = new MatchRequestDto
        {
            ZonaId = "",
            CategoriaId = ""
        };

        var result = await _controller.MatchProfessionals(request);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
    }
}
