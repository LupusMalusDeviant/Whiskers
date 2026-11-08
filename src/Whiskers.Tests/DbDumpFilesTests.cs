using Whiskers.Services.Database;
using Whiskers.Services.Scheduler;
using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>INV-19 review rounds 2 and 3 (m3, M1): a DB-backup retention prunes exactly one container's dumps of exactly
/// one database. A same-named database in another container, a database whose name only starts the same, and dumps in
/// the format before the container was part of the name (legacy) are never touched; two dumps in the same second get
/// two names.</summary>
public class DbDumpFilesTests
{
    private const string A = "web-db-1";
    private const string B = "shop_db.2";

    private static string Dump(string container, string db, string stamp, string suffix = "0a1b2c3d", bool gz = false) =>
        $"{container}@{db}_{stamp}_{suffix}.sql{(gz ? ".gz" : "")}";

    [Theory]
    [InlineData("web-db-1@app_20261107_010101000_0a1b2c3d.sql", A, "app", true)]
    [InlineData("web-db-1@app_20261107_010101000_0a1b2c3d.sql.gz", A, "app", true)]
    [InlineData("web-db-1@app_db_20261107_010101000_0a1b2c3d.sql", A, "app", false)]   // another database
    [InlineData("web-db-1@app_db_20261107_010101000_0a1b2c3d.sql", A, "app_db", true)]
    [InlineData("shop_db.2@app_20261107_010101000_0a1b2c3d.sql", A, "app", false)]   // another container
    [InlineData("web-db-1@app_20261107_010101_0a1b2c3d.sql", A, "app", false)]     // no milliseconds
    [InlineData("web-db-1@app_20261107_010101000.sql", A, "app", false)]           // no suffix
    [InlineData("web-db-1@app_20261107_010101000_0A1B2C3D.sql", A, "app", false)]  // suffix is lower-case hex
    [InlineData(" web-db-1@app_20261107_010101000_0a1b2c3d.sql", A, "app", false)] // never trimmed
    [InlineData("web-db-1@app_20261107_010101000_0a1b2c3d.sql ", A, "app", false)]
    [InlineData("web-db-1@app_20261107_010101000_0a1b2c3d.sql\r", A, "app", false)]
    [InlineData("app_20261107_010101.sql", A, "app", false)]                        // legacy: no container
    [InlineData("web-db-1@app_20261107_010101000_0a1b2c3d.sql", "-web", "app", false)]
    [InlineData("web-db-1@app_20261107_010101000_0a1b2c3d.sql", A, "a/pp", false)]
    public void A_dump_belongs_only_to_its_container_and_database(string file, string container, string db, bool expected) =>
        Assert.Equal(expected, DbDumpFiles.IsDumpOf(file, container, db));

    [Theory]
    [InlineData("app_20261107_010101.sql", "app", true)]
    [InlineData("app_20261107_010101.sql.gz", "app", true)]
    [InlineData("app_db_20261107_010101.sql", "app", false)]
    [InlineData("web-db-1@app_20261107_010101000_0a1b2c3d.sql", "app", false)]
    [InlineData(" app_20261107_010101.sql", "app", false)]
    public void A_legacy_dump_is_recognised_by_its_database_only(string file, string db, bool expected) =>
        Assert.Equal(expected, DbDumpFiles.IsLegacyDumpOf(file, db));

    [Fact]
    public void Retention_of_one_container_never_touches_another_containers_same_named_database()
    {
        var listing = string.Join('\n',
            Dump(B, "postgres", "20261101_010101000"),
            Dump(A, "postgres", "20261105_010101000"),
            Dump(B, "postgres", "20261102_010101000"),
            Dump(A, "postgres", "20261107_010101000", gz: true),
            Dump(A, "postgres", "20261106_010101000"),
            Dump(B, "postgres", "20261103_010101000"),
            "");

        Assert.Equal([Dump(A, "postgres", "20261105_010101000")], DbDumpFiles.ToPrune(listing, A, "postgres", keep: 2));
        Assert.Equal([Dump(B, "postgres", "20261101_010101000")], DbDumpFiles.ToPrune(listing, B, "postgres", keep: 2));
        Assert.Empty(DbDumpFiles.ToPrune(listing, "other", "postgres", keep: 0));
    }

