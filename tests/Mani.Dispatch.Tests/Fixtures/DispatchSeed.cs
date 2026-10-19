using Dapper;
using Npgsql;

namespace ManiDispatch.Tests.Fixtures;

/// <summary>
/// Siembra un tenant aislado (UUIDs nuevos) sobre el esquema oficial. Cada prueba crea el suyo,
/// así las pruebas no se pisan entre sí aunque compartan el contenedor.
/// </summary>
public sealed class DispatchSeed
{
    private readonly string _connectionString;

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid ZonaId { get; } = Guid.NewGuid();
    public Guid ZonaPadreId { get; } = Guid.NewGuid();
    public Guid CategoriaId { get; } = Guid.NewGuid();
    public Guid ClienteId { get; } = Guid.NewGuid();
    public Guid SitioId { get; } = Guid.NewGuid();

    private DispatchSeed(string connectionString) => _connectionString = connectionString;

    public static async Task<DispatchSeed> CreateAsync(string connectionString)
    {
        var seed = new DispatchSeed(connectionString);
        await using var c = seed.Open();
        await c.OpenAsync();

        await c.ExecuteAsync(
            "INSERT INTO tenant (id, nombre, slug, estado) VALUES (@TenantId, 'Tenant de prueba', @Slug, 'ACTIVO')",
            new { seed.TenantId, Slug = $"t-{seed.TenantId:N}" });
        await c.ExecuteAsync(
            "INSERT INTO zona (id, nivel, nombre, zona_padre_id, estado) VALUES (@Id, 'CIUDAD', 'Ciudad padre', NULL, 'ACTIVO')",
            new { Id = seed.ZonaPadreId });
        await c.ExecuteAsync(
            "INSERT INTO zona (id, nivel, nombre, zona_padre_id, estado) VALUES (@Id, 'LOCALIDAD', 'Localidad', @Padre, 'ACTIVO')",
            new { Id = seed.ZonaId, Padre = seed.ZonaPadreId });
        await c.ExecuteAsync(
            "INSERT INTO categoria_servicio (id, tenant_id, nombre, estado) VALUES (@CategoriaId, @TenantId, 'Plomeria', 'ACTIVO')",
            new { seed.CategoriaId, seed.TenantId });

        var clienteUsuario = await seed.AddUsuarioAsync(c, "CLIENTE");
        await c.ExecuteAsync(
            "INSERT INTO cliente (id, tenant_id, usuario_id, tipo) VALUES (@ClienteId, @TenantId, @Usuario, 'PERSONA_NATURAL')",
            new { seed.ClienteId, seed.TenantId, Usuario = clienteUsuario });
        await c.ExecuteAsync(
            "INSERT INTO sitio (id, tenant_id, cliente_id, zona_id, direccion) VALUES (@SitioId, @TenantId, @ClienteId, @ZonaId, 'Calle 1 # 2-3')",
            new { seed.SitioId, seed.TenantId, seed.ClienteId, seed.ZonaId });
        return seed;
    }

    public NpgsqlConnection Open() => new(_connectionString);

    private async Task<Guid> AddUsuarioAsync(NpgsqlConnection c, string rol, Guid? tenantId = null)
    {
        var id = Guid.NewGuid();
        await c.ExecuteAsync(
            "INSERT INTO usuario (id, tenant_id, email, rol, estado) VALUES (@id, @tenant, @email, @rol, 'ACTIVO')",
            new { id, tenant = tenantId ?? TenantId, email = $"{id:N}@prueba.test", rol });
        return id;
    }

    /// <summary>Crea un aliado con su usuario, categoría y cobertura en la zona indicada.</summary>
    public async Task<(Guid AliadoId, Guid UsuarioId)> AddAliadoAsync(
        string nombre,
        string estadoVerificacion = "VERIFICADO",
        string tipo = "PERSONA_NATURAL",
        Guid? zonaId = null,
        Guid? categoriaId = null,
        Guid? tenantId = null)
    {
        await using var c = Open();
        await c.OpenAsync();
        var tenant = tenantId ?? TenantId;
        var usuario = await AddUsuarioAsync(c, "ALIADO", tenant);
        var aliado = Guid.NewGuid();
        await c.ExecuteAsync(
            @"INSERT INTO aliado (id, tenant_id, usuario_id, tipo, nombre_razon_social, estado_verificacion)
              VALUES (@aliado, @tenant, @usuario, @tipo, @nombre, @estadoVerificacion)",
            new { aliado, tenant, usuario, tipo, nombre, estadoVerificacion });
        await c.ExecuteAsync(
            "INSERT INTO aliado_categoria (tenant_id, aliado_id, categoria_id) VALUES (@tenant, @aliado, @cat)",
            new { tenant, aliado, cat = categoriaId ?? CategoriaId });
        await c.ExecuteAsync(
            "INSERT INTO cobertura_aliado (tenant_id, aliado_id, zona_id) VALUES (@tenant, @aliado, @zona)",
            new { tenant, aliado, zona = zonaId ?? ZonaId });
        return (aliado, usuario);
    }

    public async Task AddAusenciaAsync(Guid aliadoId, DateTime inicio, DateTime fin)
    {
        await using var c = Open();
        await c.OpenAsync();
        await c.ExecuteAsync(
            "INSERT INTO aliado_ausencia (tenant_id, aliado_id, inicio, fin, motivo) VALUES (@TenantId, @aliadoId, @inicio, @fin, 'VACACIONES')",
            new { TenantId, aliadoId, inicio, fin });
    }

    public async Task<Guid> AddSolicitudAsync(string estado = "PENDIENTE")
    {
        await using var c = Open();
        await c.OpenAsync();
        var id = Guid.NewGuid();
        await c.ExecuteAsync(
            @"INSERT INTO solicitud (id, tenant_id, cliente_id, sitio_id, categoria_id, zona_id, estado)
              VALUES (@id, @TenantId, @ClienteId, @SitioId, @CategoriaId, @ZonaId, @estado)",
            new { id, TenantId, ClienteId, SitioId, CategoriaId, ZonaId, estado });
        return id;
    }
}
