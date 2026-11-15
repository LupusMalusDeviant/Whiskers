using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Backup;
using Whiskers.Services.Persistence;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>VolumeBackupService against a scripted host (review 1.0.0, Data): unique archive names, no partial archive
/// after a failed backup, and no record removed when its file was not.</summary>
public sealed class VolumeBackupServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sw-volbackup-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;

    public VolumeBackupServiceTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        TestSqlite.ClearPool(_dbPath);
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* temp file */ }
    }

    private IServiceScopeFactory Scopes => _sp.GetRequiredService<IServiceScopeFactory>();

    private sealed class ScriptedHost(Func<string, CommandResult> answer) : IHostCommandExecutor
    {
        public List<string> Commands { get; } = [];

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            lock (Commands) Commands.Add(command);
            return Task.FromResult(answer(command));
        }
    }

    private static CommandResult Ok(string output = "") => new() { ExitCode = 0, Output = output };

    [Fact]
    public async Task Two_backups_of_one_volume_in_the_same_second_get_archives_of_their_own()
    {
        var host = new ScriptedHost(c => c.StartsWith("stat", StringComparison.Ordinal) ? Ok("10") : Ok());
        var service = new VolumeBackupService(host, Scopes, NullLogger<VolumeBackupService>.Instance, new DataPathOptions("/app/data"));

        // Retried until both fall into one second (the file name has a resolution of one second).
        VolumeBackupEntity first, second;
        do
        {
            first = await service.BackupVolumeAsync("pgdata", "db", "local");
            second = await service.BackupVolumeAsync("pgdata", "db", "local");
        } while (first.CreatedAt.Second != second.CreatedAt.Second);

        Assert.NotEqual(first.BackupId, second.BackupId);
        Assert.True(first.FileName != second.FileName,
            $"Two recorded backups ({first.BackupId}, {second.BackupId}) share the one archive {first.FileName}: the second " +
            "overwrote the first, and deleting either removes the other's file.");
    }

    [Fact]
    public async Task A_volume_backup_whose_file_could_not_be_removed_stays_on_record()
    {
        var unreachable = false;
        var host = new ScriptedHost(c =>
            unreachable ? new CommandResult { ExitCode = -1, Error = "ssh: connect to host timed out" }
            : c.StartsWith("stat", StringComparison.Ordinal) ? Ok("10") : Ok());
        var service = new VolumeBackupService(host, Scopes, NullLogger<VolumeBackupService>.Instance, new DataPathOptions("/app/data"));
        var backup = await service.BackupVolumeAsync("pgdata", "db", "local");

        unreachable = true;                                     // the host does not answer the rm
        var thrown = await Record.ExceptionAsync(() => service.DeleteBackupAsync(backup.BackupId));

        var left = await service.ListBackupsAsync("local", "pgdata");
        Assert.True(thrown is not null || left.Any(b => b.BackupId == backup.BackupId),
            $"The rm of {backup.FileName} failed (exit -1), yet the delete reported success and the record is gone: the " +
            "archive stays on the host, listed and pruned by nothing.");
    }

    [Fact]
    public async Task A_failed_volume_backup_leaves_no_partial_archive_behind()
    {
        var host = new ScriptedHost(c => c.StartsWith("docker run", StringComparison.Ordinal)
            ? new CommandResult { ExitCode = -1, Error = "Command timed out after 600s — its effect is unknown" }
            : Ok());
        var service = new VolumeBackupService(host, Scopes, NullLogger<VolumeBackupService>.Instance, new DataPathOptions("/app/data"));

        await Assert.ThrowsAnyAsync<Exception>(() => service.BackupVolumeAsync("pgdata", "db", "local"));

        Assert.Empty(await service.ListBackupsAsync("local", "pgdata"));   // no record: nothing will ever prune the file
        var tar = host.Commands.Single(c => c.StartsWith("docker run", StringComparison.Ordinal));
        Assert.True(host.Commands.Any(c => c.StartsWith("rm ", StringComparison.Ordinal)),
            $"The backup command failed after it may have written its archive ({tar[tar.IndexOf("tar czf", StringComparison.Ordinal)..]}); " +
            "no record was kept and no command removed the partial file. Commands sent: " + host.Commands.Count);
    }

    [Fact]
    public async Task The_cleanup_of_a_failed_backup_removes_only_its_own_archive_in_the_backup_directory()
    {
        var host = new ScriptedHost(c => c.StartsWith("docker run", StringComparison.Ordinal)
            ? new CommandResult { ExitCode = 1, Error = "no space left on device" }
            : Ok());
        var service = new VolumeBackupService(host, Scopes, NullLogger<VolumeBackupService>.Instance, new DataPathOptions("/app/data"));

        await Assert.ThrowsAnyAsync<Exception>(() => service.BackupVolumeAsync("pgdata", "db", "local"));

        var tar = host.Commands.Single(c => c.StartsWith("docker run", StringComparison.Ordinal));
        var archive = tar[(tar.IndexOf("'/backup/", StringComparison.Ordinal) + "'/backup/".Length)..];
        archive = archive[..archive.IndexOf('\'')];
        Assert.Equal($"rm -f '/app/data/backups/{archive}'", host.Commands.Single(c => c.StartsWith("rm ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_successful_delete_removes_the_file_and_then_the_record()
    {
        var host = new ScriptedHost(c => c.StartsWith("stat", StringComparison.Ordinal) ? Ok("10") : Ok());
        var service = new VolumeBackupService(host, Scopes, NullLogger<VolumeBackupService>.Instance, new DataPathOptions("/app/data"));
        var backup = await service.BackupVolumeAsync("pgdata", "db", "local");

        await service.DeleteBackupAsync(backup.BackupId);

        Assert.Contains($"rm -f '/app/data/backups/{backup.FileName}'", host.Commands);
        Assert.Empty(await service.ListBackupsAsync("local", "pgdata"));
    }

    [Fact]
    public void Archive_names_stay_valid_and_differ_within_one_second()
    {
        var now = new DateTime(2026, 11, 15, 3, 0, 0, DateTimeKind.Utc);
        var a = VolumeBackupService.NewFileName("pgdata", now);
        var b = VolumeBackupService.NewFileName("pgdata", now);
        Assert.NotEqual(a, b);
        Assert.Matches(@"^pgdata_20261115_030000_[0-9a-f]{8}\.tar\.gz$", a);
    }
}
