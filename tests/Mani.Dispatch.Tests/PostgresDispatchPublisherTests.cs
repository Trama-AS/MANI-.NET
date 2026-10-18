using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Infrastructure.Dispatch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

public class PostgresDispatchPublisherTests
{
    private readonly Mock<ILogger<PostgresDispatchPublisher>> _mockLogger;

    public PostgresDispatchPublisherTests()
    {
        _mockLogger = new Mock<ILogger<PostgresDispatchPublisher>>();
    }

    [Fact]
    public async Task PublishOfferAsync_WhenConnectionStringIsEmpty_LogsWarningAndReturnsWithoutThrowing()
    {
        var config = new ConfigurationBuilder().Build();
        var publisher = new PostgresDispatchPublisher(config, _mockLogger.Object);

        var offer = new DispatchOffer(
            RequestId: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            AllyId: Guid.NewGuid(),
            AllyNombre: "Test Ally",
            Estado: "ENVIADA",
            CorrelationId: "corr-1",
            Timestamp: DateTime.UtcNow
        );

        // Does not throw
        await publisher.PublishOfferAsync(offer);
    }

    [Fact]
    public async Task GetOffersByRequestAsync_WhenConnectionStringIsEmpty_ReturnsEmptyList()
    {
        var config = new ConfigurationBuilder().Build();
        var publisher = new PostgresDispatchPublisher(config, _mockLogger.Object);

        var offers = await publisher.GetOffersByRequestAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Empty(offers);
    }

    [Fact]
    public async Task PublishOfferAsync_WhenConnectionFails_ThrowsExceptionAndDoesNotSwallowError()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "ConnectionStrings:DefaultConnection", "Host=invalid-non-existent-host.local;Port=5432;Database=test;Username=u;Password=p;" }
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var publisher = new PostgresDispatchPublisher(config, _mockLogger.Object);

        var offer = new DispatchOffer(
            RequestId: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            AllyId: Guid.NewGuid(),
            AllyNombre: "Test Ally",
            Estado: "ENVIADA",
            CorrelationId: "corr-1",
            Timestamp: DateTime.UtcNow
        );

        // Must throw and propagate the error (addressing QA review #2 point 2)
        await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishOfferAsync(offer));
    }

    [Fact]
    public async Task GetOffersByRequestAsync_WhenConnectionFails_ThrowsException()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "ConnectionStrings:DefaultConnection", "Host=invalid-non-existent-host.local;Port=5432;Database=test;Username=u;Password=p;" }
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var publisher = new PostgresDispatchPublisher(config, _mockLogger.Object);

        await Assert.ThrowsAnyAsync<Exception>(() => publisher.GetOffersByRequestAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task PublishOfferAsync_WhenCoreServiceConfiguredAndUnreachable_ThrowsAndFailsClosed()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "CORE_SERVICE_URL", "http://127.0.0.1:54321" }
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var publisher = new PostgresDispatchPublisher(config, _mockLogger.Object);

        var offer = new DispatchOffer(
            RequestId: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            AllyId: Guid.NewGuid(),
            AllyNombre: "Test Ally",
            Estado: "ENVIADA",
            CorrelationId: "corr-fail-closed",
            Timestamp: DateTime.UtcNow
        );

        await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishOfferAsync(offer));
    }

    [Fact]
    public async Task GetOffersByRequestAsync_WhenCoreServiceConfiguredAndUnreachable_ThrowsAndFailsClosed()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "CORE_SERVICE_URL", "http://127.0.0.1:54321" }
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var publisher = new PostgresDispatchPublisher(config, _mockLogger.Object);

        await Assert.ThrowsAnyAsync<Exception>(() => publisher.GetOffersByRequestAsync(Guid.NewGuid(), Guid.NewGuid()));
    }
}
