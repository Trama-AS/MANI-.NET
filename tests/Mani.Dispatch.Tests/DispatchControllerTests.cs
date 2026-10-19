using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Mani.Dispatch.Controllers;
using ManiDispatch.Application.DTOs;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Entities;
using ManiDispatch.Domain.Exceptions;
using ManiDispatch.Infrastructure.Auth;
using ManiDispatch.Infrastructure.Repositories;
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
    private readonly Mock<IDispatchPublisher> _mockPublisher;
    private readonly InMemorySolicitudAssignmentRepository _assignmentRepo;
    private readonly GetEligibleAlliesUseCase _getEligibleUseCase;
    private readonly OrchestrateDispatchUseCase _orchestrateUseCase;
    private readonly FakeAllyProfiles _allyProfiles = new();
    private readonly AcceptDispatchOfferUseCase _acceptUseCase;
    private readonly RejectDispatchOfferUseCase _rejectUseCase;
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
        _mockPublisher = new Mock<IDispatchPublisher>();
        _assignmentRepo = new InMemorySolicitudAssignmentRepository();

        _getEligibleUseCase = new GetEligibleAlliesUseCase(_mockRepo.Object);
        _orchestrateUseCase = new OrchestrateDispatchUseCase(_mockRepo.Object, _mockPublisher.Object);
        _acceptUseCase = new AcceptDispatchOfferUseCase(_assignmentRepo, _allyProfiles);
        _rejectUseCase = new RejectDispatchOfferUseCase(_assignmentRepo, _allyProfiles);

        _controller = new DispatchController(_getEligibleUseCase, _orchestrateUseCase, _acceptUseCase, _rejectUseCase)
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
        var request = new MatchRequestDto
        {
            ZonaId = Guid.NewGuid().ToString(),
            CategoriaId = Guid.NewGuid().ToString()
        };

        var result = await _controller.MatchProfessionals(request);

        var unauthResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(401, unauthResult.StatusCode);
    }

    [Fact]
    public async Task MatchProfessionals_WithCrossTenantSpoofing_Returns403Forbidden()
    {
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";

        var foreignTenantId = Guid.NewGuid();
        var request = new MatchRequestDto
        {
            TenantId = foreignTenantId.ToString(),
            ZonaId = Guid.NewGuid().ToString(),
            CategoriaId = Guid.NewGuid().ToString()
        };

        var result = await _controller.MatchProfessionals(request);

        var forbiddenResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbiddenResult.StatusCode);
    }

    [Fact]
    public async Task MatchProfessionals_WithValidToken_Returns200WithMatchedAllies()
    {
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
                EstadoVerificacion = "VERIFICADO"
            }
        };

        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ReturnsAsync((allies, 1));

        var request = new MatchRequestDto
        {
            ZonaId = zonaId.ToString(),
            CategoriaId = categoriaId.ToString()
        };

        var actionResult = await _controller.MatchProfessionals(request);

        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<MatchResponseDto>(okResult.Value);
        Assert.Equal(_testTenantId.ToString(), response.TenantId);
        Assert.Equal(zonaId.ToString(), response.ZonaId);
        Assert.Equal(categoriaId.ToString(), response.CategoriaId);
        Assert.Equal(1, response.TotalCount);
        Assert.Single(response.MatchedAllies);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_ResolvesZonaAndCategoriaFromSolicitud_WhenParamsOmitted()
    {
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

        var actionResult = await _controller.GetEligibleAlliesForRequest(
            requestId.ToString(),
            zonaId: null,
            categoriaId: null,
            page: 1,
            pageSize: 10);

        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<MatchResponseDto>(okResult.Value);
        Assert.Equal(requestId.ToString(), response.RequestId);
        Assert.Equal(dbZonaId.ToString(), response.ZonaId);
        Assert.Equal(dbCategoriaId.ToString(), response.CategoriaId);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_WhenSolicitudNotFound_Returns404NotFound()
    {
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";

        var requestId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, _testTenantId))
                 .ReturnsAsync((SolicitudContext?)null);

        var actionResult = await _controller.GetEligibleAlliesForRequest(
            requestId.ToString(),
            zonaId: null,
            categoriaId: null,
            page: 1,
            pageSize: 10);

        var notFoundResult = Assert.IsType<NotFoundObjectResult>(actionResult);
        Assert.Equal(404, notFoundResult.StatusCode);
    }

    [Fact]
    public async Task OrchestrateDispatch_WithoutAuthHeader_Returns401Unauthorized()
    {
        var dto = new OrchestrateRequestDto(Guid.NewGuid().ToString());

        var result = await _controller.OrchestrateDispatch(dto);

        var unauthResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(401, unauthResult.StatusCode);
    }

    [Fact]
    public async Task OrchestrateDispatch_WithCrossTenantSpoofing_Returns403Forbidden()
    {
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";

        var dto = new OrchestrateRequestDto(Guid.NewGuid().ToString())
        {
            TenantId = Guid.NewGuid().ToString() // Tenant diferente al del token
        };

        var result = await _controller.OrchestrateDispatch(dto);

        var forbiddenResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbiddenResult.StatusCode);
    }

    [Fact]
    public async Task OrchestrateDispatch_WhenValid_Returns200OkWithDispatchedOffers()
    {
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";

        var requestId = Guid.NewGuid();
        var zonaId = Guid.NewGuid();
        var categoriaId = Guid.NewGuid();

        var solicitud = new SolicitudContext
        {
            Id = requestId,
            TenantId = _testTenantId,
            ZonaId = zonaId,
            CategoriaId = categoriaId,
            Estado = "PENDIENTE"
        };

        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, _testTenantId))
                 .ReturnsAsync(solicitud);

        var allies = new List<Ally>
        {
            new() { Id = Guid.NewGuid(), NombreRazonSocial = "Aliado Experto", Tipo = "independiente", EstadoVerificacion = "VERIFICADO" }
        };

        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ReturnsAsync((allies, 1));

        var dto = new OrchestrateRequestDto(requestId.ToString());

        var result = await _controller.OrchestrateDispatch(dto);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<OrchestrateResponseDto>(okResult.Value);
        Assert.Equal("DISPATCHED", response.Status);
        Assert.Equal(1, response.CandidatesCount);
        Assert.Single(response.CandidatesNotified);
    }

    [Fact]
    public async Task OrchestrateDispatch_WhenSolicitudNotFound_Returns404NotFound()
    {
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";

        var requestId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, _testTenantId))
                 .ReturnsAsync((SolicitudContext?)null);

        var dto = new OrchestrateRequestDto(requestId.ToString());

        var result = await _controller.OrchestrateDispatch(dto);

        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(404, notFoundResult.StatusCode);
    }

    [Fact]
    public async Task AcceptRequest_ReturnsOkOnFirst_ConflictOnSecond_AndOkOnWinnerRetry()
    {
        string reqId = Guid.NewGuid().ToString();
        var ally1UserId = Guid.NewGuid();
        var ally2UserId = Guid.NewGuid();

        _allyProfiles.RegisterAlly(new AllyProfile(Guid.NewGuid(), _testTenantId, ally1UserId, "VERIFICADO"));
        _allyProfiles.RegisterAlly(new AllyProfile(Guid.NewGuid(), _testTenantId, ally2UserId, "VERIFICADO"));

        var ally1Token = TestTokenHelper.CreateTestToken(_testTenantId, ally1UserId.ToString(), "aliado");
        var ally2Token = TestTokenHelper.CreateTestToken(_testTenantId, ally2UserId.ToString(), "aliado");

        // Act 1: Primer aliado acepta con su token
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {ally1Token}";
        var result1 = await _controller.AcceptRequest(reqId);

        // Act 2: Segundo aliado colisiona con token distinto
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {ally2Token}";
        var result2 = await _controller.AcceptRequest(reqId);

        // Act 3: Primer aliado reintenta (idempotente)
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {ally1Token}";
        var result3 = await _controller.AcceptRequest(reqId);

        // Assert
        var okResult1 = Assert.IsType<OkObjectResult>(result1);
        Assert.Equal(200, okResult1.StatusCode);

        var conflictResult = Assert.IsType<ConflictObjectResult>(result2);
        Assert.Equal(409, conflictResult.StatusCode);

        var okResult3 = Assert.IsType<OkObjectResult>(result3);
        Assert.Equal(200, okResult3.StatusCode);
    }

    [Fact]
    public async Task AcceptRequest_WhenAllyProfileDoesNotExist_Returns403Forbidden()
    {
        string reqId = Guid.NewGuid().ToString();
        var unknownAllyUserId = Guid.NewGuid();
        var token = TestTokenHelper.CreateTestToken(_testTenantId, unknownAllyUserId.ToString(), "aliado");

        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {token}";
        var result = await _controller.AcceptRequest(reqId);

        var forbiddenResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbiddenResult.StatusCode);
    }

    [Fact]
    public async Task AcceptRequest_WhenDatabaseFails_Returns503ServiceUnavailable_AndDoesNotFallbackToMemory()
    {
        var mockConfig = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();
        var postgresRepo = new PostgresSolicitudAssignmentRepository(
            mockConfig.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresSolicitudAssignmentRepository>.Instance);
        // El aliado existe en Core (verificado): el fallo que se prueba es el de la base de Dispatch al asignar.
        var allyUserId = Guid.NewGuid();
        var profiles = new FakeAllyProfiles();
        profiles.RegisterAlly(new AllyProfile(Guid.NewGuid(), _testTenantId, allyUserId, "VERIFICADO"));
        var controller = new DispatchController(
            _getEligibleUseCase,
            _orchestrateUseCase,
            new AcceptDispatchOfferUseCase(postgresRepo, profiles),
            new RejectDispatchOfferUseCase(postgresRepo, profiles))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var token = TestTokenHelper.CreateTestToken(_testTenantId, allyUserId.ToString(), "aliado");
        controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {token}";

        var result = await controller.AcceptRequest(Guid.NewGuid().ToString());

        var unavailableResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, unavailableResult.StatusCode);
    }

    [Fact]
    public async Task RejectRequest_WithAuthenticatedAlly_ReturnsOkRejected()
    {
        string reqId = Guid.NewGuid().ToString();
        var allyUserId = Guid.NewGuid();
        _allyProfiles.RegisterAlly(new AllyProfile(Guid.NewGuid(), _testTenantId, allyUserId, "VERIFICADO"));
        var allyToken = TestTokenHelper.CreateTestToken(_testTenantId, allyUserId.ToString(), "aliado");

        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {allyToken}";
        var result = await _controller.RejectRequest(reqId, new RejectRequestDto("HORARIO_INCOMPATIBLE"));

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, okResult.StatusCode);
    }

    [Fact]
    public async Task RejectRequest_WhenDatabaseFails_Returns503ServiceUnavailable()
    {
        var mockConfig = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();
        var postgresRepo = new PostgresSolicitudAssignmentRepository(
            mockConfig.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresSolicitudAssignmentRepository>.Instance);
        // El aliado existe en Core (verificado): el fallo que se prueba es el de la base de Dispatch al asignar.
        var allyUserId = Guid.NewGuid();
        var profiles = new FakeAllyProfiles();
        profiles.RegisterAlly(new AllyProfile(Guid.NewGuid(), _testTenantId, allyUserId, "VERIFICADO"));
        var controller = new DispatchController(
            _getEligibleUseCase,
            _orchestrateUseCase,
            new AcceptDispatchOfferUseCase(postgresRepo, profiles),
            new RejectDispatchOfferUseCase(postgresRepo, profiles))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var token = TestTokenHelper.CreateTestToken(_testTenantId, allyUserId.ToString(), "aliado");
        controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {token}";

        var result = await controller.RejectRequest(Guid.NewGuid().ToString(), new RejectRequestDto("NO_DISPONIBLE"));

        var unavailableResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, unavailableResult.StatusCode);
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
    public async Task AcceptRequest_WithNonAliadoRole_Returns403Forbidden()
    {
        var clientToken = TestTokenHelper.CreateTestToken(_testTenantId, "client-1", "cliente");
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {clientToken}";

        var result = await _controller.AcceptRequest(Guid.NewGuid().ToString());
        var forbiddenResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbiddenResult.StatusCode);
    }

    [Fact]
    public async Task RejectRequest_WithNonAliadoRole_Returns403Forbidden()
    {
        var clientToken = TestTokenHelper.CreateTestToken(_testTenantId, "client-1", "cliente");
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {clientToken}";

        var result = await _controller.RejectRequest(Guid.NewGuid().ToString(), new RejectRequestDto("Motivo"));
        var forbiddenResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbiddenResult.StatusCode);
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

    [Fact]
    public async Task OrchestrateDispatch_WithAliadoRole_Returns403Forbidden()
    {
        // Arrange
        var aliadoToken = TestTokenHelper.CreateTestToken(_testTenantId, "aliado-1", "aliado");
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {aliadoToken}";

        var request = new OrchestrateRequestDto
        {
            RequestId = Guid.NewGuid().ToString()
        };

        // Act
        var result = await _controller.OrchestrateDispatch(request);

        // Assert: aliado rol debe recibir 403 Forbidden
        var forbiddenResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbiddenResult.StatusCode);
    }

    [Fact]
    public async Task OrchestrateDispatch_WithAdminTenantRole_WhenSolicitudNotFound_Returns404()
    {
        // Arrange
        var adminToken = TestTokenHelper.CreateTestToken(_testTenantId, "admin-1", "admin_tenant");
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {adminToken}";

        var requestId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, _testTenantId))
                 .ReturnsAsync((SolicitudContext?)null);

        var request = new OrchestrateRequestDto
        {
            RequestId = requestId.ToString()
        };

        // Act
        var result = await _controller.OrchestrateDispatch(request);

        // Assert
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(404, notFound.StatusCode);
    }

    [Fact]
    public async Task MatchProfessionals_WhenCoreIsUnavailable_Returns503()
    {
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";
        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ThrowsAsync(new CoreServiceUnavailableException("Core no respondió."));

        var result = await _controller.MatchProfessionals(new MatchRequestDto
        {
            ZonaId = Guid.NewGuid().ToString(),
            CategoriaId = Guid.NewGuid().ToString()
        });

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, response.StatusCode);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_WhenCoreIsUnavailableResolvingSolicitud_Returns503()
    {
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";
        var requestId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, _testTenantId))
                 .ThrowsAsync(new CoreServiceUnavailableException("Core no respondió."));

        var result = await _controller.GetEligibleAlliesForRequest(requestId.ToString(), null, null, 1, 10);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, response.StatusCode);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_WhenCoreIsUnavailableListingAllies_Returns503()
    {
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {_validJwtToken}";
        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ThrowsAsync(new CoreServiceUnavailableException("Core no respondió."));

        var result = await _controller.GetEligibleAlliesForRequest(
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), 1, 10);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, response.StatusCode);
    }

    [Fact]
    public async Task OrchestrateDispatch_WhenCoreIsUnavailable_Returns503()
    {
        var adminToken = TestTokenHelper.CreateTestToken(_testTenantId, "admin-1", "admin_tenant");
        _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {adminToken}";
        var requestId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, _testTenantId))
                 .ThrowsAsync(new CoreServiceUnavailableException("Core no respondió."));

        var result = await _controller.OrchestrateDispatch(new OrchestrateRequestDto { RequestId = requestId.ToString() });

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, response.StatusCode);
    }
}
