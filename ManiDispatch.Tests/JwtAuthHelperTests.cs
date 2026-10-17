using System;
using ManiDispatch.Infrastructure.Auth;
using ManiDispatch.Tests.Helpers;
using Xunit;

namespace ManiDispatch.Tests;

public class JwtAuthHelperTests
{
    [Fact]
    public void Authenticate_ReturnsFalse_WhenHeaderIsNull()
    {
        var result = JwtAuthHelper.Authenticate(null);
        Assert.False(result.IsAuthenticated);
        Assert.Contains("requerido", result.ErrorMessage);
    }

    [Fact]
    public void Authenticate_ReturnsFalse_WhenSchemeIsNotBearer()
    {
        var result = JwtAuthHelper.Authenticate("Basic dXNlcjpwYXNz");
        Assert.False(result.IsAuthenticated);
        Assert.Contains("Bearer", result.ErrorMessage);
    }

    [Fact]
    public void Authenticate_ReturnsTrue_WithCorrectClaims_WhenTokenIsValid()
    {
        var tenantId = Guid.NewGuid();
        var token = TestTokenHelper.CreateTestToken(tenantId, "test-user-123", "aliado");

        var result = JwtAuthHelper.Authenticate($"Bearer {token}", TestTokenHelper.DefaultTestSecret);

        Assert.True(result.IsAuthenticated);
        Assert.Equal(tenantId, result.TenantId);
        Assert.Equal("test-user-123", result.UserId);
        Assert.Equal("aliado", result.Role);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Authenticate_WithCryptographicVerification_FailsWhenSignatureIsInvalid()
    {
        var tenantId = Guid.NewGuid();
        var token = TestTokenHelper.CreateTestToken(tenantId, "test-user-123", "aliado", "correct-secret-at-least-32-chars-long!");

        // Valida con una clave distinta -> Debe fallar la verificación criptográfica
        var result = JwtAuthHelper.Authenticate($"Bearer {token}", "wrong-secret-that-does-not-match-at-least-32-chars!");

        Assert.False(result.IsAuthenticated);
        Assert.Contains("Firma", result.ErrorMessage);
    }

    [Fact]
    public void Authenticate_IgnoresRootRoleAuthenticated_AndExtractsAppMetadataRole()
    {
        var tenantId = Guid.NewGuid();
        // El helper de prueba incluye claim raíz role = "authenticated" y app_metadata.user_role = "admin_tenant"
        var token = TestTokenHelper.CreateTestToken(tenantId, "admin-1", "admin_tenant");

        var result = JwtAuthHelper.Authenticate($"Bearer {token}", TestTokenHelper.DefaultTestSecret);

        Assert.True(result.IsAuthenticated);
        Assert.Equal("admin_tenant", result.Role);
        Assert.NotEqual("authenticated", result.Role);
    }

    [Fact]
    public void Authenticate_ReturnsFalse_WhenTokenHasNoTenantClaim()
    {
        var token = TestTokenHelper.CreateTokenWithoutTenant();
        var result = JwtAuthHelper.Authenticate($"Bearer {token}", TestTokenHelper.DefaultTestSecret);

        Assert.False(result.IsAuthenticated);
        Assert.Contains("tenant_id", result.ErrorMessage);
    }

    [Fact]
    public void Authenticate_ReturnsFalse_WhenNoSecretConfigured_AndEnvironmentIsNotDevelopment()
    {
        var prevSecret = Environment.GetEnvironmentVariable("SUPABASE_JWT_SECRET");
        var prevEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", null);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");

            var token = TestTokenHelper.CreateTestToken(Guid.NewGuid());
            var result = JwtAuthHelper.Authenticate($"Bearer {token}");

            Assert.False(result.IsAuthenticated);
            Assert.Contains("Configuración de autenticación incompleta", result.ErrorMessage);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", prevSecret);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", prevEnv);
        }
    }

    [Fact]
    public void Authenticate_ReturnsFalse_WhenTokenIsExpired()
    {
        var tenantId = Guid.NewGuid();
        var expiredToken = TestTokenHelper.CreateTestToken(
            tenantId,
            secret: TestTokenHelper.DefaultTestSecret,
            expires: DateTime.UtcNow.AddMinutes(-30));

        var result = JwtAuthHelper.Authenticate($"Bearer {expiredToken}", TestTokenHelper.DefaultTestSecret);

        Assert.False(result.IsAuthenticated);
        Assert.Contains("expirado", result.ErrorMessage);
    }

    [Fact]
    public void Authenticate_ReturnsFalse_WhenTokenIsAltered()
    {
        var tenantId = Guid.NewGuid();
        var token = TestTokenHelper.CreateTestToken(tenantId, secret: TestTokenHelper.DefaultTestSecret);
        var parts = token.Split('.');
        // Alter payload slightly
        var alteredToken = $"{parts[0]}.{parts[1]}xyz.{parts[2]}";

        var result = JwtAuthHelper.Authenticate($"Bearer {alteredToken}", TestTokenHelper.DefaultTestSecret);

        Assert.False(result.IsAuthenticated);
    }

    [Fact]
    public void Authenticate_ReturnsFalse_WhenTokenIsEmptyAfterBearer()
    {
        var result = JwtAuthHelper.Authenticate("Bearer   ");
        Assert.False(result.IsAuthenticated);
        Assert.Contains("vacío", result.ErrorMessage);
    }

    [Fact]
    public void Authenticate_ReturnsFalse_WhenTokenIsMalformed()
    {
        var result = JwtAuthHelper.Authenticate("Bearer invalid.token.payload", TestTokenHelper.DefaultTestSecret);
        Assert.False(result.IsAuthenticated);
        Assert.Contains("Error", result.ErrorMessage);
    }

    [Fact]
    public void Authenticate_ReturnsFalse_WhenTenantIdIsNotValidGuid()
    {
        var token = TestTokenHelper.CreateTestToken(Guid.NewGuid());
        // Token where tenant_id is "invalid-guid"
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var key = System.Text.Encoding.UTF8.GetBytes(TestTokenHelper.DefaultTestSecret);
        var claims = new[]
        {
            new System.Security.Claims.Claim("sub", "user-1"),
            new System.Security.Claims.Claim("app_metadata", "{\"tenant_id\":\"not-a-guid\"}", System.IdentityModel.Tokens.Jwt.JsonClaimValueTypes.Json)
        };
        var descriptor = new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Subject = new System.Security.Claims.ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(key),
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256Signature)
        };
        var badToken = handler.WriteToken(handler.CreateToken(descriptor));

        var result = JwtAuthHelper.Authenticate($"Bearer {badToken}", TestTokenHelper.DefaultTestSecret);

        Assert.False(result.IsAuthenticated);
        Assert.Contains("no es un UUID válido", result.ErrorMessage);
    }

    [Fact]
    public void Authenticate_InDevelopment_WithValidToken_ReadsWithoutSecret()
    {
        var prevSecret = Environment.GetEnvironmentVariable("SUPABASE_JWT_SECRET");
        var prevEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", null);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

            var tenantId = Guid.NewGuid();
            var token = TestTokenHelper.CreateTestToken(tenantId);
            var result = JwtAuthHelper.Authenticate($"Bearer {token}");

            Assert.True(result.IsAuthenticated);
            Assert.Equal(tenantId, result.TenantId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", prevSecret);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", prevEnv);
        }
    }
}
