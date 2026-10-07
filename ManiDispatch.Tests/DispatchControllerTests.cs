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

public class DispatchControllerTests
{
    private readonly Mock<IAllyRepository> _mockRepo;
    private readonly Mock<IDispatchPublisher> _mockPublisher;
    private readonly GetEligibleAlliesUseCase _getEligibleUseCase;
    private readonly OrchestrateDispatchUseCase _orchestrateUseCase;
    private readonly DispatchController _controller;
    private readonly Guid _testTenantId;
    private readonly string _validJwtToken;

    public DispatchControllerTests()
    {
        _testTenantId = Guid.NewGuid();
        _validJwtToken = TestTokenHelper.CreateTestToken(_testTenantId, "user-client-1", "cliente");

        _mockRepo = new Mock<IAllyRepository>();
        _mockPublisher = new Mock<IDispatchPublisher>();
        _getEligibleUseCase = new GetEligibleAlliesUseCase(_mockRepo.Object);
        _orchestrateUseCase = new OrchestrateDispatchUseCase(_mockRepo.Object, _mockPublisher.Object);

        _controller = new DispatchController(_getEligibleUseCase, _orchestrateUseCase)
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
            new() { Id = Guid.NewGuid(), NombreRazonSocial = "Aliado Experto", Tipo = "independiente", EstadoVerificacion = "aprobado" }
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
    public void AcceptRequest_ReturnsOkOnFirst_AndConflictOnSecond()
    {
        string reqId = "req-unique-" + Guid.NewGuid();
        var dto1 = new AcceptRequestDto("ally-first");
        var dto2 = new AcceptRequestDto("ally-second");

        var result1 = _controller.AcceptRequest(reqId, dto1);
        var result2 = _controller.AcceptRequest(reqId, dto2);

        Assert.IsType<OkObjectResult>(result1);
        var conflictResult = Assert.IsType<ConflictObjectResult>(result2);
        Assert.Equal(409, conflictResult.StatusCode);
    }
}
