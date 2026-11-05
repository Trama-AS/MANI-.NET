using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ManiDispatch.Infrastructure.Repositories;

/// <summary>Rol con el que quedó la conexión y si se salta RLS (SUPERUSER o BYPASSRLS).</summary>
public sealed record RolConexion(string Rol, bool SaltaRls);

/// <summary>
/// Comprueba al arrancar con qué rol se conecta Dispatch a PostgreSQL (SCRUM-1468, TEC-21).
/// <para>
/// El aislamiento entre tenants (RNF-01) solo lo garantiza RLS si el rol no se lo salta: con <c>postgres</c> las
/// políticas no se evalúan aunque el repositorio fije <c>app.set_context</c>. Fuera de Development, Dispatch no
/// arranca si el rol es superusuario o tiene BYPASSRLS, ni si no puede comprobarlo; en Development solo avisa.
/// </para>
/// </summary>
public sealed class VerificacionRolServicio : IHostedService
{
    private const string Consulta =
        "SELECT current_user::text AS rol, (rolsuper OR rolbypassrls) AS salta_rls FROM pg_roles WHERE rolname = current_user";

    private readonly string? _connectionString;
    private readonly bool _exigir;
    private readonly ILogger<VerificacionRolServicio> _logger;

    public VerificacionRolServicio(IConfiguration configuration, IHostEnvironment environment, ILogger<VerificacionRolServicio> logger)
    {
        _connectionString = CadenaConexion.Resolver(configuration);
        _exigir = !environment.IsDevelopment();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static async Task<RolConexion> InspeccionarAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(Consulta, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new RolConexion("desconocido", true);
        }
        return new RolConexion(reader.GetString(0), reader.GetBoolean(1));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            // Sin base, el repositorio responde 503 (ADR-0016); no hay rol que verificar.
            return;
        }

        RolConexion rol;
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            rol = await InspeccionarAsync(connection, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            const string sinVerificar = "No se pudo comprobar el rol de PostgreSQL de Dispatch al arrancar (SCRUM-1468).";
            if (_exigir) throw new InvalidOperationException(sinVerificar, ex);
            _logger.LogWarning(ex, sinVerificar);
            return;
        }

        if (!rol.SaltaRls)
        {
            _logger.LogInformation("PostgreSQL: Dispatch conecta como {Rol}, sin BYPASSRLS (RLS activo).", rol.Rol);
            return;
        }

        var mensaje =
            $"La cadena de conexión de Dispatch usa el rol \"{rol.Rol}\", que tiene SUPERUSER o BYPASSRLS: las políticas RLS " +
            "no se evaluarían. Dispatch debe conectarse como mani_dispatch_svc (SCRUM-1468, MANI-Node db/ops/credenciales_roles_servicio.sql).";
        if (_exigir) throw new InvalidOperationException(mensaje);
        _logger.LogWarning("{Mensaje}", mensaje);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
