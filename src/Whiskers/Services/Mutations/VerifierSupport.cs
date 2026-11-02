using System.Text.Json;
using ModelContextProtocol.Protocol;
using Whiskers.Services.Server;

namespace Whiskers.Services.Mutations;

/// <summary>What the R1-37 verifiers share: reading a call's arguments and answer the same way for every surface (an MCP
/// tool answers with a <see cref="CallToolResult"/>, a page with its own value), host reads that tell "not there" from
/// "could not look", and polling an effect that a remote API applies asynchronously.</summary>
public static class VerifierSupport
{
    /// <summary>A string argument of the call, or <c>null</c>.</summary>
    public static string? Arg(MutationContext context, string name) =>
        context.Request.Arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A whole-number argument (also given as a numeric string), or <c>null</c>.</summary>
    public static long? LongArg(MutationContext context, string name)
    {
        if (!context.Request.Arguments.TryGetValue(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(value.GetString(), out var n) => n,
            _ => null,
        };
    }

    /// <summary>A boolean argument (also given as "true"/"false"), or <c>null</c>.</summary>
    public static bool? BoolArg(MutationContext context, string name)
    {
        if (!context.Request.Arguments.TryGetValue(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => b,
            _ => null,
        };
    }

    /// <summary>What the call reported for its read-back (<see cref="MutationScope.ReadBack"/>), or <c>null</c>.</summary>
    public static string? Fact(MutationContext context, string key) => context.ReadBackFacts.GetValueOrDefault(key);

    /// <summary>The server the call ran on: the target the gateway resolved, else the argument.</summary>
    public static string? ServerId(MutationContext context) =>
        context.Request.Target.ServerId is { Length: > 0 } id ? id : Arg(context, "serverId");

    /// <summary>The text of the call's answer: a string, or the text blocks of an MCP tool result.</summary>
    public static string? ResultText(object? result) => result switch
    {
        string s => s,
        CallToolResult tool => string.Join('\n', tool.Content.OfType<TextContentBlock>().Select(c => c.Text)),
        _ => null,
    };

    /// <summary>A read on the host. A command that could not run at all (no connection, a timeout: exit code -1) says
    /// nothing about the effect: the read-back failed with the state unknown.</summary>
    public static async Task<CommandResult> HostReadAsync(IServiceProvider services, string serverId, string command,
        CancellationToken ct)
    {
        var executor = services.GetRequiredService<IHostCommandExecutor>();
        var result = await executor.ExecuteAsync(serverId, command, TimeSpan.FromSeconds(30), ct);
        if (result.ExitCode == -1)
            throw new MutationVerificationFailedException(
                $"the host could not be read ({FirstLine(result.Error)}).", stateUnknown: true);
        return result;
    }

    /// <summary>A host read that must succeed: any other exit is a failed read-back with the state unknown.</summary>
    public static async Task<string> HostOutputAsync(IServiceProvider services, string serverId, string command,
        CancellationToken ct)
    {
        var result = await HostReadAsync(services, serverId, command, ct);
        if (!result.Success)
            throw new MutationVerificationFailedException(
                $"the read-back command failed (exit code {result.ExitCode}: {FirstLine(result.Error)}).", stateUnknown: true);
        return result.Output;
    }

    /// <summary>Reads until <paramref name="done"/> holds or <paramref name="budget"/> is spent (the capability's deadline
    /// still bounds it); returns the last read. For effects a provider applies after it accepted the call.</summary>
    public static async Task<T> PollAsync<T>(Func<CancellationToken, Task<T>> read, Func<T, bool> done, TimeSpan budget,
        TimeSpan interval, CancellationToken ct)
    {
        var until = DateTime.UtcNow + budget;
        while (true)
        {
            var value = await read(ct);
            if (done(value) || DateTime.UtcNow >= until) return value;
            await Task.Delay(interval, ct);
        }
    }

    internal static string FirstLine(string? text)
    {
        var line = (text ?? "").Split('\n', 2)[0].Trim();
        return line.Length == 0 ? "no output" : line.Length > 200 ? line[..200] + "…" : line;
    }
}

/// <summary>The base of a verifier that resolves what it reads from the root provider when it runs (the services it reads
/// are themselves guarded by the gateway that holds the verifiers).</summary>
public abstract class ServiceVerifier(IServiceProvider services) : IMutationVerifier
{
    protected IServiceProvider Services { get; } = services;

    public abstract string CapabilityId { get; }

    public async Task<VerificationStatus> VerifyAsync(MutationContext context, object? result, CancellationToken ct)
    {
        await CheckAsync(context, result, ct);
        return VerificationStatus.Verified;
    }

    /// <summary>Returns when the effect is there; throws <see cref="MutationVerificationFailedException"/> when it is not,
    /// <see cref="MutationNotVerifiableException"/> when this call cannot be read back.</summary>
    protected abstract Task CheckAsync(MutationContext context, object? result, CancellationToken ct);

    protected string RequireServer(MutationContext context) =>
        VerifierSupport.ServerId(context) ?? throw new MutationNotVerifiableException("the call named no server to read back.");

    /// <summary>A service of a module that may be off: without it there is nothing to read with.</summary>
    protected T Module<T>(string module) where T : class =>
        Services.GetService<T>() ?? throw new MutationNotVerifiableException($"the {module} module is not enabled.");
}
