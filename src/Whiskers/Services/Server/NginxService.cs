using System.Text;
using Whiskers.Utils;

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

    /// <summary>Where an update keeps the file it replaces until nginx -t has passed (sites-available is not included by nginx).</summary>
    internal const string PreviousSuffix = ".whiskers-prev";

    /// <summary>Where an update decodes the new content before it replaces the site.</summary>
    internal const string NextSuffix = ".whiskers-next";

    /// <summary>Whiskers' own working files beside a site, never listed as sites themselves.</summary>
    internal static bool IsWorkingFile(string name) =>
        name.EndsWith(PreviousSuffix, StringComparison.Ordinal) || name.EndsWith(NextSuffix, StringComparison.Ordinal);

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
            if (IsWorkingFile(name)) continue;   // left by an interrupted update, not a site
            if (!CommandOperands.IsSafeSiteName(name))
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
            if (!CommandOperands.IsSafeSiteName(name)) continue;
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
        var result = await _executor.ExecuteAsync(serverId, $"cat {ShellUtils.Quote("/etc/nginx/sites-available/" + siteName)} 2>/dev/null || cat {ShellUtils.Quote("/etc/nginx/sites-enabled/" + siteName)} 2>/dev/null");

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

        // Keep the current file, write the new one, then test the nginx config: reload only if the test passes, and put
        // the previous file back if it fails — a refused config must not stay live for the next reload, a certbot hook
        // or a reboot to pick up (Fable review). A site that did not exist before is removed again.
        var dest = ShellUtils.Quote(destPath);
        var prev = ShellUtils.Quote(destPath + PreviousSuffix);
        // A kept file from an interrupted earlier run is dropped first, so a kept file only ever holds this run's
        // previous content; the new content is decoded beside the site before the site itself is touched, and copied
        // over it with `cat` so the file keeps its owner and mode.
        var next = ShellUtils.Quote(destPath + NextSuffix);
        var writeCmd = $"rm -f {prev} && echo '{base64Content}' | base64 -d > {next} && " +
                       $"if [ -e {dest} ]; then cp -p {dest} {prev}; fi && cat {next} > {dest}; " +
                       $"rc=$?; rm -f {next}; exit $rc";
        var writeResult = await _executor.ExecuteAsync(serverId, writeCmd);

        if (!writeResult.Success)
        {
            // A write that failed half way left the site cut short: the kept file, if this run made one, goes back.
            await _executor.ExecuteAsync(serverId, $"if [ -e {prev} ]; then mv -f {prev} {dest}; fi");
            _logger.LogWarning("Failed to write nginx config '{SiteName}' on {ServerId}: {Error}", siteName, serverId, writeResult.Error);
            return writeResult;
        }

        var testResult = await TestConfigAsync(serverId);
        if (!testResult.Success)
        {
            _logger.LogWarning("nginx config test failed after updating '{SiteName}' on {ServerId}: {Error}", siteName, serverId, testResult.Error);
            var restore = await _executor.ExecuteAsync(serverId,
                $"if [ -e {prev} ]; then mv -f {prev} {dest}; else rm -f {dest}; fi");
            return new CommandResult
            {
                ExitCode = testResult.ExitCode,
                Output = testResult.Output,
                Error = restore.Success
                    ? $"nginx config test failed; the previous file was put back: {testResult.Error}"
                    : $"nginx config test failed, and putting the previous file back failed too ({restore.Error}); check {destPath} before the next reload: {testResult.Error}"
            };
        }

        await _executor.ExecuteAsync(serverId, $"rm -f {prev}");
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

        var linkResult = await _executor.ExecuteAsync(serverId, $"ln -sf -- {ShellUtils.Quote(availPath)} {ShellUtils.Quote(enabledPath)}");
        if (!linkResult.Success)
        {
            _logger.LogWarning("Failed to enable nginx site '{SiteName}' on {ServerId}: {Error}", siteName, serverId, linkResult.Error);
            return linkResult;
        }

        var testResult = await TestConfigAsync(serverId);
        if (!testResult.Success)
        {
            // Roll back the symlink
            await _executor.ExecuteAsync(serverId, $"rm -f -- {ShellUtils.Quote(enabledPath)}");
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
        var removeResult = await _executor.ExecuteAsync(serverId, $"rm -f -- {ShellUtils.Quote(enabledPath)}");

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

        // One rule for every site name that reaches a command (R1-42, E-03): a leading letter or digit and no "..",
        // so the name can neither be read as an option nor leave /etc/nginx/sites-*.
        CommandOperands.RequireSiteName(siteName);
    }
}
