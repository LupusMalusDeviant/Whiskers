using System.Text;
using System.Text.RegularExpressions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Mutations;
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
    private readonly IComposePlanner _planner;
    private readonly ILogger<GitDeployService> _logger;
    private GitDeployData _cached = new();
    private bool _loaded;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public GitDeployService(IHostCommandExecutor executor, IVaultService vault, IComposePlanner planner,
        ILogger<GitDeployService> logger, string? storePath = null, DataPathOptions? dataPaths = null)
    {
        _executor = executor;
        _vault = vault;
        _planner = planner;
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

    // Copies: a caller that edits what it got must not change the stored app (and its revision) behind the lock.
    public async Task<List<GitDeployApp>> GetAppsAsync() => (await LoadAsync()).Apps.Select(a => a.Clone()).ToList();

    public async Task<GitDeployApp?> GetAppAsync(string appId) =>
        (await LoadAsync()).Apps.FirstOrDefault(a => a.Id == appId)?.Clone();

    public async Task<GitDeployApp> SaveAppAsync(GitDeployApp app, string? token, string ownerId)
    {
        ValidateId(app.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        // Checked again under the lock; this early check keeps a stale edit from touching the vault token.
        if ((await LoadAsync()).Apps.FirstOrDefault(a => a.Id == app.Id) is { } before && before.Revision != app.Revision)
            throw new GitDeployConflictException($"'{before.Name}' was changed meanwhile; reload and save again.");
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
            var stored = app.Clone();
            if (idx >= 0)
            {
                var existing = data.Apps[idx];
                // Edited from a stale copy: someone saved (or a deploy was decided on) a newer revision meanwhile.
                if (existing.Revision != app.Revision)
                    throw new GitDeployConflictException($"'{existing.Name}' was changed meanwhile; reload and save again.");
                stored.CreatedAt = existing.CreatedAt;
                stored.LastDeployedAt = existing.LastDeployedAt;
                stored.LastDeployedSha = existing.LastDeployedSha;
                stored.LastDeploySucceeded = existing.LastDeploySucceeded;
            }
            // Whoever saves the inputs answers for what a webhook later deploys with them (R1-08).
            stored.Owner = ownerId;
            stored.Revision = app.Revision + 1;
            if (idx >= 0) data.Apps[idx] = stored; else data.Apps.Add(stored);
            await _store.SaveAsync(data);
            return stored.Clone();
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> SetOwnerAsync(string appId, string ownerId, string? expectedOwnerId)
    {
        ValidateId(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var data = await LoadAsync();
        await _lock.WaitAsync();
        try
        {
            var existing = data.Apps.FirstOrDefault(a => a.Id == appId);
            if (existing is null || (existing.Owner ?? "") != (expectedOwnerId ?? "")) return false;
            var stored = existing.Clone();
            stored.Owner = ownerId;
            // A new owner answers for the inputs from now on: a deployment decided before the takeover is refused.
            stored.Revision = existing.Revision + 1;
            data.Apps[data.Apps.IndexOf(existing)] = stored;
            await _store.SaveAsync(data);
            return true;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Stops the deployment unless the stored app is still exactly <paramref name="snapshot"/>: the same
    /// revision and the same inputs (server, repository, branch, compose path, token use, owner).</summary>
    private async Task EnsureUnchangedAsync(GitDeployApp snapshot)
    {
        var current = (await LoadAsync()).Apps.FirstOrDefault(a => a.Id == snapshot.Id);
        if (current is null)
            throw new GitDeployConflictException("The app was deleted meanwhile.");
        if (current.Revision != snapshot.Revision || current.ServerId != snapshot.ServerId
            || current.RepoUrl != snapshot.RepoUrl || current.Branch != snapshot.Branch
            || current.ComposePath != snapshot.ComposePath || current.HasToken != snapshot.HasToken
            || current.Owner != snapshot.Owner)
            throw new GitDeployConflictException(
                $"'{current.Name}' was changed after the deployment was decided; start the deployment again.");
    }

    /// <summary>Records a deployment's result without touching the inputs or the revision.</summary>
    private async Task RecordResultAsync(string appId, bool success, string? sha)
    {
        var data = await LoadAsync();
        await _lock.WaitAsync();
        try
        {
            var idx = data.Apps.FindIndex(a => a.Id == appId);
            if (idx < 0) return;
            var stored = data.Apps[idx].Clone();
            stored.LastDeployedAt = DateTime.UtcNow;
            stored.LastDeploySucceeded = success;
            if (sha is not null) stored.LastDeployedSha = sha;
            data.Apps[idx] = stored;
            await _store.SaveAsync(data);
        }
        finally { _lock.Release(); }
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

    public async Task<(bool Success, string Output)> DeployAsync(GitDeployApp app, ComposePlanGate gate,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(gate);
        var appId = app.Id;
        ValidateId(appId);
        // The snapshot the deployment was decided on (server, repository, branch, compose path) is what runs; the
        // stored app must still be at its revision (R1-08). Copy it so nobody can change it underneath.
        app = app.Clone();

        var log = new StringBuilder();
        void Report(string line) { progress?.Report(line); log.AppendLine(line); }

        try
        {
            await EnsureUnchangedAsync(app);

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

            // 3) Plan (R1-07): the project compose will use and every existing container `up --remove-orphans`
            //    recreates, removes or collides with — decided before the first build; build and up run as planned.
            var deployment = new ComposeDeployment(app.ServerId,
                [new ComposeFileInput(GitDeployCommands.ComposeFile(appId, app.ComposePath))]) { RemoveOrphans = true };
            var plan = await _planner.PlanAsync(deployment, ct);
            Report($"Project '{plan.ProjectNameForAudit}': {plan.Impact.Count} existing container(s) affected.");

            async Task BuildAndUp()
            {
                // Decided by the gate: the app is unchanged, planned again right before the first write, and nothing
                // undecided may run.
                await EnsureUnchangedAsync(app);
                plan = await _planner.PlanAsync(deployment, ct);
                plan.EnsureDecided(MutationScope.Current);
                // 4) Build
                Report("③ docker compose build…");
                var build = await Sh(app.ServerId, plan.BuildCommand, 600, ct);
                Report(Tail(build.Output, 15));

                // 5) Up
                Report("④ docker compose up -d…");
                var up = await Sh(app.ServerId, plan.UpCommand, 300, ct);
                Report(Tail(up.Output + up.Error, 10));
            }
            await gate(plan, BuildAndUp);

            await RecordResultAsync(appId, success: true, sha);
            Report($"🎉 Deploy finished: {app.Name} @ {sha}");
            return (true, $"{app.Name} @ {sha}");
        }
        catch (OperationCanceledException)
        {
            return Fail(Report, log, "Cancelled.");
        }
        catch (GitDeployConflictException ex)
        {
            return Fail(Report, log, ex.Message);   // nothing of this deployment ran on the changed app
        }
        catch (Exception ex) when (ex is not MutationRefusedException)
        {
            _logger.LogError(ex, "Git deploy failed for {App}", app.Name);
            try { await RecordResultAsync(appId, success: false, sha: null); } catch { /* status is best-effort */ }
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
