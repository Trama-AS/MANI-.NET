using Microsoft.Extensions.Configuration;

namespace ManiDispatch.Infrastructure.Repositories;

/// <summary>
/// Cadena de conexión a PostgreSQL de Dispatch, en el mismo orden de precedencia para el repositorio y para la
/// verificación del rol al arrancar (SCRUM-1468): <c>ConnectionStrings:DefaultConnection</c>, <c>SUPABASE_DB_URL</c>
/// y <c>DATABASE_URL</c>. En QA y producción debe conectar como <c>mani_dispatch_svc</c>, sin BYPASSRLS.
/// </summary>
public static class CadenaConexion
{
    public static string? Resolver(IConfiguration? configuration) =>
        configuration == null
            ? null
            : configuration["ConnectionStrings:DefaultConnection"]
              ?? configuration["SUPABASE_DB_URL"]
              ?? configuration["DATABASE_URL"];
}
