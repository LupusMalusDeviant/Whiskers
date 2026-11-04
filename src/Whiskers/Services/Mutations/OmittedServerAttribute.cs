using System.Collections.Concurrent;
using System.Reflection;

namespace Whiskers.Services.Mutations;

/// <summary>
/// Declares where a guarded operation acts when its <c>serverId</c> argument is omitted (R1-48, review rounds 2 and 3).
/// The scope guard resolves an omitted server by this declaration — and so does the implementation, through
/// <see cref="OmittedServer"/>, so the two cannot drift apart. On a method it wins over one on its interface.
/// <list type="bullet">
/// <item><c>[OmittedServer]</c> — the configured default server of the registry (the Docker connection manager's
/// routing).</item>
/// <item><c>[OmittedServer("local")]</c> — that fixed server id (resolved through the registry like a named one).</item>
/// </list>
/// A guarded operation with a nullable <c>serverId</c> and no declaration matches no decided server when the argument
/// is omitted (fail closed); <c>MutationGatewayCoverageTests</c> requires a declaration on every such operation.
/// Omitted means <c>null</c> — at the guard and in <see cref="OmittedServer.For{TService}"/> alike; an empty string names
/// no server and is refused by the guard (review round 4). The guard hands the implementation the server it checked
/// (the canonical id), so the routing cannot change between the check and the call.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Interface, Inherited = false)]
public sealed class OmittedServerAttribute : Attribute
{
    /// <summary>The configured default server.</summary>
    public OmittedServerAttribute() { }

    /// <summary>The fixed server <paramref name="serverId"/>.</summary>
    public OmittedServerAttribute(string serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ServerId = serverId;
    }

    /// <summary>The fixed server id, or <c>null</c> for the configured default server.</summary>
    public string? ServerId { get; }
}

/// <summary>Reads <see cref="OmittedServerAttribute"/> declarations — for the scope guard and for implementations.</summary>
public static class OmittedServer
{
    /// <summary>The id Whiskers' own host has in the server registry; the fixed fallback of the host-side services.</summary>
    public const string Local = "local";

    private static readonly ConcurrentDictionary<MethodInfo, OmittedServerAttribute?> Cache = new();

    /// <summary>The declaration of interface method <paramref name="method"/> (its own, else its interface's), or null.</summary>
    public static OmittedServerAttribute? Of(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return Cache.GetOrAdd(method, static m =>
            m.GetCustomAttribute<OmittedServerAttribute>(inherit: false)
            ?? m.DeclaringType?.GetCustomAttribute<OmittedServerAttribute>(inherit: false));
    }

    /// <summary>
    /// The server an implementation of <typeparamref name="TService"/> acts on: <paramref name="serverId"/>, or the fixed
    /// server its interface declares for an omitted argument. Throws when the interface declares no fixed server — such
    /// an implementation routes through the registry's default instead.
    /// </summary>
    public static string For<TService>(string? serverId) where TService : class
    {
        // Only null is "omitted"; an empty name is passed on as it is (it names no server, and the guard refuses it).
        if (serverId is not null) return serverId;
        return typeof(TService).GetCustomAttribute<OmittedServerAttribute>(inherit: false)?.ServerId
               ?? throw new InvalidOperationException($"{typeof(TService).Name} declares no fixed server for an omitted serverId.");
    }
}
