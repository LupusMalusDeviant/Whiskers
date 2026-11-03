namespace Whiskers.Services.Server;

public class CommandResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public bool Success => ExitCode == 0;

    /// <summary>
    /// True when the command produced more output than the cap allowed and the rest was discarded.
    ///
    /// <para>Without this flag a caller that parses the output has no way to tell a truncated document from a
    /// malformed one. That is not hypothetical: the Trivy scanner reported
    /// "'0xE2' is an invalid start of a value" for months, which is the first byte of the truncation marker
    /// and says nothing about the real cause. A caller that parses MUST check this before blaming its parser,
    /// because a truncated payload is a limit to raise, not a bug to hunt.</para>
    /// </summary>
    public bool OutputTruncated { get; set; }
}

public interface IHostCommandExecutor
{
    /// <param name="maxOutputChars">
    /// Raises the output cap for this one call. Commands whose output is a document rather than a log — a
    /// scanner report, an inventory — legitimately run to megabytes, and capping those silently corrupts them.
    /// Null keeps the default cap, which is what every log-like caller wants.
    /// </param>
    Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
        CancellationToken ct = default, int? maxOutputChars = null);

    /// <summary>
    /// The same execution as <see cref="ExecuteAsync"/>, for a command that changes something (R1-48, INV-38): a raw
    /// shell command, a <c>docker exec</c>, a compose <c>.env</c> write, a prune, a retention <c>rm</c>. It has no
    /// capability of its own — what it may do is whatever the open Mutation Gateway scope decided — so the scope guard
    /// refuses it outside one. Reads keep <see cref="ExecuteAsync"/>; <c>MutationGatewayCoverageTests</c> lists every
    /// page and tool that calls either.
    /// </summary>
    [Whiskers.Services.Mutations.MutationPrimitive]
    Task<CommandResult> ExecuteMutationAsync(string serverId, string command, TimeSpan? timeout = null,
        CancellationToken ct = default, int? maxOutputChars = null) =>
        ExecuteAsync(serverId, command, timeout, ct, maxOutputChars);
}
