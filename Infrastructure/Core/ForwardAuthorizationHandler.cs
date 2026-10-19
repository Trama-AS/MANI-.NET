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
        var header = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (request.Headers.Authorization == null
            && !string.IsNullOrWhiteSpace(header)
            && AuthenticationHeaderValue.TryParse(header, out var authorization))
        {
            request.Headers.Authorization = authorization;
        }

        return base.SendAsync(request, cancellationToken);
    }
}
