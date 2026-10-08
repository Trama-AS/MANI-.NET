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
        // Token sin app_metadata.tenant_id
        var header = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";
        var payload = "eyJzdWIiOiJ1c2VyLTEifQ"; // {"sub":"user-1"}
        var token = $"{header}.{payload}.mock-sig";

        var result = JwtAuthHelper.Authenticate($"Bearer {token}");

        Assert.False(result.IsAuthenticated);
        Assert.Contains("tenant_id", result.ErrorMessage);
    }
}
