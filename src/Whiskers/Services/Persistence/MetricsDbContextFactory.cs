using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Whiskers.Services.Persistence;

/// <summary>Design-time factory used exclusively by the EF Core tooling (<c>dotnet ef migrations add</c>).
/// It lets the tools build the model without running the full application host (Program.cs registers a
/// lot of DI/middleware that we do not want to execute just to scaffold a migration). The connection
/// string here is a throwaway — migration scaffolding reads the model, it never opens this database.</summary>
public sealed class MetricsDbContextFactory : IDesignTimeDbContextFactory<MetricsDbContext>
{
    public MetricsDbContext CreateDbContext(string[] args)
    {
        // Branch on the same env var the app uses, so `dotnet ef … add` scaffolds into the matching
        // provider's migration assembly (ADR-0004). Connection strings here are throwaways — scaffolding
        // reads the model, it never opens a database.
        var provider = (Environment.GetEnvironmentVariable("WHISKERS_DB_PROVIDER") ?? "sqlite")
            .Trim().ToLowerInvariant();

        var builder = new DbContextOptionsBuilder<MetricsDbContext>();
        if (provider == "postgres")
            builder.UseNpgsql("Host=localhost;Database=whiskers_design",
                npg => npg.MigrationsAssembly("Whiskers.Migrations.Postgres"));
        else
            builder.UseSqlite("Data Source=serverwatch-design.db",
                sql => sql.MigrationsAssembly("Whiskers.Migrations.Sqlite"));

        return new MetricsDbContext(builder.Options);
    }
}
