using Microsoft.EntityFrameworkCore;
using Whiskers.Configuration;

namespace Whiskers.Services.Persistence;

/// <summary>
/// Registers <see cref="MetricsDbContext"/> for the configured database provider (stableDB.md step 1).
/// SQLite stays the zero-config default; PostgreSQL is opt-in. One context, provider chosen at startup.
/// </summary>
public static class DatabaseRegistration
{
    /// <summary>The assembly holding the SQLite migrations of both contexts (ADR-0004).</summary>
    public const string SqliteMigrationsAssembly = "Whiskers.Migrations.Sqlite";

    /// <summary>The identity context's own migration history table (F1).</summary>
    public const string IdentityMigrationsHistoryTable = "__IdentityMigrationsHistory";

    /// <summary>The configured provider (lower case) and connection string — also read by the restore at the start,
    /// before the container is built (R1-33).</summary>
    public static (string Provider, string? ConnectionString) Resolve(IConfiguration configuration)
    {
        var configured = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
                         ?? new DatabaseOptions();

        // ENV aliases win over the config section. A *_FILE secret (Docker/K8s) wins over the plain env var.
        var provider = (configuration["WHISKERS_DB_PROVIDER"] ?? configured.Provider)
            .Trim().ToLowerInvariant();
        // A WHISKERS_DB_CONNECTION_FILE that cannot be read stops the start (SecretSource, R1-28).
        var connectionString = SecretSource.Read(configuration, "WHISKERS_DB_CONNECTION")
                               ?? configured.ConnectionString;
        // A connection string from a secret file is complete as it is: a plain environment variable never changes it.
        if (provider == "postgres" && !ConnectionFileConfigured(configuration))
            connectionString = WithPassword(connectionString, configuration[PasswordVariable]);
        return (provider, connectionString);
    }

    /// <summary>True when <c>WHISKERS_DB_CONNECTION_FILE</c> names a file — the same test <see cref="SecretSource"/> uses.</summary>
    private static bool ConnectionFileConfigured(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration["WHISKERS_DB_CONNECTION_FILE"]
                                      ?? Environment.GetEnvironmentVariable("WHISKERS_DB_CONNECTION_FILE"));

    /// <summary>The PostgreSQL password given apart from the connection string (the PostgreSQL overlay sets it from
    /// <c>WHISKERS_DB_PASSWORD</c> in <c>.env</c>). A password pasted into a connection string breaks it when it holds a
    /// <c>;</c> or starts or ends with a space; given here it is put in by Npgsql's builder, which quotes it. Order: a
    /// <c>WHISKERS_DB_CONNECTION_FILE</c> is used as it is (this password is ignored); otherwise this password wins over a
    /// <c>Password=</c> in <c>WHISKERS_DB_CONNECTION</c> or <c>Database:ConnectionString</c>.</summary>
    public const string PasswordVariable = "WHISKERS_DB_PASSWORD";

