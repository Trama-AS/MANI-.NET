using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ManiDispatch.Infrastructure.Repositories;

/// <summary>
/// Rechazos y consultas de asignación sobre PostgreSQL. Desde la decisión 6A Dispatch no escribe <c>solicitud</c>
/// (la asignación la registra Core). Cada operación abre una transacción y fija el contexto de la sesión con
/// <c>app.set_context</c>, así RLS aplica al rol <c>mani_dispatch_svc</c>, que no se salta las políticas (D-2).
/// </summary>
public class PostgresSolicitudAssignmentRepository : ISolicitudAssignmentRepository
{
    private const string RolAliado = "aliado";

    private readonly string? _connectionString;
    private readonly ILogger<PostgresSolicitudAssignmentRepository> _logger;

    public PostgresSolicitudAssignmentRepository(
        IConfiguration? configuration,
        ILogger<PostgresSolicitudAssignmentRepository> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionString = CadenaConexion.Resolver(configuration);
    }

    public Task<bool> RejectRequestAsync(Guid requestId, Guid tenantId, Guid usuarioId, Guid aliadoId, string motivo, string? detalle)
    {
        const string sql = @"
            INSERT INTO solicitud_rechazo (tenant_id, solicitud_id, aliado_id, motivo, detalle)
            VALUES (@TenantId, @RequestId, @AliadoId, @Motivo, @Detalle)
            ON CONFLICT (solicitud_id, aliado_id) DO NOTHING;";

        return WithTenantContextAsync(tenantId, usuarioId, RolAliado, "registrar el rechazo", async (connection, tx) =>
            await connection.ExecuteAsync(sql, new { TenantId = tenantId, RequestId = requestId, AliadoId = aliadoId, Motivo = motivo, Detalle = detalle }, tx) == 1);
    }

    public Task<bool> HasRejectedAsync(Guid requestId, Guid tenantId, Guid usuarioId, Guid aliadoId)
    {
        const string sql = @"
            SELECT EXISTS (
                SELECT 1 FROM solicitud_rechazo
                WHERE tenant_id = @TenantId AND solicitud_id = @RequestId AND aliado_id = @AliadoId);";

        return WithTenantContextAsync(tenantId, usuarioId, RolAliado, "consultar el rechazo del aliado", (connection, tx) =>
            connection.ExecuteScalarAsync<bool>(sql, new { TenantId = tenantId, RequestId = requestId, AliadoId = aliadoId }, tx));
    }

    public Task<IReadOnlyList<Guid>> GetRejectedRequestIdsAsync(Guid tenantId, Guid usuarioId, Guid aliadoId)
    {
        const string sql = @"
            SELECT solicitud_id
            FROM solicitud_rechazo
            WHERE tenant_id = $1 AND aliado_id = $2;";

        return QueryIdsInTenantContextAsync(tenantId, usuarioId, RolAliado, "consultar los rechazos del aliado", sql, tenantId, aliadoId);
    }

    public Task<IReadOnlyList<Guid>> GetRejectedAllyIdsAsync(Guid requestId, Guid tenantId, Guid usuarioId, string role)
    {
        // La política solicitud_rechazo_lectura_despacho (Core 0019) solo deja leer al cliente o al admin del tenant.
        const string sql = @"
            SELECT aliado_id
            FROM solicitud_rechazo
            WHERE tenant_id = $1 AND solicitud_id = $2;";

        return QueryIdsInTenantContextAsync(tenantId, usuarioId, role, "consultar los rechazos de la solicitud", sql, tenantId, requestId);
    }

    public Task<IReadOnlyList<Guid>> GetAssignedRequestIdsAsync(Guid tenantId, Guid usuarioId, Guid aliadoId)
    {
        // Asignada = Core registró al aliado en aliado_id. Cualquier estado posterior a la asignación cuenta, salvo
        // la cancelación. Core usa los ids como conjunto, así que no se ordenan.
        const string sql = @"
            SELECT id
            FROM solicitud
            WHERE tenant_id = $1
              AND aliado_id = $2
              AND estado <> 'CANCELADA';";

        return QueryIdsInTenantContextAsync(tenantId, usuarioId, RolAliado, "consultar las asignaciones del aliado", sql, tenantId, aliadoId);
    }

    /// <summary>
    /// Lectura de una lista de ids en un solo viaje a la base (SCRUM-1652): <c>app.set_context</c> y la consulta van en
    /// el mismo lote. PostgreSQL ejecuta un lote sin BEGIN explícito como una transacción implícita que termina con el
    /// Sync final, así que el contexto local (set_config con is_local) aplica a la consulta y muere con el lote, igual que
    /// con BEGIN … COMMIT, pero sin tres viajes extra. En QA cada viaje cuesta ~110 ms y retenía la conexión ese tiempo.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> QueryIdsInTenantContextAsync(
        Guid tenantId,
        Guid usuarioId,
        string role,
        string operacion,
        string sql,
        params object[] parametros)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new DispatchDatabaseUnavailableException("La base de datos PostgreSQL no está disponible: cadena de conexión no configurada (ADR-0016).");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var contexto = new NpgsqlBatchCommand("SELECT app.set_context($1, $2, $3)");
            contexto.Parameters.Add(new NpgsqlParameter { Value = tenantId });
            contexto.Parameters.Add(new NpgsqlParameter { Value = usuarioId });
            contexto.Parameters.Add(new NpgsqlParameter { Value = role.Trim().ToLowerInvariant() });
            var consulta = new NpgsqlBatchCommand(sql);
            foreach (var valor in parametros)
            {
                consulta.Parameters.Add(new NpgsqlParameter { Value = valor });
            }

            await using var batch = new NpgsqlBatch(connection) { BatchCommands = { contexto, consulta } };
            await using var reader = await batch.ExecuteReaderAsync();
            // El primer resultado es el de set_context (void); los ids vienen en el segundo.
            await reader.NextResultAsync();
            var ids = new List<Guid>();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetGuid(0));
            }
            return ids;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo en base de datos al {Operacion} (tenant {TenantId}).", operacion, tenantId);
            throw new DispatchDatabaseUnavailableException($"Fallo en base de datos al {operacion}.", ex);
        }
    }

    private async Task<T> WithTenantContextAsync<T>(
        Guid tenantId,
        Guid usuarioId,
        string role,
        string operacion,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new DispatchDatabaseUnavailableException("La base de datos PostgreSQL no está disponible: cadena de conexión no configurada (ADR-0016).");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();

            // El contexto vive solo dentro de esta transacción (set_config local), no se filtra a otra conexión del pool.
            await connection.ExecuteAsync(
                "SELECT app.set_context(@TenantId, @UsuarioId, @Role);",
                new { TenantId = tenantId, UsuarioId = usuarioId, Role = role.Trim().ToLowerInvariant() },
                tx);

            var result = await work(connection, tx);
            await tx.CommitAsync();
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo en base de datos al {Operacion} (tenant {TenantId}).", operacion, tenantId);
            throw new DispatchDatabaseUnavailableException($"Fallo en base de datos al {operacion}.", ex);
        }
    }
}
