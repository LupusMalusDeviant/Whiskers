using Microsoft.AspNetCore.Identity;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Auth;

/// <summary>
/// R1-49: host-level recovery of a local account, for when no Admin can sign in any more.
/// <c>dotnet Whiskers.dll --recover-local-account &lt;email&gt;</c>, run on the host inside the running container
/// (<c>docker compose exec serverwatch dotnet Whiskers.dll --recover-local-account admin@example.com</c>), issues a
/// one-time recovery code for an existing local account and prints it to this terminal only — never to a log, the
/// audit trail or a file. The account's password is removed and its sessions end (new security stamp, R1-26); its
/// owner sets a new password at <c>/account/recover</c> within <see cref="RecoveryCodes.Lifetime"/>.
/// <para>Authority: whoever can run a command in the Whiskers container already holds the database and the data
/// directory (PRODUCT_CONTRACT §5.2), so the CLI is not a new way in. It never creates an account and never grants a
/// role. The audit row (actor <see cref="Actor"/>) is written <b>before</b> anything changes; without it nothing is
/// changed. It exits without starting the web host and applies no staged restore.</para>
/// <para>Contract 0009 A1.2: while the guardrail policy blocks redeeming a code (fail-closed store, or
/// <c>recovery_code</c> blocked under the kill switch) it refuses without <see cref="ForceFlag"/> and names the way back.</para>
/// </summary>
public static class LocalAccountRecoveryCli
{
    public const string Flag = "--recover-local-account";
    public const string IssuedAction = "auth.recovery_issued";
    internal const string Actor = "cli (--recover-local-account)";

    /// <summary>True when the command line asks for this CLI (also in the <c>--recover-local-account=&lt;email&gt;</c> form).</summary>
    public static bool Requested(IReadOnlyList<string> args) =>
        args.Any(a => a == Flag || a.StartsWith(Flag + "=", StringComparison.Ordinal));

    /// <summary>The command line without <see cref="Flag"/>, the value of the <c>=</c> form as its own argument.</summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<string> args)
    {
        var rest = new List<string>();
        foreach (var a in args)
        {
            if (a == Flag) continue;
            rest.Add(a.StartsWith(Flag + "=", StringComparison.Ordinal) ? a[(Flag.Length + 1)..] : a);
        }
        return rest;
    }

    /// <summary>Issues the code although redeeming it is blocked right now (contract 0009, amendment A1.2).</summary>
    public const string ForceFlag = "--force";

    /// <summary>Returns a process exit code: 0 issued, 2 usage, 3 no such local account, 4 refused because the code
    /// could not be redeemed now (nothing changed; <see cref="ForceFlag"/> issues it anyway), 1 failed (nothing
    /// changed, or the change could not be saved).</summary>
    public static async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> args, TextWriter output,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(output);
        var values = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var force = args.Contains(ForceFlag, StringComparer.Ordinal);
        if (values.Count != 1 || args.Count != (force ? 2 : 1) || string.IsNullOrWhiteSpace(values[0]))
        {
            await output.WriteLineAsync($"Usage: dotnet Whiskers.dll {Flag} <email of the local account> [{ForceFlag}]");
            await output.WriteLineAsync("  Issues a one-time recovery code; the account's owner sets a new password at /account/recover.");
            return 2;
        }
        var email = values[0].Trim();

        // Contract 0009 A1.2: issuing clears the password. While the policy blocks redeeming the code (the store is
        // fail-closed, or the kill switch is on and blocks recovery codes), that would leave the account without any
        // way to sign in. The CLI stays usable (E3, D1) but asks for an explicit --force and says what helps instead.
        // The CLI reads guardrails.json itself, read-only (no save, no unreadable copy), unless the store was already
        // loaded (a booted host): it does not depend on the start-up order of Program.cs. In the app the interface is
        // the mutation scope guard's proxy, so the store behind it is taken (as AddInitializable does). Anything else —
        // no store, not the real store, or a store still not loaded — counts as blocked, like the fail-closed policy
        // (D4): an unchecked policy is never read as "allowed", and --force still issues (E3 is not refused outright).
        var guardrails = await LoadGuardrailsAsync(services, ct);
        string? forcedPast = null;
        string? why = null;
        if (guardrails is null)
        {
            forcedPast = "an unchecked guardrail policy (policy unchecked)";
            why = "the guardrail policy (guardrails.json) could not be checked, so it counts as fail-closed, which blocks recovery codes";
        }
        else if (guardrails.Current.KillSwitchBlocks(Whiskers.Configuration.KillSwitchExemptions.RecoveryCode))
        {
            var failClosed = guardrails.LoadError is not null;
            forcedPast = failClosed ? "fail-closed guardrails" : "the recovery_code block under the kill switch";
            why = failClosed
                ? "the guardrail configuration (guardrails.json) cannot be read, so the fail-closed policy is enforced, and it blocks recovery codes"
                : "the read-only kill switch is on and an Admin blocked recovery codes while it is on";
        }
        if (why is not null)
        {
            if (!force)
            {
                await output.WriteLineAsync($"Refused: a recovery code issued now could not be redeemed, because {why}.");
                await output.WriteLineAsync("Issuing it would still remove the account's password. Nothing was changed.");
                await WriteWayBackAsync(output);
                await output.WriteLineAsync($"To issue the code anyway, run the command again with {ForceFlag}.");
                return 4;
            }
            await output.WriteLineAsync($"Warning ({ForceFlag}): the code cannot be redeemed until this is fixed, because {why}.");
            await WriteWayBackAsync(output);
        }

        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var db = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var time = scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        string? announced = null;   // the account an issued-row was written for, until its outcome is known
        try
        {
            var user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                await output.WriteLineAsync("There is no local account with that email. Nothing was changed.");
                return 3;
            }
            var account = user.Email ?? email;

