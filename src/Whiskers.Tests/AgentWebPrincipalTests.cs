using System.Text.RegularExpressions;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Auth;

namespace Whiskers.Tests;

/// <summary>
/// Fable review (Medium): the agent pages built the signed-in user's principal without the sign-in credential and the
/// web session (contract 0014, D2), so an agent call or approval from the web named no session to trace or revoke.
/// </summary>
public class AgentWebPrincipalTests
{
    private sealed class SignedIn : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("ops@example.org");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(AppRole.Operator);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(required <= AppRole.Operator);
        public Task<(string? CredentialId, string? SessionId)> GetPrincipalFieldsAsync() =>
            Task.FromResult<(string?, string?)>(("local:ops@example.org", "sid-42"));
    }

    [Fact]
    public async Task The_web_user_principal_carries_credential_and_session()
    {
        var principal = await AgentPrincipalResolver.ForWebUserAsync(new SignedIn());

        Assert.Equal(AgentPrincipalKind.WebUser, principal.Kind);
        Assert.Equal("ops@example.org", principal.UserEmail);
        Assert.Equal(Whiskers.Services.Mutations.CapabilityCatalog.LevelFor(AppRole.Operator), principal.PermissionLevel);
        Assert.Equal("local:ops@example.org", principal.CredentialId);
        Assert.Equal("sid-42", principal.WebSessionId);
    }

    [Fact]
    public void No_page_builds_a_web_user_principal_of_its_own()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx"))) dir = dir.Parent;
        var components = Path.Combine(dir!.FullName, "src", "Whiskers", "Components");
        var own = new Regex(@"AgentPrincipalKind\.WebUser");
        var offenders = Directory.EnumerateFiles(components, "*.razor*", SearchOption.AllDirectories)
            .Where(f => own.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }
}
