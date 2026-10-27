using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;
using ManiDispatch.Domain.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ManiDispatch.Infrastructure.Core;

/// <summary>
/// Cliente del comando de asignación y de la bandeja del aliado en Core (decisión 6A, SCRUM-1403/1404). Falla
/// cerrado: si Core no responde o responde algo inesperado, lanza CoreServiceUnavailableException y nada se da por
/// asignado.
/// </summary>
public class CoreAssignmentClient : ICoreAssignmentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly string? _coreServiceUrl;
    private readonly ILogger<CoreAssignmentClient> _logger;

    public CoreAssignmentClient(HttpClient httpClient, IConfiguration configuration, ILogger<CoreAssignmentClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _coreServiceUrl = configuration["CORE_SERVICE_URL"]?.TrimEnd('/');
    }

    public async Task<AssignmentResult> AssignAsync(Guid requestId, Guid tenantId, string? idempotencyKey, string? correlationId)
    {
        var url = $"{RequireBaseUrl()}/api/v1/solicitudes/{requestId}/asignacion";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        using var response = await SendAsync(request, tenantId, correlationId);
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
            {
                var body = await ReadAsync<CoreAssignmentResponse>(response);
                if (body?.Solicitud == null)
                {
                    throw new CoreServiceUnavailableException("Core confirmó la asignación sin devolver la solicitud.");
                }
                return body.Asignada
                    ? new AssignmentResult(AssignmentStatus.Assigned, body.Solicitud.AliadoId, "Solicitud asignada exitosamente al profesional.")
                    : new AssignmentResult(AssignmentStatus.AlreadyAssigned, body.Solicitud.AliadoId, "Solicitud ya asignada previamente a este aliado.");
            }
            case HttpStatusCode.NotFound:
            {
                var error = await ReadErrorAsync(response);
                return new AssignmentResult(AssignmentStatus.NotFound, null, error?.Error, error?.Code ?? "MANI-SOL-404");
            }
            case HttpStatusCode.Conflict:
            {
                // Core no revela quién ganó: el 409 no lleva el id del otro aliado.
                var error = await ReadErrorAsync(response);
                var code = error?.Code ?? "MANI-SOL-409";
                var status = code == "MANI-SOL-409E" ? AssignmentStatus.InvalidState : AssignmentStatus.Conflict;
                return new AssignmentResult(status, null, error?.Error, code);
            }
            case HttpStatusCode.Forbidden:
            {
                var error = await ReadErrorAsync(response);
                return new AssignmentResult(AssignmentStatus.Forbidden, null, error?.Error, error?.Code ?? "MANI-SOL-403");
            }
            case HttpStatusCode.Unauthorized:
            {
                var error = await ReadErrorAsync(response);
                throw new CoreAccessDeniedException("Core no aceptó la sesión reenviada.", error?.Code);
            }
            default:
                _logger.LogError("Core respondió {StatusCode} al asignar la solicitud {RequestId}.", (int)response.StatusCode, requestId);
                throw new CoreServiceUnavailableException($"Core respondió {(int)response.StatusCode} al asignar la solicitud.");
        }
    }

    public async Task<IReadOnlyList<AllyOffer>> GetOwnOffersAsync(Guid tenantId, string? correlationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{RequireBaseUrl()}/api/v1/allies/me/offers");
        using var response = await SendAsync(request, tenantId, correlationId);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var error = await ReadErrorAsync(response);
            throw new CoreAccessDeniedException("Core no reconoce a la sesión como aliado.", error?.Code);
        }
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Core respondió {StatusCode} al consultar la bandeja del aliado.", (int)response.StatusCode);
            throw new CoreServiceUnavailableException($"Core respondió {(int)response.StatusCode} al consultar la bandeja del aliado.");
        }

        var body = await ReadAsync<CoreOffersResponse>(response);
        return body?.Items ?? new List<AllyOffer>();
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

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Guid tenantId, string? correlationId)
    {
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

    // El cuerpo de error es informativo: si no se puede leer, se sigue con el código HTTP.
    private static async Task<CoreError?> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var json = await response.Content.ReadAsStringAsync();
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<CoreError>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class CoreAssignmentResponse
    {
        public CoreAssignedRequest? Solicitud { get; set; }
        public bool Asignada { get; set; }
    }

    private sealed class CoreAssignedRequest
    {
        public Guid Id { get; set; }
        public string? Estado { get; set; }
        public Guid? AliadoId { get; set; }
    }

    private sealed class CoreOffersResponse
    {
        public List<AllyOffer>? Items { get; set; }
    }

    private sealed class CoreError
    {
        public string? Error { get; set; }
        public string? Code { get; set; }
    }
}
