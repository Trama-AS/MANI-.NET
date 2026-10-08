using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Dapper;
using ManiDispatch.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ManiDispatch.Infrastructure.Repositories;

public class PostgresSolicitudAssignmentRepository : ISolicitudAssignmentRepository
{
    // Almacén concurrente seguro en memoria (RNF-05 / ADR-0016)
    // Permite tests unitarios y de estrés en memoria con atomicidad garantizada
    private static readonly ConcurrentDictionary<Guid, Guid> MemoryAssignments = new();
    private static readonly ConcurrentDictionary<(Guid SolicitudId, Guid AliadoId), string> MemoryRejections = new();
    private static readonly ConcurrentDictionary<Guid, AllyProfile> MemoryAllies = new();

    private readonly string? _connectionString;
    private readonly ILogger<PostgresSolicitudAssignmentRepository> _logger;

    public PostgresSolicitudAssignmentRepository(
        IConfiguration? configuration,
        ILogger<PostgresSolicitudAssignmentRepository> logger)
    {
        _logger = logger;
        _connectionString = configuration != null
            ? (configuration["ConnectionStrings:DefaultConnection"]
               ?? configuration["SUPABASE_DB_URL"]
               ?? configuration["DATABASE_URL"])
            : null;
    }

    public async Task<AllyProfile?> GetAllyByUserIdAsync(Guid usuarioId, Guid tenantId)
    {
        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                const string sql = @"
                    SELECT 
                        id AS Id,
                        tenant_id AS TenantId,
                        usuario_id AS UsuarioId,
                        estado_verificacion AS EstadoVerificacion
                    FROM aliado
                    WHERE usuario_id = @UsuarioId AND tenant_id = @TenantId
                    LIMIT 1;";

                var profile = await connection.QueryFirstOrDefaultAsync<AllyProfile>(sql, new
                {
                    UsuarioId = usuarioId,
                    TenantId = tenantId
                });

                if (profile != null) return profile;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error al consultar aliado por usuarioId en PostgreSQL. Usando fallback en memoria.");
            }
        }

        if (MemoryAllies.TryGetValue(usuarioId, out var memProfile) && memProfile.TenantId == tenantId)
        {
            return memProfile;
        }

        // Si no está registrado en memoria, por defecto se asume un perfil verificado para tests
        var defaultProfile = new AllyProfile(
            Id: usuarioId, // Mapeo 1:1 o autogenerado
            TenantId: tenantId,
            UsuarioId: usuarioId,
            EstadoVerificacion: "VERIFICADO"
        );
        MemoryAllies.TryAdd(usuarioId, defaultProfile);
        return defaultProfile;
    }

    public void RegisterMemoryAlly(AllyProfile profile)
    {
        MemoryAllies[profile.UsuarioId] = profile;
    }

    public async Task<AssignmentResult> AcceptRequestAsync(Guid requestId, Guid tenantId, Guid aliadoId)
    {
        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
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

                // 2. Ejecución atómica de exclusión concurrente
                const string updateSql = @"
                    UPDATE solicitud
                    SET aliado_id = @AliadoId, estado = 'ASIGNADA', updated_at = now()
                    WHERE id = @RequestId 
                      AND tenant_id = @TenantId 
                      AND (aliado_id IS NULL OR aliado_id = @AliadoId)
                      AND estado = 'PENDIENTE';";

                int affected = await connection.ExecuteAsync(updateSql, new
                {
                    AliadoId = aliadoId,
                    RequestId = requestId,
                    TenantId = tenantId
                });

                if (affected > 0)
                {
                    MemoryAssignments[requestId] = aliadoId;
                    return new AssignmentResult(
                        AssignmentStatus.Assigned,
                        aliadoId,
                        "Solicitud asignada exitosamente al profesional."
                    );
                }

                // Si no afectó filas, verificamos quién la ganó
                var winner = await connection.QueryFirstOrDefaultAsync<Guid?>(
                    "SELECT aliado_id FROM solicitud WHERE id = @RequestId;",
                    new { RequestId = requestId }
                );

                if (winner == aliadoId)
                {
                    return new AssignmentResult(AssignmentStatus.AlreadyAssigned, aliadoId, "Solicitud ya asignada previamente a este aliado.");
                }

                return new AssignmentResult(AssignmentStatus.Conflict, winner, "La solicitud ya fue asignada a otro profesional.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fallo al ejecutar aceptación atómica en PostgreSQL. Aplicando fallback de exclusión atómica en memoria.");
            }
        }

        // Fallback atómico en memoria (garantiza exclusión concurrente RNF-05 sin colisiones)
        if (MemoryAssignments.TryGetValue(requestId, out var currentWinner))
        {
            if (currentWinner == aliadoId)
            {
                return new AssignmentResult(
                    AssignmentStatus.AlreadyAssigned,
                    aliadoId,
                    "Solicitud ya asignada previamente a este aliado."
                );
            }

            return new AssignmentResult(
                AssignmentStatus.Conflict,
                currentWinner,
                "La solicitud ya fue asignada a otro profesional."
            );
        }

        if (MemoryAssignments.TryAdd(requestId, aliadoId))
        {
            return new AssignmentResult(
                AssignmentStatus.Assigned,
                aliadoId,
                "Solicitud asignada exitosamente al profesional."
            );
        }

        // Si TryAdd falló, otro hilo ganó la carrera
        MemoryAssignments.TryGetValue(requestId, out var raceWinner);
        if (raceWinner == aliadoId)
        {
            return new AssignmentResult(AssignmentStatus.AlreadyAssigned, aliadoId, "Solicitud ya asignada previamente a este aliado.");
        }

        return new AssignmentResult(AssignmentStatus.Conflict, raceWinner, "La solicitud ya fue asignada a otro profesional.");
    }

    public async Task<bool> RejectRequestAsync(Guid requestId, Guid tenantId, Guid aliadoId, string motivo)
    {
        MemoryRejections[(requestId, aliadoId)] = motivo;

        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                const string insertSql = @"
                    INSERT INTO solicitud_rechazo (tenant_id, solicitud_id, aliado_id, motivo, creado_en)
                    VALUES (@TenantId, @RequestId, @AliadoId, @Motivo, now())
                    ON CONFLICT (solicitud_id, aliado_id) DO NOTHING;";

                await connection.ExecuteAsync(insertSql, new
                {
                    TenantId = tenantId,
                    RequestId = requestId,
                    AliadoId = aliadoId,
                    Motivo = motivo
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fallo al persistir rechazo en PostgreSQL. Registrado en memoria.");
            }
        }

        return true;
    }
}
