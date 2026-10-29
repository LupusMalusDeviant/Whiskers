using System.Runtime.CompilerServices;
using Whiskers.Models;
using Whiskers.Services.Server;

namespace Whiskers.Services.Mutations;

/// <summary>
/// Recognises results that report a failure without throwing — <c>false</c>, a failed <see cref="CommandResult"/>,
/// a <see cref="QueryResult"/> with an error, a tuple whose first item is <c>false</c> — so the gateway records them
/// as <c>Failed</c> instead of <c>Executed</c>. The reason is generic on purpose: the service's own error text can
/// carry command output or credentials, and the caller still gets the value to show it.
/// </summary>
public static class MutationResults
{
    public static string? FailureOf(object? value) => value switch
    {
        null => null,
        bool ok => ok ? null : "The action reported no success.",
        CommandResult c => c.Success ? null : $"The command failed (exit code {c.ExitCode}).",
        QueryResult q => q.Error is null ? null : "The query failed.",
        ITuple { Length: > 0 } t when t[0] is bool ok => ok ? null : "The action reported a failure.",
        // A result object that says whether it succeeded (a deploy result, a git deploy outcome) is believed (R1-21):
        // a page must not count "the delegate returned" as success.
        _ => SuccessOf(value) is false ? "The action reported a failure." : null,
    };

    private static bool? SuccessOf(object value) =>
        value.GetType().GetProperty("Success", typeof(bool)) is { CanRead: true } p
        && p.GetIndexParameters().Length == 0
            ? (bool?)p.GetValue(value)
            : null;

    /// <summary>Marks the current gateway operation failed when <paramref name="value"/> reports a failure.</summary>
    public static T Report<T>(T value)
    {
        if (FailureOf(value) is { } reason) MutationScope.Fail(reason);
        return value;
    }
}
