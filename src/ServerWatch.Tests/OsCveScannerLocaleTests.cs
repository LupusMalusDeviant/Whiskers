using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Services.Cve;
using ServerWatch.Services.Server;

namespace ServerWatch.Tests;

public class OsCveScannerLocaleTests
{
    private sealed class CapturingExecutor : IHostCommandExecutor
    {
        public readonly List<string> Commands = new();
        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(new CommandResult { ExitCode = 0, Output = "", Error = "" });
        }
    }

    private sealed class StubMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    [Fact]
    public async Task AptCommands_ForceCLocale()
    {
        var exec = new CapturingExecutor();
        var scanner = new OsCveScanner(exec, new StubMonitor<CveMonitorSettings>(new CveMonitorSettings()),
            NullLogger<OsCveScanner>.Instance);

        await scanner.ScanAsync("local");

        // Every issued apt command must force the C locale, else the upgradable regex silently misses
        // on non-English hosts (the German "[aktualisierbar von: …]" bug → 0 findings).
        Assert.NotEmpty(exec.Commands);
        Assert.All(exec.Commands, c => Assert.StartsWith("LC_ALL=C.UTF-8 ", c));
    }
}
