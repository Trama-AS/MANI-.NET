using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using ManiDispatch.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ManiDispatch.Infrastructure.Repositories;

public class PostgresSolicitudAssignmentRepository : ISolicitudAssignmentRepository
{
    private readonly string? _connectionString;
    private readonly ILogger<PostgresSolicitudAssignmentRepository> _logger;

    public PostgresSolicitudAssignmentRepository(
        IConfiguration? configuration,
        ILogger<PostgresSolicitudAssignmentRepository> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionString = configuration != null
            ? (configuration["ConnectionStrings:DefaultConnection"]
               ?? configuration["SUPABASE_DB_URL"]
               ?? configuration["DATABASE_URL"])
            : null;
    }

    private void EnsureConnectionStringConfigured()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("La base de datos PostgreSQL no está disponible: cadena de conexión no configurada (ADR-0016).");
        }
    }

    public async Task<AssignmentResult> AcceptRequestAsync(Guid requestId, Guid tenantId, Guid aliadoId)
    {
        EnsureConnectionStringConfigured();

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // 1. Verificación previa de existencia y estado (Paridad con RPC 005)
            const string queryCurrent = @"
                SELECT aliado_id, estado 
                FROM solicitud 
                WHERE id = @RequestId AND tenant_id = @TenantId;";

            var current = await connection.QueryFirstOrDefaultAsync<(Guid? AliadoId, string Estado)>(queryCurrent, new
            {
                RequestId = requestId,
                TenantId = tenantId
            });

            if (current == default)
            {
                return new AssignmentResult(AssignmentStatus.NotFound, null, "Solicitud no encontrada para este tenant.");
            }

            // Idempotencia para el mismo aliado ganador (RPC 005 / DoD §9.3)
            if (current.AliadoId == aliadoId)
            {
                return new AssignmentResult(
                    AssignmentStatus.AlreadyAssigned,
                    aliadoId,
                    "Solicitud ya asignada previamente a este aliado."
                );
            }

            if (current.AliadoId.HasValue && current.AliadoId.Value != aliadoId)
            {
                return new AssignmentResult(
                    AssignmentStatus.Conflict,
                    current.AliadoId.Value,
                    "La solicitud ya fue asignada a otro profesional."
                );
            }

            // 2. Ejecución atómica de exclusión concurrente (ADR-0016 / RNF-05)
            const string updateSql = @"
                UPDATE solicitud
                SET aliado_id = @AliadoId, estado = 'ASIGNADA', updated_at = now()
                WHERE id = @RequestId 
                  AND tenant_id = @TenantId 
                  AND aliado_id IS NULL
                  AND estado = 'PENDIENTE';";

            int affected = await connection.ExecuteAsync(updateSql, new
            {
                AliadoId = aliadoId,
                RequestId = requestId,
                TenantId = tenantId
            });

            if (affected > 0)
            {
                return new AssignmentResult(
                    AssignmentStatus.Assigned,
                    aliadoId,
                    "Solicitud asignada exitosamente al profesional."
                );
            }

            // Si no afectó filas, verificamos quién la ganó concurrentemente
            var winner = await connection.QueryFirstOrDefaultAsync<Guid?>(
                "SELECT aliado_id FROM solicitud WHERE id = @RequestId AND tenant_id = @TenantId;",
                new { RequestId = requestId, TenantId = tenantId }
            );

            if (winner == aliadoId)
            {
                return new AssignmentResult(AssignmentStatus.AlreadyAssigned, aliadoId, "Solicitud ya asignada previamente a este aliado.");
            }

            return new AssignmentResult(AssignmentStatus.Conflict, winner, "La solicitud ya fue asignada a otro profesional.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo en base de datos al ejecutar aceptación atómica en PostgreSQL para solicitud {RequestId}.", requestId);
            throw;
        }
    }

    public async Task<bool> RejectRequestAsync(Guid requestId, Guid tenantId, Guid aliadoId, string motivo)
    {
        EnsureConnectionStringConfigured();

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string insertSql = @"
                INSERT INTO solicitud_rechazo (tenant_id, solicitud_id, aliado_id, motivo)
                VALUES (@TenantId, @RequestId, @AliadoId, @Motivo)
                ON CONFLICT (solicitud_id, aliado_id) DO NOTHING;";

            await connection.ExecuteAsync(insertSql, new
            {
                TenantId = tenantId,
                RequestId = requestId,
                AliadoId = aliadoId,
                Motivo = motivo
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo al persistir rechazo en PostgreSQL para solicitud {RequestId}.", requestId);
            throw;
        }
    }

    public async Task<IReadOnlyList<Guid>> GetAssignedRequestIdsAsync(Guid tenantId, Guid aliadoId)
    {
        EnsureConnectionStringConfigured();

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Asignada = el aliado ganó la exclusión concurrente (aliado_id). Cualquier estado posterior a la
            // asignación cuenta, salvo la cancelación. Core usa los ids como conjunto, así que no se ordenan.
            const string sql = @"
                SELECT id
                FROM solicitud
                WHERE tenant_id = @TenantId
                  AND aliado_id = @AliadoId
                  AND estado <> 'CANCELADA';";

            var ids = await connection.QueryAsync<Guid>(sql, new { TenantId = tenantId, AliadoId = aliadoId });
            return ids.ToList();
        }
        catch (Exception ex)
        {
            // El controlador responde 503; aquí solo se agrega el contexto de la consulta.
            throw new InvalidOperationException($"Fallo en base de datos al consultar las asignaciones del aliado {aliadoId}.", ex);
        }
    }
}
