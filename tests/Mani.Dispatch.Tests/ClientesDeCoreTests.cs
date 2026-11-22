using System;
using System.Net.Http;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Infrastructure.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>SCRUM-1651 (T-14): el tiempo de espera de Dispatch hacia Core no es menor que la espera del pool de Core.</summary>
public class ClientesDeCoreTests
{
    /// <summary><c>connectionTimeoutMs</c> por omisión de <c>PgPoolFactory</c> en MANI-Node: lo que Core espera una conexión libre.</summary>
    private static readonly TimeSpan EsperaDelPoolDeCore = TimeSpan.FromSeconds(10);

    [Fact]
    public void Espera_NoEsMenorQueLaDelPoolDeCore()
    {
        Assert.True(ClientesDeCore.Espera >= EsperaDelPoolDeCore,
            $"Dispatch espera {ClientesDeCore.Espera.TotalSeconds} s a Core, menos que los {EsperaDelPoolDeCore.TotalSeconds} s de su pool");
    }

    [Fact]
    public void ClienteDeCore_QuedaRegistradoConEsaEspera()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddTransient<ForwardAuthorizationHandler>();
        services.AddClienteDeCore<IAllyRepository, CoreAllyClient>();
        using var provider = services.BuildServiceProvider();

        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(typeof(IAllyRepository).Name);

        Assert.Equal(ClientesDeCore.Espera, http.Timeout);
    }

    [Fact]
    public void Program_NoDejaClientesDeCoreConElTimeoutDeTresSegundos()
    {
        var codigo = System.IO.File.ReadAllText(BuscarArchivo("Program.cs"));

        foreach (var linea in codigo.Split('\n'))
        {
            if (linea.Contains("AddHttpClient<") && linea.Contains("Core") && linea.Contains("FromSeconds(3)"))
                Assert.Fail("Un cliente hacia Core sigue con Timeout de 3 s en Program.cs; usa AddClienteDeCore: " + linea.Trim());
        }
    }

    private static string BuscarArchivo(string nombre)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, nombre)))
            dir = dir.Parent;
        return dir == null ? throw new System.IO.FileNotFoundException(nombre) : System.IO.Path.Combine(dir.FullName, nombre);
    }
}
