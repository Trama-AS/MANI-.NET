using System;
using Microsoft.Extensions.DependencyInjection;

namespace ManiDispatch.Infrastructure.Core;

/// <summary>
/// Registro de los clientes HTTP de Dispatch hacia Core con un único tiempo de espera (SCRUM-1651, T-14).
/// <para>
/// Core espera hasta 10 s una conexión libre de su pool de PostgreSQL (<c>connectionTimeoutMs</c> de
/// <c>PgPoolFactory</c>). Si Dispatch corta antes, cancela peticiones que Core todavía puede atender y el Gateway
/// responde 503 a los ~3 s, que es lo que midió QA bajo carga. Por eso este tiempo debe ser mayor o igual
/// que la espera del pool de Core; si ese valor cambia, cambia también este.
/// </para>
/// </summary>
public static class ClientesDeCore
{
    /// <summary>Espera máxima de Dispatch por una respuesta de Core: no menor que la espera del pool de Core (10 s).</summary>
    public static readonly TimeSpan Espera = TimeSpan.FromSeconds(10);

    /// <summary>Cliente tipado hacia Core: reenvía el JWT de la petición en curso y usa <see cref="Espera"/>.</summary>
    public static IHttpClientBuilder AddClienteDeCore<TCliente, TImplementacion>(this IServiceCollection services)
        where TCliente : class
        where TImplementacion : class, TCliente
        => services
            .AddHttpClient<TCliente, TImplementacion>(client => client.Timeout = Espera)
            .AddHttpMessageHandler<ForwardAuthorizationHandler>();
}
