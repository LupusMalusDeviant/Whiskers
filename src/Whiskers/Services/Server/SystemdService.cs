using System.Text.RegularExpressions;

namespace Whiskers.Services.Server;

public class SystemdUnit
{
    public string Name { get; set; } = "";
    public string LoadState { get; set; } = "";
    public string ActiveState { get; set; } = "";
    public string SubState { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; }
}

public class SystemdService : ISystemdService
{
    private readonly IHostCommandExecutor _executor;
    private readonly ILogger<SystemdService> _logger;

    // Safe service name: must start with a letter/digit (a leading '-' would be an option-injection into
    // systemctl), then letters, digits, dots, underscores, hyphens, @.
    private static readonly Regex SafeServiceNameRegex = new(@"^[a-zA-Z0-9][a-zA-Z0-9._@-]*$", RegexOptions.Compiled);

    public SystemdService(IHostCommandExecutor executor, ILogger<SystemdService> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    public async Task<List<SystemdUnit>> ListServicesAsync(string serverId)
    {
        // Fire both queries concurrently (running units vs. unit-file enabled state) — they're
        // independent, so we avoid a second sequential SSH round-trip. --plain produces whitespace-
        // separated columns: UNIT LOAD ACTIVE SUB DESCRIPTION...
        var unitsTask = _executor.ExecuteAsync(serverId,
            "systemctl list-units --type=service --no-pager --no-legend --plain 2>/dev/null");
        var filesTask = _executor.ExecuteAsync(serverId,
            "systemctl list-unit-files --type=service --no-pager --no-legend --plain 2>/dev/null");
        var result = await unitsTask;
        var enabledResult = await filesTask;

        var units = new List<SystemdUnit>();

        if (!result.Success)
        {
            _logger.LogWarning("systemctl list-units failed on {ServerId}: {Error}", serverId, result.Error);
            return units;
        }

        var enabledMap = ParseUnitFiles(enabledResult.Output);

        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var unit = ParseUnitLine(line);
            if (unit == null) continue;

            if (enabledMap.TryGetValue(unit.Name, out var enabledState))
                unit.Enabled = enabledState is "enabled" or "enabled-runtime" or "static";

            units.Add(unit);
        }

        return units;
    }

    public async Task<string> GetStatusAsync(string serverId, string serviceName)
    {
        ValidateServiceName(serviceName);
        var result = await _executor.ExecuteAsync(serverId, $"systemctl status {serviceName} --no-pager 2>&1");
        return result.Output + result.Error;
    }

    public async Task<string> GetJournalAsync(string serverId, string serviceName, int lines = 100)
    {
        ValidateServiceName(serviceName);

        if (lines <= 0) lines = 100;
        if (lines > 10000) lines = 10000; // Reasonable upper bound

        var result = await _executor.ExecuteAsync(serverId,
            $"journalctl -u {serviceName} -n {lines} --no-pager 2>&1",
            timeout: TimeSpan.FromSeconds(60));

        return result.Output + result.Error;
    }

    public async Task<CommandResult> StartAsync(string serverId, string serviceName)
    {
        ValidateServiceName(serviceName);
        _logger.LogInformation("Starting service '{ServiceName}' on {ServerId}", serviceName, serverId);
        return await _executor.ExecuteAsync(serverId, $"systemctl start {serviceName}");
    }

    public async Task<CommandResult> StopAsync(string serverId, string serviceName)
    {
        ValidateServiceName(serviceName);
        _logger.LogInformation("Stopping service '{ServiceName}' on {ServerId}", serviceName, serverId);
        return await _executor.ExecuteAsync(serverId, $"systemctl stop {serviceName}");
    }

    public async Task<CommandResult> RestartAsync(string serverId, string serviceName)
    {
        ValidateServiceName(serviceName);
        _logger.LogInformation("Restarting service '{ServiceName}' on {ServerId}", serviceName, serverId);
        return await _executor.ExecuteAsync(serverId, $"systemctl restart {serviceName}");
    }

    public async Task<CommandResult> EnableAsync(string serverId, string serviceName)
    {
        ValidateServiceName(serviceName);
        _logger.LogInformation("Enabling service '{ServiceName}' on {ServerId}", serviceName, serverId);
        return await _executor.ExecuteAsync(serverId, $"systemctl enable {serviceName}");
    }

    public async Task<CommandResult> DisableAsync(string serverId, string serviceName)
    {
        ValidateServiceName(serviceName);
        _logger.LogInformation("Disabling service '{ServiceName}' on {ServerId}", serviceName, serverId);
        return await _executor.ExecuteAsync(serverId, $"systemctl disable {serviceName}");
    }

    // --- Helpers ---

    private static void ValidateServiceName(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));

        if (!SafeServiceNameRegex.IsMatch(serviceName))
            throw new ArgumentException(
                $"Invalid service name '{serviceName}'. Only letters, digits, dots, underscores, hyphens, and @ are allowed.",
                nameof(serviceName));
    }

    private static SystemdUnit? ParseUnitLine(string line)
    {
        // Format: UNIT  LOAD  ACTIVE  SUB  DESCRIPTION
        // Fields are separated by variable amounts of whitespace; split by 5 parts max
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return null;

        return new SystemdUnit
        {
            Name = parts[0],
            LoadState = parts[1],
            ActiveState = parts[2],
            SubState = parts[3],
            Description = parts.Length > 4 ? string.Join(" ", parts[4..]) : ""
        };
    }

    private static Dictionary<string, string> ParseUnitFiles(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                map[parts[0]] = parts[1].ToLowerInvariant();
        }
        return map;
    }
}