    [Fact]
    public void Retention_for_app_never_touches_app_db_or_legacy_dumps()
    {
        var listing = string.Join('\n',
            Dump(A, "app_db", "20261101_010101000"),
            Dump(A, "app", "20261102_010101000"),
            Dump(A, "app", "20261103_010101000"),
            "app_20261001_010101.sql",           // legacy dumps of the same database: never pruned
            "app_20261002_010101.sql.gz",
            "app_db_20261003_010101.sql",
            "notes.txt");

        var pruned = DbDumpFiles.ToPrune(listing, A, "app", keep: 0);

        Assert.Equal([Dump(A, "app", "20261103_010101000"), Dump(A, "app", "20261102_010101000")], pruned);
        Assert.All(pruned, f => Assert.True(DbDumpFiles.IsDumpOf(f, A, "app")));
        Assert.DoesNotContain(pruned, f => DbDumpFiles.IsLegacyDumpOf(f, "app"));
        Assert.Empty(DbDumpFiles.ToPrune("app_20261001_010101.sql\napp_20261002_010101.sql\n", A, "app", keep: 0));
    }

    [Fact]
    public void Two_dumps_in_the_same_second_and_millisecond_get_two_names_and_both_are_kept()
    {
        var at = new DateTime(2026, 11, 7, 1, 2, 3, 456, DateTimeKind.Utc);
        var first = DbDumpFiles.NewFileName(A, "app", at);
        var second = DbDumpFiles.NewFileName(A, "app", at);

        Assert.NotEqual(first, second);
        Assert.StartsWith("web-db-1@app_20261107_010203456_", first, StringComparison.Ordinal);
        Assert.True(DbDumpFiles.IsDumpOf(first, A, "app"));
        Assert.True(DbDumpFiles.IsDumpOf(second + ".gz", A, "app"));
        Assert.Empty(DbDumpFiles.ToPrune($"{first}\n{second}\n", A, "app", keep: 2));
    }

    [Fact]
    public void An_odd_file_name_cannot_pose_as_a_dump_or_count_twice()
    {
        var real = Dump(A, "app", "20261105_010101000");
        // ls -q prints a line feed in a name as '?', so "x\n<real>" arrives as "x?<real>"; a leading space stays.
        var listing = string.Join('\n', $"x?{real}", $" {real}", real, real, "");

        Assert.Equal([real], DbDumpFiles.DumpsOf(listing, A, "app"));
        Assert.Empty(DbDumpFiles.ToPrune(listing, A, "app", keep: 1));
    }

    [Theory]
    [InlineData("web-db-1", "0123456789abcdef", "web-db-1")]
    [InlineData("/web-db-1", "0123456789abcdef", "web-db-1")]
    [InlineData(null, "0123456789abcdef0123", "id-0123456789ab")]
    [InlineData("-rf", "0123456789abcdef", "id-0123456789ab")]
    public void The_container_key_is_the_name_else_the_prefixed_short_id(string? name, string id, string expected) =>
        Assert.Equal(expected, DbDumpFiles.ContainerKey(name, id));

    [Fact]
    public void A_long_name_is_keyed_by_a_hash_of_the_whole_name_that_survives_a_re_create()
    {
        // INV-19 review round 4 (m2): a re-created container keeps its name, not its id; two long names that only share
        // their start get two keys.
        var name = "my-very-long-compose-project-name-for-the-shop-" + new string('x', 30) + "-postgres-1";
        var key = DbDumpFiles.ContainerKey(name, "0123456789abcdef");

        Assert.Matches(@"^name-[0-9a-f]{16}\z", key);
        Assert.Equal(key, DbDumpFiles.ContainerKey(name, "fedcba9876543210"));
        Assert.NotEqual(key, DbDumpFiles.ContainerKey(name[..^1] + "2", "0123456789abcdef"));
        Assert.True(DbDumpFiles.IsDumpOf(DbDumpFiles.NewFileName(key, "app", DateTime.UtcNow), key, "app"));
    }

    [Fact]
    public void A_container_without_a_usable_name_or_id_is_refused()
    {
        Assert.Throws<UnsafeOperandException>(() => DbDumpFiles.ContainerKey("a b", "--x"));
        Assert.Throws<UnsafeOperandException>(() => DbDumpFiles.ContainerKey(null, "not-an-id-at-all"));
        Assert.Throws<UnsafeOperandException>(() => DbDumpFiles.NewFileName("web", "-app", DateTime.UtcNow));
        Assert.Throws<UnsafeOperandException>(() => DbDumpFiles.NewFileName("a@b", "app", DateTime.UtcNow));
    }

    [Fact]
    public void The_listing_shows_every_file_on_one_line_and_the_retention_removes_exactly_the_quoted_dumps()
    {
        Assert.Equal("ls -1Aq -- '/var/backups/db'", DbDumpFiles.ListingCommand("/var/backups/db"));
        Assert.Equal("true", TaskExecutor.RetentionCommandFor("/var/backups/db", []));
        var dump = Dump(A, "app", "20261105_010101000");
        Assert.Equal($"rm -f -- '/var/backups/db/{dump}'", TaskExecutor.RetentionCommandFor("/var/backups/db", [dump]));
    }
}
