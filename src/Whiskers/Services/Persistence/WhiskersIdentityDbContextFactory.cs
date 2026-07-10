using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Whiskers.Services.Persistence;

/// <summary>Design-time factory used exclusively by the EF Core tooling
/// (<c>dotnet ef migrations add --context WhiskersIdentityDbContext</c>). Mirrors
/// <see cref="MetricsDbContextFactory"/> but for the Identity context, and pins its own
/// <c>__IdentityMigrationsHistory</c> table so the two contexts' histories never collide in the shared
/// database. Branches on <c>WHISKERS_DB_PROVIDER</c> so scaffolding lands in the matching provider's
/// migration assembly (ADR-0004). Connection strings here are throwaways — scaffolding reads the model.</summary>
public sealed class WhiskersIdentityDbContextFactory : IDesignTimeDbContextFactory<WhiskersIdentityDbContext>
{
    public WhiskersIdentityDbContext CreateDbContext(string[] args)
    {
        var provider = (Environment.GetEnvironmentVariable("WHISKERS_DB_PROVIDER") ?? "sqlite")
            .Trim().ToLowerInvariant();

        var builder = new DbContextOptionsBuilder<WhiskersIdentityDbContext>();
        if (provider == "postgres")
            builder.UseNpgsql("Host=localhost;Database=whiskers_design", npg =>
            {
                npg.MigrationsAssembly("Whiskers.Migrations.Postgres");
                npg.MigrationsHistoryTable("__IdentityMigrationsHistory");
            });
        else
            builder.UseSqlite("Data Source=serverwatch-design.db", sql =>
            {
                sql.MigrationsAssembly("Whiskers.Migrations.Sqlite");
                sql.MigrationsHistoryTable("__IdentityMigrationsHistory");
            });

        return new WhiskersIdentityDbContext(builder.Options);
    }
}
