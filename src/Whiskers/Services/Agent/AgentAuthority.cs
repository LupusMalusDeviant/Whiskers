using Microsoft.Extensions.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Auth;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.Agent;

/// <summary>
/// The authority a running agent session holds right now (R1-26, INV-08). A session used to keep the principal it was
/// started with for its whole life; now every tool call re-reads it. Never more than at the start: the level is the
/// lower of the two, an allow-list only narrows.
/// </summary>
public interface IAgentAuthority
{
    /// <summary>The principal as it stands now, or the reason it was revoked: a deleted or disabled key, a user whose
    /// account was deleted or who may no longer sign in, or a store that could not be read (fail closed).</summary>
    AgentAuthorityCheck Current(AgentPrincipal started);
}

/// <summary>The outcome of <see cref="IAgentAuthority.Current"/>: <paramref name="RevokedReason"/> is set when the run
/// may not take another step.</summary>
public sealed record AgentAuthorityCheck(AgentPrincipal Principal, string? RevokedReason = null)
{
    public bool Revoked => RevokedReason is not null;
}

/// <summary>
/// Keys are read from <see cref="IMcpPermissionService"/>, users through <see cref="IAutomationOwnerResolver"/> (the
/// same view of a deleted account, the whitelist and the role as an automation owner, R1-04). An AI trigger's run is
/// left as it is: the gateway re-checks the trigger's owner for each of its mutations (R1-13). The trusted-LAN
/// <c>Auth:Disabled</c> local user stays as started.
/// </summary>
public sealed class AgentAuthority : IAgentAuthority
{
    private readonly IMcpPermissionService _keys;
    private readonly IAutomationOwnerResolver _owners;
    private readonly IRoleService _roles;
    private readonly bool _authDisabled;

    public AgentAuthority(IMcpPermissionService keys, IAutomationOwnerResolver owners, IRoleService roles,
        IConfiguration? configuration = null)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _owners = owners ?? throw new ArgumentNullException(nameof(owners));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _authDisabled = configuration?.GetValue<bool>("Auth:Disabled") == true;
    }

    public AgentAuthorityCheck Current(AgentPrincipal started)
    {
        ArgumentNullException.ThrowIfNull(started);
        try
        {
            return started.Kind switch
            {
                AgentPrincipalKind.McpKey => KeyNow(started),
                AgentPrincipalKind.WebUser => UserNow(started),
                _ => new AgentAuthorityCheck(started),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentAuthorityCheck(started,
                $"The authority behind this run could not be re-checked ({ex.GetType().Name}).");
        }
    }

    private AgentAuthorityCheck KeyNow(AgentPrincipal started)
    {
        if (string.IsNullOrWhiteSpace(started.McpKeyId))
            return new AgentAuthorityCheck(started, "The run names no MCP key.");
        var key = _keys.GetPermissionData().ApiKeys.FirstOrDefault(k => k.Id == started.McpKeyId);
        if (key is null) return new AgentAuthorityCheck(started, "The MCP key that started this run was deleted.");
        if (!key.Enabled) return new AgentAuthorityCheck(started, "The MCP key that started this run was disabled.");
        return new AgentAuthorityCheck(started with
        {
            PermissionLevel = Lower(started.PermissionLevel, key.PermissionLevel),
            AllowedTools = Narrow(started.AllowedTools, key.AllowedTools),
        });
    }

    private AgentAuthorityCheck UserNow(AgentPrincipal started)
    {
        if (string.IsNullOrWhiteSpace(started.UserEmail))
            return new AgentAuthorityCheck(started, "The run names no user.");
        if (_authDisabled && string.Equals(started.UserEmail, CurrentUserService.AuthDisabledEmail, StringComparison.OrdinalIgnoreCase))
            return new AgentAuthorityCheck(started);
        var ownerId = PrincipalIds.User(started.UserEmail);
        var state = _owners.Describe(ownerId).State;
        if (state != OwnerState.Active)
            return new AgentAuthorityCheck(started, state switch
            {
                OwnerState.AccountDeleted => "The account of the user who started this run was deleted.",
                OwnerState.NotAllowedToSignIn => "The user who started this run may no longer sign in.",
                _ => "The user who started this run could not be verified.",
            });
        // The account state was read once above (it reads the account store); the level comes from the role alone,
        // mapped as the owner resolver maps it.
        var level = _roles.GetRole(started.UserEmail) switch
        {
            AppRole.Admin => McpPermissionLevels.Admin,
            AppRole.Operator => McpPermissionLevels.Write,
            _ => McpPermissionLevels.Read,
        };
        return new AgentAuthorityCheck(started with { PermissionLevel = Lower(started.PermissionLevel, level) });
    }

    private static string Lower(string a, string b)
    {
        var x = McpPermissionLevels.Normalize(a);
        var y = McpPermissionLevels.Normalize(b);
        return McpPermissionLevels.GetRank(x) <= McpPermissionLevels.GetRank(y) ? x : y;
    }

    /// <summary>The tools allowed now: an allow-list only narrows; <c>null</c> on both sides means the level's defaults.</summary>
    private static IReadOnlyList<string>? Narrow(IReadOnlyList<string>? started, IReadOnlyList<string>? now) =>
        (started, now) switch
        {
            (null, null) => null,
            (null, _) => now!.ToList(),
            (_, null) => started,
            _ => started.Intersect(now!, StringComparer.Ordinal).ToList(),
        };
}
