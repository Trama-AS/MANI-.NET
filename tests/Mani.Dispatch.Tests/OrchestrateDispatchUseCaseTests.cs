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
            new() { Id = Guid.NewGuid(), NombreRazonSocial = "Aliado 1 S.A.S.", Tipo = "empresa", EstadoVerificacion = "VERIFICADO" },
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
    public async Task ExecuteAsync_WhenEligibleAlliesSpanSeveralPages_BroadcastsToAllOfThem()
    {
        // Arrange (SCRUM-1402 C1: 51 elegibles, uno más que la página de 50 que se pide a Core)
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

        var allies = Enumerable.Range(1, 51)
            .Select(i => new Ally { Id = Guid.NewGuid(), NombreRazonSocial = $"Aliado {i}", Tipo = "PERSONA_NATURAL", EstadoVerificacion = "VERIFICADO" })
            .ToList();

        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, tenantId))
                 .ReturnsAsync(solicitud);

        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ReturnsAsync((MatchCriteria c) => ((IEnumerable<Ally>)allies.Skip((c.Page - 1) * c.PageSize).Take(c.PageSize).ToList(), allies.Count));

        var request = new OrchestrateRequestDto
        {
            RequestId = requestId.ToString()
        };

        // Act
        var result = await _useCase.ExecuteAsync(request, "corr-orch-51", tenantId);

        // Assert
        Assert.Equal("DISPATCHED", result.Status);
        Assert.Equal(51, result.CandidatesCount);
        Assert.Equal(allies.Select(a => a.Id.ToString()).OrderBy(id => id), result.CandidatesNotified.Select(o => o.AllyId).OrderBy(id => id));
        Assert.Contains("51 aliados", result.Message);

        _mockPublisher.Verify(p => p.PublishOfferAsync(It.IsAny<DispatchOffer>()), Times.Exactly(51));
        _mockRepo.Verify(r => r.GetEligibleAlliesAsync(It.Is<MatchCriteria>(c => c.Page == 1 && c.PageSize == 50)), Times.Once);
        _mockRepo.Verify(r => r.GetEligibleAlliesAsync(It.Is<MatchCriteria>(c => c.Page == 2 && c.PageSize == 50)), Times.Once);
        _mockRepo.Verify(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_WhenCoreRepeatsTheSamePage_StopsAndPublishesEachAllyOnce()
    {
        // Arrange: si Core devolviera la misma página una y otra vez, el recorrido termina y no duplica ofertas
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

        var pagina = Enumerable.Range(1, 50)
            .Select(i => new Ally { Id = Guid.NewGuid(), NombreRazonSocial = $"Aliado {i}", Tipo = "PERSONA_NATURAL", EstadoVerificacion = "VERIFICADO" })
            .ToList();

        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, tenantId))
                 .ReturnsAsync(solicitud);

        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ReturnsAsync((pagina, 120));

        var request = new OrchestrateRequestDto
        {
            RequestId = requestId.ToString()
        };

        // Act
        var result = await _useCase.ExecuteAsync(request, "corr-orch-repetida", tenantId);

        // Assert
        Assert.Equal(50, result.CandidatesCount);
        _mockPublisher.Verify(p => p.PublishOfferAsync(It.IsAny<DispatchOffer>()), Times.Exactly(50));
        _mockRepo.Verify(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()), Times.Exactly(2));
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
    public async Task ExecuteAsync_WhenSolicitudNotFoundInDb_ThrowsKeyNotFoundException()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, tenantId))
                 .ReturnsAsync((SolicitudContext?)null);

        var request = new OrchestrateRequestDto
        {
            RequestId = requestId.ToString(),
            ZonaId = Guid.NewGuid().ToString(),
            CategoriaId = Guid.NewGuid().ToString()
        };

        // Act & Assert (DoD §9.3: La solicitud DEBE existir en base de datos)
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _useCase.ExecuteAsync(request, "corr-not-found", tenantId));
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

    [Fact]
    public async Task ExecuteAsync_WhenOffersAlreadyExist_ReturnsExistingOffers_WithoutDuplicatePublishing()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var zonaId = Guid.NewGuid();
        var categoriaId = Guid.NewGuid();
        var allyId = Guid.NewGuid();

        var solicitud = new SolicitudContext
        {
            Id = requestId,
            TenantId = tenantId,
            ZonaId = zonaId,
            CategoriaId = categoriaId,
            Estado = "PENDIENTE"
        };

        _mockRepo.Setup(r => r.GetSolicitudContextAsync(requestId, tenantId))
                 .ReturnsAsync(solicitud);

        var existingOffers = new List<DispatchOffer>
        {
            new(requestId, tenantId, allyId, "Aliado Existente", "ENVIADA", "corr-prev", DateTime.UtcNow.AddMinutes(-5))
        };

        _mockPublisher.Setup(p => p.GetOffersByRequestAsync(requestId, tenantId))
                      .ReturnsAsync(existingOffers);

        var request = new OrchestrateRequestDto
        {
            RequestId = requestId.ToString()
        };

        // Act
        var result = await _useCase.ExecuteAsync(request, "corr-retry", tenantId);

        // Assert (RNF-03: idempotente, no re-publica)
        Assert.NotNull(result);
        Assert.Equal("DISPATCHED", result.Status);
        Assert.Equal(1, result.CandidatesCount);
        Assert.Single(result.CandidatesNotified);
        Assert.Equal(allyId.ToString(), result.CandidatesNotified.First().AllyId);
        Assert.Contains("idempotente", result.Message, StringComparison.OrdinalIgnoreCase);

        _mockPublisher.Verify(p => p.PublishOfferAsync(It.IsAny<DispatchOffer>()), Times.Never);
    }
}
