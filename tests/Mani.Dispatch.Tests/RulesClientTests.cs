using System.Net;
using System.Text.Json;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;
using ManiDispatch.Domain.Exceptions;
using ManiDispatch.Infrastructure.Core;
using ManiDispatch.Infrastructure.Rules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>Clientes del orden de elegibles (SCRUM-1462): cualquier fallo termina en 503, nunca en un orden local.</summary>
public class RulesClientTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

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

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static IConfiguration Config(string key, string? value) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

    private static RulesClient Rules(StubHandler handler, string? url = "http://rules:8080/") =>
        new(new HttpClient(handler), Config("RULES_SERVICE_URL", url), NullLogger<RulesClient>.Instance);

    private static CoreTenantRulesClient TenantRules(StubHandler handler) =>
        new(new HttpClient(handler), Config("CORE_SERVICE_URL", "http://core:3000"), NullLogger<CoreTenantRulesClient>.Instance);

    private static Task<IReadOnlyList<RankedAlly>> Rank(RulesClient client, params Ally[] candidatos) =>
        client.RankAlliesAsync("CALIFICACION", candidatos, TenantId, "cliente", "corr-1");

    [Fact]
    public async Task RankAllies_SendsRealMetricsAndHeaders_AndReadsTheOrder()
    {
        var aliado = new Ally { Id = Guid.NewGuid(), CalificacionPromedio = 4.5m, TotalCalificaciones = 12, ZonasCobertura = 3 };
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            $$"""{"criterio":"CALIFICACION","aliados":[{"id":"{{aliado.Id}}","posicion":1,"puntaje":0.9,"razones":["Calificación promedio 4.5"]}]}"""));

        var ranking = await Rank(Rules(handler), aliado);

        var ranked = Assert.Single(ranking);
        Assert.Equal(aliado.Id, ranked.AllyId);
        Assert.Equal(1, ranked.Posicion);
        Assert.Equal(0.9m, ranked.Puntaje);
        Assert.Equal("http://rules:8080/api/v1/rules/rank-allies", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(TenantId.ToString(), handler.LastRequest.Headers.GetValues("X-Tenant-Id").Single());
        Assert.Equal("cliente", handler.LastRequest.Headers.GetValues("X-User-Role").Single());
        Assert.Equal("corr-1", handler.LastRequest.Headers.GetValues("X-Correlation-ID").Single());

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("CALIFICACION", body.RootElement.GetProperty("criterio").GetString());
        var candidato = body.RootElement.GetProperty("candidatos")[0];
        Assert.Equal(aliado.Id.ToString(), candidato.GetProperty("id").GetString());
        Assert.Equal(4.5m, candidato.GetProperty("calificacionPromedio").GetDecimal());
        Assert.Equal(12, candidato.GetProperty("totalCalificaciones").GetInt32());
        Assert.Equal(3, candidato.GetProperty("zonasCobertura").GetInt32());
    }

    [Fact]
    public async Task RankAllies_WithoutUrl_Fails()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("no debe llamar"));
        await Assert.ThrowsAsync<RulesServiceUnavailableException>(() => Rank(Rules(handler, url: null), new Ally()));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"VALIDATION_ERROR"}""")]
    [InlineData(HttpStatusCode.OK, "no es json")]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.OK, """{"aliados":[{"id":"no-es-uuid","posicion":1}]}""")]
    [InlineData(HttpStatusCode.OK, """{"aliados":[{"posicion":1}]}""")]
    public async Task RankAllies_WhenRulesAnswersBadly_Fails(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(_ => Json(status, body));
        await Assert.ThrowsAsync<RulesServiceUnavailableException>(() => Rank(Rules(handler), new Ally()));
    }

    [Fact]
    public async Task RankAllies_WhenRulesTimesOut_Fails()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timeout"));
        await Assert.ThrowsAsync<RulesServiceUnavailableException>(() => Rank(Rules(handler), new Ally()));
    }

    [Fact]
    public async Task TenantRules_ReadsTheCurrentCriterion()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"version":3,"reglas":{"ordenListado":{"criterio":"COBERTURA"}}}"""));

        var criterio = await TenantRules(handler).GetCriterioOrdenListadoAsync(TenantId, "corr-1");

        Assert.Equal("COBERTURA", criterio);
        Assert.Equal("http://core:3000/api/v1/tenant/rules", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(TenantId.ToString(), handler.LastRequest.Headers.GetValues("X-Tenant-Id").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.OK, """{"reglas":{"ordenListado":{"criterio":" "}}}""")]
    [InlineData(HttpStatusCode.OK, "no es json")]
    public async Task TenantRules_WithoutReadableCriterion_Fails(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(_ => Json(status, body));
        await Assert.ThrowsAsync<RulesServiceUnavailableException>(() =>
            TenantRules(handler).GetCriterioOrdenListadoAsync(TenantId, "corr-1"));
    }

    [Fact]
    public async Task TenantRules_WhenCoreDeniesTheSession_IsAccessDenied()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Forbidden, "{}"));
        await Assert.ThrowsAsync<CoreAccessDeniedException>(() =>
            TenantRules(handler).GetCriterioOrdenListadoAsync(TenantId, "corr-1"));
    }
}
