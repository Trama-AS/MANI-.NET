using System;
using Microsoft.Extensions.Configuration;

namespace ManiDispatch.Infrastructure.Core;

/// <summary>
/// URL base de Core para los clientes HTTP de Dispatch (TEC-06, SCRUM-1453).
/// <para>
/// Se lee de <c>CORE_SERVICE_URL</c> y, si no está, de <c>ServiceUrls:CoreService</c> (appsettings o la variable
/// <c>ServiceUrls__CoreService</c>). No hay valor por omisión: una URL inventada ocultaría que el despliegue no la
/// declaró. Dispatch no arranca sin ella (<see cref="Exigir"/>, llamado desde Program.cs).
/// </para>
/// </summary>
public static class UrlServicioCore
{
    public const string Variable = "CORE_SERVICE_URL";
    public const string Respaldo = "ServiceUrls:CoreService";

    /// <summary>URL configurada, sin espacios ni barra final, o <c>null</c> si no hay ninguna.</summary>
    public static string? Leer(IConfiguration? configuration)
    {
        if (configuration == null) return null;
        var valor = configuration[Variable];
        if (string.IsNullOrWhiteSpace(valor)) valor = configuration[Respaldo];
        return string.IsNullOrWhiteSpace(valor) ? null : valor.Trim().TrimEnd('/');
    }

    /// <summary>
    /// Devuelve la URL de Core o lanza <see cref="InvalidOperationException"/> con un mensaje que dice qué falta y
    /// dónde declararlo. También rechaza un valor que no sea una URL http(s) absoluta.
    /// </summary>
    public static string Exigir(IConfiguration configuration)
    {
        var url = Leer(configuration);
        if (url == null)
        {
            throw new InvalidOperationException(
                $"Falta la URL de Core: declare {Variable} (o {Respaldo}) en el entorno. " +
                "Dispatch la necesita para elegibilidad, ofertas y asignaciones; ver .env.example y docker-compose.yml.");
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{Variable} debe ser una URL http(s) absoluta; se recibió \"{url}\".");
        }
        return url;
    }
}
