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

    /// <summary>Returns a process exit code: 0 issued, 2 usage, 3 no such local account, 1 failed (nothing changed, or
    /// the change could not be saved).</summary>
    public static async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> args, TextWriter output,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(output);
        var values = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (values.Count != 1 || args.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            await output.WriteLineAsync($"Usage: dotnet Whiskers.dll {Flag} <email of the local account>");
            await output.WriteLineAsync("  Issues a one-time recovery code; the account's owner sets a new password at /account/recover.");
            return 2;
        }
        var email = values[0].Trim();

        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var db = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var time = scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
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
            if (!await audit.TryLogAsync(Actor, "system", IssuedAction, AuthAuditLog.Target, account, account,
                    "a recovery code was issued on the host; the password was removed and every session of the account ended",
                    success: true))
            {
                await output.WriteLineAsync("The audit log could not be written. Nothing was changed.");
                return 1;
            }

            var (result, code, expires) = await RecoveryCodes.IssueAsync(users, db, user, time.GetUtcNow(), ct);
            if (!result.Succeeded || code is null)
            {
                await audit.TryLogAsync(Actor, "system", IssuedAction, AuthAuditLog.Target, account, account,
                    "the recovery code could not be saved; nothing was changed", success: false);
                await output.WriteLineAsync("The recovery code could not be saved (the account changed at the same time). Nothing was changed; run the command again.");
                return 1;
            }

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
            // The type only: a message may carry a connection string.
            await output.WriteLineAsync($"The local account store could not be used ({ex.GetType().Name}). Nothing was changed.");
            return 1;
        }
    }
}
