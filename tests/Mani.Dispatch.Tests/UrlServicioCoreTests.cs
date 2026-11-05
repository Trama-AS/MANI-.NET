using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ManiDispatch.Domain.Entities;
using ManiDispatch.Infrastructure.Core;
using ManiDispatch.Infrastructure.Dispatch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>TEC-06 (SCRUM-1453): de dónde sale la URL de Core y cuándo Dispatch se niega a arrancar.</summary>
public class UrlServicioCoreTests
{
    private static IConfiguration Config(params (string Clave, string? Valor)[] valores)
    {
        var settings = new Dictionary<string, string?>();
        foreach (var (clave, valor) in valores) settings[clave] = valor;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Fact]
    public void Leer_PrefiereLaVariable_SobreElRespaldo()
    {
        var config = Config((UrlServicioCore.Variable, "http://core:3000"), (UrlServicioCore.Respaldo, "http://otro:3000"));

        Assert.Equal("http://core:3000", UrlServicioCore.Leer(config));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Leer_SinVariable_UsaServiceUrlsCoreService(string? variable)
    {
        var config = Config((UrlServicioCore.Variable, variable), (UrlServicioCore.Respaldo, " http://core-service:3000/ "));

        Assert.Equal("http://core-service:3000", UrlServicioCore.Leer(config));
    }

    [Fact]
    public void Leer_SinNingunaDeLasDos_DevuelveNull()
    {
        Assert.Null(UrlServicioCore.Leer(Config()));
        Assert.Null(UrlServicioCore.Leer(null));
    }

    [Fact]
    public void Exigir_SinUrl_FallaDiciendoQueVariableDeclarar()
    {
        var error = Assert.Throws<InvalidOperationException>(() => UrlServicioCore.Exigir(Config()));

        Assert.Contains("CORE_SERVICE_URL", error.Message);
        Assert.Contains("ServiceUrls:CoreService", error.Message);
        Assert.Contains(".env.example", error.Message);
    }

    [Theory]
    [InlineData("core-service:3000")]
    [InlineData("ftp://core:21")]
    [InlineData("/api/v1")]
    public void Exigir_ConUnValorQueNoEsUrlHttp_Falla(string valor)
    {
        var error = Assert.Throws<InvalidOperationException>(() => UrlServicioCore.Exigir(Config((UrlServicioCore.Variable, valor))));

        Assert.Contains("http(s) absoluta", error.Message);
    }

    [Theory]
    [InlineData("http://core-service:3000/", "http://core-service:3000")]
    [InlineData("https://core.mani.test", "https://core.mani.test")]
    public void Exigir_ConUrlValida_LaDevuelveNormalizada(string valor, string esperado)
    {
        Assert.Equal(esperado, UrlServicioCore.Exigir(Config((UrlServicioCore.Variable, valor))));
    }

    [Fact]
    public void Appsettings_NoTraeUnaUrlDeCorePorOmision()
    {
        // Una URL por omisión en appsettings.json haría pasar Exigir aunque el despliegue no declare la variable.
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        Assert.Null(UrlServicioCore.Leer(config));
    }

    /// <summary>Responde 200 con <paramref name="cuerpo"/> y guarda la URL pedida.</summary>
    private sealed class Registro(string cuerpo) : HttpMessageHandler
    {
        public Uri? Ultima { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Ultima = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(cuerpo, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task LosClientes_UsanElRespaldo_CuandoSoloEstaServiceUrlsCoreService()
    {
        var config = Config((UrlServicioCore.Respaldo, "http://core-respaldo:3000/"));

        var aliados = new Registro("{\"allies\":[],\"totalCount\":0}");
        await new CoreAllyClient(new HttpClient(aliados), config, NullLogger<CoreAllyClient>.Instance)
            .GetEligibleAlliesAsync(new MatchCriteria { TenantId = Guid.NewGuid(), ZonaId = Guid.NewGuid(), CategoriaId = Guid.NewGuid(), Page = 1, PageSize = 5, CorrelationId = "c" });
        Assert.StartsWith("http://core-respaldo:3000/api/v1/", aliados.Ultima!.ToString());

        var ofertas = new Registro("[]");
        await new CoreDispatchPublisher(new HttpClient(ofertas), config, NullLogger<CoreDispatchPublisher>.Instance)
            .GetOffersByRequestAsync(Guid.NewGuid(), Guid.NewGuid());
        Assert.StartsWith("http://core-respaldo:3000/api/v1/", ofertas.Ultima!.ToString());
    }
}
