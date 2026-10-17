using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;
using ManiDispatch.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

public class PostgresAllyRepositoryTests
{
    private readonly Mock<ILogger<PostgresAllyRepository>> _mockLogger;

    public PostgresAllyRepositoryTests()
    {
        _mockLogger = new Mock<ILogger<PostgresAllyRepository>>();
    }

    [Fact]
    public async Task GetEligibleAlliesAsync_WhenConnectionStringIsEmpty_ReturnsEmptyAndZero()
    {
        var config = new ConfigurationBuilder().Build();
        var repo = new PostgresAllyRepository(config, _mockLogger.Object);

        var criteria = new MatchCriteria
        {
            TenantId = Guid.NewGuid(),
            ZonaId = Guid.NewGuid(),
            CategoriaId = Guid.NewGuid(),
            Page = 1,
            PageSize = 10
        };

        var (allies, totalCount) = await repo.GetEligibleAlliesAsync(criteria);

        Assert.Empty(allies);
        Assert.Equal(0, totalCount);
    }

    [Fact]
    public async Task GetSolicitudContextAsync_WhenConnectionStringIsEmpty_ReturnsNull()
    {
        var config = new ConfigurationBuilder().Build();
        var repo = new PostgresAllyRepository(config, _mockLogger.Object);

        var result = await repo.GetSolicitudContextAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetEligibleAlliesAsync_WithInvalidHostConnectionString_ThrowsException()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "ConnectionStrings:DefaultConnection", "Host=invalid-non-existent-host.local;Port=5432;Database=test;Username=u;Password=p;" }
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var repo = new PostgresAllyRepository(config, _mockLogger.Object);

        var criteria = new MatchCriteria
        {
            TenantId = Guid.NewGuid(),
            ZonaId = Guid.NewGuid(),
            CategoriaId = Guid.NewGuid(),
            Page = 1,
            PageSize = 10
        };

        await Assert.ThrowsAnyAsync<Exception>(() => repo.GetEligibleAlliesAsync(criteria));
    }

    [Fact]
    public async Task GetSolicitudContextAsync_WithInvalidHostConnectionString_ThrowsException()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "ConnectionStrings:DefaultConnection", "Host=invalid-non-existent-host.local;Port=5432;Database=test;Username=u;Password=p;" }
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var repo = new PostgresAllyRepository(config, _mockLogger.Object);

        await Assert.ThrowsAnyAsync<Exception>(() => repo.GetSolicitudContextAsync(Guid.NewGuid(), Guid.NewGuid()));
    }
}
