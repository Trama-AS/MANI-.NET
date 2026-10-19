using ManiDispatch.Application.DTOs;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Exceptions;
using ManiDispatch.Infrastructure.Core;
using ManiDispatch.Infrastructure.Dispatch;
using ManiDispatch.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// OrchestrateDispatchUseCase con los clientes HTTP reales contra un Core simulado en localhost:
/// las ofertas se guardan una sola vez por aliado aunque el despacho se oriente varias veces (RNF-03)
/// y los errores de Core no se ocultan (ADR-0030).
/// </summary>
public class OrchestrateDispatchIntegrationTests
{
    private static OrchestrateDispatchUseCase CreateUseCase(string coreUrl)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CORE_SERVICE_URL"] = coreUrl })
            .Build();
        return new OrchestrateDispatchUseCase(
            new CoreAllyClient(new HttpClient { Timeout = TimeSpan.FromSeconds(3) }, config, NullLogger<CoreAllyClient>.Instance),
            new CoreDispatchPublisher(new HttpClient { Timeout = TimeSpan.FromSeconds(3) }, config, NullLogger<CoreDispatchPublisher>.Instance));
    }

    [Fact]
    public async Task Orchestrate_TwiceForTheSameRequest_DoesNotDuplicateOffers()
    {
        using var core = new FakeCoreServer();
        var tenant = Guid.NewGuid();
        core.AddEligibleAlly(tenant, "Ana");
        core.AddEligibleAlly(tenant, "Beto");
        var solicitud = core.AddSolicitud(tenant);
        var useCase = CreateUseCase(core.BaseUrl);
        var request = new OrchestrateRequestDto(solicitud.ToString());

        var first = await useCase.ExecuteAsync(request, "corr-1", tenant);
        var second = await useCase.ExecuteAsync(request, "corr-2", tenant);

        Assert.Equal("DISPATCHED", first.Status);
        Assert.Equal(2, first.CandidatesCount);
        Assert.Equal("DISPATCHED", second.Status);
        Assert.Equal(2, second.CandidatesCount);
        Assert.Equal(2, core.CountOffers(tenant, solicitud));
    }

    [Fact]
    public async Task Orchestrate_WithoutEligibleAllies_ReportsNoCandidates_AndStoresNothing()
    {
        using var core = new FakeCoreServer();
        var tenant = Guid.NewGuid();
        var solicitud = core.AddSolicitud(tenant);

        var result = await CreateUseCase(core.BaseUrl).ExecuteAsync(new OrchestrateRequestDto(solicitud.ToString()), "corr", tenant);

        Assert.Equal("NO_ELIGIBLE_ALLIES", result.Status);
        Assert.Equal(0, core.CountOffers(tenant, solicitud));
    }

    [Fact]
    public async Task Orchestrate_RequestOfAnotherTenant_ThrowsNotFound()
    {
        using var core = new FakeCoreServer();
        var solicitud = core.AddSolicitud(Guid.NewGuid());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateUseCase(core.BaseUrl).ExecuteAsync(new OrchestrateRequestDto(solicitud.ToString()), "corr", Guid.NewGuid()));
    }

    [Fact]
    public async Task Orchestrate_AlreadyAssignedRequest_ThrowsInvalidOperation()
    {
        using var core = new FakeCoreServer();
        var tenant = Guid.NewGuid();
        var solicitud = core.AddSolicitud(tenant, estado: "ASIGNADA");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateUseCase(core.BaseUrl).ExecuteAsync(new OrchestrateRequestDto(solicitud.ToString()), "corr", tenant));
    }

    [Fact]
    public async Task Orchestrate_WhenCoreRejectsTheOffers_TheErrorPropagatesAndIsNotHidden()
    {
        using var core = new FakeCoreServer { FailNotifications = true };
        var tenant = Guid.NewGuid();
        core.AddEligibleAlly(tenant, "Ana");
        var solicitud = core.AddSolicitud(tenant);

        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() =>
            CreateUseCase(core.BaseUrl).ExecuteAsync(new OrchestrateRequestDto(solicitud.ToString()), "corr", tenant));
    }
}
