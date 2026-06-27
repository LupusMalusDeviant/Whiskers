using System.Text.RegularExpressions;

namespace ServerWatch.Services.Server;

public class FirewallRule
{
    public int Number { get; set; }
    public string Action { get; set; } = "";       // ALLOW, DENY
    public string Direction { get; set; } = "";     // IN, OUT
    public string From { get; set; } = "Anywhere";
    public string To { get; set; } = "";
    public string Port { get; set; } = "";
    public string Protocol { get; set; } = "";      // tcp, udp, any
    public string Raw { get; set; } = "";           // Original line
}

public class FirewallStatus
{
    public bool Active { get; set; }
    public List<FirewallRule> Rules { get; set; } = new();
    public string RawOutput { get; set; } = "";
}

public class FirewallService
{
    private readonly IHostCommandExecutor _executor;
    private readonly ILogger<FirewallService> _logger;

    // Matches lines like: [ 1] 22/tcp                     ALLOW IN    Anywhere
    // or:                  [ 2] 80/tcp                     ALLOW IN    Anywhere (v6)
    private static readonly Regex RuleLineRegex = new(
        @"^\[\s*(\d+)\]\s+(.+?)\s+(ALLOW|DENY|REJECT|LIMIT)\s+(IN|OUT|FWD)?\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches port/protocol segment like: 22/tcp, 80, 443/udp
    private static readonly Regex PortProtoRegex = new(@"^(\d+(?::\d+)?(?:/(?:tcp|udp))?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public FirewallService(IHostCommandExecutor executor, ILogger<FirewallService> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    public async Task<FirewallStatus> GetStatusAsync(string serverId)
    {
        var result = await _executor.ExecuteAsync(serverId, "ufw status numbered");

        var status = new FirewallStatus
        {
            RawOutput = result.Output
        };

        if (!result.Success)
        {
            _logger.LogWarning("ufw status numbered failed on {ServerId}: {Error}", serverId, result.Error);
            return status;
        }

        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Parse active status
            if (trimmed.StartsWith("Status:", StringComparison.OrdinalIgnoreCase))
            {
                status.Active = trimmed.Contains("active", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            // Skip header lines
            if (trimmed.StartsWith("To", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("--", StringComparison.OrdinalIgnoreCase))
                continue;

            // Parse rule lines
            var rule = ParseRuleLine(trimmed);
            if (rule != null)
                status.Rules.Add(rule);
        }

        return status;
    }

    private static FirewallRule? ParseRuleLine(string line)
    {
        var match = RuleLineRegex.Match(line);
        if (!match.Success) return null;

        // A malformed/overlong rule number must skip this line, not abort the whole list.
        if (!int.TryParse(match.Groups[1].Value, out var ruleNumber))
            return null;

        var rule = new FirewallRule
        {
            Raw = line,
            Number = ruleNumber,
            Action = match.Groups[3].Value.Trim().ToUpperInvariant(),
            Direction = (match.Groups[4].Value.Trim().ToUpperInvariant() is { Length: > 0 } dir ? dir : "IN")
        };

        // The "To" field (destination/port) is group 2; "From" is group 5
        var toField = match.Groups[2].Value.Trim();
        var fromField = match.Groups[5].Value.Trim();

        // Strip "(v6)" annotation
        fromField = Regex.Replace(fromField, @"\s*\(v6\)\s*$", "").Trim();

        rule.From = string.IsNullOrEmpty(fromField) ? "Anywhere" : fromField;
        rule.To = toField;

        // Extract port and protocol from the To field (e.g., "22/tcp", "80", "8080:8090/udp")
        var portMatch = PortProtoRegex.Match(toField);
        if (portMatch.Success)
        {
            var parts = toField.Split('/');
            rule.Port = parts[0];
            rule.Protocol = parts.Length > 1 ? parts[1].ToLowerInvariant() : "any";
        }
        else
        {
            rule.Port = toField;
            rule.Protocol = "any";
        }

        return rule;
    }

    public async Task<CommandResult> AddRuleAsync(
        string serverId,
        string port,
        string protocol = "tcp",
        string action = "allow",
        string? from = null)
    {
        // Validate port: must be numeric (optionally with range and/or protocol)
        if (!IsValidPort(port))
            return new CommandResult { ExitCode = -1, Error = $"Invalid port value: '{port}'" };

        // Validate protocol
        if (!IsValidProtocol(protocol))
            return new CommandResult { ExitCode = -1, Error = $"Invalid protocol: '{protocol}'" };

        // Validate action
        if (!IsValidUfwAction(action))
            return new CommandResult { ExitCode = -1, Error = $"Invalid action: '{action}'" };

        string command;

        if (!string.IsNullOrWhiteSpace(from))
        {
            // Validate "from" value – must be an IP/CIDR (no arbitrary shell input)
            if (!IsValidIpOrCidr(from))
                return new CommandResult { ExitCode = -1, Error = $"Invalid source address: '{from}'" };

            command = $"ufw {action} from {from} to any port {port} proto {protocol}";
        }
        else
        {
            command = $"ufw {action} {port}/{protocol}";
        }

        _logger.LogInformation("Adding firewall rule on {ServerId}: {Command}", serverId, command);
        return await _executor.ExecuteAsync(serverId, command);
    }

    public async Task<CommandResult> RemoveRuleAsync(string serverId, int ruleNumber)
    {
        if (ruleNumber <= 0)
            return new CommandResult { ExitCode = -1, Error = "Rule number must be a positive integer" };

        var command = $"echo y | ufw delete {ruleNumber}";
        _logger.LogInformation("Removing firewall rule #{RuleNumber} on {ServerId}", ruleNumber, serverId);
        return await _executor.ExecuteAsync(serverId, command);
    }

    public async Task<CommandResult> SetStatusAsync(string serverId, bool enable)
    {
        var command = enable ? "echo y | ufw enable" : "ufw disable";
        _logger.LogInformation("{Action} UFW on {ServerId}", enable ? "Enabling" : "Disabling", serverId);
        return await _executor.ExecuteAsync(serverId, command);
    }

    // --- Input validation helpers ---

    private static bool IsValidPort(string port)
    {
        if (string.IsNullOrWhiteSpace(port)) return false;
        // Allow: "80", "8080:8090" (range), "80/tcp" (embedded proto handled separately)
        return Regex.IsMatch(port, @"^\d+(:\d+)?$");
    }

    private static bool IsValidProtocol(string proto) =>
        proto is "tcp" or "udp" or "any";

    private static bool IsValidUfwAction(string action) =>
        action is "allow" or "deny" or "reject" or "limit";

    private static bool IsValidIpOrCidr(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        // Allow IPv4, IPv4/CIDR, IPv6, IPv6/CIDR, and the keyword "Anywhere"
        return Regex.IsMatch(value, @"^(Anywhere|[\da-fA-F:\.]+(?:/\d{1,3})?)$");
    }
}
