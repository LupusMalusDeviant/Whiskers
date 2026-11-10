using System.Text.RegularExpressions;

namespace Whiskers.Services.Server;

/// <summary>
/// Secrets a host command needs but must never carry in its text (CodeQL #23–#26,
/// <c>cs/cleartext-storage-of-sensitive-information</c>): the command names them as environment variables
/// (<c>docker exec -e PGPASSWORD …</c>), and <see cref="HostCommandExecutor"/> delivers the values beside it — in the
/// environment of the local <c>nsenter</c> process, in the environment of the mTLS helper container, or on the standard
/// input of <c>ssh</c>, read into the variable by the remote shell before the command runs. The command text that is
/// logged, audited or listed by the host's process list therefore holds the names only, whatever the values look like
/// (any length, spaces, quotes).
/// </summary>
/// <remarks>
/// The values flow with the async call (<see cref="AsyncLocal{T}"/>), so <see cref="IHostCommandExecutor"/> keeps its
/// one signature and every gateway rule on its callers. <see cref="Provide"/> wraps exactly one executor call; nested
/// calls in that call see the same values. An executor that does not deliver them (a test double) runs the command
/// without the variables — it fails, it never leaks.
/// </remarks>
public static partial class HostCommandSecrets
{
    /// <summary>Exit code of the remote shell when it could not read a value from standard input (SSH).</summary>
    internal const int ReadFailedExitCode = 97;

    private static readonly AsyncLocal<IReadOnlyList<KeyValuePair<string, string>>?> Ambient = new();

    /// <summary>The secrets the current host command needs; empty outside <see cref="Provide"/>.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Current => Ambient.Value ?? [];

    /// <summary>Makes <paramref name="secrets"/> the values of the next host command(s) in this flow until the handle is
    /// disposed. A name is an environment variable name (<c>A–Z</c>, digits, <c>_</c>, not starting with a digit); a
    /// value holds no line break and no NUL (it travels as one line on SSH and as an environment entry). Anything else
    /// is refused before a command starts.</summary>
    public static IDisposable Provide(IEnumerable<KeyValuePair<string, string>> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var list = secrets.ToList();
        foreach (var (name, value) in list)
        {
            if (!Name().IsMatch(name ?? ""))
                throw new ArgumentException("A host command secret needs an environment variable name.", nameof(secrets));
            if (value is null || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new ArgumentException($"The value of {name} cannot be passed to a host command (line break or NUL).",
                    nameof(secrets));
        }
        if (list.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != list.Count)
            throw new ArgumentException("A host command secret is named twice.", nameof(secrets));
        var previous = Ambient.Value;
        Ambient.Value = list;
        return new Restore(previous);
    }

    /// <summary>What the remote shell runs before the command on SSH: each value read from standard input into its
    /// variable and exported (names only — the values follow on standard input, <see cref="StandardInput"/>). A value
    /// that cannot be read stops the command before it starts.</summary>
    internal static string SshPrefix(IReadOnlyList<KeyValuePair<string, string>> secrets) =>
        string.Concat(secrets.Select(s =>
            $"IFS= read -r {s.Key} || exit {ReadFailedExitCode}; export {s.Key}; "));

    /// <summary>The standard input of <c>ssh</c> for <see cref="SshPrefix"/>: one value per line, in order.</summary>
    internal static string StandardInput(IReadOnlyList<KeyValuePair<string, string>> secrets) =>
        string.Concat(secrets.Select(s => s.Value + "\n"));

    [GeneratedRegex("^[A-Z_][A-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();

    private sealed class Restore(IReadOnlyList<KeyValuePair<string, string>>? previous) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Ambient.Value = previous;
        }
    }
}
