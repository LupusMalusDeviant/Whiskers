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
        var options = new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite("Data Source=serverwatch-design.db")
            .Options;
        return new MetricsDbContext(options);
    }
}
