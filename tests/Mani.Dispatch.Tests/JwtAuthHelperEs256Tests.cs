using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ManiDispatch.Infrastructure.Auth;
using ManiDispatch.Tests.Helpers;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// SCRUM-1362 (BUG-SEC-04): Supabase Auth firma los tokens de acceso con ES256 y Dispatch solo
/// aceptaba HS256. El JWKS lo sirve un HttpMessageHandler falso en la misma ruta que Supabase.
/// </summary>
[Collection("JwtAuthEnvironment")]
public class JwtAuthHelperEs256Tests
{
    private const string SupabaseUrl = "https://proyecto-qa.supabase.co";
    private const string Issuer = SupabaseUrl + "/auth/v1";
    private const string Kid = "kid-qa-1";

    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _intruderKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Guid _tenantId = Guid.NewGuid();

    private sealed class JwksHandler : HttpMessageHandler
    {
        private readonly string? _body;
        public int Requests { get; private set; }
        public string? LastUri { get; private set; }

        public JwksHandler(string? body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastUri = request.RequestUri?.ToString();
            var response = _body is null
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
            return Task.FromResult(response);
        }
    }

    private string JwksJson()
    {
        var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(_signingKey));
        var key = new Dictionary<string, string?>
        {
            ["kty"] = jwk.Kty, ["crv"] = jwk.Crv, ["x"] = jwk.X, ["y"] = jwk.Y,
            ["kid"] = Kid, ["alg"] = "ES256", ["use"] = "sig",
        };
        return JsonSerializer.Serialize(new { keys = new[] { key } });
    }

    private string Es256Token(
        ECDsa? key = null,
        string? kid = Kid,
        string issuer = Issuer,
        string audience = "authenticated",
        DateTime? expires = null,
        bool withTenant = true)
    {
        var appMetadata = withTenant
            ? JsonSerializer.Serialize(new { tenant_id = _tenantId.ToString(), user_role = "aliado" })
            : JsonSerializer.Serialize(new { user_role = "aliado" });
        var securityKey = new ECDsaSecurityKey(key ?? _signingKey) { KeyId = kid };
        var exp = expires ?? DateTime.UtcNow.AddHours(1);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim("sub", "usr-1"),
                new Claim("role", "authenticated"),
                new Claim("app_metadata", appMetadata, JsonClaimValueTypes.Json),
            }),
            Issuer = issuer,
            Audience = audience,
            NotBefore = exp.AddHours(-2),
            Expires = exp,
            SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.EcdsaSha256),
        };
        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    private (JwksKeySource Source, JwksHandler Handler) Jwks(string? body = "")
    {
        var handler = new JwksHandler(body == "" ? JwksJson() : body);
        return (new JwksKeySource(SupabaseUrl, handler), handler);
    }

    [Fact]
    public void Es256Valido_AutenticaConTenantRolYUsuario_YCacheaElJwks()
    {
        var (source, handler) = Jwks();
        var token = Es256Token();

        var first = JwtAuthHelper.Authenticate($"Bearer {token}", jwks: source);
        var second = JwtAuthHelper.Authenticate($"Bearer {token}", jwks: source);

        Assert.True(first.IsAuthenticated, first.ErrorMessage);
        Assert.Equal(_tenantId, first.TenantId);
        Assert.Equal("usr-1", first.UserId);
        Assert.Equal("aliado", first.Role);
        Assert.True(second.IsAuthenticated);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(Issuer + "/.well-known/jwks.json", handler.LastUri);
    }

    [Fact]
    public void Es256FirmadoConOtraClaveBajoElMismoKid_SeRechaza()
    {
        var (source, _) = Jwks();
        var result = JwtAuthHelper.Authenticate($"Bearer {Es256Token(key: _intruderKey)}", jwks: source);

        Assert.False(result.IsAuthenticated);
        Assert.Null(result.TenantId);
    }

    [Fact]
    public void Es256ConFirmaAlterada_SeRechaza()
    {
        var (source, _) = Jwks();
        var parts = Es256Token().Split('.');
        var flipped = parts[2][10] == 'A' ? 'B' : 'A';
        var tampered = $"{parts[0]}.{parts[1]}.{parts[2][..10]}{flipped}{parts[2][11..]}";

        Assert.False(JwtAuthHelper.Authenticate($"Bearer {tampered}", jwks: source).IsAuthenticated);
    }

    [Fact]
    public void Es256ConKidDesconocidoOSinKid_SeRechaza()
    {
        var (source, _) = Jwks();

        Assert.False(JwtAuthHelper.Authenticate($"Bearer {Es256Token(kid: "kid-ajeno")}", jwks: source).IsAuthenticated);
        Assert.False(JwtAuthHelper.Authenticate($"Bearer {Es256Token(kid: null)}", jwks: source).IsAuthenticated);
    }

    [Fact]
    public void Es256DeOtroProyectoOConOtraAudiencia_SeRechaza()
    {
        var (source, _) = Jwks();

        Assert.False(JwtAuthHelper.Authenticate($"Bearer {Es256Token(issuer: "https://otro.supabase.co/auth/v1")}", jwks: source).IsAuthenticated);
        Assert.False(JwtAuthHelper.Authenticate($"Bearer {Es256Token(audience: "anon")}", jwks: source).IsAuthenticated);
    }

    [Fact]
    public void Es256Expirado_SeRechaza()
    {
        var (source, _) = Jwks();
        var result = JwtAuthHelper.Authenticate($"Bearer {Es256Token(expires: DateTime.UtcNow.AddMinutes(-10))}", jwks: source);

        Assert.False(result.IsAuthenticated);
        Assert.Contains("expirado", result.ErrorMessage);
    }

    [Fact]
    public void Es256SinTenantId_SeRechaza()
    {
        var (source, _) = Jwks();
        var result = JwtAuthHelper.Authenticate($"Bearer {Es256Token(withTenant: false)}", jwks: source);

        Assert.False(result.IsAuthenticated);
        Assert.Contains("tenant_id", result.ErrorMessage);
    }

    [Fact]
    public void JwksInalcanzable_FallaCerrado()
    {
        var (source, _) = Jwks(body: null);

        Assert.False(JwtAuthHelper.Authenticate($"Bearer {Es256Token()}", jwks: source).IsAuthenticated);
    }

    [Fact]
    public void SinSupabaseUrl_Es256SeRechaza_YHs256SigueFuncionando()
    {
        var previous = Environment.GetEnvironmentVariable("SUPABASE_URL");
        Environment.SetEnvironmentVariable("SUPABASE_URL", null);
        try
        {
            var es256 = JwtAuthHelper.Authenticate($"Bearer {Es256Token()}", TestTokenHelper.DefaultTestSecret);
            Assert.False(es256.IsAuthenticated);
            Assert.Contains("SUPABASE_URL", es256.ErrorMessage);

            var hs256 = TestTokenHelper.CreateTestToken(_tenantId, "usr-1", "aliado");
            Assert.True(JwtAuthHelper.Authenticate($"Bearer {hs256}", TestTokenHelper.DefaultTestSecret).IsAuthenticated);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUPABASE_URL", previous);
        }
    }

    [Fact]
    public void Hs512_SeRechazaAunqueLaFirmaSeaCorrecta()
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim("sub", "usr-1"),
                new Claim("app_metadata", JsonSerializer.Serialize(new { tenant_id = _tenantId.ToString(), user_role = "aliado" }), JsonClaimValueTypes.Json),
            }),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestTokenHelper.DefaultTestSecret + TestTokenHelper.DefaultTestSecret)),
                SecurityAlgorithms.HmacSha512),
        };
        var handler = new JwtSecurityTokenHandler();
        var hs512 = handler.WriteToken(handler.CreateToken(descriptor));

        var result = JwtAuthHelper.Authenticate($"Bearer {hs512}", TestTokenHelper.DefaultTestSecret + TestTokenHelper.DefaultTestSecret);

        Assert.False(result.IsAuthenticated);
    }

    [Fact]
    public void AlgConfusion_Hs256FirmadoConLaClavePublicaComoSecreto_SeRechaza()
    {
        var (source, _) = Jwks();
        var publicPem = _signingKey.ExportSubjectPublicKeyInfoPem();
        var b64 = (object o) => Base64UrlEncoder.Encode(JsonSerializer.Serialize(o));
        var unsigned = $"{b64(new { alg = "HS256", typ = "JWT", kid = Kid })}." +
            $"{b64(new { sub = "usr-1", iss = Issuer, aud = "authenticated", exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), app_metadata = new { tenant_id = _tenantId.ToString(), user_role = "admin_tenant" } })}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(publicPem));
        var signature = Base64UrlEncoder.Encode(hmac.ComputeHash(Encoding.UTF8.GetBytes(unsigned)));

        // El secreto real de HS256 es otro: la clave pública nunca sirve como secreto.
        var result = JwtAuthHelper.Authenticate($"Bearer {unsigned}.{signature}", TestTokenHelper.DefaultTestSecret, source);

        Assert.False(result.IsAuthenticated);
    }
}
