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
        var testAllies = new List<Ally>
        {
            new() { Id = "ally-1", Nombre = "Laura Mendoza", Telefono = "+57 300 000 0001", CalificacionPromedio = 4.9m },
            new() { Id = "ally-2", Nombre = "Diana Castro", Telefono = "+57 300 000 0002", CalificacionPromedio = 4.7m }
        };

        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ReturnsAsync((testAllies, 2));

        var request = new MatchRequestDto
        {
            TenantId = "tenant-alpha",
            ZonaId = "zona-chapinero",
            CategoriaId = "cat-manicura",
            Page = 1,
            PageSize = 10
        };

        // Act
        var result = await _useCase.ExecuteAsync(request, "corr-12345", "tenant-alpha");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("corr-12345", result.CorrelationId);
        Assert.Equal("tenant-alpha", result.TenantId);
        Assert.Equal("zona-chapinero", result.ZonaId);
        Assert.Equal("cat-manicura", result.CategoriaId);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.MatchedAllies.Count());
        Assert.Equal("Laura Mendoza", result.MatchedAllies.First().Name);

        _mockRepo.Verify(r => r.GetEligibleAlliesAsync(It.Is<MatchCriteria>(c => 
            c.TenantId == "tenant-alpha" && 
            c.ZonaId == "zona-chapinero" && 
            c.CategoriaId == "cat-manicura" && 
            c.Page == 1 && 
            c.PageSize == 10
        )), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFallbackToDefaultTenant_WhenTenantIdNotProvided()
    {
        // Arrange
        _mockRepo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
                 .ReturnsAsync((new List<Ally>(), 0));

        var request = new MatchRequestDto
        {
            TenantId = null,
            Location = "zona-usaquen",
            Category = "cat-pedicura"
        };

        // Act
        var result = await _useCase.ExecuteAsync(request, "corr-default", "fallback-tenant");

        // Assert
        Assert.Equal("fallback-tenant", result.TenantId);
        Assert.Equal("zona-usaquen", result.ZonaId);
        Assert.Equal("cat-pedicura", result.CategoriaId);
        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.PageSize);
    }
}
