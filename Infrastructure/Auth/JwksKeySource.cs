using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Microsoft.IdentityModel.Tokens;

namespace ManiDispatch.Infrastructure.Auth;

/// <summary>
/// Claves públicas del proyecto de Supabase (<c>&lt;SUPABASE_URL&gt;/auth/v1/.well-known/jwks.json</c>)
/// para verificar los tokens ES256 de Supabase Auth (ADR-0031 §1, SCRUM-1362).
/// Cachea el JWKS y solo lo vuelve a descargar al vencer el caché o, como máximo una vez por
/// minuto, cuando llega un <c>kid</c> desconocido (rotación de claves). Si no se puede
/// descargar, no devuelve claves: la verificación falla cerrado.
/// </summary>
public sealed class JwksKeySource
{
    private static readonly ConcurrentDictionary<string, JwksKeySource> BySupabaseUrl = new();

    private readonly HttpClient _http;
    private readonly string _jwksUri;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _minRefreshInterval;
    private readonly object _lock = new();
    private IReadOnlyList<JsonWebKey> _keys = Array.Empty<JsonWebKey>();
    private DateTime _nextRefresh = DateTime.MinValue;
    private DateTime _lastAttempt = DateTime.MinValue;

    public JwksKeySource(
        string supabaseUrl,
        HttpMessageHandler? handler = null,
        TimeSpan? ttl = null,
        TimeSpan? minRefreshInterval = null)
    {
        if (string.IsNullOrWhiteSpace(supabaseUrl))
        {
            throw new ArgumentException("supabaseUrl es obligatorio", nameof(supabaseUrl));
        }

        var baseUrl = supabaseUrl.Trim().TrimEnd('/');
        Issuer = $"{baseUrl}/auth/v1";
        _jwksUri = $"{Issuer}/.well-known/jwks.json";
        _http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(3) };
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
        _minRefreshInterval = minRefreshInterval ?? TimeSpan.FromMinutes(1);
    }

    /// <summary>Issuer que Supabase Auth pone en los tokens de este proyecto.</summary>
    public string Issuer { get; }

    /// <summary>
    /// Fuente compartida para la URL de Supabase del ambiente, o null si no está configurada
    /// (sin SUPABASE_URL no se acepta ningún token ES256).
    /// </summary>
    public static JwksKeySource? ForSupabaseUrl(string? supabaseUrl)
    {
        if (string.IsNullOrWhiteSpace(supabaseUrl))
        {
            return null;
        }

        return BySupabaseUrl.GetOrAdd(supabaseUrl.Trim().TrimEnd('/'), url => new JwksKeySource(url));
    }

    /// <summary>Claves del JWKS cuyo kid coincide con el del token (vacío si no hay ninguna).</summary>
    public IReadOnlyList<SecurityKey> GetKeys(string? kid)
    {
        if (string.IsNullOrWhiteSpace(kid))
        {
            return Array.Empty<SecurityKey>();
        }

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var expired = now >= _nextRefresh;
            var unknownKid = _keys.All(k => k.Kid != kid) && now - _lastAttempt >= _minRefreshInterval;
            if (expired || unknownKid)
            {
                Refresh(now);
            }

            return _keys.Where(k => k.Kid == kid).Cast<SecurityKey>().ToList();
        }
    }

    private void Refresh(DateTime now)
    {
        _lastAttempt = now;
        try
        {
            // Authenticate es síncrono en todos los controladores; la descarga es poco frecuente
            // por el caché, así que se espera aquí con un timeout corto.
            var json = _http.GetStringAsync(_jwksUri).GetAwaiter().GetResult();
            _keys = new JsonWebKeySet(json).Keys.ToList();
            _nextRefresh = now + _ttl;
        }
        catch (Exception)
        {
            // Se conservan las claves anteriores y se reintenta pronto. Si nunca se descargaron,
            // no hay clave y los tokens ES256 se rechazan (falla cerrado).
            _nextRefresh = now + _minRefreshInterval;
        }
    }
}
