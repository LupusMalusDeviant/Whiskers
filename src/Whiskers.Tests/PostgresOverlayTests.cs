using System.Text.RegularExpressions;

namespace Whiskers.Tests;

/// <summary>
/// The PostgreSQL overlay (deploy/docker-compose.postgres.yml) on PostgreSQL 18. The 18 image refuses a mount at
/// /var/lib/postgresql/data (measured: it exits with "there appears to be PostgreSQL data in /var/lib/postgresql/data
/// (unused mount/volume)", even for an empty volume), so the data volume moved to /var/lib/postgresql under a new name.
/// The PostgreSQL 17 volume stays the way back, and a guard keeps 18 from starting empty next to it — a pulled overlay
/// must never look like a fresh install. The upgrade itself (dump on 17, restore on 18, rollback) was run end to end by
/// hand with Docker; docs/operations/postgres-18-upgrade.md records the steps.
/// </summary>
public class PostgresOverlayTests
{
    private static string Overlay => DeploymentScripts.ReadRepoFile("deploy", "docker-compose.postgres.yml").Replace("\r\n", "\n");

    private static string Service(string name)
    {
        var start = Overlay.IndexOf($"\n  {name}:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"service {name} not found");
        var rest = Overlay[(start + 1)..];
        var next = Regex.Match(rest[1..], @"\n(  [a-z][a-z0-9-]*:\n|[a-z]+:\n)");
        return next.Success ? rest[..(next.Index + 1)] : rest;
    }

    [Fact]
    public void The_database_runs_postgresql_18_on_its_own_volume_mounted_at_the_parent_directory()
    {
        var db = Service("db");
        Assert.Matches(@"(?m)^    image: postgres:18-alpine$", db);
        Assert.Contains("- whiskers-db18-data:/var/lib/postgresql\n", db);
        Assert.DoesNotMatch(@"(?m)^\s+- [^#\n]*:/var/lib/postgresql/data", db);   // a mount line, not the comment explaining it
        Assert.DoesNotContain("whiskers-db-data:", db);
    }

    [Fact]
    public void The_database_waits_for_the_upgrade_guard_and_the_app_for_a_healthy_database()
    {
        Assert.Matches(@"depends_on:\n      db-upgrade-guard:\n        condition: service_completed_successfully", Service("db"));
        Assert.Matches(@"depends_on:\n      db:\n        condition: service_healthy", Service("serverwatch"));
    }

    [Fact]
    public void The_guard_reads_both_volumes_read_only_and_refuses_17_data_next_to_an_empty_18_volume()
    {
        var guard = Service("db-upgrade-guard");
        Assert.Contains("- whiskers-db-data:/old:ro", guard);
        Assert.Contains("- whiskers-db18-data:/new:ro", guard);
        Assert.Contains("if [ -s /old/PG_VERSION ] && [ ! -s /new/18/docker/PG_VERSION ]; then", guard);
        Assert.Contains("exit 1", guard);
        Assert.Contains("docs/operations/postgres-18-upgrade.md", guard);
        // "$(" alone is an invalid interpolation for compose; it must be escaped as "$$(".
        Assert.DoesNotMatch(@"(?<!\$)\$\(", guard);
    }

    [Fact]
    public void Both_volumes_are_declared_so_the_17_volume_is_kept_and_never_written()
    {
        var volumes = Overlay[Overlay.IndexOf("\nvolumes:\n", StringComparison.Ordinal)..];
        Assert.Matches(@"(?m)^  whiskers-db-data:$", volumes);
        Assert.Matches(@"(?m)^  whiskers-db18-data:$", volumes);
        // The 17 volume is mounted by the guard only, read-only.
        Assert.Single(Regex.Matches(Overlay, @"whiskers-db-data:/"));
    }

    [Fact]
    public void The_upgrade_guide_exists_and_names_the_steps_the_guard_points_to()
    {
        var guide = DeploymentScripts.ReadRepoFile("docs", "operations", "postgres-18-upgrade.md");
        Assert.Contains("pg_dump", guide);
        Assert.Contains("--no-deps", guide);
        Assert.Contains("--single-transaction", guide);
        Assert.Contains("whiskers-db-data", guide);
    }
}
