using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using System.Net.Http;
using System.Text.Json;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ManiDispatch.Infrastructure.Repositories;

public class CoreEligibleResponse
{
    public IEnumerable<Ally>? Allies { get; set; }
    public int TotalCount { get; set; }
}

public class PostgresAllyRepository : IAllyRepository
{
    private readonly string? _connectionString;
    private readonly string? _coreServiceUrl;
    private readonly ILogger<PostgresAllyRepository> _logger;

    public PostgresAllyRepository(IConfiguration configuration, ILogger<PostgresAllyRepository> logger)
    {
        _logger = logger;
        _coreServiceUrl = configuration["CORE_SERVICE_URL"];
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? configuration["SUPABASE_DB_URL"]
                            ?? configuration["DATABASE_URL"];
    }

    public async Task<(IEnumerable<Ally> Allies, int TotalCount)> GetEligibleAlliesAsync(MatchCriteria criteria)
    {
        // ADR-0030: si CORE_SERVICE_URL está configurada, se cruza la frontera de datos por HTTP hacia Core
        if (!string.IsNullOrWhiteSpace(_coreServiceUrl))
        {
            try
            {
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var url = $"{_coreServiceUrl.TrimEnd('/')}/api/v1/allies/eligible?categoriaId={criteria.CategoriaId}&zonaId={criteria.ZonaId}&page={criteria.Page}&pageSize={criteria.PageSize}";
                if (criteria.RequestId.HasValue)
                {
                    url += $"&requestId={criteria.RequestId.Value}";
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("X-Tenant-Id", criteria.TenantId.ToString());
                if (!string.IsNullOrWhiteSpace(criteria.CorrelationId))
                {
                    req.Headers.Add("X-Correlation-ID", criteria.CorrelationId);
                }

                var response = await httpClient.SendAsync(req);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Core service respondió {StatusCode} al consultar elegibilidad de aliados.", response.StatusCode);
                    throw new HttpRequestException($"Error consultando elegibilidad en Core service: {response.StatusCode}");
                }

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<CoreEligibleResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return (result?.Allies ?? Enumerable.Empty<Ally>(), result?.TotalCount ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo de conexión o timeout consultando elegibilidad hacia Core service.");
                throw;
            }
        }

        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger.LogWarning("Cadena de conexión PostgreSQL no configurada en PostgresAllyRepository.");
            return (Enumerable.Empty<Ally>(), 0);
        }

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string countSql = @"
                WITH RECURSIVE ascendencia AS (
                    SELECT z.id, z.zona_padre_id, 1 AS nivel FROM zona z WHERE z.id = @ZonaId
                    UNION ALL
                    SELECT z.id, z.zona_padre_id, a.nivel + 1
                    FROM zona z JOIN ascendencia a ON z.id = a.zona_padre_id
                    WHERE a.nivel < 10
                )
                SELECT COUNT(DISTINCT a.id)
                FROM aliado a
                INNER JOIN aliado_categoria ac ON a.id = ac.aliado_id AND a.tenant_id = ac.tenant_id
                INNER JOIN cobertura_aliado ca ON a.id = ca.aliado_id AND a.tenant_id = ca.tenant_id
                INNER JOIN ascendencia asc_z ON ca.zona_id = asc_z.id
                INNER JOIN zona z ON z.id = ca.zona_id AND z.estado = 'ACTIVO'
                WHERE a.tenant_id = @TenantId
                  AND ac.categoria_id = @CategoriaId
                  AND a.estado_verificacion = 'VERIFICADO'
                  AND NOT EXISTS (
                      SELECT 1 FROM aliado_ausencia aa
                      WHERE aa.aliado_id = a.id
                        AND aa.tenant_id = a.tenant_id
                        AND aa.inicio <= now() AND aa.fin > now()
                  )
                  AND (@RequestId IS NULL OR NOT EXISTS (
                      SELECT 1 FROM solicitud_rechazo r
                      WHERE r.solicitud_id = @RequestId AND r.aliado_id = a.id
                  ));";

            const string selectSql = @"
                WITH RECURSIVE ascendencia AS (
                    SELECT z.id, z.zona_padre_id, 1 AS nivel FROM zona z WHERE z.id = @ZonaId
                    UNION ALL
                    SELECT z.id, z.zona_padre_id, a.nivel + 1
                    FROM zona z JOIN ascendencia a ON z.id = a.zona_padre_id
                    WHERE a.nivel < 10
                )
                SELECT DISTINCT 
                    a.id AS Id,
                    a.nombre_razon_social AS NombreRazonSocial,
                    a.tipo AS Tipo,
                    a.estado_verificacion AS EstadoVerificacion
                FROM aliado a
                INNER JOIN aliado_categoria ac ON a.id = ac.aliado_id AND a.tenant_id = ac.tenant_id
                INNER JOIN cobertura_aliado ca ON a.id = ca.aliado_id AND a.tenant_id = ca.tenant_id
                INNER JOIN ascendencia asc_z ON ca.zona_id = asc_z.id
                INNER JOIN zona z ON z.id = ca.zona_id AND z.estado = 'ACTIVO'
                WHERE a.tenant_id = @TenantId
                  AND ac.categoria_id = @CategoriaId
                  AND a.estado_verificacion = 'VERIFICADO'
                  AND NOT EXISTS (
                      SELECT 1 FROM aliado_ausencia aa
                      WHERE aa.aliado_id = a.id
                        AND aa.tenant_id = a.tenant_id
                        AND aa.inicio <= now() AND aa.fin > now()
                  )
                  AND (@RequestId IS NULL OR NOT EXISTS (
                      SELECT 1 FROM solicitud_rechazo r
                      WHERE r.solicitud_id = @RequestId AND r.aliado_id = a.id
                  ))
                ORDER BY a.nombre_razon_social ASC
                OFFSET @Offset LIMIT @Limit;";

            int offset = (criteria.Page - 1) * criteria.PageSize;
            int limit = criteria.PageSize;

            var parameters = new
            {
                TenantId = criteria.TenantId,
                RequestId = criteria.RequestId,
                ZonaId = criteria.ZonaId,
                CategoriaId = criteria.CategoriaId,
                Offset = offset,
                Limit = limit
            };

            var totalCount = await connection.ExecuteScalarAsync<int>(countSql, parameters);
            var allies = (await connection.QueryAsync<Ally>(selectSql, parameters)).ToList();

            return (allies, totalCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al consultar aliados elegibles en PostgreSQL para TenantId {TenantId}.", criteria.TenantId);
            throw;
        }
    }

