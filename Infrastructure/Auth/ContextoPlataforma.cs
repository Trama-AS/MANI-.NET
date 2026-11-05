using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ManiDispatch.Infrastructure.Auth;

/// <summary>
/// Cabeceras con las que el administrador de plataforma elige el tenant y el usuario con el que actúa (SCRUM-901).
/// </summary>
public static class CabecerasPlataforma
{
    public const string Tenant = "X-Mani-Tenant";
    public const string ActuarComo = "X-Mani-Actuar-Como";

    public static bool Presentes(HttpRequest request) =>
        !string.IsNullOrWhiteSpace(request.Headers[Tenant].ToString())
        || !string.IsNullOrWhiteSpace(request.Headers[ActuarComo].ToString());
}

/// <summary>
/// Sesión con la que se atiende la petición. Para el administrador de plataforma es la que resolvió Core
/// (tenant elegido, usuario con el que actúa y su rol); para los demás roles, la del JWT tal cual.
/// </summary>
public static class AuthEfectiva
{
    public const string ClaveItems = "mani.auth-efectiva";

    public static AuthResult Obtener(HttpContext context) =>
        context.Items.TryGetValue(ClaveItems, out var valor) && valor is AuthResult efectiva
            ? efectiva
            : JwtAuthHelper.Authenticate(context.Request.Headers.Authorization);
}

/// <summary>Contexto efectivo devuelto por Core (GET /api/v1/plataforma/contexto).</summary>
public record ContextoEfectivoCore(Guid TenantId, string UserId, string Role);

/// <summary>Resultado de consultar a Core: el contexto, o el status y cuerpo con que Core lo negó.</summary>
public record ResultadoContexto(ContextoEfectivoCore? Contexto, int Status, string? Cuerpo);

public interface ICoreContextoClient
{
    Task<ResultadoContexto> ObtenerAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Pide a Core el contexto efectivo reenviando el JWT y las cabeceras de plataforma (ForwardAuthorizationHandler):
/// así Core y Dispatch atienden la petición con el mismo tenant, usuario y rol.
/// </summary>
public class CoreContextoClient : ICoreContextoClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly string? _coreServiceUrl;
    private readonly ILogger<CoreContextoClient> _logger;

    public CoreContextoClient(HttpClient http, IConfiguration configuration, ILogger<CoreContextoClient> logger)
    {
        _http = http;
        _coreServiceUrl = configuration["CORE_SERVICE_URL"]?.TrimEnd('/');
        _logger = logger;
    }

    public async Task<ResultadoContexto> ObtenerAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_coreServiceUrl))
        {
            _logger.LogError("CORE_SERVICE_URL no está configurada; no se puede resolver el contexto de plataforma.");
            return new ResultadoContexto(null, StatusCodes.Status503ServiceUnavailable, null);
        }

        try
        {
            using var response = await _http.GetAsync($"{_coreServiceUrl}/api/v1/plataforma/contexto", cancellationToken);
            var cuerpo = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ResultadoContexto(null, (int)response.StatusCode, cuerpo);
            }

            var contexto = JsonSerializer.Deserialize<ContextoEfectivoCore>(cuerpo, Json);
            return contexto is null || contexto.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(contexto.Role)
                ? new ResultadoContexto(null, StatusCodes.Status502BadGateway, null)
                : new ResultadoContexto(contexto, (int)HttpStatusCode.OK, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Core no respondió al resolver el contexto de plataforma.");
            return new ResultadoContexto(null, StatusCodes.Status503ServiceUnavailable, null);
        }
    }
}

/// <summary>
/// Resuelve la sesión efectiva antes de los controladores (SCRUM-901).
/// - Administrador de plataforma: la pide a Core y la deja en <see cref="AuthEfectiva"/>.
/// - Otro rol con cabeceras de plataforma: 403 (fallo cerrado).
/// - Sin token o con token inválido: sigue y el controlador responde 401 como siempre.
/// </summary>
public class ContextoPlataformaMiddleware
{
    private readonly RequestDelegate _next;

    public ContextoPlataformaMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ICoreContextoClient core)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            await _next(context);
            return;
        }

        var auth = JwtAuthHelper.Authenticate(header);
        if (!auth.IsAuthenticated)
        {
            await _next(context);
            return;
        }

        var correlationId = context.Request.Headers["X-Correlation-ID"].ToString();
        var esPlataforma = string.Equals(auth.Role, "admin_plataforma", StringComparison.OrdinalIgnoreCase);
        if (!esPlataforma)
        {
            if (CabecerasPlataforma.Presentes(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "MANI-PLA-403: solo el administrador de plataforma puede elegir tenant o actuar como otro usuario",
                    code = "MANI-PLA-403",
                    status = StatusCodes.Status403Forbidden,
                    correlationId
                });
                return;
            }
            await _next(context);
            return;
        }

        var resultado = await core.ObtenerAsync(context.RequestAborted);
        if (resultado.Contexto is null)
        {
            context.Response.StatusCode = resultado.Status;
            if (!string.IsNullOrWhiteSpace(resultado.Cuerpo))
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(resultado.Cuerpo);
            }
            else
            {
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "MANI-SRV-503: no se pudo resolver el contexto del administrador de plataforma",
                    code = "MANI-SRV-503",
                    status = resultado.Status,
                    correlationId
                });
            }
            return;
        }

        var c = resultado.Contexto;
        context.Items[AuthEfectiva.ClaveItems] = new AuthResult(true, c.TenantId, c.UserId, c.Role, null);
        await _next(context);
    }
}
