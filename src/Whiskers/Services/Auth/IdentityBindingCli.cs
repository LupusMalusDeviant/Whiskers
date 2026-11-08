namespace Whiskers.Services.Auth;

/// <summary>
/// R1-27 (contract 0003): host-level confirmation of a pending Google/OIDC binding request, for an OIDC provider that is
/// not trusted to verify addresses (the default) or a federated-only Admin after an upgrade.
/// <c>dotnet Whiskers.dll --confirm-identity-binding &lt;email&gt; [--issuer &lt;issuer&gt;]</c>, run inside the running
/// container (<c>docker compose exec whiskers dotnet Whiskers.dll --confirm-identity-binding admin@example.com</c>),
/// binds the newest pending request of that configured address. Exit codes: <c>0</c> bound, <c>2</c> usage, <c>3</c> no
/// pending request for that address, <c>4</c> ambiguous (several issuers wait: one line per request with issuer, time and
/// fingerprint — no email, no subject), <c>1</c> nothing changed.
/// <para>Authority: whoever can run a command in the container already holds the data directory (PRODUCT_CONTRACT §5.2).
/// It binds only what a sign-in requested for an address an administrator configured; it never creates an entry. The
/// audit row (<c>auth.identity_bound</c>, actor <see cref="IdentityBindingStore.CliActor"/>) is written before anything
/// changes; without it nothing changes. It exits without starting the web host and applies no staged restore.</para>
/// </summary>
public static class IdentityBindingCli
{
    public const string Flag = "--confirm-identity-binding";
    public const string IssuerFlag = "--issuer";

    /// <summary>True when the command line asks for this CLI (also in the <c>--confirm-identity-binding=&lt;email&gt;</c> form).</summary>
    public static bool Requested(IReadOnlyList<string> args) =>
        args.Any(a => a == Flag || a.StartsWith(Flag + "=", StringComparison.Ordinal));

    /// <summary>The command line without <see cref="Flag"/>, the value of the <c>=</c> forms as their own arguments.</summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<string> args)
    {
        var rest = new List<string>();
        foreach (var a in args)
        {
            if (a == Flag) continue;
            if (a.StartsWith(Flag + "=", StringComparison.Ordinal)) rest.Add(a[(Flag.Length + 1)..]);
            else if (a.StartsWith(IssuerFlag + "=", StringComparison.Ordinal)) rest.AddRange([IssuerFlag, a[(IssuerFlag.Length + 1)..]]);
            else rest.Add(a);
        }
        return rest;
    }

    public static async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> args, TextWriter output,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(output);
        if (!TryParse(args, out var email, out var issuer))
        {
            await output.WriteLineAsync($"Usage: dotnet Whiskers.dll {Flag} <email> [{IssuerFlag} <issuer>]");
            await output.WriteLineAsync("  Binds the newest pending Google/OIDC sign-in request of a configured address.");
            return 2;
        }

        try
        {
            // The CLI runs next to the live instance without the start-up warm-ups: it loads what it reads itself — the
            // whitelist (10), the identity bindings (15) and the roles (20), i.e. what an entry is.
            foreach (var initializable in services.GetServices<IInitializable>().Where(i => i.Order <= 20).OrderBy(i => i.Order))
                await initializable.InitializeAsync(ct);
            var store = services.GetRequiredService<IIdentityBindingStore>();
            if (!store.IsAvailable && !await store.RefreshAsync(ct))
            {
                await output.WriteLineAsync("The identity bindings file cannot be read. Nothing was changed.");
                return 1;
            }

            var result = await store.ConfirmFromHostAsync(email, issuer, ct);
            if (result.Success)
            {
                await output.WriteLineAsync($"The pending sign-in of {email.Trim()} is bound (binding {result.BindingId}).");
                await output.WriteLineAsync("The person can sign in now.");
                return 0;
            }
            switch (result.Error)
            {
                case IdentityBindingErrors.NotFound:
                    await output.WriteLineAsync("There is no pending sign-in request for that address. Nothing was changed.");
                    await output.WriteLineAsync("The person signs in once (it is refused and recorded), then run this command again within 24 hours.");
                    return 3;
                case IdentityBindingErrors.Ambiguous:
                    await output.WriteLineAsync($"Several issuers wait for that address; name one with {IssuerFlag}. Nothing was changed.");
                    foreach (var c in result.Candidates)
                        await output.WriteLineAsync($"  {c.Issuer}  requested {c.RequestedUtc:yyyy-MM-dd HH:mm} UTC  fingerprint {c.Fingerprint}");
                    return 4;
                case IdentityBindingErrors.AuditUnavailable:
                    await output.WriteLineAsync("The audit log could not be written. Nothing was changed.");
                    return 1;
                default:
                    await output.WriteLineAsync($"The binding could not be made ({result.Error}). Nothing was changed.");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The type only: a message may carry a path or an address.
            await output.WriteLineAsync($"The identity bindings could not be used ({ex.GetType().Name}). Nothing was changed.");
            return 1;
        }
    }

    private static bool TryParse(IReadOnlyList<string> args, out string email, out string? issuer)
    {
        email = "";
        issuer = null;
        var values = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == IssuerFlag)
            {
                if (issuer is not null || i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1])
                    || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return false;
                issuer = args[++i].Trim();
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal)) return false;
            else values.Add(args[i]);
        }
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0])) return false;
        email = values[0].Trim();
        return true;
    }
}
