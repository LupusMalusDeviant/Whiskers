using Whiskers.Services.AuditLog;

namespace Whiskers.Services.Guardrails;

/// <summary>
/// Contract 0010 D6: the host-level way out of "the guardrail policy is missing although one existed".
/// <c>dotnet Whiskers.dll --reset-guardrails</c>, run on the host inside the container
/// (<c>docker compose exec whiskers dotnet Whiskers.dll --reset-guardrails</c>), writes
/// <see cref="Whiskers.Configuration.GuardrailConfig.SafeDefault"/> with the kill switch <b>on</b> (Q2) and ensures the
/// database's record (<c>reset-cli</c>). The operator then restarts Whiskers and an Admin switches the kill switch off on
/// the Guardrails page (E1, audited).
/// <para>Authority: class E3 of contract 0009 — a shell in the container is root-equivalent (PRODUCT_CONTRACT §5.2). It
/// refuses while <c>guardrails.json</c> or <c>guardrails.json.bak</c> exists (those are repaired, not replaced). The audit
/// row <see cref="ResetAction"/> is written <b>before</b> anything changes; without it nothing changes. It exits without
/// starting the web host and applies no staged restore.</para>
/// </summary>
public static class GuardrailResetCli
{
    public const string Flag = "--reset-guardrails";
    public const string ResetAction = "guardrails.reset_on_host";
    internal const string Actor = "cli (--reset-guardrails)";

    /// <summary>True when the command line asks for this CLI.</summary>
    public static bool Requested(IReadOnlyList<string> args) => args.Any(a => a == Flag);

    /// <summary>The command line without <see cref="Flag"/>.</summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<string> args) => args.Where(a => a != Flag).ToList();

    /// <summary>Returns a process exit code: 0 written, 2 usage, 3 refused because a policy file exists (nothing
    /// changed), 1 failed (nothing changed, or the file could not be written).</summary>
    public static async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> args, TextWriter output,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(output);
        if (args.Count != 0)
        {
            await output.WriteLineAsync($"Usage: dotnet Whiskers.dll {Flag}");
            await output.WriteLineAsync("  Writes the safe default guardrail policy with the kill switch on, when guardrails.json and");
            await output.WriteLineAsync("  guardrails.json.bak are both missing. Restart Whiskers afterwards.");
            return 2;
        }

        var resolved = services.GetService<IGuardrailStore>();
        var inner = resolved is Whiskers.Services.Mutations.IMutationScopeProxy guarded ? guarded.Inner : resolved;
        if (inner is not GuardrailStore store)
        {
            await output.WriteLineAsync("The guardrail store is not available. Nothing was changed.");
            return 1;
        }
        if (store.PolicyFileExists())
        {
            await output.WriteLineAsync("Refused: guardrails.json or guardrails.json.bak exists in the data directory. Nothing was changed.");
            await output.WriteLineAsync("Repair that file (a copy of an unreadable one is kept as guardrails.json.unreadable-<timestamp>),");
            await output.WriteLineAsync("or restore guardrails.json from a self-backup, then restart Whiskers.");
            return 3;
        }

        var audit = services.GetRequiredService<IAuditLogService>();
        // The record first: a policy reset on the host that the trail does not show did not happen.
        if (!await audit.TryLogAsync(Actor, "system", ResetAction, "guardrails", "guardrails.json", "guardrails.json",
                "the guardrail policy was reset on the host: the safe default with the kill switch on", success: true))
        {
            await output.WriteLineAsync("The audit log could not be written. Nothing was changed.");
            return 1;
        }

        string? markerProblem;
        try
        {
            markerProblem = await store.ResetOnHostAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await audit.TryLogAsync(Actor, "system", ResetAction, "guardrails", "guardrails.json", "guardrails.json",
                $"the guardrail policy could not be reset ({ex.GetType().Name}); nothing was changed", success: false);
            await output.WriteLineAsync(ex is InvalidOperationException
                ? $"Refused: {ex.Message} Nothing was changed."
                : $"guardrails.json could not be written ({ex.GetType().Name}). Nothing was changed.");
            return 1;
        }

        await output.WriteLineAsync("guardrails.json was written: the safe default policy with the kill switch ON.");
        if (markerProblem is not null)
            await output.WriteLineAsync($"Note: {markerProblem}; the next start records it.");
        await output.WriteLineAsync("Next: restart Whiskers, then an Admin switches the kill switch off on the Guardrails page (audited)");
        await output.WriteLineAsync("and sets up the guardrail policy again.");
        return 0;
    }
}
