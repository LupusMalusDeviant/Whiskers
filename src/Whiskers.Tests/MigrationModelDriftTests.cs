using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>R1-34: the model and the migrations of both contexts agree on both providers, and both providers ship
/// the same migrations. A model change without a migration (or a migration for one provider only) would otherwise
/// only show on the first boot of the other provider, as a schema the code does not match.</summary>
public sealed class MigrationModelDriftTests
{
    // No connection is opened: the model diff and the migration list need none.
    private const string NoServer = "Host=localhost;Database=drift-check;Username=none";

    private static MetricsDbContext Metrics(string provider) => new(provider == "sqlite"
        ? new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite("Data Source=:memory:", o => o.MigrationsAssembly(DatabaseRegistration.SqliteMigrationsAssembly)).Options
        : new DbContextOptionsBuilder<MetricsDbContext>()
            .UseNpgsql(NoServer, o => o.MigrationsAssembly("Whiskers.Migrations.Postgres")).Options);

    private static WhiskersIdentityDbContext Identity(string provider) => new(provider == "sqlite"
        ? new DbContextOptionsBuilder<WhiskersIdentityDbContext>()
            .UseSqlite("Data Source=:memory:", o =>
            {
                o.MigrationsAssembly(DatabaseRegistration.SqliteMigrationsAssembly);
                o.MigrationsHistoryTable(DatabaseRegistration.IdentityMigrationsHistoryTable);
            }).Options
        : new DbContextOptionsBuilder<WhiskersIdentityDbContext>()
            .UseNpgsql(NoServer, o =>
            {
                o.MigrationsAssembly("Whiskers.Migrations.Postgres");
                o.MigrationsHistoryTable(DatabaseRegistration.IdentityMigrationsHistoryTable);
            }).Options);

    private static DbContext Context(string context, string provider) =>
        context == "metrics" ? Metrics(provider) : Identity(provider);

    [Theory]
    [InlineData("metrics", "sqlite")]
    [InlineData("metrics", "postgres")]
    [InlineData("identity", "sqlite")]
    [InlineData("identity", "postgres")]
    public void The_model_has_no_change_without_a_migration(string context, string provider)
    {
        using var db = Context(context, provider);
        Assert.False(db.Database.HasPendingModelChanges(),
            $"The {context} model differs from the {provider} migrations' snapshot: add a migration for {provider} " +
            "(dotnet ef migrations add, see src/Whiskers.Migrations.*/README.md).");
    }

    [Theory]
    [InlineData("metrics", "sqlite")]
    [InlineData("metrics", "postgres")]
    [InlineData("identity", "sqlite")]
    [InlineData("identity", "postgres")]
    public void Every_context_has_migrations(string context, string provider)
    {
        using var db = Context(context, provider);
        Assert.NotEmpty(db.Database.GetMigrations());
    }

    /// <summary>The migration ids without their timestamps: the two providers' migrations are generated separately,
    /// seconds apart, so only the names are the same.</summary>
    private static IReadOnlyList<string> Names(DbContext db) =>
        db.Database.GetMigrations().Select(id => Regex.Replace(id, @"^\d{14}_", "")).ToList();

    [Theory]
    [InlineData("metrics")]
    [InlineData("identity")]
    public void Both_providers_ship_the_same_migrations_in_the_same_order(string context)
    {
        using var sqlite = Context(context, "sqlite");
        using var postgres = Context(context, "postgres");
        Assert.Equal(Names(sqlite), Names(postgres));
    }

    [Theory]
    [InlineData("metrics")]
    [InlineData("identity")]
    public void Migration_ids_carry_a_timestamp_so_the_names_compare(string context)
    {
        foreach (var provider in new[] { "sqlite", "postgres" })
        {
            using var db = Context(context, provider);
            Assert.All(db.Database.GetMigrations(), id => Assert.Matches(@"^\d{14}_\w+$", id));
        }
    }
}
