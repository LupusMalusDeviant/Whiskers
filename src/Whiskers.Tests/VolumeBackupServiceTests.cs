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
    public async Task A_cleanup_that_throws_does_not_hide_the_backup_s_own_error()
    {
        var host = new ScriptedHost(c =>
            c.StartsWith("docker run", StringComparison.Ordinal) ? new CommandResult { ExitCode = 1, Error = "volume not found" }
            : c.StartsWith("rm ", StringComparison.Ordinal) ? throw new TimeoutException("ssh session lost")
            : Ok());
        var service = new VolumeBackupService(host, Scopes, NullLogger<VolumeBackupService>.Instance, new DataPathOptions("/app/data"));

        var thrown = await Record.ExceptionAsync(() => service.BackupVolumeAsync("pgdata", "db", "local"));

        Assert.NotNull(thrown);
        Assert.IsNotType<TimeoutException>(thrown);
        Assert.Contains("volume not found", thrown.Message);
        Assert.Empty(await service.ListBackupsAsync("local", "pgdata"));
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

    // Contract 0023 (batch 1, safe points): the token is observed before each step, never inside one.

    [Fact]
    public async Task A_backup_stopped_before_it_starts_sends_nothing_and_records_nothing()
    {
        var host = new ScriptedHost(c => c.StartsWith("stat", StringComparison.Ordinal) ? Ok("10") : Ok());
        var service = new VolumeBackupService(host, Scopes, NullLogger<VolumeBackupService>.Instance, new DataPathOptions("/app/data"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.BackupVolumeAsync("pgdata", "db", "local", ct: new CancellationToken(canceled: true)));

        Assert.Empty(host.Commands);
        Assert.Empty(await service.ListBackupsAsync("local", "pgdata"));
    }

    [Fact]
    public async Task A_backup_stopped_while_its_archive_is_written_runs_to_its_end_and_is_recorded()
    {
        using var stop = new CancellationTokenSource();
        var host = new ScriptedHost(c =>
        {
            if (c.StartsWith("docker run", StringComparison.Ordinal)) stop.Cancel();
            return c.StartsWith("stat", StringComparison.Ordinal) ? Ok("10") : Ok();
        });
        var service = new VolumeBackupService(host, Scopes, NullLogger<VolumeBackupService>.Instance, new DataPathOptions("/app/data"));

        var backup = await service.BackupVolumeAsync("pgdata", "db", "local", ct: stop.Token);

        Assert.True(stop.IsCancellationRequested);
        Assert.DoesNotContain(host.Commands, c => c.StartsWith("rm -f", StringComparison.Ordinal));
        Assert.Equal(backup.BackupId, Assert.Single(await service.ListBackupsAsync("local", "pgdata")).BackupId);
    }

    [Fact]
    public async Task A_restore_stopped_during_its_safety_backup_ends_before_the_wipe_and_leaves_the_volume_untouched()
    {
        var host = new ScriptedHost(c => c.StartsWith("stat", StringComparison.Ordinal) ? Ok("10") : Ok());
        var service = new VolumeBackupService(host, Scopes, NullLogger<VolumeBackupService>.Instance, new DataPathOptions("/app/data"));
        var backup = await service.BackupVolumeAsync("pgdata", "db", "local");
        host.Commands.Clear();

        using var stop = new CancellationTokenSource();
        var stopping = new ScriptedHost(c =>
        {
            if (c.Contains("tar czf", StringComparison.Ordinal)) stop.Cancel();   // the safety backup's archive
            return c.StartsWith("stat", StringComparison.Ordinal) ? Ok("10") : Ok();
        });
        var restoring = new VolumeBackupService(stopping, Scopes, NullLogger<VolumeBackupService>.Instance, new DataPathOptions("/app/data"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restoring.RestoreVolumeAsync(backup.BackupId, ct: stop.Token));

        Assert.Contains(stopping.Commands, c => c.Contains("tar czf", StringComparison.Ordinal));
        Assert.DoesNotContain(stopping.Commands, c => c.Contains("find /data", StringComparison.Ordinal));
        // The safety backup ran to its end and is on record: the volume can still be brought back.
        Assert.Equal(2, (await service.ListBackupsAsync("local", "pgdata")).Count);
    }

    [Fact]
    public async Task A_restore_stopped_during_the_wipe_and_extract_runs_to_its_end()
    {
        using var stop = new CancellationTokenSource();
        var host = new ScriptedHost(c =>
        {
            if (c.Contains("find /data", StringComparison.Ordinal)) stop.Cancel();
            return c.StartsWith("stat", StringComparison.Ordinal) ? Ok("10") : Ok();
        });
        var service = new VolumeBackupService(host, Scopes, NullLogger<VolumeBackupService>.Instance, new DataPathOptions("/app/data"));
        var backup = await service.BackupVolumeAsync("pgdata", "db", "local");

        await service.RestoreVolumeAsync(backup.BackupId, ct: stop.Token);

        Assert.True(stop.IsCancellationRequested);
        Assert.Contains(host.Commands, c => c.Contains("find /data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_delete_stopped_before_the_rm_keeps_file_and_record_and_one_stopped_after_it_removes_both()
    {
        var host = new ScriptedHost(c => c.StartsWith("stat", StringComparison.Ordinal) ? Ok("10") : Ok());
        var service = new VolumeBackupService(host, Scopes, NullLogger<VolumeBackupService>.Instance, new DataPathOptions("/app/data"));
        var backup = await service.BackupVolumeAsync("pgdata", "db", "local");
        host.Commands.Clear();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DeleteBackupAsync(backup.BackupId, new CancellationToken(canceled: true)));
        Assert.Empty(host.Commands);
        Assert.Single(await service.ListBackupsAsync("local", "pgdata"));

        using var stop = new CancellationTokenSource();
        var stopping = new ScriptedHost(c =>
        {
            if (c.StartsWith("rm -f", StringComparison.Ordinal)) stop.Cancel();
            return Ok();
        });
        var deleting = new VolumeBackupService(stopping, Scopes, NullLogger<VolumeBackupService>.Instance, new DataPathOptions("/app/data"));
        await deleting.DeleteBackupAsync(backup.BackupId, stop.Token);
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
