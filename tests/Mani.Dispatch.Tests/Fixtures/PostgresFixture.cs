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
