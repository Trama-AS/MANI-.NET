using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;
using ManiDispatch.Domain.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ManiDispatch.Infrastructure.Core;

public class CoreEligibleResponse
{
    public IEnumerable<Ally>? Allies { get; set; }
    public int TotalCount { get; set; }
}

/// <summary>
/// Pide a Core la elegibilidad de aliados y el contexto de la solicitud por HTTP (SAD §7.4, ADR-0030).
/// Dispatch no lee las tablas de Core. Sin CORE_SERVICE_URL o si Core falla, lanza CoreServiceUnavailableException.
/// </summary>
public class CoreAllyClient : IAllyRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly string? _coreServiceUrl;
    private readonly ILogger<CoreAllyClient> _logger;

    public CoreAllyClient(HttpClient httpClient, IConfiguration configuration, ILogger<CoreAllyClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _coreServiceUrl = configuration["CORE_SERVICE_URL"]?.TrimEnd('/');
    }

    public async Task<(IEnumerable<Ally> Allies, int TotalCount)> GetEligibleAlliesAsync(MatchCriteria criteria)
    {
        var url = $"{RequireBaseUrl()}/api/v1/allies/eligible" +
                  $"?categoriaId={criteria.CategoriaId}&zonaId={criteria.ZonaId}&page={criteria.Page}&pageSize={criteria.PageSize}";
        // Los rechazos son dominio de Dispatch (T1): se mandan como lista y Core los quita antes de paginar.
        if (criteria.ExcludeAllyIds.Count > 0)
        {
            url += $"&excludeAllyIds={string.Join(",", criteria.ExcludeAllyIds.Distinct())}";
        }

        if (!string.IsNullOrWhiteSpace(criteria.Tipo))
        {
            url += $"&tipo={Uri.EscapeDataString(criteria.Tipo)}";
        }

        using var response = await SendAsync(url, criteria.TenantId, criteria.CorrelationId);
        ThrowIfAccessDenied(response, "la elegibilidad de aliados");
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Core respondió {StatusCode} al consultar la elegibilidad de aliados.", (int)response.StatusCode);
            throw new CoreServiceUnavailableException($"Core respondió {(int)response.StatusCode} al consultar la elegibilidad de aliados.");
        }

        var result = await ReadAsync<CoreEligibleResponse>(response);
        return (result?.Allies ?? Enumerable.Empty<Ally>(), result?.TotalCount ?? 0);
    }

    public async Task<SolicitudContext?> GetSolicitudContextAsync(Guid requestId, Guid tenantId)
    {
        var url = $"{RequireBaseUrl()}/api/v1/solicitudes/{requestId}/context";

        using var response = await SendAsync(url, tenantId, null);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        ThrowIfAccessDenied(response, "el contexto de la solicitud");
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Core respondió {StatusCode} al consultar el contexto de la solicitud {RequestId}.", (int)response.StatusCode, requestId);
            throw new CoreServiceUnavailableException($"Core respondió {(int)response.StatusCode} al consultar el contexto de la solicitud.");
        }

        return await ReadAsync<SolicitudContext>(response);
    }

    public async Task<AllyProfile?> GetAllyProfileAsync(Guid usuarioId, Guid tenantId)
    {
        var url = $"{RequireBaseUrl()}/api/v1/allies/me";

        using var response = await SendAsync(url, tenantId, null);
        if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Forbidden)
        {
            // Core no encontró un aliado para el token reenviado, o el rol no es de aliado: no hay perfil de aliado.
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Core respondió {StatusCode} al consultar el perfil del aliado.", (int)response.StatusCode);
            throw new CoreServiceUnavailableException($"Core respondió {(int)response.StatusCode} al consultar el perfil del aliado.");
        }

        var profile = await ReadAsync<AllyProfile>(response);

        // Core resuelve al aliado por el JWT reenviado: si no coincide con la cuenta y el tenant autenticados en Dispatch,
        // no se acepta (evita actuar con el perfil de otra cuenta).
        if (profile == null || profile.UsuarioId != usuarioId || profile.TenantId != tenantId)
        {
            _logger.LogWarning("El perfil de aliado devuelto por Core no corresponde a la cuenta autenticada.");
            return null;
        }

        return profile;
    }

    // Un 401/403 de Core es la sesión rechazada, no una caída: se responde 403, no 503 (T2/T5).
    private void ThrowIfAccessDenied(HttpResponseMessage response, string recurso)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Core respondió {StatusCode} al consultar {Recurso}: sesión sin acceso.", (int)response.StatusCode, recurso);
            throw new CoreAccessDeniedException($"Core negó el acceso a {recurso}.");
        }
    }

    private string RequireBaseUrl()
    {
        if (string.IsNullOrWhiteSpace(_coreServiceUrl))
        {
            _logger.LogError("CORE_SERVICE_URL no está configurada; Dispatch no puede consultar a Core.");
            throw new CoreServiceUnavailableException("CORE_SERVICE_URL no está configurada.");
        }
        return _coreServiceUrl;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, Guid tenantId, string? correlationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            request.Headers.Add("X-Correlation-ID", correlationId);
        }

        try
        {
            return await _httpClient.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Fallo de conexión o tiempo de espera agotado hacia Core.");
            throw new CoreServiceUnavailableException("Core no respondió.", ex);
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response)
    {
        try
        {
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new CoreServiceUnavailableException("Core devolvió una respuesta que no se pudo interpretar.", ex);
        }
    }
}
