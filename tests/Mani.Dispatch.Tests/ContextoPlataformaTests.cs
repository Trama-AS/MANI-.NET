using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ManiDispatch.Infrastructure.Auth;
using ManiDispatch.Infrastructure.Core;
using ManiDispatch.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// SCRUM-901: el administrador de plataforma opera en el tenant elegido o actuando como un usuario, con el contexto
/// que resuelve Core; ningún otro rol puede usar esas cabeceras.
/// </summary>
[Collection("JwtAuthEnvironment")]
public class ContextoPlataformaTests
{
    private static readonly Guid T1 = Guid.Parse("10000000-0000-4000-8000-000000000011");
    private static readonly Guid T2 = Guid.Parse("10000000-0000-4000-8000-000000000021");
    private const string Plataforma = "30000000-0000-4000-8000-000000000099";
    private const string AliadoT2 = "30000000-0000-4000-8000-000000000023";

    public ContextoPlataformaTests()
    {
        Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", TestTokenHelper.DefaultTestSecret);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
    }

    private sealed class CoreFalso : ICoreContextoClient
    {
        public ResultadoContexto Respuesta { get; set; } =
            new(new ContextoEfectivoCore(T2, AliadoT2, "aliado"), 200, null);
        public int Llamadas { get; private set; }

        public Task<ResultadoContexto> ObtenerAsync(CancellationToken cancellationToken = default)
        {
            Llamadas++;
            return Task.FromResult(Respuesta);
        }
    }

    private static DefaultHttpContext Contexto(string rol, string usuario, Guid tenant, bool conCabeceras)
    {
        var c = new DefaultHttpContext();
        c.Response.Body = new MemoryStream();
        c.Request.Headers.Authorization = $"Bearer {TestTokenHelper.CreateTestToken(tenant, usuario, rol)}";
        if (conCabeceras)
        {
            c.Request.Headers[CabecerasPlataforma.Tenant] = T2.ToString();
            c.Request.Headers[CabecerasPlataforma.ActuarComo] = AliadoT2;
        }
        return c;
    }

    private static async Task<(bool Siguio, AuthResult? Efectiva)> Correr(HttpContext c, ICoreContextoClient core)
    {
        var siguio = false;
        AuthResult? efectiva = null;
        var mw = new ContextoPlataformaMiddleware(ctx =>
        {
            siguio = true;
            efectiva = AuthEfectiva.Obtener(ctx);
            return Task.CompletedTask;
        });
        await mw.InvokeAsync(c, core);
        return (siguio, efectiva);
    }

    [Fact]
    public async Task ElAdminDePlataformaActuaConElContextoQueResuelveCore()
    {
        var core = new CoreFalso();
        var c = Contexto("admin_plataforma", Plataforma, T1, conCabeceras: true);

        var (siguio, efectiva) = await Correr(c, core);

        Assert.True(siguio);
        Assert.Equal(1, core.Llamadas);
        Assert.Equal(T2, efectiva!.TenantId);
        Assert.Equal(AliadoT2, efectiva.UserId);
        Assert.Equal("aliado", efectiva.Role);
    }

    [Fact]
    public async Task SinCabecerasElAdminDePlataformaAdministraSuTenantSegunCore()
    {
        var core = new CoreFalso { Respuesta = new(new ContextoEfectivoCore(T1, Plataforma, "admin_tenant"), 200, null) };
        var (siguio, efectiva) = await Correr(Contexto("admin_plataforma", Plataforma, T1, conCabeceras: false), core);

        Assert.True(siguio);
        Assert.Equal("admin_tenant", efectiva!.Role);
        Assert.Equal(T1, efectiva.TenantId);
    }

    [Fact]
    public async Task SiCoreNiegaElContextoSeRespondeConSuStatusYNoSeSigue()
    {
        var core = new CoreFalso { Respuesta = new(null, 404, "{\"code\":\"MANI-PLA-404\"}") };
        var c = Contexto("admin_plataforma", Plataforma, T1, conCabeceras: true);

        var (siguio, _) = await Correr(c, core);

        Assert.False(siguio);
        Assert.Equal(404, c.Response.StatusCode);
    }

    [Theory]
    [InlineData("aliado")]
    [InlineData("cliente")]
    [InlineData("admin_tenant")]
    public async Task OtroRolConCabecerasDePlataformaRecibe403SinConsultarACore(string rol)
    {
        var core = new CoreFalso();
        var c = Contexto(rol, "30000000-0000-4000-8000-000000000011", T1, conCabeceras: true);

        var (siguio, _) = await Correr(c, core);

        Assert.False(siguio);
        Assert.Equal(403, c.Response.StatusCode);
        Assert.Equal(0, core.Llamadas);
    }

    [Fact]
    public async Task OtroRolSinCabecerasSigueConSuPropioToken()
    {
        var core = new CoreFalso();
        var c = Contexto("aliado", AliadoT2, T2, conCabeceras: false);

        var (siguio, efectiva) = await Correr(c, core);

        Assert.True(siguio);
        Assert.Equal(0, core.Llamadas);
        Assert.Equal("aliado", efectiva!.Role);
        Assert.Equal(AliadoT2, efectiva.UserId);
    }

    private sealed class Captura : HttpMessageHandler
    {
        public HttpRequestMessage? Vista { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Vista = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task ElReenvioACoreLlevaLasCabecerasDePlataforma()
    {
        var c = Contexto("admin_plataforma", Plataforma, T1, conCabeceras: true);
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(c);
        var captura = new Captura();
        var http = new HttpClient(new ForwardAuthorizationHandler(accessor.Object) { InnerHandler = captura });

        await http.GetAsync("http://core/api/v1/allies/me");

        Assert.Equal(T2.ToString(), string.Join(",", captura.Vista!.Headers.GetValues(CabecerasPlataforma.Tenant)));
        Assert.Equal(AliadoT2, string.Join(",", captura.Vista.Headers.GetValues(CabecerasPlataforma.ActuarComo)));
    }
}
