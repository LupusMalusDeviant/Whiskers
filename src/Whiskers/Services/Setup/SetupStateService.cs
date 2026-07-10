using Microsoft.AspNetCore.Identity;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Setup;

/// <summary>First-run setup state (W1). The AUTHORITATIVE truth is "an Admin role exists" (roles.json); the
/// <c>setup-complete</c> flag file is only a reconciled mirror (a plain flag OR admin-role would strand an
/// admin-less "complete" instance). Singleton; <see cref="IInitializable"/> Order 25 (right after RoleService
/// 20) so <see cref="IsSetupComplete"/> is set before the first request. Single-process assumption: the
/// in-process lock is sufficient because Whiskers runs one process (no horizontal scale).</summary>
public sealed class SetupStateService : ISetupStateService, IInitializable
{
    private readonly IRoleService _roles;
    private readonly IWhitelistService _whitelist;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _flagPath;
    private readonly ILogger<SetupStateService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _complete;

    public SetupStateService(IRoleService roles, IWhitelistService whitelist, IServiceScopeFactory scopeFactory,
        ILogger<SetupStateService> logger, DataPathOptions? dataPaths = null)
    {
        _roles = roles;
        _whitelist = whitelist;
        _scopeFactory = scopeFactory;
        _flagPath = (dataPaths ?? DataPathOptions.Default).SetupCompleteFlag;
        _logger = logger;
    }

    public int Order => 25;
    public bool IsSetupComplete => _complete;   // hot path: cached volatile bool, never re-scans roles

    public Task InitializeAsync(CancellationToken ct = default) => RefreshAsync(ct);

    private async Task RefreshAsync(CancellationToken ct)
    {
        var truth = AdminRoleExists();
        try
        {
            if (truth && !File.Exists(_flagPath))
                await File.WriteAllTextAsync(_flagPath, DateTime.UtcNow.ToString("O"), ct); // reconcile forward
            else if (!truth && File.Exists(_flagPath))
                File.Delete(_flagPath);                                                     // distrust a stale flag
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Setup-complete flag reconcile failed"); }
        _complete = truth;
        _logger.LogInformation("Setup state: {State}", truth ? "complete (admin exists)" : "first-run (no admin — wizard active)");
    }

    private bool AdminRoleExists() => _roles.GetRoleData().Roles.Any(r => r.Role == AppRole.Admin);

    public async Task<SetupCompletionResult> CompleteSetupAsync(SetupAdminRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            return SetupCompletionResult.Failed(new[] { "An admin email is required." });
        var email = req.Email.Trim();

        await _lock.WaitAsync(ct);
        try
        {
            if (_complete || AdminRoleExists())
                return SetupCompletionResult.AlreadyComplete;   // someone already won the race

            if (req.IsLocal)
            {
                if (string.IsNullOrEmpty(req.Password))
                    return SetupCompletionResult.Failed(new[] { "A password is required for a local account." });
                using var scope = _scopeFactory.CreateScope();   // UserManager is scoped — never inject into a singleton
                var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                if (await users.FindByEmailAsync(email) is null)
                {
                    var r = await users.CreateAsync(new AppUser { UserName = email, Email = email, EmailConfirmed = true }, req.Password);
                    if (!r.Succeeded)
                        return SetupCompletionResult.Failed(r.Errors.Select(e => e.Description));
                }
            }

            // Write order matters (crash-safety): role FIRST (already admits via the fail-closed path), then the
            // whitelist email, then the mirror flag LAST — the flag can never precede the authoritative role.
            await _roles.SetRoleAsync(email, AppRole.Admin);
            var wl = _whitelist.GetWhitelist();
            if (!wl.Emails.Contains(email, StringComparer.OrdinalIgnoreCase))
            {
                wl.Emails.Add(email);   // Enabled deliberately NOT flipped (disabled→enabled would lock out unlisted OIDC users)
                await _whitelist.SaveWhitelistAsync(wl);
            }
            try { await File.WriteAllTextAsync(_flagPath, DateTime.UtcNow.ToString("O"), ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Writing setup-complete flag failed (the admin role is authoritative)"); }

            _complete = true;
            _logger.LogInformation("Setup completed: admin {Email} ({Kind})", email, req.IsLocal ? "local" : "federated");
            return SetupCompletionResult.Success;
        }
        finally { _lock.Release(); }
    }
}
