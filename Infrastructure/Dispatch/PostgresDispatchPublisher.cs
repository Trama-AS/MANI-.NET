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
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger.LogWarning("Cadena de conexión PostgreSQL no configurada en PostgresDispatchPublisher.");
            return;
        }

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

            int rowsAffected = await connection.ExecuteAsync(insertSql, new
            {
                offer.TenantId,
                offer.AllyId,
                Payload = payloadJson,
                offer.Timestamp
            });

            if (rowsAffected == 0)
            {
                _logger.LogWarning("[DISPATCH-BROADCAST] No se encontró usuario_id para aliado {AllyId} en tenant {TenantId}. Notificación no insertada.", offer.AllyId, offer.TenantId);
            }
            else
            {
                _logger.LogInformation(
                    "[DISPATCH-BROADCAST] Oferta transmitida y persistida para aliado {AllyId} ({AllyNombre}) sobre solicitud {RequestId} [Tenant {TenantId}, Corr {CorrelationId}].",
                    offer.AllyId, offer.AllyNombre, offer.RequestId, offer.TenantId, offer.CorrelationId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al persistir notificación en PostgreSQL para la oferta al aliado {AllyId} sobre solicitud {RequestId}.", offer.AllyId, offer.RequestId);
            throw;
        }
    }

    public async Task<IReadOnlyList<DispatchOffer>> GetOffersByRequestAsync(Guid requestId, Guid tenantId)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return Array.Empty<DispatchOffer>();
        }

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string selectSql = @"
                SELECT 
                    payload->>'requestId' AS RequestIdStr,
                    tenant_id AS TenantId,
                    payload->>'allyId' AS AllyIdStr,
                    payload->>'allyNombre' AS AllyNombre,
                    COALESCE(payload->>'estado', 'ENVIADA') AS Estado,
                    COALESCE(payload->>'correlationId', '') AS CorrelationId,
                    enviado_at AS Timestamp
                FROM notificacion
                WHERE tipo = 'OFERTA_DESPACHO'
                  AND tenant_id = @TenantId
                  AND payload->>'requestId' = @RequestIdStr;";

            var rows = await connection.QueryAsync(selectSql, new
            {
                TenantId = tenantId,
                RequestIdStr = requestId.ToString()
            });

            var offers = new List<DispatchOffer>();
            foreach (var row in rows)
            {
                Guid.TryParse((string)row.requestidstr, out Guid rId);
                Guid.TryParse((string)row.allyidstr, out Guid aId);
                offers.Add(new DispatchOffer(
                    RequestId: rId != Guid.Empty ? rId : requestId,
                    TenantId: (Guid)row.tenantid,
                    AllyId: aId,
                    AllyNombre: (string)row.allynombre ?? string.Empty,
                    Estado: (string)row.estado,
                    CorrelationId: (string)row.correlationid,
                    Timestamp: (DateTime)row.timestamp
                ));
            }

            return offers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al consultar ofertas de solicitud {RequestId} en notificacion.", requestId);
            throw;
        }
    }
}
