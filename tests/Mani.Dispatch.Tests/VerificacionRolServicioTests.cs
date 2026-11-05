using ManiDispatch.Infrastructure.Repositories;
using ManiDispatch.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// SCRUM-1468 (TEC-21): Dispatch no arranca fuera de Development si su conexión usa un rol que salta RLS. Corre contra
/// el PostgreSQL real del fixture, donde <c>postgres</c> es superusuario y un rol propio sin BYPASSRLS no lo es.
/// </summary>
[Collection("Postgres")]
public class VerificacionRolServicioTests
{
    private const string RolServicio = "mani_dispatch_svc";
    private readonly PostgresFixture _db;

    public VerificacionRolServicioTests(PostgresFixture db) => _db = db;

    private static IHostEnvironment Ambiente(string nombre)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(nombre);
        return env.Object;
    }

    private static VerificacionRolServicio Verificacion(string? connectionString, string ambiente) =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connectionString })
                .Build(),
            Ambiente(ambiente),
            NullLogger<VerificacionRolServicio>.Instance);

    /// <summary>Conexión que entra como el rol de servicio (como en QA), creado sin BYPASSRLS si no existe.</summary>
    private async Task<string> ComoRolDeServicioAsync()
    {
        await using (var admin = new NpgsqlConnection(_db.ConnectionString))
        {
            await admin.OpenAsync();
            await using var crear = new NpgsqlCommand(
                $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{RolServicio}') THEN " +
                $"CREATE ROLE {RolServicio} NOLOGIN NOBYPASSRLS NOSUPERUSER; END IF; END $$;",
                admin);
            await crear.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(_db.ConnectionString) { Options = $"-c role={RolServicio}" }.ConnectionString;
    }

    [Fact]
    public async Task Inspeccionar_ConPostgres_ReportaQueSaltaRls()
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();

        var rol = await VerificacionRolServicio.InspeccionarAsync(connection);

        Assert.Equal(new RolConexion("postgres", true), rol);
    }

    [Fact]
    public async Task Inspeccionar_ConRolDeServicio_NoSaltaRls()
    {
        await using var connection = new NpgsqlConnection(await ComoRolDeServicioAsync());
        await connection.OpenAsync();

        var rol = await VerificacionRolServicio.InspeccionarAsync(connection);

        Assert.Equal(new RolConexion(RolServicio, false), rol);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("QA")]
    [InlineData("Staging")]
    public async Task FueraDeDevelopment_UnRolQueSaltaRls_ImpideArrancar(string ambiente)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Verificacion(_db.ConnectionString, ambiente).StartAsync(CancellationToken.None));

        Assert.Contains("mani_dispatch_svc", error.Message);
        Assert.Contains("BYPASSRLS", error.Message);
    }

    [Fact]
    public async Task EnDevelopment_UnRolQueSaltaRls_SoloAvisa()
    {
        var error = await Record.ExceptionAsync(
            () => Verificacion(_db.ConnectionString, Environments.Development).StartAsync(CancellationToken.None));

        Assert.Null(error);
    }

    [Fact]
    public async Task FueraDeDevelopment_ConElRolDeServicio_Arranca()
    {
        var verificacion = Verificacion(await ComoRolDeServicioAsync(), "Production");

        var error = await Record.ExceptionAsync(() => verificacion.StartAsync(CancellationToken.None));

        Assert.Null(error);
    }

    [Fact]
    public async Task SinCadenaDeConexion_NoHayRolQueVerificar()
    {
        var error = await Record.ExceptionAsync(() => Verificacion(null, "Production").StartAsync(CancellationToken.None));

        Assert.Null(error);
    }

    [Fact]
    public async Task SinPoderComprobarElRol_FueraDeDevelopmentNoArranca_YEnDevelopmentSigue()
    {
        const string inalcanzable = "Host=127.0.0.1;Port=1;Database=mani;Username=mani;Timeout=2";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Verificacion(inalcanzable, "Production").StartAsync(CancellationToken.None));
        Assert.Null(await Record.ExceptionAsync(
            () => Verificacion(inalcanzable, Environments.Development).StartAsync(CancellationToken.None)));
    }
}
