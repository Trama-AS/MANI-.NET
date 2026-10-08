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
    private static readonly JwtSecurityTokenHandler TokenHandler = new();

    public static AuthResult Authenticate(string? authHeader, string? explicitJwtSecret = null)
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
            var jwtSecret = explicitJwtSecret ?? Environment.GetEnvironmentVariable("SUPABASE_JWT_SECRET");

            ClaimsPrincipal principal;
            JwtSecurityToken jwtToken;

            if (!string.IsNullOrEmpty(jwtSecret))
            {
                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(5)
                };

                principal = TokenHandler.ValidateToken(token, validationParameters, out var validatedToken);
                jwtToken = (JwtSecurityToken)validatedToken;
            }
            else
            {
                // Fallback para entornos de desarrollo/test donde no se configure SUPABASE_JWT_SECRET
                if (!TokenHandler.CanReadToken(token))
                {
                    return new AuthResult(false, null, null, null, "Formato de token JWT inválido.");
                }

                jwtToken = TokenHandler.ReadJwtToken(token);
                if (jwtToken.ValidTo != DateTime.MinValue && jwtToken.ValidTo < DateTime.UtcNow)
                {
                    return new AuthResult(false, null, null, null, "El token ha expirado.");
                }

                var identity = new ClaimsIdentity(jwtToken.Claims, "jwt");
                principal = new ClaimsPrincipal(identity);
            }

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
        catch (Exception ex)
        {
            return new AuthResult(false, null, null, null, $"Error al validar el token de autenticación: {ex.Message}");
        }
    }
}
