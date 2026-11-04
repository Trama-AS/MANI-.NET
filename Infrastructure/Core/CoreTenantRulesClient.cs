using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ManiDispatch.Infrastructure.Core;

/// <summary>
/// Lee de Core las reglas operativas vigentes del tenant (SCRUM-1366), con el JWT reenviado. Sin criterio legible no
/// hay orden: lanza RulesServiceUnavailableException y Dispatch no elige uno por su cuenta (T2).
/// </summary>
public class CoreTenantRulesClient : ITenantRulesReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string? _coreServiceUrl;
    private readonly ILogger<CoreTenantRulesClient> _logger;

    public CoreTenantRulesClient(HttpClient httpClient, IConfiguration configuration, ILogger<CoreTenantRulesClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _coreServiceUrl = configuration["CORE_SERVICE_URL"]?.TrimEnd('/');
    }

    public async Task<string> GetCriterioOrdenListadoAsync(Guid tenantId, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(_coreServiceUrl))
        {
            throw new RulesServiceUnavailableException("CORE_SERVICE_URL no está configurada.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_coreServiceUrl}/api/v1/tenant/rules");
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            request.Headers.Add("X-Correlation-ID", correlationId);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new CoreAccessDeniedException("Core negó el acceso a las reglas del tenant.");
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Core respondió {StatusCode} al leer las reglas del tenant.", (int)response.StatusCode);
                throw new RulesServiceUnavailableException($"Core respondió {(int)response.StatusCode} al leer las reglas del tenant.");
            }

            var reglas = JsonSerializer.Deserialize<TenantRulesResponse>(await response.Content.ReadAsStringAsync(), JsonOptions);
            var criterio = reglas?.Reglas?.OrdenListado?.Criterio;
            if (string.IsNullOrWhiteSpace(criterio))
            {
                throw new RulesServiceUnavailableException("Las reglas del tenant no traen el criterio de orden.");
            }
            return criterio;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Fallo de conexión, tiempo de espera agotado o respuesta inválida al leer las reglas del tenant.");
            throw new RulesServiceUnavailableException("No se pudieron leer las reglas del tenant.", ex);
        }
    }

    private sealed record TenantRulesResponse(ReglasOperativas? Reglas);

    private sealed record ReglasOperativas(OrdenListadoRegla? OrdenListado);

    private sealed record OrdenListadoRegla(string? Criterio);
}
