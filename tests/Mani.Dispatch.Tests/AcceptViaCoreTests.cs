using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Entities;
using ManiDispatch.Domain.Exceptions;
using ManiDispatch.Infrastructure.Core;
using ManiDispatch.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// Cliente del comando de asignación y de la bandeja en Core (decisión 6A, SCRUM-1403/1404), y la bandeja que arma
/// Dispatch quitando los rechazos del aliado.
/// </summary>
public class AcceptViaCoreTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return _respond(request);
        }
    }

    private static CoreAssignmentClient CreateClient(StubHandler handler, string? coreUrl = "http://core.test")
    {
        var settings = new Dictionary<string, string?>();
        if (coreUrl != null)
        {
            settings["CORE_SERVICE_URL"] = coreUrl;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new CoreAssignmentClient(new HttpClient(handler), config, NullLogger<CoreAssignmentClient>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task AssignAsync_PostsTheCommandWithHeaders_AndMapsAFreshAssignment()
    {
        var requestId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var allyId = Guid.NewGuid();
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            $"{{\"solicitud\":{{\"id\":\"{requestId}\",\"estado\":\"ASIGNADA\",\"aliadoId\":\"{allyId}\"}},\"asignada\":true,\"correlationId\":\"c\"}}"));

        var result = await CreateClient(handler).AssignAsync(requestId, tenantId, "clave-1", "corr-1");

        Assert.Equal(AssignmentStatus.Assigned, result.Status);
        Assert.Equal(allyId, result.AssignedAllyId);
        var request = handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"http://core.test/api/v1/solicitudes/{requestId}/asignacion", request.RequestUri!.ToString());
        Assert.Equal("clave-1", request.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("corr-1", request.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal(tenantId.ToString(), request.Headers.GetValues("X-Tenant-Id").Single());
        Assert.Equal("{}", handler.LastBody);
    }

    [Fact]
    public async Task AssignAsync_WhenCoreSaysItWasAlreadyTheAllys_IsAnIdempotentRetry()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            $"{{\"solicitud\":{{\"id\":\"{Guid.NewGuid()}\",\"aliadoId\":\"{Guid.NewGuid()}\"}},\"asignada\":false}}"));

        var result = await CreateClient(handler).AssignAsync(Guid.NewGuid(), Guid.NewGuid(), null, null);

        Assert.Equal(AssignmentStatus.AlreadyAssigned, result.Status);
        Assert.False(handler.LastRequest!.Headers.Contains("Idempotency-Key"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "MANI-SOL-409", AssignmentStatus.Conflict)]
    [InlineData(HttpStatusCode.Conflict, "MANI-SOL-409E", AssignmentStatus.InvalidState)]
    [InlineData(HttpStatusCode.NotFound, "MANI-SOL-404", AssignmentStatus.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, "MANI-SOL-403O", AssignmentStatus.Forbidden)]
    [InlineData(HttpStatusCode.Forbidden, "MANI-SOL-403E", AssignmentStatus.Forbidden)]
    [InlineData(HttpStatusCode.Forbidden, "MANI-SOL-403V", AssignmentStatus.Forbidden)]
    public async Task AssignAsync_MapsCoreRefusals_KeepingTheCode(HttpStatusCode status, string code, AssignmentStatus expected)
    {
        var handler = new StubHandler(_ => Json(status, $"{{\"error\":\"no\",\"code\":\"{code}\",\"correlationId\":\"c\"}}"));

        var result = await CreateClient(handler).AssignAsync(Guid.NewGuid(), Guid.NewGuid(), "k", "c");

        Assert.Equal(expected, result.Status);
        Assert.Equal(code, result.Code);
        Assert.Null(result.AssignedAllyId);
    }

    [Fact]
    public async Task AssignAsync_WithAConflictWithoutBody_StillReportsTheConflict()
    {
        var result = await CreateClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)))
            .AssignAsync(Guid.NewGuid(), Guid.NewGuid(), null, null);

        Assert.Equal(AssignmentStatus.Conflict, result.Status);
        Assert.Equal("MANI-SOL-409", result.Code);
    }

    [Fact]
    public async Task AssignAsync_When401_ThrowsAccessDenied()
    {
        var client = CreateClient(new StubHandler(_ => Json(HttpStatusCode.Unauthorized, "{\"code\":\"MANI-SOL-401\"}")));

        var error = await Assert.ThrowsAsync<CoreAccessDeniedException>(() => client.AssignAsync(Guid.NewGuid(), Guid.NewGuid(), null, null));
        Assert.Equal("MANI-SOL-401", error.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.BadRequest, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>no</html>")]
    [InlineData(HttpStatusCode.OK, "{\"asignada\":true}")]
    public async Task AssignAsync_FailsClosed_WhenCoreAnswersSomethingUnexpected(HttpStatusCode status, string body)
    {
        var client = CreateClient(new StubHandler(_ => Json(status, body)));

        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() => client.AssignAsync(Guid.NewGuid(), Guid.NewGuid(), null, null));
    }

    [Fact]
    public async Task AssignAsync_WhenCoreIsUnreachableOrUnconfigured_ThrowsUnavailable()
    {
        var timeout = CreateClient(new StubHandler(_ => throw new TaskCanceledException("timeout")));
        var unconfigured = CreateClient(new StubHandler(_ => Json(HttpStatusCode.OK, "{}")), coreUrl: null);

        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() => timeout.AssignAsync(Guid.NewGuid(), Guid.NewGuid(), null, null));
        await Assert.ThrowsAsync<CoreServiceUnavailableException>(() => unconfigured.AssignAsync(Guid.NewGuid(), Guid.NewGuid(), null, null));
    }

    [Fact]
    public async Task GetOwnOffersAsync_MapsTheInbox()
    {
        var id = Guid.NewGuid();
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            $"{{\"items\":[{{\"id\":\"{id}\",\"estado\":\"PENDIENTE\",\"esMia\":false,\"categoria\":\"Plomeria\",\"descripcion\":\"Gotea el lavaplatos\",\"fotos\":2,\"zona\":\"Chapinero\",\"reglasSitio\":{{\"epp\":true}},\"direccion\":null,\"creadaEn\":\"2026-10-27T10:00:00.000Z\",\"asignadaEn\":null}}],\"correlationId\":\"c\"}}"));

        var offers = await CreateClient(handler).GetOwnOffersAsync(Guid.NewGuid(), "corr-1");

        var offer = Assert.Single(offers);
        Assert.Equal(id, offer.Id);
        Assert.Equal("Plomeria", offer.Categoria);
        Assert.Equal("Gotea el lavaplatos", offer.Descripcion);
        Assert.Equal(2, offer.Fotos);
        Assert.False(offer.EsMia);
        Assert.Null(offer.Direccion);
        Assert.True(offer.ReglasSitio!.Value.GetProperty("epp").GetBoolean());
        Assert.Equal("http://core.test/api/v1/allies/me/offers", handler.LastRequest!.RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, typeof(CoreAccessDeniedException))]
    [InlineData(HttpStatusCode.Unauthorized, typeof(CoreAccessDeniedException))]
    [InlineData(HttpStatusCode.ServiceUnavailable, typeof(CoreServiceUnavailableException))]
    public async Task GetOwnOffersAsync_MapsErrors(HttpStatusCode status, Type expected)
    {
        var client = CreateClient(new StubHandler(_ => Json(status, "{}")));

        await Assert.ThrowsAsync(expected, () => client.GetOwnOffersAsync(Guid.NewGuid(), null));
    }

    [Fact]
    public async Task AllyOffers_HideTheOffersTheAllyRejected_ButKeepItsAssignedRequests()
    {
        var tenantId = Guid.NewGuid();
        var usuarioId = Guid.NewGuid();
        var allyId = Guid.NewGuid();
        var profiles = new FakeAllyProfiles();
        profiles.RegisterAlly(new AllyProfile(allyId, tenantId, usuarioId, "VERIFICADO"));
        var core = new InMemoryCoreAssignments(profiles, () => usuarioId);
        var abierta = Guid.NewGuid();
        var rechazada = Guid.NewGuid();
        var mia = Guid.NewGuid();
        core.Offers.Add(new AllyOffer { Id = abierta, Estado = "PENDIENTE" });
        core.Offers.Add(new AllyOffer { Id = rechazada, Estado = "PENDIENTE" });
        core.Offers.Add(new AllyOffer { Id = mia, Estado = "ASIGNADA", EsMia = true });
        var repo = new InMemorySolicitudAssignmentRepository();
        await repo.RejectRequestAsync(rechazada, tenantId, usuarioId, allyId, "SIN_DISPONIBILIDAD", null);
        await repo.RejectRequestAsync(mia, tenantId, usuarioId, allyId, "FUERA_DE_ZONA", null); // rechazo viejo de algo que luego ganó

        var offers = await new GetAllyOffersUseCase(core, repo, profiles).ExecuteAsync(tenantId, usuarioId, "corr");

        Assert.Equal(new[] { abierta, mia }, offers.Select(o => o.Id));
    }

    [Fact]
    public async Task AllyOffers_WithoutAllyProfile_AreUnauthorized_AndCoreIsNotAsked()
    {
        var profiles = new FakeAllyProfiles();
        var core = new InMemoryCoreAssignments(profiles, Guid.NewGuid) { Failure = new InvalidOperationException("no debía llamarse") };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new GetAllyOffersUseCase(core, new InMemorySolicitudAssignmentRepository(), profiles).ExecuteAsync(Guid.NewGuid(), Guid.NewGuid(), null));
    }
}
