using System;
using System.IdentityModel.Tokens.Jwt;
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

            TokenHandler.ValidateToken(token, validationParameters, out var validatedToken);
            var jwtToken = (JwtSecurityToken)validatedToken;

            // Misma regla de claims que validate-token de Core (ADR-0031 §2, SCRUM-1333, TEC-07):
            // tenant y rol salen EXCLUSIVAMENTE de app_metadata (objeto JSON) y sub es obligatorio.
            // Sin respaldo a la raíz del token: en Supabase la raíz trae role = "authenticated", y un
            // tenant_id o user_role en la raíz no los emite el Custom Access Token Hook.
            var userId = jwtToken.Subject;
            var (tenantIdStr, role) = ReadAppMetadata(jwtToken);

            if (string.IsNullOrWhiteSpace(tenantIdStr))
            {
                return new AuthResult(false, null, null, null, "El token no contiene el claim obligatorio app_metadata.tenant_id.");
            }

            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(role))
            {
                return new AuthResult(false, null, null, null, "El token no contiene los claims obligatorios sub y app_metadata.user_role.");
            }

            if (!Guid.TryParse(tenantIdStr, out var tenantGuid))
            {
                return new AuthResult(false, null, null, null, $"El tenant_id '{tenantIdStr}' no es un UUID válido.");
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

    /// <summary>
    /// Lee tenant_id y user_role de app_metadata. Solo se acepta app_metadata como objeto JSON y cada
    /// valor como cadena; cualquier otra forma (texto con JSON dentro, número, objeto) cuenta como ausente.
    /// </summary>
    private static (string? TenantId, string? Role) ReadAppMetadata(JwtSecurityToken jwtToken)
    {
        var claim = jwtToken.Claims.FirstOrDefault(c => c.Type == "app_metadata");
        if (claim is null || claim.ValueType != JsonClaimValueTypes.Json)
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(claim.Value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            return (StringProperty(doc.RootElement, "tenant_id"), StringProperty(doc.RootElement, "user_role"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? StringProperty(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
