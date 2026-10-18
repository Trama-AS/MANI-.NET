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

public class GetEligibleAlliesUseCaseTests
{
    private readonly Mock<IAllyRepository> _mockRepo;
    private readonly GetEligibleAlliesUseCase _useCase;

    public GetEligibleAlliesUseCaseTests()
    {
        _mockRepo = new Mock<IAllyRepository>();
        _useCase = new GetEligibleAlliesUseCase(_mockRepo.Object);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnEligibleAllies_WhenCriteriaMatches()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var zonaId = Guid.NewGuid();
        var categoriaId = Guid.NewGuid();

        var testAllies = new List<Ally>
        {
            new()
            {
                Id = Guid.NewGuid(),
                NombreRazonSocial = "Laura Mendoza S.A.S.",
                Tipo = "empresa",
                EstadoVerificacion = "aprobado"
            },
            new()
            {
                Id = Guid.NewGuid(),
                NombreRazonSocial = "Diana Castro",
                Tipo = "independiente",
                EstadoVerificacion = "VERIFICADO"
            }
        };

        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ReturnsAsync((testAllies, 2));

        var request = new MatchRequestDto
        {
            TenantId = tenantId.ToString(),
            ZonaId = zonaId.ToString(),
            CategoriaId = categoriaId.ToString(),
            Page = 1,
            PageSize = 10
        };

        // Act
        var result = await _useCase.ExecuteAsync(request, "corr-12345", tenantId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("corr-12345", result.CorrelationId);
        Assert.Equal(tenantId.ToString(), result.TenantId);
        Assert.Equal(zonaId.ToString(), result.ZonaId);
        Assert.Equal(categoriaId.ToString(), result.CategoriaId);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.MatchedAllies.Count());
        Assert.Equal("Laura Mendoza S.A.S.", result.MatchedAllies.First().NombreRazonSocial);
        Assert.Equal("empresa", result.MatchedAllies.First().Tipo);

        _mockRepo.Verify(r => r.GetEligibleAlliesAsync(It.Is<MatchCriteria>(c =>
            c.TenantId == tenantId &&
            c.ZonaId == zonaId &&
            c.CategoriaId == categoriaId &&
            c.Page == 1 &&
            c.PageSize == 10
        )), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsArgumentException_WhenZonaIdIsNotValidGuid()
    {
        var request = new MatchRequestDto
        {
            ZonaId = "invalid-zona-uuid",
            CategoriaId = Guid.NewGuid().ToString()
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _useCase.ExecuteAsync(request, "corr-1", Guid.NewGuid()));
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsArgumentException_WhenCategoriaIdIsNotValidGuid()
    {
        var request = new MatchRequestDto
        {
            ZonaId = Guid.NewGuid().ToString(),
            CategoriaId = "invalid-cat-uuid"
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _useCase.ExecuteAsync(request, "corr-2", Guid.NewGuid()));
    }

    [Fact]
    public async Task GetSolicitudContextAsync_DelegatesToRepository()
    {
        var reqId = Guid.NewGuid();
        var tenId = Guid.NewGuid();
        var expected = new SolicitudContext
        {
            Id = reqId,
            TenantId = tenId,
            ZonaId = Guid.NewGuid(),
            CategoriaId = Guid.NewGuid(),
            Estado = "PENDIENTE"
        };

        _mockRepo.Setup(r => r.GetSolicitudContextAsync(reqId, tenId))
                 .ReturnsAsync(expected);

        var actual = await _useCase.GetSolicitudContextAsync(reqId, tenId);

        Assert.NotNull(actual);
        Assert.Equal(reqId, actual.Id);
        Assert.Equal(tenId, actual.TenantId);
        Assert.Equal("PENDIENTE", actual.Estado);
    }
}
