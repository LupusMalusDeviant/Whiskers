using System.Diagnostics;
using Docker.DotNet.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Database;
using Whiskers.Services.Docker.Operations;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>CodeQL #23–#26 (<c>cs/cleartext-storage-of-sensitive-information</c>, docs/security/codeql-triage-2026-11.md):
/// a database password never stands in a host command — not in its text, which is logged and audited, and not in an
/// argv the host's or the container's process list shows — whatever its length and characters. The command names the
/// client's variable (<c>docker exec -e PGPASSWORD</c>); the executor delivers the value beside it: the environment of
/// <c>nsenter</c> (local), the environment of the helper container (mTLS), standard input read by the remote shell
/// (SSH).</summary>
public sealed class HostCommandSecretsTests
{
    /// <summary>Every password contains this; no command text contains it otherwise.</summary>
    private const string Marker = "Zq";

    public static TheoryData<DatabaseType, string> Passwords()
    {
        var data = new TheoryData<DatabaseType, string>();
        foreach (var type in new[] { DatabaseType.PostgreSQL, DatabaseType.MySQL, DatabaseType.Redis, DatabaseType.Neo4j, DatabaseType.MongoDB })
            foreach (var password in new[] { "Zq9", "a Zq", "Zq'9", "Zq\"9", "Zq9 horse's \"battery\" $HOME `id`" })
                data.Add(type, password);
        return data;
    }

    [Theory]
    [MemberData(nameof(Passwords))]
    public async Task A_database_password_is_passed_beside_the_command_never_in_it(DatabaseType type, string password)
    {
        var host = new RecordingHost();
        var db = new DatabaseService(host, NullLogger<DatabaseService>.Instance);
        var creds = new DatabaseCredentials { User = "admin", Password = password, Database = "app" };

        await db.GetDatabasesAsync("c0ffee", type, creds, "local");
        await db.ExecuteQueryAsync("c0ffee", "select 1", type, creds, "local");
        await db.GetTablesAsync("c0ffee", "app", type, creds, "local");
        await db.GetSchemaAsync("c0ffee", "app", "users", type, creds, "local");
        if (type is DatabaseType.PostgreSQL or DatabaseType.MySQL)
            await db.BackupDatabaseAsync("c0ffee", "app", type, creds, "local", containerName: "db");

        var execs = host.Calls.Where(c => c.Command.StartsWith("docker exec ", StringComparison.Ordinal)
                                          && !c.Command.Contains("'rm'", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(execs);
        var variable = DatabaseService.PasswordVariable(type);
        foreach (var (command, secrets) in host.Calls)
        {
            // Not in the text the executor logs and the audit keeps, not as an argv element anywhere.
            Assert.DoesNotContain(Marker, command);
            Assert.DoesNotContain("'-p'", command);
        }
        foreach (var (command, secrets) in execs)
        {
            if (variable is null)
            {
                // MongoDB: Whiskers does not authenticate to it — the password is sent nowhere.
                Assert.Empty(secrets);
                continue;
            }
            Assert.StartsWith($"docker exec -e {variable} -- 'c0ffee' ", command);
            var secret = Assert.Single(secrets);
            Assert.Equal(variable, secret.Key);
            Assert.Equal(password, secret.Value);
        }
        // Outside the call the secret is gone.
        Assert.Empty(HostCommandSecrets.Current);
    }

    [Fact]
    public async Task Without_a_password_no_variable_is_named_or_passed()
    {
        var host = new RecordingHost();
        var db = new DatabaseService(host, NullLogger<DatabaseService>.Instance);

        await db.GetDatabasesAsync("c0ffee", DatabaseType.PostgreSQL, new DatabaseCredentials { User = "postgres" }, "local");

        var (command, secrets) = Assert.Single(host.Calls);
        Assert.StartsWith("docker exec -- 'c0ffee' 'psql' ", command);
        Assert.Empty(secrets);
    }

    [Fact]
    public async Task The_mtls_helper_gets_the_secret_as_its_environment_and_not_in_its_command()
    {
        var docker = new CapturingShellDocker();
        var shell = new HostShellOperations(_ => Task.FromResult<IHostShellDocker>(docker),
            new MemoryCache(new MemoryCacheOptions()));

        using (HostCommandSecrets.Provide([new("PGPASSWORD", "a Zq'\"")]))
            await shell.RunHostShellAsync("docker exec -e PGPASSWORD -- 'c0ffee' 'psql'", "srv", TimeSpan.FromSeconds(10));
        await shell.RunHostShellAsync("uptime", "srv", TimeSpan.FromSeconds(10));

        Assert.Equal(2, docker.Created.Count);
        Assert.Equal(["PGPASSWORD=a Zq'\""], docker.Created[0].Env);
        Assert.DoesNotContain(docker.Created[0].Cmd, a => a.Contains(Marker, StringComparison.Ordinal));
        Assert.Empty(docker.Created[1].Env);   // a later command gets nothing
    }

    [SkippableFact]
    public async Task Over_ssh_the_remote_shell_reads_each_secret_from_standard_input()
    {
        Skip.IfNot(CanRun("sh"), "no POSIX shell on this machine");
        KeyValuePair<string, string>[] secrets = [new("PGPASSWORD", "a Zq'\" $HOME `id`"), new("MYSQL_PWD", "x")];
        // What the remote login shell runs: the prefix the executor puts in front, then the command.
        var command = HostCommandSecrets.SshPrefix(secrets) + "printf '%s|%s' \"$PGPASSWORD\" \"$MYSQL_PWD\"";
        Assert.DoesNotContain(Marker, command);

        var result = await Executor().RunProcessAsync("sh", ["-c", command], TimeSpan.FromSeconds(20), default,
            standardInput: HostCommandSecrets.StandardInput(secrets));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("a Zq'\" $HOME `id`|x", result.Output);
    }

    [SkippableFact]
    public async Task Over_ssh_a_missing_value_stops_the_command_before_it_runs()
    {
        Skip.IfNot(CanRun("sh"), "no POSIX shell on this machine");
        KeyValuePair<string, string>[] secrets = [new("PGPASSWORD", "x")];

        var result = await Executor().RunProcessAsync("sh", ["-c", HostCommandSecrets.SshPrefix(secrets) + "echo ran"],
            TimeSpan.FromSeconds(20), default, standardInput: "");

        Assert.Equal(HostCommandSecrets.ReadFailedExitCode, result.ExitCode);
        Assert.DoesNotContain("ran", result.Output);
    }

    [SkippableFact]
    public async Task Locally_the_secret_is_in_the_environment_of_the_process_not_its_arguments()
    {
        Skip.IfNot(CanRun("sh"), "no POSIX shell on this machine");

        var result = await Executor().RunProcessAsync("sh", ["-c", "printf '%s' \"$PGPASSWORD\""], TimeSpan.FromSeconds(20),
            default, environment: [new("PGPASSWORD", "a Zq'\"")]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("a Zq'\"", result.Output);
    }

    [Theory]
    [InlineData("pg-password", "x")]
    [InlineData("1PW", "x")]
    [InlineData("pgpassword", "x")]
    [InlineData("PGPASSWORD", "a\nb")]
    [InlineData("PGPASSWORD", "a\rb")]
    [InlineData("PGPASSWORD", "a\0b")]
    public void A_name_or_a_value_that_cannot_travel_is_refused(string name, string value)
    {
        Assert.Throws<ArgumentException>(() => HostCommandSecrets.Provide([new(name, value)]));
        Assert.Empty(HostCommandSecrets.Current);
    }

    [Fact]
    public void A_scope_ends_with_its_handle_and_restores_the_outer_one()
    {
        using (HostCommandSecrets.Provide([new("A", "1")]))
        {
            using (HostCommandSecrets.Provide([new("B", "2")]))
                Assert.Equal("B", Assert.Single(HostCommandSecrets.Current).Key);
            Assert.Equal("A", Assert.Single(HostCommandSecrets.Current).Key);
        }
        Assert.Empty(HostCommandSecrets.Current);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static HostCommandExecutor Executor() => new(null!, null!, NullLogger<HostCommandExecutor>.Instance);

    private static bool CanRun(string program)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(program, ["-c", "true"])
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p is null) return false;
            p.WaitForExit(10_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Records each command and the secrets in force when it was handed over.</summary>
    private sealed class RecordingHost : IHostCommandExecutor
    {
        public List<(string Command, IReadOnlyList<KeyValuePair<string, string>> Secrets)> Calls { get; } = [];

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            lock (Calls) Calls.Add((command, HostCommandSecrets.Current.ToList()));
            return Task.FromResult(new CommandResult { ExitCode = 0, Output = "" });
        }
    }

    /// <summary>A helper-container backend that records what each helper is created with and runs nothing.</summary>
    private sealed class CapturingShellDocker : IHostShellDocker
    {
        public List<CreateContainerParameters> Created { get; } = [];

        public Task<bool> HasImageAsync(string image, CancellationToken ct) => Task.FromResult(true);
        public Task PullImageAsync(string repository, string tag, CancellationToken ct) => Task.CompletedTask;

        public Task<string> CreateAsync(CreateContainerParameters parameters, CancellationToken ct)
        {
            lock (Created) Created.Add(parameters);
            return Task.FromResult($"helper{Created.Count}");
        }

        public Task StartAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task<long> WaitAsync(string id, CancellationToken ct) => Task.FromResult(0L);
        public Task<(string Output, string Error)> ReadLogsAsync(string id, CancellationToken ct) => Task.FromResult(("", ""));
        public Task KillAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<HostShellHelper>> ListHelpersAsync(string label, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<HostShellHelper>>([]);
    }
}
