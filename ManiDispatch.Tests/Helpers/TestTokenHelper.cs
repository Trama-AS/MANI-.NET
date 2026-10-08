using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace ManiDispatch.Tests.Helpers;

public static class TestTokenHelper
{
    public const string DefaultTestSecret = "super-secret-jwt-key-for-testing-purposes-at-least-32-chars!";

    public static string CreateTestToken(
        Guid tenantId, 
        string userId = "test-user-id", 
        string role = "cliente", 
        string secret = DefaultTestSecret)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(secret);

        var claims = new List<Claim>
        {
            new Claim("sub", userId),
            new Claim("role", "authenticated"), // Simulates Supabase default root role
            new Claim("app_metadata", JsonSerializer.Serialize(new
            {
                tenant_id = tenantId.ToString(),
                user_role = role
            }), JsonClaimValueTypes.Json)
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddHours(2),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }
}
