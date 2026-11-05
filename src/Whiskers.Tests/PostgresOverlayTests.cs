using System.Text.RegularExpressions;

namespace Whiskers.Tests;

/// <summary>
/// The PostgreSQL overlay (deploy/docker-compose.postgres.yml) on PostgreSQL 18, its structure. The 18 image refuses a
/// mount at /var/lib/postgresql/data (measured: it exits with "there appears to be PostgreSQL data in
/// /var/lib/postgresql/data (unused mount/volume)", even for an empty volume), so the data volume moved to
/// /var/lib/postgresql under a new name. The PostgreSQL 17 volume stays the way back, and db-upgrade-guard keeps 18 from
/// starting next to it until a restore has been confirmed for exactly that 17 data. The guard script itself runs in
/// <see cref="PostgresUpgradeGuardTests"/>, the guide's dump and restore and the confirm run in
/// <see cref="PostgresUpgradeTests"/>, and compose reads the file in <see cref="PostgresOverlayComposeTests"/>.
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

    /// <summary>The guard's command as written in the compose file: the YAML block scalar under <c>command: - |</c>,
    /// de-indented, still with compose's <c>$$</c> escapes.</summary>
    internal static string GuardScriptRaw()
    {
        var guard = Service("db-upgrade-guard");
        const string head = "    command:\n      - |\n";
        var at = guard.IndexOf(head, StringComparison.Ordinal);
        Assert.True(at >= 0, "the guard's command is not a block scalar");
        var lines = new List<string>();
        foreach (var line in guard[(at + head.Length)..].Split('\n'))
        {
            if (line.Length > 0 && !line.StartsWith("        ", StringComparison.Ordinal)) break;
            lines.Add(line.Length == 0 ? "" : line[8..]);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>The guard's script as the container's shell receives it: compose turns every <c>$$</c> into <c>$</c>.
    /// The file must not contain a single <c>$</c>, which compose would interpolate from the host's environment.</summary>
    internal static string GuardScript()
    {
        var raw = GuardScriptRaw();
        Assert.DoesNotContain("$", raw.Replace("$$", ""));
        return raw.Replace("$$", "$");
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
    public void The_health_check_asks_over_tcp_so_the_init_server_never_counts_as_ready()
    {
        Assert.Contains("pg_isready -h 127.0.0.1 -U ${WHISKERS_DB_USER:-whiskers} -d ${WHISKERS_DB_NAME:-whiskers}", Service("db"));
    }

    [Fact]
    public void The_database_waits_for_the_upgrade_guard_and_the_app_for_a_healthy_database()
    {
        Assert.Matches(@"depends_on:\n      db-upgrade-guard:\n        condition: service_completed_successfully", Service("db"));
        Assert.Matches(@"depends_on:\n      db:\n        condition: service_healthy", Service("serverwatch"));
    }

    [Fact]
    public void The_guard_reads_the_17_volume_read_only_and_may_write_its_marker_into_the_18_volume()
    {
        var guard = Service("db-upgrade-guard");
        Assert.Contains("- whiskers-db-data:/old:ro\n", guard);
        Assert.Contains("- whiskers-db18-data:/new\n", guard);
        Assert.Contains("restart: \"no\"", guard);
        Assert.Contains("image: postgres:18-alpine", guard);
        Assert.Contains("entrypoint: [\"/bin/sh\", \"-c\"]", guard);
    }

    [Fact]
    public void The_guard_checks_the_restore_with_the_credentials_of_the_database_service()
    {
        var guard = Service("db-upgrade-guard");
        Assert.Contains("- POSTGRES_DB=${WHISKERS_DB_NAME:-whiskers}\n", guard);
        Assert.Contains("- POSTGRES_USER=${WHISKERS_DB_USER:-whiskers}\n", guard);
        Assert.Contains("- PGPASSWORD=${WHISKERS_DB_PASSWORD:?set WHISKERS_DB_PASSWORD in your .env}\n", guard);
    }

    [Fact]
    public void The_guard_script_keys_on_the_17_clusters_fingerprint_and_the_confirmed_restore()
    {
        var script = GuardScript();
        Assert.Contains("find /old -maxdepth 2 -name PG_VERSION -type f", script);
        Assert.Contains("sha256sum \"$cluster/global/pg_control\"", script);
        Assert.Contains("marker=/new/.whiskers-restored-from-17", script);
        Assert.Contains("\"${WHISKERS_CONFIRM_RESTORE:-}\" = 1", script);
        Assert.Contains("psql -h db -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\"", script);
        Assert.Contains("\"__EFMigrationsHistory\"", script);
        Assert.Contains("docs/operations/postgres-18-upgrade.md", script);
        Assert.Contains("docker volume ls", script);
    }

    [Fact]
    public void Both_volumes_are_declared_so_the_17_volume_is_kept_and_never_written()
    {
        var volumes = Overlay[Overlay.IndexOf("\nvolumes:\n", StringComparison.Ordinal)..];
        Assert.Matches(@"(?m)^  whiskers-db-data:$", volumes);
        Assert.Matches(@"(?m)^  whiskers-db18-data:$", volumes);
        // The 17 volume is mounted by the guard only, read-only.
        Assert.Single(Regex.Matches(Overlay, @"whiskers-db-data:/"));
        Assert.Contains("whiskers-db-data:/old:ro", Overlay);
    }

    [Fact]
    public void The_upgrade_guide_holds_the_checked_restore_and_the_confirm_run()
    {
        var guide = DeploymentScripts.ReadRepoFile("docs", "operations", "postgres-18-upgrade.md");
        Assert.Contains("c17 exec -T db sh -c 'pg_dump -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\" --no-owner --no-privileges'", guide);
        Assert.Contains("grep -q 'PostgreSQL database dump complete' whiskers-pg17.sql", guide);
        Assert.Contains("-v ON_ERROR_STOP=1 --single-transaction -q' < whiskers-pg17.sql", guide);
        Assert.Contains("c run --rm --no-deps -e WHISKERS_CONFIRM_RESTORE=1 db-upgrade-guard", guide);
        Assert.Contains("umask 077", guide);
        Assert.Contains("c rm -f db-upgrade-guard", guide);
        Assert.DoesNotContain("$C", guide);   // shell functions, not word-split variables (zsh)
    }
}
