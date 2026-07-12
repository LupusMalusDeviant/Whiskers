using System.Text;
using System.Text.RegularExpressions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Persistence;
using Whiskers.Services.Server;
using Whiskers.Services.Vault;

namespace Whiskers.Services.GitDeploy;

/// <summary>Implements F5. All target-side work goes through <see cref="IHostCommandExecutor"/>
/// (SSH / nsenter / mTLS host shell — whatever the server's plane is); commands come from the
/// unit-tested <see cref="GitDeployCommands"/> builders. Private-repo tokens live in the vault and
/// are materialized only as a 0600 askpass file inside the app's workspace on the target.</summary>
public class GitDeployService : IGitDeployService
{
    private readonly JsonFileStore<GitDeployData> _store;
    private readonly IHostCommandExecutor _executor;
    private readonly IVaultService _vault;
    private readonly ILogger<GitDeployService> _logger;
    private GitDeployData _cached = new();
    private bool _loaded;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public GitDeployService(IHostCommandExecutor executor, IVaultService vault,
        ILogger<GitDeployService> logger, string? storePath = null, DataPathOptions? dataPaths = null)
    {
        _executor = executor;
        _vault = vault;
        _logger = logger;
        var paths = dataPaths ?? DataPathOptions.Default;
        _store = new JsonFileStore<GitDeployData>(storePath ?? $"{paths.RootDir}/git-deploys.json");
    }

    private static string TokenVaultKey(string appId) => $"git-token:{appId}";

    // App IDs flow into remote paths UNQUOTED by design (fixed hex alphabet) — enforce that here
    // so a hand-edited store file can never smuggle shell metacharacters into a path.
    private static void ValidateId(string appId)
    {
        if (!Regex.IsMatch(appId ?? "", "^[0-9a-f]{12}$"))
            throw new ArgumentException($"Invalid git-deploy app id '{appId}'.", nameof(appId));
    }

    private async Task<GitDeployData> LoadAsync()
    {
        if (_loaded) return _cached;
        await _lock.WaitAsync();
        try
        {
            if (!_loaded)
            {
                _cached = _store.Exists() ? await _store.LoadAsync() : new GitDeployData();
                _loaded = true;
            }
            return _cached;
        }
        finally { _lock.Release(); }
    }

    public async Task<List<GitDeployApp>> GetAppsAsync() => (await LoadAsync()).Apps.ToList();

    public async Task<GitDeployApp?> GetAppAsync(string appId) =>
        (await LoadAsync()).Apps.FirstOrDefault(a => a.Id == appId);

