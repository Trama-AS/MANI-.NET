using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ManiDispatch.Application.DTOs;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Entities;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

public class OrchestrateDispatchUseCaseTests
{
    private readonly Mock<IAllyRepository> _mockRepo;
    private readonly Mock<IDispatchPublisher> _mockPublisher;
    private readonly OrchestrateDispatchUseCase _useCase;

    public OrchestrateDispatchUseCaseTests()
    {
        _mockRepo = new Mock<IAllyRepository>();
        _mockPublisher = new Mock<IDispatchPublisher>();
        _useCase = new OrchestrateDispatchUseCase(_mockRepo.Object, _mockPublisher.Object);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSolicitudExists_DispatchesToEligibleAllies_AndBroadcastsOffers()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var zonaId = Guid.NewGuid();
        var categoriaId = Guid.NewGuid();

        var solicitud = new SolicitudContext
        {
            Id = requestId,
            TenantId = tenantId,
            ZonaId = zonaId,
            CategoriaId = categoriaId,
            Estado = "PENDIENTE"
        };

        var allies = new List<Ally>
        {
            new() { Id = Guid.NewGuid(), NombreRazonSocial = "Aliado 1 S.A.S.", Tipo = "empresa", EstadoVerificacion = "aprobado" },
            new() { Id = Guid.NewGuid(), NombreRazonSocial = "Aliado 2", Tipo = "independiente", EstadoVerificacion = "VERIFICADO" }
        };

        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, tenantId))
                 .ReturnsAsync(solicitud);

        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.Is<MatchCriteria>(c =>
                     c.TenantId == tenantId && c.ZonaId == zonaId && c.CategoriaId == categoriaId)))
                 .ReturnsAsync((allies, 2));

        var request = new OrchestrateRequestDto
        {
            RequestId = requestId.ToString()
        };

        // Act
        var result = await _useCase.ExecuteAsync(request, "corr-orch-1", tenantId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("DISPATCHED", result.Status);
        Assert.Equal(2, result.CandidatesCount);
        Assert.Equal(2, result.CandidatesNotified.Count());
        Assert.Equal(zonaId.ToString(), result.ZonaId);
        Assert.Equal(categoriaId.ToString(), result.CategoriaId);

        _mockPublisher.Verify(p => p.PublishOfferAsync(It.IsAny<DispatchOffer>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_WhenSolicitudInInvalidState_ThrowsInvalidOperationException()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        var solicitud = new SolicitudContext
        {
            Id = requestId,
            TenantId = tenantId,
            ZonaId = Guid.NewGuid(),
            CategoriaId = Guid.NewGuid(),
            Estado = "ASIGNADA"
        };

        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, tenantId))
                 .ReturnsAsync(solicitud);

        var request = new OrchestrateRequestDto
        {
            RequestId = requestId.ToString()
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _useCase.ExecuteAsync(request, "corr-err", tenantId));
    }

    [Fact]
    public async Task ExecuteAsync_WhenSolicitudNotFoundAndNoExplicitParams_ThrowsKeyNotFoundException()
    {
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, tenantId))
                 .ReturnsAsync((SolicitudContext?)null);

        var request = new OrchestrateRequestDto
        {
            RequestId = requestId.ToString()
        };

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _useCase.ExecuteAsync(request, "corr-err", tenantId));
    }

    [Fact]
    public async Task ExecuteAsync_WhenSolicitudNotFoundButExplicitParamsProvided_DispatchesSuccessfully()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var zonaId = Guid.NewGuid();
        var categoriaId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, tenantId))
                 .ReturnsAsync((SolicitudContext?)null);

        var allies = new List<Ally>
        {
            new() { Id = Guid.NewGuid(), NombreRazonSocial = "Aliado Independiente", Tipo = "independiente", EstadoVerificacion = "aprobado" }
        };

        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ReturnsAsync((allies, 1));

        var request = new OrchestrateRequestDto
        {
            RequestId = requestId.ToString(),
            ZonaId = zonaId.ToString(),
            CategoriaId = categoriaId.ToString()
        };

        // Act
        var result = await _useCase.ExecuteAsync(request, "corr-fallback", tenantId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("DISPATCHED", result.Status);
        Assert.Equal(1, result.CandidatesCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoEligibleAlliesFound_ReturnsStatusNoEligibleAllies()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        var solicitud = new SolicitudContext
        {
            Id = requestId,
            TenantId = tenantId,
            ZonaId = Guid.NewGuid(),
            CategoriaId = Guid.NewGuid(),
            Estado = "PENDIENTE"
        };

        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, tenantId))
                 .ReturnsAsync(solicitud);

        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ReturnsAsync((new List<Ally>(), 0));

        var request = new OrchestrateRequestDto
        {
            RequestId = requestId.ToString()
        };

        // Act
        var result = await _useCase.ExecuteAsync(request, "corr-empty", tenantId);

        // Assert
        Assert.Equal("NO_ELIGIBLE_ALLIES", result.Status);
        Assert.Equal(0, result.CandidatesCount);
        Assert.Empty(result.CandidatesNotified);

        _mockPublisher.Verify(p => p.PublishOfferAsync(It.IsAny<DispatchOffer>()), Times.Never);
    }
}
