using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ManiDispatch.Infrastructure.Repositories;

public class PostgresAllyRepository : IAllyRepository
{
    private readonly string? _connectionString;
    private readonly ILogger<PostgresAllyRepository> _logger;

    public PostgresAllyRepository(IConfiguration configuration, ILogger<PostgresAllyRepository> logger)
    {
        _logger = logger;
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? configuration["SUPABASE_DB_URL"]
                            ?? configuration["DATABASE_URL"];
    }

    public async Task<(IEnumerable<Ally> Allies, int TotalCount)> GetEligibleAlliesAsync(MatchCriteria criteria)
    {
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
                SELECT COUNT(DISTINCT a.id)
                FROM aliado a
                INNER JOIN aliado_categoria ac ON a.id = ac.aliado_id AND a.tenant_id = ac.tenant_id
                INNER JOIN cobertura_aliado ca ON a.id = ca.aliado_id AND a.tenant_id = ca.tenant_id
                WHERE a.tenant_id = @TenantId
                  AND ca.zona_id = @ZonaId
                  AND ac.categoria_id = @CategoriaId
                  AND (a.estado_verificacion = 'VERIFICADO' OR a.estado_verificacion = 'aprobado');";

            const string selectSql = @"
                SELECT DISTINCT 
                    a.id AS Id,
                    a.nombre_razon_social AS NombreRazonSocial,
                    a.tipo AS Tipo,
                    a.estado_verificacion AS EstadoVerificacion
                FROM aliado a
                INNER JOIN aliado_categoria ac ON a.id = ac.aliado_id AND a.tenant_id = ac.tenant_id
                INNER JOIN cobertura_aliado ca ON a.id = ca.aliado_id AND a.tenant_id = ca.tenant_id
                WHERE a.tenant_id = @TenantId
                  AND ca.zona_id = @ZonaId
                  AND ac.categoria_id = @CategoriaId
                  AND (a.estado_verificacion = 'VERIFICADO' OR a.estado_verificacion = 'aprobado')
                ORDER BY a.nombre_razon_social ASC
                OFFSET @Offset LIMIT @Limit;";

            int offset = (criteria.Page - 1) * criteria.PageSize;
            int limit = criteria.PageSize;

            var parameters = new
            {
                TenantId = criteria.TenantId,
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
