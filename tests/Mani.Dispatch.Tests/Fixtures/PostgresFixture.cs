using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ManiDispatch.Tests.Fixtures;

/// <summary>
/// PostgreSQL 15 real en un contenedor, con las migraciones oficiales 001-008 del Gateway.
/// Se crea una sola vez por colección; cada prueba siembra sus propios datos con un tenant nuevo.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:15")
        .WithDatabase("mani")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // Supabase provee el esquema auth; Postgres plano no. Solo hace falta auth.uid() para
        // crear las funciones de 002/004/005, que las pruebas de Dispatch no invocan.
        await Execute(connection, @"
            CREATE SCHEMA IF NOT EXISTS auth;
            CREATE OR REPLACE FUNCTION auth.uid() RETURNS uuid LANGUAGE sql STABLE AS $$ SELECT NULL::uuid $$;");

        var dir = Path.Combine(AppContext.BaseDirectory, "Database", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            await Execute(connection, await File.ReadAllTextAsync(file));
        }

        // app.set_context la crea la migración 0006 de Core (MANI-Node), que no vive en este repositorio. El doble
        // fija los mismos claims locales a la transacción y además deja constancia del contexto pedido, para que las
        // pruebas comprueben que Dispatch lo fija (D-2). Las políticas RLS por rol se prueban en MANI-Node.
        await Execute(connection, @"
            CREATE SCHEMA IF NOT EXISTS app;
            CREATE TABLE IF NOT EXISTS app.contexto_registrado (
                tenant_id uuid NOT NULL,
                user_id uuid,
                user_role text NOT NULL,
                txid bigint NOT NULL DEFAULT txid_current()
            );
            CREATE OR REPLACE FUNCTION app.set_context(p_tenant_id uuid, p_user_id uuid, p_user_role text)
            RETURNS void LANGUAGE plpgsql AS $$
            BEGIN
                PERFORM set_config('request.jwt.claims', json_build_object(
                    'sub', p_user_id,
                    'app_metadata', json_build_object('tenant_id', p_tenant_id, 'user_role', lower(p_user_role)))::text, true);
                INSERT INTO app.contexto_registrado (tenant_id, user_id, user_role) VALUES (p_tenant_id, p_user_id, lower(p_user_role));
            END $$;");
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }
