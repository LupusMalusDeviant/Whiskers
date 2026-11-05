using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.EndpointChecks;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>R1-57: the doubles shared by <see cref="EndpointCheckTests"/> and <see cref="EndpointCheckGatingTests"/>.</summary>
internal static class EndpointCheckTestDoubles
{
    public const string Admin = "admin@example.com";
    public static readonly string AdminId = PrincipalIds.User(Admin);

    /// <summary>A server reached over SSH: it has a shell transport.</summary>
    public static Whiskers.Models.ServerConfig SshServer(string id = "web") =>
        new() { Id = id, Name = id, ConnectionType = ConnectionType.SSH, SshHost = "198.51.100.7" };

    public static EndpointCheckDraft Draft(EndpointCheckKind kind, string target, int? port = null,
        string serverId = "web", int? expectedStatus = null, int? tlsWarnDays = null, int interval = 60, int timeout = 5,
        IReadOnlyList<OutboundExceptionEntry>? exceptions = null, string? id = null, string name = "check") =>
        new(id, serverId, name, kind, target, port, expectedStatus, tlsWarnDays, interval, timeout, exceptions ?? [], true);

    public static EndpointCheckDefinition Definition(EndpointCheckKind kind, string target, int? port = null,
        string serverId = "web", int? expectedStatus = null, int? tlsWarnDays = null, int interval = 60, int timeout = 5,
        IReadOnlyList<OutboundExceptionEntry>? exceptions = null, string id = "c1", string? ownerId = null,
        DateTime? lastRunUtc = null, bool enabled = true) =>
        new(id, serverId, "check " + id, kind, target, port, expectedStatus, tlsWarnDays, interval, timeout, exceptions ?? [],
            enabled, null, ownerId ?? AdminId, DateTime.UnixEpoch, DateTime.UnixEpoch, lastRunUtc, null);

    /// <summary>getent's answer listing <paramref name="addresses"/> (each once as STREAM, DGRAM and RAW, as glibc does).</summary>
    public static string Getent(params string[] addresses) =>
        string.Concat(addresses.Select(a => $"{a,-15} STREAM example.com\n{a,-15} DGRAM\n{a,-15} RAW\n")) + "__rc=0\n";

    /// <summary>A file-backed SQLite <see cref="MetricsDbContext"/> behind a scope factory, as the store uses it.</summary>
    public sealed class Db : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"endpoint-checks-{Guid.NewGuid():N}.db");
        public ServiceProvider Services { get; }

        public Db()
        {
            var services = new ServiceCollection();
            services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_path}"));
            Services = services.BuildServiceProvider();
            using var scope = Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
        }

        public IServiceScopeFactory Scopes => Services.GetRequiredService<IServiceScopeFactory>();

        public int ResultCount()
        {
            using var scope = Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<MetricsDbContext>().EndpointCheckResults.Count();
        }

        public void Dispose()
        {
            Services.Dispose();
            SqliteConnection.ClearAllPools();
            try { File.Delete(_path); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// The managed host. It answers by <see cref="Answer"/>, records each call with its timeout and the decision it
    /// ran in, and — like the host's shell would — applies the template's own <c>head -c</c> bound when the command
    /// carries it, so a template that lost its bound is visible in the output.
    /// </summary>
    public sealed class Host : IHostCommandExecutor
    {
        public sealed record Call(string Command, TimeSpan? Timeout, string? Capability, string? OperationId);

        public ConcurrentQueue<Call> Calls { get; } = new();
        public Func<string, TimeSpan?, CancellationToken, Task<CommandResult>> Answer { get; set; } =
            (_, _, _) => Task.FromResult(new CommandResult { ExitCode = 0, Output = "__rc=0\n" });

        /// <summary>Answers a resolve with <paramref name="resolve"/> and every probe with <paramref name="probe"/>.</summary>
        public Host Answering(string resolve, string probe)
        {
            Answer = (command, _, _) => Task.FromResult(new CommandResult
            {
                ExitCode = 0,
                Output = command.Contains("getent ahosts", StringComparison.Ordinal) ? resolve : probe,
            });
            return this;
        }

        public List<string> Probes => Calls.Select(c => c.Command).Where(c => !c.Contains("getent", StringComparison.Ordinal)).ToList();

        public async Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            Calls.Enqueue(new Call(command, timeout, MutationScope.Current?.CapabilityId, MutationScope.Current?.OperationId));
            var result = await Answer(command, timeout, ct);
            var cap = $" | head -c {EndpointCheckCommands.OutputCap}";
            if (command.EndsWith(cap, StringComparison.Ordinal) && result.Output.Length > EndpointCheckCommands.OutputCap)
                result.Output = result.Output[..EndpointCheckCommands.OutputCap];
            return result;
        }
    }

    /// <summary>A clock the test moves by hand.</summary>
    public sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>Waits for the runs a cycle started (the monitor does not await them itself).</summary>
    public static async Task SettleAsync(EndpointCheckMonitor monitor)
    {
        var field = typeof(EndpointCheckMonitor).GetField("_inFlight", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var inFlight = (ConcurrentDictionary<string, Task>)field.GetValue(monitor)!;
        await Task.WhenAll(inFlight.Values).WaitAsync(TimeSpan.FromSeconds(30));
    }
}
