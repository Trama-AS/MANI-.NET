using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace ManiDispatch.Infrastructure.Auth;

public record AuthResult(bool IsAuthenticated, Guid? TenantId, string? UserId, string? Role, string? ErrorMessage);

public static class JwtAuthHelper
{
    // Audiencia que Supabase Auth pone en los tokens de acceso de usuarios con sesión.
    private const string SupabaseAudience = "authenticated";

    private static readonly JwtSecurityTokenHandler TokenHandler = new();

    /// <summary>
    /// Verifica el JWT con la misma política que validate-token de Core (ADR-0031 §1, SCRUM-1362):
    /// ES256 con la clave pública del JWKS de SUPABASE_URL (elegida por kid, con aud e iss del
    /// proyecto) o HS256 con SUPABASE_JWT_SECRET. Cada algoritmo usa solo su clave, así que un
    /// token no puede elegir con qué se verifica; cualquier otro algoritmo se rechaza.
    /// </summary>
    /// <param name="jwks">Fuente de claves para pruebas; por omisión, la de SUPABASE_URL.</param>
    public static AuthResult Authenticate(string? authHeader, string? explicitJwtSecret = null, JwksKeySource? jwks = null)
    {
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            return new AuthResult(false, null, null, null, "Encabezado Authorization requerido conforme a ADR-0018.");
        }

        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return new AuthResult(false, null, null, null, "Esquema de autenticación inválido. Debe ser 'Bearer <token>'.");
        }

        var token = authHeader.Substring("Bearer ".Length).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new AuthResult(false, null, null, null, "Token de autenticación vacío.");
        }

        try
        {
            var header = TokenHandler.ReadJwtToken(token).Header;
            TokenValidationParameters validationParameters;

            if (header.Alg == SecurityAlgorithms.EcdsaSha256)
            {
                var keySource = jwks ?? JwksKeySource.ForSupabaseUrl(Environment.GetEnvironmentVariable("SUPABASE_URL"));
                if (keySource is null)
                {
                    return new AuthResult(false, null, null, null, "Configuración de autenticación incompleta: SUPABASE_URL no configurado para verificar ES256.");
                }

                var keys = keySource.GetKeys(header.Kid);
                if (keys.Count == 0)
                {
                    return new AuthResult(false, null, null, null, "Firma del token JWT inválida: no se encontró la clave pública del token.");
                }

                validationParameters = new TokenValidationParameters
                {
                    ValidAlgorithms = new[] { SecurityAlgorithms.EcdsaSha256 },
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeys = keys,
                    ValidateIssuer = true,
                    ValidIssuer = keySource.Issuer,
                    ValidateAudience = true,
                    ValidAudience = SupabaseAudience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(5)
                };
            }
            else if (header.Alg == SecurityAlgorithms.HmacSha256)
            {
                var jwtSecret = explicitJwtSecret ?? Environment.GetEnvironmentVariable("SUPABASE_JWT_SECRET");

                if (string.IsNullOrWhiteSpace(jwtSecret))
                {
                    return new AuthResult(false, null, null, null, "Configuración de autenticación incompleta: SUPABASE_JWT_SECRET no configurado.");
                }

                validationParameters = new TokenValidationParameters
                {
                    ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(5)
                };
            }
            else
            {
                return new AuthResult(false, null, null, null, "Error de autenticación: algoritmo de firma no permitido.");
            }

            var principal = TokenHandler.ValidateToken(token, validationParameters, out var validatedToken);
            var jwtToken = (JwtSecurityToken)validatedToken;

            string? userId = jwtToken.Subject ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            string? tenantIdStr = null;
            string? role = null;

            // En Supabase Auth, los claims de negocio están estrictamente en app_metadata (ADR-0018).
            // La raíz contiene role: "authenticated", el cual DEBE ignorarse.
            var appMetadataClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "app_metadata");
            if (appMetadataClaim != null && !string.IsNullOrWhiteSpace(appMetadataClaim.Value))
            {
                try
                {
                    using var doc = JsonDocument.Parse(appMetadataClaim.Value);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("tenant_id", out var tenantProp))
                    {
                        tenantIdStr = tenantProp.GetString();
                    }
                    if (root.TryGetProperty("user_role", out var userRoleProp))
                    {
                        role = userRoleProp.GetString();
                    }
                    else if (root.TryGetProperty("role", out var roleProp))
                    {
                        role = roleProp.GetString();
                    }
                }
                catch
                {
                    // Ignora fallo de deserialización de json en claim
                }
            }

            // Fallback para claims planos emitidos por otros middlewares
            if (string.IsNullOrWhiteSpace(tenantIdStr))
            {
                tenantIdStr = jwtToken.Claims.FirstOrDefault(c => c.Type == "tenant_id" || c.Type == "tenantId")?.Value;
            }

            if (string.IsNullOrWhiteSpace(role))
            {
                role = jwtToken.Claims.FirstOrDefault(c => c.Type == "user_role")?.Value;
            }

            if (string.IsNullOrWhiteSpace(tenantIdStr))
            {
                return new AuthResult(false, null, userId, role, "El token no contiene el claim obligatorio tenant_id.");
            }

            if (!Guid.TryParse(tenantIdStr, out var tenantGuid))
            {
                return new AuthResult(false, null, userId, role, $"El tenant_id '{tenantIdStr}' no es un UUID válido.");
            }

            return new AuthResult(true, tenantGuid, userId, role, null);
        }
        catch (SecurityTokenExpiredException)
        {
            return new AuthResult(false, null, null, null, "El token ha expirado.");
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            return new AuthResult(false, null, null, null, "Firma del token JWT inválida.");
        }
        catch (Exception)
        {
            // No se expone el detalle de la excepción al cliente: solo se informa que el token no es válido.
            return new AuthResult(false, null, null, null, "Error de autenticación: el token es inválido o tiene un formato incorrecto.");
        }
    }
}
