using Microsoft.Extensions.Configuration;
using Npgsql;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// Review 1.0.0 (Deploy, T6): settings that the deployment files compose or pass on must reach Whiskers intact.
/// <list type="bullet">
/// <item>A PostgreSQL password with <c>;</c> or leading/trailing spaces broke the connection string the PostgreSQL overlay
/// pasted together. The overlay now passes it apart as <c>WHISKERS_DB_PASSWORD</c>, and Whiskers puts it in with Npgsql's
/// builder, which quotes it.</item>
/// <item><c>VAULT_KEY_NEW_FILE</c> (a key rotation, contract 0015) set in <c>.env</c> never reached the container: the
/// compose files did not pass it on.</item>
/// </list>
/// </summary>
public sealed class DeployPlumbingTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Theory]
    [InlineData("pa;ss")]
    [InlineData(" leading and trailing ")]
    [InlineData("a'b\"c=d;Host=evil")]
    public void A_postgres_password_given_apart_reaches_npgsql_unchanged(string password)
    {
        var (provider, connection) = DatabaseRegistration.Resolve(Config(
            ("WHISKERS_DB_PROVIDER", "postgres"),
            ("WHISKERS_DB_CONNECTION", "Host=db;Port=5432;Database=whiskers;Username=whiskers"),
            (DatabaseRegistration.PasswordVariable, password)));

        Assert.Equal("postgres", provider);
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        Assert.Equal(password, parsed.Password);
        Assert.Equal("db", parsed.Host);
        Assert.Equal("whiskers", parsed.Database);
        Assert.Equal("whiskers", parsed.Username);
    }

    [Fact]
    public void The_password_given_apart_wins_and_is_ignored_where_it_does_not_apply()
    {
        var wins = DatabaseRegistration.Resolve(Config(("WHISKERS_DB_PROVIDER", "postgres"),
            ("WHISKERS_DB_CONNECTION", "Host=db;Username=u;Password=old"), (DatabaseRegistration.PasswordVariable, "new")));
        Assert.Equal("new", new NpgsqlConnectionStringBuilder(wins.ConnectionString).Password);

        // Without it the connection string is used as it is.
        var plain = DatabaseRegistration.Resolve(Config(("WHISKERS_DB_PROVIDER", "postgres"),
            ("WHISKERS_DB_CONNECTION", "Host=db;Username=u;Password=old")));
        Assert.Equal("Host=db;Username=u;Password=old", plain.ConnectionString);

        // SQLite takes no password; no connection string stays none, so the start still stops with its own message.
        var sqlite = DatabaseRegistration.Resolve(Config((DatabaseRegistration.PasswordVariable, "x")));
        Assert.Equal(("sqlite", ""), (sqlite.Provider, sqlite.ConnectionString));
        var none = DatabaseRegistration.Resolve(Config(("WHISKERS_DB_PROVIDER", "postgres"), (DatabaseRegistration.PasswordVariable, "x")));
        Assert.True(string.IsNullOrEmpty(none.ConnectionString));
    }

    /// <summary>Review of T6: a connection string from <c>WHISKERS_DB_CONNECTION_FILE</c> (a secret) is used as it is; a
    /// plain <c>WHISKERS_DB_PASSWORD</c> in the environment must not override the password in it.</summary>
    [Fact]
    public void A_connection_file_wins_over_the_password_given_apart()
    {
        var file = Path.Combine(Path.GetTempPath(), $"whiskers-dbconn-{Guid.NewGuid():N}");
        File.WriteAllText(file, "Host=db;Username=u;Password=from-file\n");
        try
        {
            var (_, connection) = DatabaseRegistration.Resolve(Config(("WHISKERS_DB_PROVIDER", "postgres"),
                ("WHISKERS_DB_CONNECTION_FILE", file), ("WHISKERS_DB_CONNECTION", "Host=db;Username=u"),
                (DatabaseRegistration.PasswordVariable, "from-env")));
            Assert.Equal("Host=db;Username=u;Password=from-file", connection);

            // An empty _FILE (what the compose files pass when it is unset) is no file: the password applies.
            var (_, plain) = DatabaseRegistration.Resolve(Config(("WHISKERS_DB_PROVIDER", "postgres"),
                ("WHISKERS_DB_CONNECTION_FILE", " "), ("WHISKERS_DB_CONNECTION", "Host=db;Username=u"),
                (DatabaseRegistration.PasswordVariable, "from-env")));
            Assert.Equal("from-env", new NpgsqlConnectionStringBuilder(plain).Password);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void The_postgres_overlay_passes_the_password_apart_from_the_connection_string()
    {
        var overlay = DeploymentScripts.ReadRepoFile("deploy", "docker-compose.postgres.yml");
        var app = overlay[overlay.IndexOf("\n  serverwatch:\n", StringComparison.Ordinal)..];

        Assert.Contains("      - WHISKERS_DB_CONNECTION=Host=db;Port=5432;Database=${WHISKERS_DB_NAME:-whiskers};Username=${WHISKERS_DB_USER:-whiskers}\n", app);
        Assert.Contains("      - WHISKERS_DB_PASSWORD=${WHISKERS_DB_PASSWORD:?set WHISKERS_DB_PASSWORD in your .env}\n", app);
        Assert.DoesNotContain("Password=", app);
    }

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-compose.hardened.yml")]
    public void A_key_rotation_set_in_the_env_file_reaches_the_container(string compose)
    {
        var text = DeploymentScripts.ReadRepoFile(compose);
        Assert.Contains("      - VAULT_KEY_FILE=${VAULT_KEY_FILE:-}\n", text);
        Assert.Contains("      - VAULT_KEY_NEW_FILE=${VAULT_KEY_NEW_FILE:-}\n", text);
    }
}
