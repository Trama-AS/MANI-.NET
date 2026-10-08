using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using ManiDispatch.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ManiDispatch.Infrastructure.Dispatch;

public class PostgresDispatchPublisher : IDispatchPublisher
{
    private static readonly ConcurrentBag<DispatchOffer> PublishedOffers = new();
    private readonly string? _connectionString;
    private readonly ILogger<PostgresDispatchPublisher> _logger;

    public PostgresDispatchPublisher(IConfiguration configuration, ILogger<PostgresDispatchPublisher> logger)
    {
        _logger = logger;
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? configuration["SUPABASE_DB_URL"]
                            ?? configuration["DATABASE_URL"];
    }

    public async Task PublishOfferAsync(DispatchOffer offer)
    {
        PublishedOffers.Add(offer);

        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                const string insertSql = @"
                    INSERT INTO notificacion (tenant_id, usuario_id, tipo, canal, payload, enviado_at)
                    SELECT 
                        @TenantId,
                        a.usuario_id,
                        'OFERTA_DESPACHO',
                        'PUSH',
                        @Payload::jsonb,
                        @Timestamp
                    FROM aliado a
                    WHERE a.id = @AllyId AND a.tenant_id = @TenantId
                    LIMIT 1;";

                var payloadJson = JsonSerializer.Serialize(new
                {
                    requestId = offer.RequestId,
                    tenantId = offer.TenantId,
                    allyId = offer.AllyId,
                    allyNombre = offer.AllyNombre,
                    estado = offer.Estado,
                    correlationId = offer.CorrelationId,
                    timestamp = offer.Timestamp
                });

                await connection.ExecuteAsync(insertSql, new
                {
                    offer.TenantId,
                    offer.AllyId,
                    Payload = payloadJson,
                    offer.Timestamp
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo persistir notificación en PostgreSQL para la oferta al aliado {AllyId}. Continuando con entrega broadcast.", offer.AllyId);
            }
        }

        _logger.LogInformation(
            "[DISPATCH-BROADCAST] Oferta transmitida y persistida para aliado {AllyId} ({AllyNombre}) sobre solicitud {RequestId} [Tenant {TenantId}, Corr {CorrelationId}].",
            offer.AllyId, offer.AllyNombre, offer.RequestId, offer.TenantId, offer.CorrelationId);
    }

    public Task<IReadOnlyList<DispatchOffer>> GetOffersByRequestAsync(Guid requestId, Guid tenantId)
    {
        var offers = PublishedOffers
            .Where(o => o.RequestId == requestId && o.TenantId == tenantId)
            .ToList();

        return Task.FromResult<IReadOnlyList<DispatchOffer>>(offers);
    }
}
