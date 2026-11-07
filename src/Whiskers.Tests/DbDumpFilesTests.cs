using Whiskers.Services.Database;
using Whiskers.Services.Scheduler;

namespace Whiskers.Tests;

/// <summary>INV-19 review round 2 (m3): the DB-backup retention picks a database's dumps by exact name, so pruning
/// <c>app</c> never removes <c>app_db</c>'s recovery points, and its verifier counts the same files.</summary>
public class DbDumpFilesTests
{
    [Theory]
    [InlineData("app_20261107_010101.sql", "app", true)]
    [InlineData("app_20261107_010101.sql.gz", "app", true)]
    [InlineData("app_db_20261107_010101.sql", "app", false)]
    [InlineData("app_db_20261107_010101.sql", "app_db", true)]
    [InlineData("my.app_20261107_010101.sql", "my.app", true)]
    [InlineData("app_20261107_0101.sql", "app", false)]
    [InlineData("app_20261107_010101.sql.bak", "app", false)]
    [InlineData("app_20261107_010101.sql\n", "app", false)]
    [InlineData("xapp_20261107_010101.sql", "app", false)]
    [InlineData("app_20261107_010101.sql", "-app", false)]
    [InlineData("app_20261107_010101.sql", "a/pp", false)]
    public void A_dump_belongs_only_to_the_database_it_is_named_after(string file, string db, bool expected) =>
        Assert.Equal(expected, DbDumpFiles.IsDumpOf(file, db));

    [Fact]
    public void Retention_for_app_never_touches_app_db_dumps()
    {
        // ls -1t: newest first.
        var listing = string.Join('\n',
            "app_db_20261107_030303.sql",
            "app_20261107_020202.sql",
            "app_db_20261106_030303.sql",
            "app_20261106_020202.sql.gz",
            "app_db_20261105_030303.sql",
            "app_20261105_020202.sql",
            "notes.txt");

        var pruned = DbDumpFiles.ToPrune(listing, "app", keep: 1);

        Assert.Equal(["app_20261106_020202.sql.gz", "app_20261105_020202.sql"], pruned);
        Assert.DoesNotContain(pruned, f => f.StartsWith("app_db_", StringComparison.Ordinal));
        Assert.Equal(["app_db_20261105_030303.sql"], DbDumpFiles.ToPrune(listing, "app_db", keep: 2));
    }

    [Fact]
    public void Nothing_is_pruned_within_keep_or_for_an_unsafe_name()
    {
        const string listing = "app_20261107_020202.sql\napp_20261106_020202.sql\n";
        Assert.Empty(DbDumpFiles.ToPrune(listing, "app", keep: 2));
        Assert.Empty(DbDumpFiles.ToPrune(listing, "--app", keep: 0));
        Assert.Empty(DbDumpFiles.ToPrune("", "app", keep: 0));
    }

    [Fact]
    public void The_retention_command_removes_exactly_the_quoted_dumps()
    {
        Assert.Equal("true", TaskExecutor.RetentionCommandFor("/var/backups/db", []));
        Assert.Equal("rm -f -- '/var/backups/db/app_20261105_020202.sql' '/var/backups/db/app_20261106_020202.sql.gz'",
            TaskExecutor.RetentionCommandFor("/var/backups/db",
                ["app_20261105_020202.sql", "app_20261106_020202.sql.gz"]));
    }
}
