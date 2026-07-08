using System.Text;
using System.Text.RegularExpressions;

namespace Whiskers.Services.Server;

public class NginxSite
{
    public string Name { get; set; } = "";
    public string ConfigPath { get; set; } = "";
    public bool Enabled { get; set; }
    public string Content { get; set; } = "";
}

public class NginxService : INginxService
{
    private readonly IHostCommandExecutor _executor;
    private readonly ILogger<NginxService> _logger;

    // Only allow safe site names: letters, digits, dots, hyphens, underscores
    private static readonly Regex SafeSiteNameRegex = new(@"^[a-zA-Z0-9._-]+$", RegexOptions.Compiled);

    public NginxService(IHostCommandExecutor executor, ILogger<NginxService> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    public async Task<List<NginxSite>> ListSitesAsync(string serverId)
    {
        // Both listings are independent — fire them concurrently to save a sequential SSH round-trip.
        var availableTask = _executor.ExecuteAsync(serverId, "ls /etc/nginx/sites-available/ 2>/dev/null");
        var enabledTask = _executor.ExecuteAsync(serverId, "ls /etc/nginx/sites-enabled/ 2>/dev/null");
        var availableResult = await availableTask;
        var enabledResult = await enabledTask;

        var enabledSet = new HashSet<string>(
            ParseFileList(enabledResult.Output),
            StringComparer.Ordinal);

        var sites = new List<NginxSite>();

        foreach (var name in ParseFileList(availableResult.Output))
        {
            if (!SafeSiteNameRegex.IsMatch(name))
            {
                _logger.LogWarning("Skipping nginx site with unsafe name: {Name}", name);
                continue;
            }

            sites.Add(new NginxSite
            {
                Name = name,
                ConfigPath = $"/etc/nginx/sites-available/{name}",
                Enabled = enabledSet.Contains(name)
            });
        }

        // Include any enabled sites that are not in sites-available (e.g., symlinks to other paths)
        foreach (var name in enabledSet)
        {
            if (!SafeSiteNameRegex.IsMatch(name)) continue;
            if (!sites.Any(s => s.Name == name))
            {
                sites.Add(new NginxSite
                {
                    Name = name,
                    ConfigPath = $"/etc/nginx/sites-enabled/{name}",
                    Enabled = true
                });
            }
        }

        return sites;
    }

    public async Task<string> GetSiteConfigAsync(string serverId, string siteName)
    {
        ValidateSiteName(siteName);

        // Try sites-available first, then sites-enabled
        var result = await _executor.ExecuteAsync(serverId, $"cat /etc/nginx/sites-available/{siteName} 2>/dev/null || cat /etc/nginx/sites-enabled/{siteName} 2>/dev/null");

        if (!result.Success && !string.IsNullOrEmpty(result.Error))
            _logger.LogWarning("Failed to read nginx site config '{SiteName}' on {ServerId}: {Error}", siteName, serverId, result.Error);

        return result.Output;
    }

    public async Task<CommandResult> UpdateSiteConfigAsync(string serverId, string siteName, string content)
    {
        ValidateSiteName(siteName);

        if (string.IsNullOrEmpty(content))
            return new CommandResult { ExitCode = -1, Error = "Config content cannot be empty" };

        // Use base64 encoding to safely transfer arbitrary file content without shell escaping issues
        var base64Content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        var destPath = $"/etc/nginx/sites-available/{siteName}";

        // Write the file, then test the nginx config, and reload only if the test passes
        var writeCmd = $"echo '{base64Content}' | base64 -d | tee {destPath} > /dev/null";
        var writeResult = await _executor.ExecuteAsync(serverId, writeCmd);

        if (!writeResult.Success)
        {
            _logger.LogWarning("Failed to write nginx config '{SiteName}' on {ServerId}: {Error}", siteName, serverId, writeResult.Error);
            return writeResult;
        }

        var testResult = await TestConfigAsync(serverId);
        if (!testResult.Success)
        {
            _logger.LogWarning("nginx config test failed after updating '{SiteName}' on {ServerId}: {Error}", siteName, serverId, testResult.Error);
            return new CommandResult
            {
                ExitCode = testResult.ExitCode,
                Output = testResult.Output,
                Error = $"nginx config test failed: {testResult.Error}"
            };
        }

        return await ReloadAsync(serverId);
    }

    public async Task<CommandResult> TestConfigAsync(string serverId)
    {
        return await _executor.ExecuteAsync(serverId, "nginx -t 2>&1");
    }

    public async Task<CommandResult> ReloadAsync(string serverId)
    {
        _logger.LogInformation("Reloading nginx on {ServerId}", serverId);
        return await _executor.ExecuteAsync(serverId, "systemctl reload nginx");
    }

    public async Task<CommandResult> EnableSiteAsync(string serverId, string siteName)
    {
        ValidateSiteName(siteName);

        var availPath = $"/etc/nginx/sites-available/{siteName}";
        var enabledPath = $"/etc/nginx/sites-enabled/{siteName}";

        var linkResult = await _executor.ExecuteAsync(serverId, $"ln -sf {availPath} {enabledPath}");
        if (!linkResult.Success)
        {
            _logger.LogWarning("Failed to enable nginx site '{SiteName}' on {ServerId}: {Error}", siteName, serverId, linkResult.Error);
            return linkResult;
        }

        var testResult = await TestConfigAsync(serverId);
        if (!testResult.Success)
        {
            // Roll back the symlink
            await _executor.ExecuteAsync(serverId, $"rm -f {enabledPath}");
            return new CommandResult
            {
                ExitCode = testResult.ExitCode,
                Output = testResult.Output,
                Error = $"nginx config test failed after enabling '{siteName}': {testResult.Error}"
            };
        }

        _logger.LogInformation("Enabled nginx site '{SiteName}' on {ServerId}", siteName, serverId);
        return await ReloadAsync(serverId);
    }

    public async Task<CommandResult> DisableSiteAsync(string serverId, string siteName)
    {
        ValidateSiteName(siteName);

        var enabledPath = $"/etc/nginx/sites-enabled/{siteName}";
        var removeResult = await _executor.ExecuteAsync(serverId, $"rm -f {enabledPath}");

        if (!removeResult.Success)
        {
            _logger.LogWarning("Failed to disable nginx site '{SiteName}' on {ServerId}: {Error}", siteName, serverId, removeResult.Error);
            return removeResult;
        }

        _logger.LogInformation("Disabled nginx site '{SiteName}' on {ServerId}", siteName, serverId);
        return await ReloadAsync(serverId);
    }

    // --- Helpers ---

    private static IEnumerable<string> ParseFileList(string output)
    {
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
    }

    private static void ValidateSiteName(string siteName)
    {
        if (string.IsNullOrWhiteSpace(siteName))
            throw new ArgumentException("Site name cannot be null or empty", nameof(siteName));

        if (!SafeSiteNameRegex.IsMatch(siteName))
            throw new ArgumentException($"Invalid nginx site name '{siteName}'. Only letters, digits, dots, hyphens, and underscores are allowed.", nameof(siteName));
    }
}
