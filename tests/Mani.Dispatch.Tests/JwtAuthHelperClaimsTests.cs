using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using ManiDispatch.Infrastructure.Auth;
using ManiDispatch.Tests.Helpers;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// TEC-07 (SCRUM-1454): Dispatch aplica la misma regla de claims que validate-token de Core.
/// tenant_id y user_role salen solo de app_metadata (objeto JSON), sub es obligatorio y no hay
/// respaldo a claims de la raíz del token. Todos los tokens están bien firmados: lo que se prueba
/// es la regla de claims, no la firma.
/// </summary>
[Collection("JwtAuthEnvironment")]
public class JwtAuthHelperClaimsTests
{
    private static readonly string Tenant = Guid.NewGuid().ToString();
    private const string User = "20000000-0000-4000-8000-000000000021";

    private static string Token(Dictionary<string, object> claims)
    {
        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Claims = claims,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestTokenHelper.DefaultTestSecret)),
                SecurityAlgorithms.HmacSha256),
        };
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    private static AuthResult Authenticate(Dictionary<string, object> claims) =>
        JwtAuthHelper.Authenticate($"Bearer {Token(claims)}", TestTokenHelper.DefaultTestSecret);

    private static Dictionary<string, object> AppMetadata(object appMetadata, string? sub = User)
    {
        var claims = new Dictionary<string, object> { ["role"] = "authenticated", ["app_metadata"] = appMetadata };
        if (sub is not null)
        {
            claims["sub"] = sub;
        }
        return claims;
    }

    [Fact]
    public void ControlPositivo_ClaimsEnAppMetadata_Autentica()
    {
        var result = Authenticate(AppMetadata(new Dictionary<string, object> { ["tenant_id"] = Tenant, ["user_role"] = "aliado" }));

        Assert.True(result.IsAuthenticated, result.ErrorMessage);
        Assert.Equal(Guid.Parse(Tenant), result.TenantId);
        Assert.Equal(User, result.UserId);
        Assert.Equal("aliado", result.Role);
    }

    [Fact]
    public void TenantYRolEnLaRaiz_SinAppMetadata_SeRechaza()
    {
        var result = Authenticate(new Dictionary<string, object>
        {
            ["sub"] = User,
            ["tenant_id"] = Tenant,
            ["tenantId"] = Tenant,
            ["user_role"] = "admin_tenant",
        });

        Assert.False(result.IsAuthenticated);
        Assert.Null(result.TenantId);
        Assert.Contains("app_metadata.tenant_id", result.ErrorMessage);
    }

    [Fact]
    public void TenantEnLaRaiz_NoCompletaUnAppMetadataSinTenant()
    {
        var claims = AppMetadata(new Dictionary<string, object> { ["user_role"] = "aliado" });
        claims["tenant_id"] = Tenant;

        var result = Authenticate(claims);

        Assert.False(result.IsAuthenticated);
        Assert.Null(result.TenantId);
    }

    [Fact]
    public void RolEnLaRaiz_NoCompletaUnAppMetadataSinUserRole()
    {
        var claims = AppMetadata(new Dictionary<string, object> { ["tenant_id"] = Tenant });
        claims["user_role"] = "admin_tenant";

        var result = Authenticate(claims);

        Assert.False(result.IsAuthenticated);
        Assert.Null(result.Role);
        Assert.Contains("app_metadata.user_role", result.ErrorMessage);
    }

    [Fact]
    public void AppMetadataRole_NoSustituyeAUserRole()
    {
        var result = Authenticate(AppMetadata(new Dictionary<string, object> { ["tenant_id"] = Tenant, ["role"] = "admin_tenant" }));

        Assert.False(result.IsAuthenticated);
        Assert.Null(result.Role);
    }

    [Fact]
    public void SinSub_SeRechaza()
    {
        var result = Authenticate(AppMetadata(
            new Dictionary<string, object> { ["tenant_id"] = Tenant, ["user_role"] = "aliado" },
            sub: null));

        Assert.False(result.IsAuthenticated);
        Assert.Contains("sub", result.ErrorMessage);
    }

    [Fact]
    public void AppMetadataComoTextoConJsonDentro_SeRechaza()
    {
        var comoTexto = JsonSerializer.Serialize(new { tenant_id = Tenant, user_role = "admin_tenant" });

        var result = Authenticate(AppMetadata(comoTexto));

        Assert.False(result.IsAuthenticated);
        Assert.Null(result.TenantId);
    }

    [Fact]
    public void TenantIdQueNoEsCadena_SeRechaza()
    {
        foreach (var tenant in new object[] { 123, new Dictionary<string, object> { ["id"] = Tenant }, "", "   " })
        {
            var result = Authenticate(AppMetadata(new Dictionary<string, object> { ["tenant_id"] = tenant, ["user_role"] = "aliado" }));

            Assert.False(result.IsAuthenticated, $"tenant_id={JsonSerializer.Serialize(tenant)}");
        }
    }

    [Fact]
    public void UserRoleQueNoEsCadena_SeRechaza()
    {
        var result = Authenticate(AppMetadata(new Dictionary<string, object> { ["tenant_id"] = Tenant, ["user_role"] = 1 }));

        Assert.False(result.IsAuthenticated);
    }

    [Fact]
    public void UnRechazoNoDevuelveIdentidadParcial()
    {
        var result = Authenticate(AppMetadata(new Dictionary<string, object> { ["tenant_id"] = "no-es-uuid", ["user_role"] = "aliado" }));

        Assert.False(result.IsAuthenticated);
        Assert.Null(result.TenantId);
        Assert.Null(result.UserId);
        Assert.Null(result.Role);
    }
}
