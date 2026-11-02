using System.Diagnostics;
using Whiskers.Services.Backup;

namespace Whiskers.Tests;

/// <summary>R1-33: the parts of the PostgreSQL client-tool runner that need no server — how the connection reaches the
/// tools, a missing tool, and which archives the panel offers for a restore. The round trip itself runs against a real
/// server in <see cref="PostgresBackupRestoreTests"/>.</summary>
public class PostgresDumpToolTests
{
    [Fact]
    public void The_connection_reaches_the_tools_through_their_environment_and_the_password_never_their_command_line()
    {
        var start = new ProcessStartInfo("pg_dump");
        start.ArgumentList.Add("--format=custom");
        start.Environment["PGSERVICE"] = "elsewhere";           // inherited libpq settings may not redirect the tools
        start.Environment["PGPASSFILE"] = "/somewhere/.pgpass";

        PostgresDumpTool.Connect(start, "Host=db.internal;Port=5433;Database=whiskers;Username=app;Password=pw-value;SSL Mode=Require");

        Assert.Equal("db.internal", start.Environment["PGHOST"]);
        Assert.Equal("5433", start.Environment["PGPORT"]);
        Assert.Equal("whiskers", start.Environment["PGDATABASE"]);
        Assert.Equal("app", start.Environment["PGUSER"]);
        Assert.Equal("pw-value", start.Environment["PGPASSWORD"]);
        Assert.Equal("require", start.Environment["PGSSLMODE"]);
        Assert.False(start.Environment.ContainsKey("PGSERVICE"));
        Assert.False(start.Environment.ContainsKey("PGPASSFILE"));
        Assert.DoesNotContain(start.ArgumentList, a => a.Contains("pw-value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_tool_is_named_with_the_setting_that_points_at_it()
    {
        var tools = new PostgresDumpTool(Path.Combine(Path.GetTempPath(), $"no-pg-bin-{Guid.NewGuid():N}"));

        var ex = await Assert.ThrowsAsync<PostgresToolException>(() =>
            tools.DumpAsync("Host=127.0.0.1;Database=x;Username=u;Password=secret-value", Path.Combine(Path.GetTempPath(), "x.pgdump")));

        Assert.Contains("pg_dump is not available", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PostgresDumpTool.BinDirKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sqlite", false, true)]
    [InlineData("postgres", true, true)]
    [InlineData("postgres", false, false)]   // a PostgreSQL archive from before R1-33: the files only
    public void Only_an_archive_that_holds_its_database_is_offered_for_a_restore(string provider, bool includesDatabase, bool restorable)
        => Assert.Equal(restorable, new SelfBackupInfo { Provider = provider, IncludesDatabase = includesDatabase }.Restorable);
}
