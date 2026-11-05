using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;
using ManiDispatch.Domain.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ManiDispatch.Infrastructure.Rules;

/// <summary>
/// Pide a Rules el orden de los candidatos por la red interna (D-5, SCRUM-1462). Dispatch ya validó el JWT: pasa el
/// tenant y el rol por encabezado, igual que Core. Sin RULES_SERVICE_URL o si Rules falla, lanza
/// RulesServiceUnavailableException; no hay orden local (T2).
/// </summary>
public class RulesClient : IRulesClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string? _rulesServiceUrl;
    private readonly ILogger<RulesClient> _logger;

    public RulesClient(HttpClient httpClient, IConfiguration configuration, ILogger<RulesClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rulesServiceUrl = configuration["RULES_SERVICE_URL"]?.TrimEnd('/');
    }

    public async Task<IReadOnlyList<RankedAlly>> RankAlliesAsync(
        string criterio, IReadOnlyCollection<Ally> candidatos, Guid tenantId, string role, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(_rulesServiceUrl))
        {
            _logger.LogError("RULES_SERVICE_URL no está configurada; Dispatch no puede pedir el orden de los aliados.");
            throw new RulesServiceUnavailableException("RULES_SERVICE_URL no está configurada.");
        }

        var body = new RankRequest(criterio, candidatos
            .Select(a => new RankCandidate(a.Id.ToString(), a.CalificacionPromedio, a.TotalCalificaciones, a.ZonasCobertura))
            .ToList());

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_rulesServiceUrl}/api/v1/rules/rank-allies")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        request.Headers.Add("X-User-Role", role);
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            request.Headers.Add("X-Correlation-ID", correlationId);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Rules respondió {StatusCode} al ordenar los aliados.", (int)response.StatusCode);
                throw new RulesServiceUnavailableException($"Rules respondió {(int)response.StatusCode} al ordenar los aliados.");
            }

            var ranking = JsonSerializer.Deserialize<RankResponse>(await response.Content.ReadAsStringAsync(), JsonOptions);
            return ToRankedAllies(ranking);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException or ArgumentNullException)
        {
            _logger.LogError(ex, "Fallo de conexión, tiempo de espera agotado o respuesta inválida de Rules.");
            throw new RulesServiceUnavailableException("Rules no respondió o su respuesta no se pudo interpretar.", ex);
        }
    }

    private static List<RankedAlly> ToRankedAllies(RankResponse? ranking)
    {
        if (ranking?.Aliados == null)
        {
            throw new RulesServiceUnavailableException("Rules devolvió una respuesta sin aliados.");
        }

        return ranking.Aliados
            .Select(a => new RankedAlly(Guid.Parse(a.Id), a.Posicion, a.Puntaje, a.Razones ?? new List<string>()))
            .ToList();
    }

    private sealed record RankRequest(string Criterio, List<RankCandidate> Candidatos);

    private sealed record RankCandidate(string Id, decimal? CalificacionPromedio, int TotalCalificaciones, int? ZonasCobertura);

    private sealed record RankResponse(List<RankResponseAlly>? Aliados);

    private sealed record RankResponseAlly(string Id, int Posicion, decimal? Puntaje, List<string>? Razones);
}
