using System;
using System.Text;
using System.Text.Json;

namespace ManiDispatch.Infrastructure.Auth;

public record AuthResult(bool IsAuthenticated, Guid? TenantId, string? UserId, string? Role, string? ErrorMessage);

public static class JwtAuthHelper
{
    public static AuthResult Authenticate(string? authHeader)
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
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return new AuthResult(false, null, null, null, "Formato de token JWT inválido.");
        }

        try
        {
            var payloadJson = DecodeBase64Url(parts[1]);
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            string? tenantIdStr = null;
            string? userId = null;
            string? role = null;

            if (root.TryGetProperty("sub", out var subProp))
            {
                userId = subProp.GetString();
            }

            // Supabase Auth coloca los claims en app_metadata (ADR-0018)
            if (root.TryGetProperty("app_metadata", out var appMetadata) && appMetadata.ValueKind == JsonValueKind.Object)
            {
                if (appMetadata.TryGetProperty("tenant_id", out var tenantProp))
                {
                    tenantIdStr = tenantProp.GetString();
                }
                if (appMetadata.TryGetProperty("user_role", out var roleProp))
                {
                    role = roleProp.GetString();
                }
            }

            // Fallback para claims en la raíz si no estuvieran bajo app_metadata
            if (string.IsNullOrWhiteSpace(tenantIdStr) && root.TryGetProperty("tenant_id", out var rootTenantProp))
            {
                tenantIdStr = rootTenantProp.GetString();
            }
            if (string.IsNullOrWhiteSpace(role) && root.TryGetProperty("role", out var rootRoleProp))
            {
                role = rootRoleProp.GetString();
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
        catch (Exception ex)
        {
            return new AuthResult(false, null, null, null, $"Error al procesar el token de autenticación: {ex.Message}");
        }
    }

    public static string CreateTestToken(Guid tenantId, string userId = "test-user-id", string role = "cliente")
    {
        var header = EncodeBase64Url(JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" }));
        var payload = EncodeBase64Url(JsonSerializer.Serialize(new
        {
            sub = userId,
            app_metadata = new
            {
                tenant_id = tenantId.ToString(),
                user_role = role
            },
            exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        }));
        var signature = EncodeBase64Url("mock-signature");

        return $"{header}.{payload}.{signature}";
    }

    private static string DecodeBase64Url(string base64Url)
    {
        string padded = base64Url.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        var bytes = Convert.FromBase64String(padded);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string EncodeBase64Url(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