    public async Task<SolicitudContext?> GetSolicitudContextAsync(Guid requestId, Guid tenantId)
    {
        if (!string.IsNullOrWhiteSpace(_coreServiceUrl))
        {
            try
            {
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var url = $"{_coreServiceUrl.TrimEnd('/')}/api/v1/solicitudes/{requestId}/context";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("X-Tenant-Id", tenantId.ToString());

                var response = await httpClient.SendAsync(req);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return null;
                }
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Core service respondió {StatusCode} al consultar contexto de solicitud {RequestId}.", response.StatusCode, requestId);
                    throw new HttpRequestException($"Error consultando contexto de solicitud en Core: {response.StatusCode}");
                }

                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<SolicitudContext>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo consultando contexto de solicitud hacia Core service.");
                throw;
            }
        }

        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger.LogWarning("Cadena de conexión PostgreSQL no configurada en PostgresAllyRepository.");
            return null;
        }

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = @"
                SELECT 
                    id AS Id,
                    tenant_id AS TenantId,
                    zona_id AS ZonaId,
                    categoria_id AS CategoriaId,
                    estado AS Estado
                FROM solicitud
                WHERE id = @RequestId AND tenant_id = @TenantId;";

            return await connection.QueryFirstOrDefaultAsync<SolicitudContext>(sql, new
            {
                RequestId = requestId,
                TenantId = tenantId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al consultar contexto de solicitud {RequestId} para TenantId {TenantId}.", requestId, tenantId);
            throw;
        }
    }
}