    /// <summary><paramref name="connectionString"/> with <paramref name="password"/> as its password, built with
    /// <see cref="Npgsql.NpgsqlConnectionStringBuilder"/> (a password given here wins over one in the string). Unchanged
    /// when either is empty: without a connection string the start still stops with its own message.</summary>
    internal static string? WithPassword(string? connectionString, string? password)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(connectionString)) return connectionString;
        return new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { Password = password }.ConnectionString;
    }

    /// <summary>The full path of the SQLite file a connection string opens, or null when it names none (empty, in-memory).
    /// A restore writes the archive's database there, the file the app opens (review 1.0.0, Data). A source given as a
    /// <c>file:</c> URI or through <c>|DataDirectory|</c> is refused: which file SQLite opens is not read off it here, and a
    /// restore must not guess.</summary>
    public static string? SqliteFilePath(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        var source = builder.DataSource;
        if (string.IsNullOrWhiteSpace(source) || source == ":memory:"
            || builder.Mode == Microsoft.Data.Sqlite.SqliteOpenMode.Memory)
            return null;
        if (source.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || source.Contains("|DataDirectory|", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "the SQLite connection string names its file as a URI or through |DataDirectory|; give the file path as Data Source");
        return Path.GetFullPath(source);
    }

    /// <summary>
    /// Wires up <see cref="MetricsDbContext"/> for <c>Database:Provider</c> (sqlite | postgres). With no
    /// configuration this is byte-identical to the previous inline SQLite registration (connection string
    /// derived from <paramref name="dataPaths"/>). An unknown provider fails fast at startup.
    /// </summary>
    public static WebApplicationBuilder AddWhiskersDatabase(this WebApplicationBuilder builder, DataPathOptions dataPaths)
    {
        var (provider, connectionString) = Resolve(builder.Configuration);

        // Publish the resolved values so services can read them via IOptions if needed.
        builder.Services.Configure<DatabaseOptions>(o =>
        {
            o.Provider = provider;
            o.ConnectionString = connectionString;
        });

        builder.Services.AddWhiskersDbContexts(provider, connectionString, dataPaths);
        return builder;
    }

    /// <summary>The two contexts for <paramref name="provider"/> — also used by the VAULT_KEY rotation at the start, before
    /// the container is built (contract 0015).</summary>
    public static IServiceCollection AddWhiskersDbContexts(this IServiceCollection services, string provider, string? connectionString,
        DataPathOptions dataPaths)
    {
        switch (provider)
        {
            case "sqlite":
                var sqliteCs = string.IsNullOrWhiteSpace(connectionString)
                    ? dataPaths.DbConnectionString
                    : connectionString;
                // Context in Whiskers.Data, SQLite migrations in Whiskers.Migrations.Sqlite — EF must be
                // told which assembly to load them from (ADR-0004, separate migration assemblies).
                // R1-24: every sink row is redacted on save, whoever wrote it.
                services.AddDbContext<MetricsDbContext>(
                    o => o.UseSqlite(sqliteCs, sql => sql.MigrationsAssembly(SqliteMigrationsAssembly))
                        .AddInterceptors(SinkRedactionInterceptor.Instance),
                    ServiceLifetime.Transient);
                // Local-auth Identity context (F1): SAME database, its OWN __IdentityMigrationsHistory so it
                // never touches the metrics legacy-baseline path. Scoped (default) — UserManager/store are Scoped.
                services.AddDbContext<WhiskersIdentityDbContext>(
                    o => o.UseSqlite(sqliteCs, sql =>
                    {
                        sql.MigrationsAssembly(SqliteMigrationsAssembly);
                        sql.MigrationsHistoryTable(IdentityMigrationsHistoryTable);
                    }));
                break;

            case "postgres":
                if (string.IsNullOrWhiteSpace(connectionString))
                    throw new InvalidOperationException(
                        "Database:Provider=postgres requires a connection string " +
                        "(Database:ConnectionString / WHISKERS_DB_CONNECTION / WHISKERS_DB_CONNECTION_FILE).");
                services.AddDbContext<MetricsDbContext>(
                    o => o.UseNpgsql(connectionString, npg =>
                    {
                        npg.EnableRetryOnFailure(3);
                        npg.MigrationsAssembly("Whiskers.Migrations.Postgres");
                    }).AddInterceptors(SinkRedactionInterceptor.Instance),
                    ServiceLifetime.Transient);
                // Local-auth Identity context (F1): SAME database, its OWN __IdentityMigrationsHistory. Scoped.
                services.AddDbContext<WhiskersIdentityDbContext>(
                    o => o.UseNpgsql(connectionString, npg =>
                    {
                        npg.EnableRetryOnFailure(3);
                        npg.MigrationsAssembly("Whiskers.Migrations.Postgres");
                        npg.MigrationsHistoryTable("__IdentityMigrationsHistory");
                    }));
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown Database:Provider '{provider}'. Use 'sqlite' or 'postgres'.");
        }

        return services;
    }
}