    public async Task<GitDeployApp> SaveAppAsync(GitDeployApp app, string? token)
    {
        ValidateId(app.Id);
        if (string.IsNullOrWhiteSpace(app.Name)) throw new ArgumentException("Name is required.");
        if (!app.RepoUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only https:// repository URLs are supported.");
        if (string.IsNullOrWhiteSpace(app.Branch)) app.Branch = "main";
        if (string.IsNullOrWhiteSpace(app.ComposePath)) app.ComposePath = "docker-compose.yml";
        if (app.ComposePath.StartsWith('/') || app.ComposePath.Contains(".."))
            throw new ArgumentException("Compose path must be relative to the repo root.");

        if (token is not null)
        {
            if (token.Length == 0)
            {
                if (_vault.IsEnabled) await _vault.DeleteSecretAsync(TokenVaultKey(app.Id));
                app.HasToken = false;
            }
            else
            {
                if (!_vault.IsEnabled)
                    throw new InvalidOperationException("Private repositories require the vault — set VAULT_KEY and restart.");
                await _vault.SetSecretAsync(TokenVaultKey(app.Id), token.Trim());
                app.HasToken = true;
            }
        }

        var data = await LoadAsync();
        await _lock.WaitAsync();
        try
        {
            var idx = data.Apps.FindIndex(a => a.Id == app.Id);
            if (idx >= 0) data.Apps[idx] = app; else data.Apps.Add(app);
            await _store.SaveAsync(data);
        }
        finally { _lock.Release(); }
        return app;
    }

    public async Task DeleteAppAsync(string appId)
    {
        ValidateId(appId);
        var data = await LoadAsync();
        var app = data.Apps.FirstOrDefault(a => a.Id == appId);
        if (app is null) return;

        // Best-effort teardown of the target workspace (the server may be gone — never block delete).
        try
        {
            await _executor.ExecuteAsync(app.ServerId, GitDeployCommands.RemoveWorkspaceCommand(appId),
                TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Workspace teardown for {App} failed", app.Name); }

        if (_vault.IsEnabled)
            try { await _vault.DeleteSecretAsync(TokenVaultKey(appId)); } catch { /* best effort */ }

        await _lock.WaitAsync();
        try
        {
            data.Apps.RemoveAll(a => a.Id == appId);
            await _store.SaveAsync(data);
        }
        finally { _lock.Release(); }
    }

    public async Task<(bool Success, string Output)> DeployAsync(string appId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ValidateId(appId);
        var app = await GetAppAsync(appId);
        if (app is null) return (false, "Git deploy app not found.");

        var log = new StringBuilder();
        void Report(string line) { progress?.Report(line); log.AppendLine(line); }

        try
        {
            // 1) Credentials (private repos): refresh the askpass pair from the vault every run,
            //    so a rotated token takes effect without re-saving the app.
            if (app.HasToken)
            {
                var token = _vault.IsEnabled ? _vault.GetSecret(TokenVaultKey(appId)) : null;
                if (string.IsNullOrEmpty(token))
                    return Fail(Report, log, "No token found in the vault — store the token in the app again.");
                Report("① Preparing credentials…");
                var tokenB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
                await Sh(app.ServerId, GitDeployCommands.WriteCredentialsCommand(appId, tokenB64), 30, ct);
            }

            // 2) Clone or update
            Report($"② Updating repository ({app.Branch})…");
            await Sh(app.ServerId, GitDeployCommands.CloneOrUpdateCommand(appId, app.RepoUrl, app.Branch, app.HasToken), 300, ct);
            var sha = (await Sh(app.ServerId, GitDeployCommands.CurrentShaCommand(appId), 20, ct)).Output.Trim();
            Report($"✅ Revision: {sha}");

            // 3) Build
            Report("③ docker compose build…");
            var build = await Sh(app.ServerId, GitDeployCommands.ComposeBuildCommand(appId, app.ComposePath), 600, ct);
            Report(Tail(build.Output, 15));

            // 4) Up
            Report("④ docker compose up -d…");
            var up = await Sh(app.ServerId, GitDeployCommands.ComposeUpCommand(appId, app.ComposePath), 300, ct);
            Report(Tail(up.Output + up.Error, 10));

            app.LastDeployedAt = DateTime.UtcNow;
            app.LastDeployedSha = sha;
            app.LastDeploySucceeded = true;
            await SaveAppAsync(app, token: null);
            Report($"🎉 Deploy finished: {app.Name} @ {sha}");
            return (true, $"{app.Name} @ {sha}");
        }
        catch (OperationCanceledException)
        {
            return Fail(Report, log, "Cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git deploy failed for {App}", app.Name);
            app.LastDeployedAt = DateTime.UtcNow;
            app.LastDeploySucceeded = false;
            try { await SaveAppAsync(app, token: null); } catch { /* status is best-effort */ }
            return Fail(Report, log, ex.Message);
        }
    }

    private static (bool, string) Fail(Action<string> report, StringBuilder log, string message)
    {
        report($"❌ {message}");
        return (false, message);
    }

    private async Task<CommandResult> Sh(string serverId, string command, int timeoutSeconds, CancellationToken ct)
    {
        var r = await _executor.ExecuteAsync(serverId, command, TimeSpan.FromSeconds(timeoutSeconds), ct);
        if (!r.Success)
            throw new InvalidOperationException(
                $"Command failed (exit {r.ExitCode}): {Tail(r.Error, 10)}{Tail(r.Output, 5)}");
        return r;
    }

    private static string Tail(string? s, int lines)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var all = s.Trim().Split('\n');
        return string.Join('\n', all.TakeLast(lines));
    }
}
