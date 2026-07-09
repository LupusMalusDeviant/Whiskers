using Microsoft.EntityFrameworkCore;
using Whiskers.Configuration;

namespace Whiskers.Services.Persistence;

/// <summary>
/// Registers <see cref="MetricsDbContext"/> for the configured database provider (stableDB.md step 1).
/// SQLite stays the zero-config default; PostgreSQL is opt-in. One context, provider chosen at startup.
/// </summary>
public static class DatabaseRegistration
{
    /// <summary>
    /// Wires up <see cref="MetricsDbContext"/> for <c>Database:Provider</c> (sqlite | postgres). With no
    /// configuration this is byte-identical to the previous inline SQLite registration (connection string
    /// derived from <paramref name="dataPaths"/>). An unknown provider fails fast at startup.
    /// </summary>
    public static WebApplicationBuilder AddWhiskersDatabase(this WebApplicationBuilder builder, DataPathOptions dataPaths)
    {
        var configured = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
                         ?? new DatabaseOptions();

        // ENV aliases win over the config section. A *_FILE secret (Docker/K8s) wins over the plain env var.
        var provider = (builder.Configuration["WHISKERS_DB_PROVIDER"] ?? configured.Provider)
            .Trim().ToLowerInvariant();
        var connectionString = ReadSecretFile(builder.Configuration["WHISKERS_DB_CONNECTION_FILE"])
                               ?? builder.Configuration["WHISKERS_DB_CONNECTION"]
                               ?? configured.ConnectionString;

        // Publish the resolved values so services can read them via IOptions if needed.
        builder.Services.Configure<DatabaseOptions>(o =>
        {
            o.Provider = provider;
            o.ConnectionString = connectionString;
        });

        switch (provider)
        {
            case "sqlite":
                var sqliteCs = string.IsNullOrWhiteSpace(connectionString)
                    ? dataPaths.DbConnectionString
                    : connectionString;
                // Context in Whiskers.Data, SQLite migrations in Whiskers.Migrations.Sqlite — EF must be
                // told which assembly to load them from (ADR-0004, separate migration assemblies).
                builder.Services.AddDbContext<MetricsDbContext>(
                    o => o.UseSqlite(sqliteCs, sql => sql.MigrationsAssembly("Whiskers.Migrations.Sqlite")),
                    ServiceLifetime.Transient);
                break;

            case "postgres":
                if (string.IsNullOrWhiteSpace(connectionString))
                    throw new InvalidOperationException(
                        "Database:Provider=postgres requires a connection string " +
                        "(Database:ConnectionString / WHISKERS_DB_CONNECTION / WHISKERS_DB_CONNECTION_FILE).");
                builder.Services.AddDbContext<MetricsDbContext>(
                    o => o.UseNpgsql(connectionString, npg =>
                    {
                        npg.EnableRetryOnFailure(3);
                        npg.MigrationsAssembly("Whiskers.Migrations.Postgres");
                    }),
                    ServiceLifetime.Transient);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown Database:Provider '{provider}'. Use 'sqlite' or 'postgres'.");
        }

        return builder;
    }

    private static string? ReadSecretFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? File.ReadAllText(path).Trim()
            : null;
}
