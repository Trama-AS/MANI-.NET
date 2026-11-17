using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace ManiDispatch.Infrastructure.Core;

/// <summary>
/// Reenvía a Core el Authorization (JWT del usuario) de la petición en curso, para que Core autentique
/// con su propio middleware y tome el tenant y el rol del claim verificado (ADR-0030).
/// </summary>
public class ForwardAuthorizationHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ForwardAuthorizationHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var entrante = _httpContextAccessor.HttpContext?.Request;
        var header = entrante?.Headers.Authorization.ToString();
        if (request.Headers.Authorization == null
            && !string.IsNullOrWhiteSpace(header)
            && AuthenticationHeaderValue.TryParse(header, out var authorization))
        {
            request.Headers.Authorization = authorization;
        }

        // SCRUM-901 y SCRUM-1658: tenant elegido, usuario con el que actúa el administrador de plataforma y
        // X-Correlation-ID de la petición en curso para que el mismo ID viaje por todos los servicios.
        if (entrante != null)
        {
            foreach (var nombre in new[] { "X-Mani-Tenant", "X-Mani-Actuar-Como", "X-Correlation-ID" })
            {
                var valor = entrante.Headers[nombre].ToString();
                if (!string.IsNullOrWhiteSpace(valor) && !request.Headers.Contains(nombre))
                {
                    request.Headers.TryAddWithoutValidation(nombre, valor);
                }
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