            // The record first: a credential handed out on the host that the trail does not show is not handed out.
            if (!await audit.TryLogAsync(AuditPrincipals.HostCli("recover-local-account", Actor), IssuedAction, AuthAuditLog.Target, account, account,
                    "a recovery code was issued on the host; the password was removed and every session of the account ended"
                    + (forcedPast is null ? "" : $"; forced (--force): redeeming is blocked by {forcedPast}"),
                    success: true))
            {
                await output.WriteLineAsync("The audit log could not be written. Nothing was changed.");
                return 1;
            }
            announced = account;

            var (result, code, expires) = await RecoveryCodes.IssueAsync(users, db, user, time.GetUtcNow(), ct);
            if (!result.Succeeded || code is null)
            {
                await audit.TryLogAsync(AuditPrincipals.HostCli("recover-local-account", Actor), IssuedAction, AuthAuditLog.Target, account, account,
                    "the recovery code could not be saved; nothing was changed", success: false);
                await output.WriteLineAsync("The recovery code could not be saved (the account changed at the same time). Nothing was changed; run the command again.");
                return 1;
            }
            announced = null;   // issued: the row stands

            await output.WriteLineAsync($"Recovery code for {account} (shown once, valid until {expires:yyyy-MM-dd HH:mm} UTC):");
            await output.WriteLineAsync();
            await output.WriteLineAsync($"    {code}");
            await output.WriteLineAsync();
            await output.WriteLineAsync("Open <your Whiskers address>/account/recover, enter the email, this code and a new password.");
            await output.WriteLineAsync("The old password no longer works and every session of the account has ended.");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The row written before announced a code that was not issued: its outcome follows (best effort).
            if (announced is not null)
                await audit.TryLogAsync(AuditPrincipals.HostCli("recover-local-account", Actor), IssuedAction, AuthAuditLog.Target, announced, announced,
                    $"the recovery code could not be saved ({ex.GetType().Name}); nothing was changed", success: false);
            // The type only: a message may carry a connection string.
            await output.WriteLineAsync($"The local account store could not be used ({ex.GetType().Name}). Nothing was changed.");
            return 1;
        }
    }

    /// <summary>The loaded guardrail store, read from disk now if nothing loaded it; <c>null</c> when there is none, it is not
    /// the real store or it could not be loaded (the caller treats that as fail-closed).</summary>
    private static async Task<Whiskers.Services.Guardrails.GuardrailStore?> LoadGuardrailsAsync(IServiceProvider services,
        CancellationToken ct)
    {
        var resolved = services.GetService<Whiskers.Services.Guardrails.IGuardrailStore>();
        var inner = resolved is Whiskers.Services.Mutations.IMutationScopeProxy guarded ? guarded.Inner : resolved;
        if (inner is not Whiskers.Services.Guardrails.GuardrailStore store) return null;
        if (!store.IsLoaded)
        {
            try { await store.InspectAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
        }
        return store.IsLoaded ? store : null;
    }

    /// <summary>The real way back while redeeming is blocked (contract 0009 A1.2).</summary>
    private static async Task WriteWayBackAsync(TextWriter output)
    {
        // Contract 0010 D6/D7: removing guardrails.json is no way back any more (with a recorded policy it stays fail-closed).
        await output.WriteLineAsync("The way back: on the host, put a valid guardrails.json into the data directory (repair it, or restore it");
        await output.WriteLineAsync("from a self-backup), or run --reset-guardrails when guardrails.json and guardrails.json.bak are both");
        await output.WriteLineAsync("missing; or turn off ReadOnlyMode or the recovery_code block in guardrails.json. Then restart Whiskers.");
    }
}
