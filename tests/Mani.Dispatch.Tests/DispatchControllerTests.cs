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
    private readonly InMemoryCoreAssignments _coreAssignments;
    private readonly AcceptDispatchOfferUseCase _acceptUseCase;
    private readonly RejectDispatchOfferUseCase _rejectUseCase;
    private readonly DispatchController _controller;
    private readonly Guid _testTenantId;
    private readonly Guid _clientUserId = Guid.NewGuid();
    private readonly string _validJwtToken;

    public DispatchControllerTests()
    {
        Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", TestTokenHelper.DefaultTestSecret);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        _testTenantId = Guid.NewGuid();
        _validJwtToken = TestTokenHelper.CreateTestToken(_testTenantId, _clientUserId.ToString(), "cliente");

        _mockRepo = new Mock<IAllyRepository>();
        _mockPublisher = new Mock<IDispatchPublisher>();
        _assignmentRepo = new InMemorySolicitudAssignmentRepository();

        _getEligibleUseCase = new GetEligibleAlliesUseCase(_mockRepo.Object, _assignmentRepo);
        _orchestrateUseCase = new OrchestrateDispatchUseCase(_mockRepo.Object, _mockPublisher.Object, _assignmentRepo);
        // Core identifica al aliado por el JWT reenviado: el doble lo lee de la cabecera de la petición en curso.
        _coreAssignments = new InMemoryCoreAssignments(_allyProfiles,
            () => Guid.Parse(JwtAuthHelper.Authenticate(_controller!.HttpContext.Request.Headers.Authorization).UserId!));
        _acceptUseCase = new AcceptDispatchOfferUseCase(_assignmentRepo, _allyProfiles, _coreAssignments);
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
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";

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
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";

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
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";

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
            page: 1,
            pageSize: 10);

        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<MatchResponseDto>(okResult.Value);
        Assert.Equal(requestId.ToString(), response.RequestId);
        Assert.Equal(dbZonaId.ToString(), response.ZonaId);
        Assert.Equal(dbCategoriaId.ToString(), response.CategoriaId);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_FiltersByTipo_WhenTipoQueryParamIsSent()
    {
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";
        var requestId = SetupSolicitud();
        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ReturnsAsync((new List<Ally>(), 0));

        var actionResult = await _controller.GetEligibleAlliesForRequest(
            requestId.ToString(),
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
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";
        var requestId = SetupSolicitud();

        var actionResult = await _controller.GetEligibleAlliesForRequest(
            requestId.ToString(),
            page: 1,
            pageSize: 10,
            tipo: "EMPRESA");

        Assert.IsType<BadRequestObjectResult>(actionResult);
        _mockRepo.Verify(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()), Times.Never);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_WhenSolicitudNotFound_Returns404NotFound()
    {
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";

        var requestId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, _testTenantId))
                 .ReturnsAsync((SolicitudContext?)null);

        var actionResult = await _controller.GetEligibleAlliesForRequest(
            requestId.ToString(),
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
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";

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
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";

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
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";

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
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {ally1Token}";
        var result1 = await _controller.AcceptRequest(reqId);

        // Act 2: Segundo aliado colisiona con token distinto
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {ally2Token}";
        var result2 = await _controller.AcceptRequest(reqId);

        // Act 3: Primer aliado reintenta (idempotente)
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {ally1Token}";
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

        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";
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
            new AcceptDispatchOfferUseCase(postgresRepo, profiles, new InMemoryCoreAssignments(profiles, () => allyUserId)),
            new RejectDispatchOfferUseCase(postgresRepo, profiles))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var token = TestTokenHelper.CreateTestToken(_testTenantId, allyUserId.ToString(), "aliado");
        controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";

        var result = await controller.AcceptRequest(Guid.NewGuid().ToString());

        var unavailableResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, unavailableResult.StatusCode);
    }

    [Fact]
    public async Task RejectRequest_WithAuthenticatedAlly_ReturnsOkRejected_AndRepeatingItIsARetry()
    {
        var reqId = RegisterOffer();
        var (allyId, allyToken) = RegisterAllyToken();

        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {allyToken}";
        var result = await _controller.RejectRequest(reqId.ToString(), new RejectRequestDto("sin_disponibilidad"));
        var retry = await _controller.RejectRequest(reqId.ToString(), new RejectRequestDto("OTRO", "Tengo otro compromiso"));

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, okResult.StatusCode);
        Assert.Equal("SIN_DISPONIBILIDAD", Body(result).GetProperty("motivo").GetString());
        Assert.False(Body(result).GetProperty("isRetry").GetBoolean());
        Assert.True(Body(Assert.IsType<OkObjectResult>(retry)).GetProperty("isRetry").GetBoolean());
        Assert.Equal("SIN_DISPONIBILIDAD", _assignmentRepo.MotivoDe(reqId, allyId));
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
            new AcceptDispatchOfferUseCase(postgresRepo, profiles, new InMemoryCoreAssignments(profiles, () => allyUserId)),
            new RejectDispatchOfferUseCase(postgresRepo, profiles))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var token = TestTokenHelper.CreateTestToken(_testTenantId, allyUserId.ToString(), "aliado");
        controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";

        var reqId = Guid.NewGuid();
        profiles.RegisterSolicitud(new SolicitudContext { Id = reqId, TenantId = _testTenantId, Estado = "PENDIENTE" });
        var result = await controller.RejectRequest(reqId.ToString(), new RejectRequestDto("FUERA_DE_ZONA"));

        var unavailableResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, unavailableResult.StatusCode);
    }

    [Fact]
    public async Task MatchProfessionals_WithAlteredToken_Returns401Unauthorized()
    {
        // Arrange
        var token = TestTokenHelper.CreateTestToken(_testTenantId, secret: "wrong-secret-that-does-not-match-at-least-32-chars!");
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";

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
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {expiredToken}";

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
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";

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
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";

        var result = await _controller.GetEligibleAlliesForRequest(
            "not-a-valid-uuid", 1, 10);

        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequestResult.StatusCode);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_WithoutAuth_Returns401Unauthorized()
    {
        _controller.HttpContext.Request.Headers.Remove("Authorization");

        var result = await _controller.GetEligibleAlliesForRequest(
            Guid.NewGuid().ToString(), 1, 10);

        var unauthResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(401, unauthResult.StatusCode);
    }

    [Fact]
    public async Task AcceptRequest_WithNonAliadoRole_Returns403Forbidden()
    {
        var clientToken = TestTokenHelper.CreateTestToken(_testTenantId, "client-1", "cliente");
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {clientToken}";

        var result = await _controller.AcceptRequest(Guid.NewGuid().ToString());
        var forbiddenResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbiddenResult.StatusCode);
    }

    [Fact]
    public async Task RejectRequest_WithNonAliadoRole_Returns403Forbidden()
    {
        var clientToken = TestTokenHelper.CreateTestToken(_testTenantId, "client-1", "cliente");
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {clientToken}";

        var result = await _controller.RejectRequest(Guid.NewGuid().ToString(), new RejectRequestDto("Motivo"));
        var forbiddenResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbiddenResult.StatusCode);
    }

    [Fact]
    public async Task MatchProfessionals_WhenUseCaseThrowsArgumentException_Returns400BadRequest()
    {
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";

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
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {aliadoToken}";

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
        var adminToken = TestTokenHelper.CreateTestToken(_testTenantId, Guid.NewGuid().ToString(), "admin_tenant");
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {adminToken}";

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
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";
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
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";
        var requestId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, _testTenantId))
                 .ThrowsAsync(new CoreServiceUnavailableException("Core no respondió."));

        var result = await _controller.GetEligibleAlliesForRequest(requestId.ToString(), 1, 10);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, response.StatusCode);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_WhenCoreIsUnavailableListingAllies_Returns503()
    {
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";
        var requestId = SetupSolicitud();
        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ThrowsAsync(new CoreServiceUnavailableException("Core no respondió."));

        var result = await _controller.GetEligibleAlliesForRequest(requestId.ToString(), 1, 10);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, response.StatusCode);
    }

    [Fact]
    public async Task OrchestrateDispatch_WhenCoreIsUnavailable_Returns503()
    {
        var adminToken = TestTokenHelper.CreateTestToken(_testTenantId, Guid.NewGuid().ToString(), "admin_tenant");
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {adminToken}";
        var requestId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, _testTenantId))
                 .ThrowsAsync(new CoreServiceUnavailableException("Core no respondió."));

        var result = await _controller.OrchestrateDispatch(new OrchestrateRequestDto { RequestId = requestId.ToString() });

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, response.StatusCode);
    }

    // --- Decisión 6A, rechazos y códigos (SCRUM-1402..1405) ---

    private Guid SetupSolicitud(string estado = "PENDIENTE")
    {
        var requestId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, _testTenantId))
                 .ReturnsAsync(new SolicitudContext
                 {
                     Id = requestId, TenantId = _testTenantId, ZonaId = Guid.NewGuid(), CategoriaId = Guid.NewGuid(), Estado = estado
                 });
        return requestId;
    }

    /// <summary>Solicitud que Core entrega como contexto al aliado (tiene oferta propia).</summary>
    private Guid RegisterOffer(string estado = "PENDIENTE", bool esMia = false)
    {
        var requestId = Guid.NewGuid();
        _allyProfiles.RegisterSolicitud(new SolicitudContext { Id = requestId, TenantId = _testTenantId, Estado = estado, EsMia = esMia });
        return requestId;
    }

    private (Guid AllyId, string Token) RegisterAllyToken()
    {
        var allyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _allyProfiles.RegisterAlly(new AllyProfile(allyId, _testTenantId, userId, "VERIFICADO"));
        return (allyId, TestTokenHelper.CreateTestToken(_testTenantId, userId.ToString(), "aliado"));
    }

    private static System.Text.Json.JsonElement Body(IActionResult result)
    {
        var value = Assert.IsAssignableFrom<ObjectResult>(result).Value;
        return System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(value)).RootElement;
    }

    [Fact]
    public async Task AcceptRequest_Conflict_CarriesTheCode_AndDoesNotRevealTheWinner()
    {
        var reqId = Guid.NewGuid().ToString();
        var (_, winnerToken) = RegisterAllyToken();
        var (_, loserToken) = RegisterAllyToken();

        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {winnerToken}";
        await _controller.AcceptRequest(reqId);
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {loserToken}";
        var result = await _controller.AcceptRequest(reqId);

        Assert.IsType<ConflictObjectResult>(result);
        var body = Body(result);
        Assert.Equal("MANI-SOL-409", body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("assignedTo", out _));
        Assert.Equal("test-corr-id", body.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task AcceptRequest_SendsTheIdempotencyKeyAndCorrelationIdToCore_AndMarksTheRetry()
    {
        var reqId = Guid.NewGuid();
        var (allyId, token) = RegisterAllyToken();
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";

        // Idempotency-Key llega por model binding ([FromHeader]); llamando la acción directamente se pasa como argumento.
        var first = await _controller.AcceptRequest(reqId.ToString(), "toque-1");
        var retry = await _controller.AcceptRequest(reqId.ToString(), "toque-1");

        Assert.Equal((reqId, "toque-1", "test-corr-id"), _coreAssignments.LastCommand);
        Assert.False(Body(first).GetProperty("isRetry").GetBoolean());
        Assert.Equal(allyId, Body(first).GetProperty("assignedAllyId").GetGuid());
        Assert.True(Body(retry).GetProperty("isRetry").GetBoolean());
    }

    [Fact]
    public async Task AcceptRequest_AfterRejectingIt_Returns409R_WithoutCallingCore()
    {
        var reqId = RegisterOffer();
        var (_, token) = RegisterAllyToken();
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";
        await _controller.RejectRequest(reqId.ToString(), new RejectRequestDto("SIN_DISPONIBILIDAD"));

        var result = await _controller.AcceptRequest(reqId.ToString());

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("MANI-DSP-409R", Body(result).GetProperty("code").GetString());
        Assert.Equal(0, _coreAssignments.Calls);
    }

    [Theory]
    [InlineData(AssignmentStatus.InvalidState, "MANI-SOL-409E", 409)]
    [InlineData(AssignmentStatus.NotFound, "MANI-SOL-404", 404)]
    [InlineData(AssignmentStatus.Forbidden, "MANI-SOL-403O", 403)]
    [InlineData(AssignmentStatus.Forbidden, "MANI-SOL-403E", 403)]
    public async Task AcceptRequest_MapsTheCoreOutcome_ToStatusAndCode(AssignmentStatus status, string code, int expected)
    {
        var (_, token) = RegisterAllyToken();
        var core = new Mock<ICoreAssignmentClient>();
        core.Setup(c => c.AssignAsync(It.IsAny<Guid>(), _testTenantId, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new AssignmentResult(status, null, "Core dijo que no.", code));
        var controller = ControllerWith(new AcceptDispatchOfferUseCase(_assignmentRepo, _allyProfiles, core.Object), token);

        var result = await controller.AcceptRequest(Guid.NewGuid().ToString());

        Assert.Equal(expected, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal(code, Body(result).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AcceptRequest_WhenCoreFailsOrDeniesTheSession_Returns503Or403()
    {
        var (_, token) = RegisterAllyToken();
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";

        _coreAssignments.Failure = new CoreServiceUnavailableException("Core no respondió.");
        var unavailable = await _controller.AcceptRequest(Guid.NewGuid().ToString());
        _coreAssignments.Failure = new CoreAccessDeniedException("Sesión rechazada.", "MANI-SOL-401");
        var denied = await _controller.AcceptRequest(Guid.NewGuid().ToString());

        Assert.Equal(503, Assert.IsType<ObjectResult>(unavailable).StatusCode);
        Assert.Equal("CORE_SERVICE_UNAVAILABLE", Body(unavailable).GetProperty("code").GetString());
        Assert.Equal(403, Assert.IsType<ObjectResult>(denied).StatusCode);
        Assert.Equal("MANI-SOL-401", Body(denied).GetProperty("code").GetString());
    }

    [Fact]
    public async Task RejectRequest_WithAReasonOutsideTheCatalog_Returns422_AndStoresNothing()
    {
        var reqId = RegisterOffer();
        var (_, token) = RegisterAllyToken();
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";

        var result = await _controller.RejectRequest(reqId.ToString(), new RejectRequestDto("NO_DISPONIBLE"));

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.Equal(422, unprocessable.StatusCode);
        Assert.Equal("MANI-SOL-422M", Body(result).GetProperty("code").GetString());
        Assert.Equal(0, _assignmentRepo.RejectionCount);
    }

    [Fact]
    public async Task RejectRequest_WithoutBody_Returns422_AndStoresNothing()
    {
        // SCRUM-1617, C1/C3: el motivo es obligatorio y el servidor no inventa ninguno.
        var reqId = RegisterOffer();
        var (_, token) = RegisterAllyToken();
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";

        var result = await _controller.RejectRequest(reqId.ToString(), null);

        Assert.Equal(422, Assert.IsType<UnprocessableEntityObjectResult>(result).StatusCode);
        Assert.Equal("MANI-SOL-422M", Body(result).GetProperty("code").GetString());
        Assert.Equal(0, _assignmentRepo.RejectionCount);
    }

    [Theory]
    [InlineData(null, null, "MANI-SOL-422M")]
    [InlineData("", null, "MANI-SOL-422M")]
    [InlineData("   ", null, "MANI-SOL-422M")]
    [InlineData("OTRO", null, "MANI-SOL-422D")]
    [InlineData("OTRO", "   ", "MANI-SOL-422D")]
    [InlineData("OTRO", "ok", "MANI-SOL-422D")]
    [InlineData("SIN_DISPONIBILIDAD", "Estoy de viaje", "MANI-SOL-422D")]
    public async Task RejectRequest_WithAnInvalidReasonOrDetail_Returns422_AndStoresNothing(string? motivo, string? detalle, string code)
    {
        var reqId = RegisterOffer();
        var (_, token) = RegisterAllyToken();
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";

        var result = await _controller.RejectRequest(reqId.ToString(), new RejectRequestDto(motivo, detalle));

        Assert.Equal(422, Assert.IsType<UnprocessableEntityObjectResult>(result).StatusCode);
        Assert.Equal(code, Body(result).GetProperty("code").GetString());
        Assert.Equal(0, _assignmentRepo.RejectionCount);
    }

    [Fact]
    public async Task RejectRequest_WithOtroAndDetail_StoresTheTrimmedDetail_AndReturnsIt()
    {
        var reqId = RegisterOffer();
        var (allyId, token) = RegisterAllyToken();
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";

        var result = await _controller.RejectRequest(reqId.ToString(), new RejectRequestDto("otro", "  Mi herramienta está en reparación  "));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("OTRO", _assignmentRepo.MotivoDe(reqId, allyId));
        Assert.Equal("Mi herramienta está en reparación", _assignmentRepo.DetalleDe(reqId, allyId));
        Assert.Equal("Mi herramienta está en reparación", Body(result).GetProperty("detalle").GetString());
    }

    [Fact]
    public async Task RejectRequest_DetailOf280Characters_IsAccepted_281_IsRejected()
    {
        var (_, token) = RegisterAllyToken();
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";

        var limite = await _controller.RejectRequest(RegisterOffer().ToString(), new RejectRequestDto("OTRO", new string('a', 280)));
        var excedido = await _controller.RejectRequest(RegisterOffer().ToString(), new RejectRequestDto("OTRO", new string('a', 281)));

        Assert.IsType<OkObjectResult>(limite);
        Assert.Equal("MANI-SOL-422D", Body(excedido).GetProperty("code").GetString());
        Assert.Equal(1, _assignmentRepo.RejectionCount);
    }

    [Fact]
    public async Task RejectRequest_WithoutReason_WhenTheDatabaseIsDown_Returns422_Not503()
    {
        // C4: la validación va antes de cualquier acceso a Core o a la base. Mismo escenario que
        // RejectRequest_WhenDatabaseFails_Returns503ServiceUnavailable (aliado verificado, solicitud PENDIENTE y
        // base caída), que con un motivo válido responde 503; sin motivo debe ser 422.
        var postgresRepo = new PostgresSolicitudAssignmentRepository(
            new Mock<Microsoft.Extensions.Configuration.IConfiguration>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresSolicitudAssignmentRepository>.Instance);
        var allyUserId = Guid.NewGuid();
        var profiles = new FakeAllyProfiles();
        profiles.RegisterAlly(new AllyProfile(Guid.NewGuid(), _testTenantId, allyUserId, "VERIFICADO"));
        var reqId = Guid.NewGuid();
        profiles.RegisterSolicitud(new SolicitudContext { Id = reqId, TenantId = _testTenantId, Estado = "PENDIENTE" });
        var controller = new DispatchController(
            _getEligibleUseCase,
            _orchestrateUseCase,
            new AcceptDispatchOfferUseCase(postgresRepo, profiles, new InMemoryCoreAssignments(profiles, () => allyUserId)),
            new RejectDispatchOfferUseCase(postgresRepo, profiles))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var token = TestTokenHelper.CreateTestToken(_testTenantId, allyUserId.ToString(), "aliado");
        controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";

        var sinMotivo = await controller.RejectRequest(reqId.ToString(), new RejectRequestDto(null));
        var otroSinDetalle = await controller.RejectRequest(reqId.ToString(), new RejectRequestDto("OTRO"));

        Assert.Equal(422, Assert.IsType<UnprocessableEntityObjectResult>(sinMotivo).StatusCode);
        Assert.Equal(422, Assert.IsType<UnprocessableEntityObjectResult>(otroSinDetalle).StatusCode);
    }

    [Theory]
    [InlineData(false, "PENDIENTE", true, 404, "MANI-DSP-404")]
    [InlineData(true, "ASIGNADA", false, 409, "MANI-DSP-409A")]
    [InlineData(false, "ASIGNADA", false, 409, "MANI-DSP-409E")]
    [InlineData(false, "CANCELADA", false, 409, "MANI-DSP-409E")]
    public async Task RejectRequest_WhenTheRequestCannotBeRejected_ReturnsTheCode_AndStoresNothing(
        bool esMia, string estado, bool unknown, int expected, string code)
    {
        var reqId = unknown ? Guid.NewGuid() : RegisterOffer(estado, esMia);
        var (_, token) = RegisterAllyToken();
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";

        var result = await _controller.RejectRequest(reqId.ToString(), new RejectRequestDto("NO_ES_MI_ESPECIALIDAD"));

        Assert.Equal(expected, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal(code, Body(result).GetProperty("code").GetString());
        Assert.Equal(0, _assignmentRepo.RejectionCount);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_ExcludesTheAlliesThatRejected_ReadingWithTheClientContext()
    {
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";
        var requestId = SetupSolicitud();
        var rechazo = Guid.NewGuid();
        await _assignmentRepo.RejectRequestAsync(requestId, _testTenantId, Guid.NewGuid(), rechazo, "SIN_DISPONIBILIDAD", null);
        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>())).ReturnsAsync((new List<Ally>(), 0));

        var result = await _controller.GetEligibleAlliesForRequest(requestId.ToString(), 1, 10);

        Assert.IsType<OkObjectResult>(result);
        _mockRepo.Verify(r => r.GetEligibleAlliesAsync(It.Is<MatchCriteria>(c =>
            c.ExcludeAllyIds.Contains(rechazo) && c.CorrelationId == "test-corr-id")), Times.Once);
        Assert.Contains((_clientUserId, "cliente"), _assignmentRepo.Contexts);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_ForAnAlly_Returns403_WithoutAskingCore()
    {
        var (_, token) = RegisterAllyToken();
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";

        var result = await _controller.GetEligibleAlliesForRequest(Guid.NewGuid().ToString(), 1, 10);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal("MANI-DSP-403", Body(result).GetProperty("code").GetString());
        _mockRepo.Verify(r => r.GetSolicitudContextAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetEligibleAlliesForRequest_WhenCoreDeniesTheSession_Returns403Not503()
    {
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";
        var requestId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, _testTenantId))
                 .ThrowsAsync(new CoreAccessDeniedException("Core negó el acceso."));

        var result = await _controller.GetEligibleAlliesForRequest(requestId.ToString(), 1, 10);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task OrchestrateDispatch_WhenTheRequestIsNotPending_Returns409E_AndPublishesNothing()
    {
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";
        var requestId = SetupSolicitud("ASIGNADA");

        var result = await _controller.OrchestrateDispatch(new OrchestrateRequestDto(requestId.ToString()));

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("MANI-DSP-409E", Body(result).GetProperty("code").GetString());
        _mockPublisher.Verify(p => p.PublishOfferAsync(It.IsAny<DispatchOffer>()), Times.Never);
    }

    [Fact]
    public async Task OrchestrateDispatch_DoesNotOfferAgainToTheAlliesThatRejected()
    {
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";
        var requestId = SetupSolicitud();
        var rechazo = Guid.NewGuid();
        await _assignmentRepo.RejectRequestAsync(requestId, _testTenantId, Guid.NewGuid(), rechazo, "FUERA_DE_ZONA", null);
        _mockPublisher.Setup(p => p.GetOffersByRequestAsync(requestId, _testTenantId)).ReturnsAsync(new List<DispatchOffer>());
        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>())).ReturnsAsync((new List<Ally>(), 0));

        var result = await _controller.OrchestrateDispatch(new OrchestrateRequestDto(requestId.ToString()));

        Assert.IsType<OkObjectResult>(result);
        _mockRepo.Verify(r => r.GetEligibleAlliesAsync(It.Is<MatchCriteria>(c =>
            c.ExcludeAllyIds.Contains(rechazo) && c.CorrelationId == "test-corr-id")), Times.Once);
    }

    [Fact]
    public async Task OrchestrateDispatch_WhenTheDatabaseFails_Returns503NotConflict()
    {
        _controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";
        var requestId = SetupSolicitud();
        _mockPublisher.Setup(p => p.GetOffersByRequestAsync(requestId, _testTenantId)).ReturnsAsync(new List<DispatchOffer>());
        var postgresRepo = new PostgresSolicitudAssignmentRepository(
            new Mock<Microsoft.Extensions.Configuration.IConfiguration>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresSolicitudAssignmentRepository>.Instance);
        var controller = new DispatchController(
            _getEligibleUseCase,
            new OrchestrateDispatchUseCase(_mockRepo.Object, _mockPublisher.Object, postgresRepo),
            _acceptUseCase,
            _rejectUseCase)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Request.Headers.Authorization = $"Bearer {_validJwtToken}";

        var result = await controller.OrchestrateDispatch(new OrchestrateRequestDto(requestId.ToString()));

        Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal("DATABASE_UNAVAILABLE", Body(result).GetProperty("code").GetString());
    }

    private DispatchController ControllerWith(AcceptDispatchOfferUseCase accept, string token)
    {
        var controller = new DispatchController(_getEligibleUseCase, _orchestrateUseCase, accept, _rejectUseCase)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Request.Headers.Authorization = $"Bearer {token}";
        controller.HttpContext.Request.Headers["X-Correlation-ID"] = "test-corr-id";
        return controller;
    }
}
