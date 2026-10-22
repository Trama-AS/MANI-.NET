using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ManiDispatch.Infrastructure.Dispatch;

/// <summary>
/// Publica y consulta las ofertas de despacho a través de Core (SAD §7.4, ADR-0030): la mensajería es de Core,
/// Dispatch no escribe ni lee la tabla notificacion. Sin CORE_SERVICE_URL o ante cualquier fallo lanza
/// CoreServiceUnavailableException para no ocultar el error.
/// </summary>
public class CoreDispatchPublisher : IDispatchPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly string? _coreServiceUrl;
    private readonly ILogger<CoreDispatchPublisher> _logger;

    public CoreDispatchPublisher(HttpClient httpClient, IConfiguration configuration, ILogger<CoreDispatchPublisher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _coreServiceUrl = configuration["CORE_SERVICE_URL"]?.TrimEnd('/');
    }

    public async Task PublishOfferAsync(DispatchOffer offer)
    {
        var body = new
        {
            tenantId = offer.TenantId,
            allyId = offer.AllyId,
            tipo = "OFERTA_DESPACHO",
            canal = "PUSH",
            payload = new
            {
                requestId = offer.RequestId,
                tenantId = offer.TenantId,
                allyId = offer.AllyId,
                allyNombre = offer.AllyNombre,
                estado = offer.Estado,
                correlationId = offer.CorrelationId,
                timestamp = offer.Timestamp
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{RequireBaseUrl()}/api/v1/notifications")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Tenant-Id", offer.TenantId.ToString());
        if (!string.IsNullOrWhiteSpace(offer.CorrelationId))
        {
            request.Headers.Add("X-Correlation-ID", offer.CorrelationId);
        }

        using var response = await SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Core respondió {StatusCode} al publicar la oferta para el aliado {AllyId} sobre la solicitud {RequestId}.",
                (int)response.StatusCode, offer.AllyId, offer.RequestId);
            throw new CoreServiceUnavailableException($"Core respondió {(int)response.StatusCode} al publicar la oferta.");
        }

        _logger.LogInformation(
            "[DISPATCH-BROADCAST] Oferta enviada a Core para el aliado {AllyId} ({AllyNombre}) sobre la solicitud {RequestId} [Tenant {TenantId}, Corr {CorrelationId}].",
            offer.AllyId, offer.AllyNombre, offer.RequestId, offer.TenantId, offer.CorrelationId);
    }

    public async Task<IReadOnlyList<DispatchOffer>> GetOffersByRequestAsync(Guid requestId, Guid tenantId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{RequireBaseUrl()}/api/v1/notifications?requestId={requestId}&tipo=OFERTA_DESPACHO");
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());

        using var response = await SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Core respondió {StatusCode} al consultar las ofertas de la solicitud {RequestId}.", (int)response.StatusCode, requestId);
            throw new CoreServiceUnavailableException($"Core respondió {(int)response.StatusCode} al consultar las ofertas.");
        }

        List<CoreOfferDto>? coreOffers;
        try
        {
            coreOffers = JsonSerializer.Deserialize<List<CoreOfferDto>>(await response.Content.ReadAsStringAsync(), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new CoreServiceUnavailableException("Core devolvió una respuesta que no se pudo interpretar.", ex);
        }

        if (coreOffers == null)
        {
            return Array.Empty<DispatchOffer>();
        }

        return coreOffers.Select(c => new DispatchOffer(
            RequestId: c.RequestId != Guid.Empty ? c.RequestId : requestId,
            TenantId: c.TenantId != Guid.Empty ? c.TenantId : tenantId,
            AllyId: c.AllyId,
            AllyNombre: c.AllyNombre ?? string.Empty,
            Estado: c.Estado ?? "ENVIADA",
            CorrelationId: c.CorrelationId ?? string.Empty,
            Timestamp: c.Timestamp != default ? c.Timestamp : DateTime.UtcNow
        )).ToList();
    }

    private string RequireBaseUrl()
    {
        if (string.IsNullOrWhiteSpace(_coreServiceUrl))
        {
            _logger.LogError("CORE_SERVICE_URL no está configurada; Dispatch no puede publicar ofertas.");
            throw new CoreServiceUnavailableException("CORE_SERVICE_URL no está configurada.");
        }
        return _coreServiceUrl;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
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

    private class CoreOfferDto
    {
        public Guid RequestId { get; set; }
        public Guid TenantId { get; set; }
        public Guid AllyId { get; set; }
        public string? AllyNombre { get; set; }
        public string? Estado { get; set; }
        public string? CorrelationId { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
