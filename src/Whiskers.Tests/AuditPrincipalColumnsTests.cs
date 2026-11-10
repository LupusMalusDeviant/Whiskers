using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Auth;
using Whiskers.Services.Backup;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0014 (D7, INV-01): every audit row Whiskers writes outside the gateway carries the structured principal —
/// kind, id and, where the scheme defines them, credential, session and on-behalf-of — with the display actor and the
/// actor type derived from it. The string overloads stay for the MCP tools' enrichment only.
/// </summary>
public sealed class AuditPrincipalColumnsTests : IDisposable
{
    private readonly AuditTrailFixture _trail = new(AuditHashChainTests.VaultKey);

    public void Dispose() => _trail.Dispose();

    private async Task<AuditLogEntity> LastRowAsync()
    {
        using var scope = _trail.Scope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .OrderByDescending(a => a.Id).FirstAsync();
    }

    [Fact]
    public async Task The_principal_overload_writes_every_principal_column_and_derives_the_actor()
    {
        var owner = new MutationPrincipal(MutationPrincipalKind.Human, "user:ops@example.com", "Olivia",
            McpPermissionLevels.Admin, CredentialId: "local:u1", SessionId: "sid-1");
        var principal = new MutationPrincipal(MutationPrincipalKind.Webhook, "webhook:w1", "Webhook 'deploy'",
            McpPermissionLevels.Write, CredentialId: "webhook-secret:w1@2", OnBehalfOf: owner, SessionId: "run-1",
            DelegatedBy: "user:ops@example.com");

        Assert.True(await _trail.Audit.TryLogAsync(principal, "test.action", "webhook", "w1", "deploy"));

        var row = await LastRowAsync();
        Assert.Equal("Webhook", row.PrincipalKind);
        Assert.Equal("webhook:w1", row.PrincipalId);
        Assert.Equal("webhook-secret:w1@2", row.CredentialId);
        Assert.Equal("run-1", row.PrincipalSessionId);
        Assert.Equal("user:ops@example.com", row.OnBehalfOfId);
        Assert.Equal("Human", row.OnBehalfOfKind);
        Assert.Equal("user:ops@example.com", row.DelegatedBy);
        Assert.Equal("Webhook 'deploy'", row.Actor);
        Assert.Equal("webhook", row.ActorType);
    }

    /// <summary>The credential ids are ids, not secrets: the sinks' redaction keeps them in the column, also a webhook
    /// secret's generation.</summary>
    [Theory]
    [InlineData("webhook-secret:w1@2")]
    [InlineData("binding:b_0123456789abcdef")]
    [InlineData("local:9c1f6c1e-1d7a-4c39-9a51-3f0f7d1e2b44")]
    public async Task Credential_ids_survive_the_sinks_redaction(string credential)
    {
        using var trail = new AuditTrailFixture(AuditHashChainTests.VaultKey, redacting: true);
        var principal = new MutationPrincipal(MutationPrincipalKind.Human, "user:ops@example.com", "ops@example.com",
            McpPermissionLevels.Write, CredentialId: credential);

        Assert.True(await trail.Audit.TryLogAsync(principal, "test.action", "audit", "x", "x",
            details: JsonSerializer.Serialize(new { principal = new { signIn = credential } })));

        using var scope = trail.Scope();
        var row = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .OrderByDescending(a => a.Id).FirstAsync();
        Assert.Equal(credential, row.CredentialId);
        // In the free-text details the value redactor still blanks anything that reads like "secret:<value>" — the column
        // is the record of a webhook's secret generation; a person's binding or local account is kept in both.
        if (!credential.StartsWith(PrincipalCredentials.WebhookSecretPrefix, StringComparison.Ordinal))
            Assert.Contains(credential, row.Details);
    }

    public static TheoryData<string, string, string> Factories() => new()
    {
        { "retention", "System", "system:audit-retention" },
        { "suspension", "System", "system:authority-check" },
        { "startup", "System", "system:startup" },
        { "cli-recovery", "System", "system:host-cli:recover-local-account" },
        { "cli-binding", "System", "system:host-cli:confirm-identity-binding" },
        { "person", "Human", "user:ops@example.com" },
        { "anonymous", "Human", "anonymous" },
    };

    [Theory]
    [MemberData(nameof(Factories))]
    public async Task Each_principal_of_a_row_outside_the_gateway_is_in_the_scheme(string factory, string kind, string id)
    {
        var principal = factory switch
        {
            "retention" => AuditPrincipals.System("audit-retention"),
            "suspension" => AuditPrincipals.System("authority-check"),
            "startup" => AuditPrincipals.System("startup"),
            "cli-recovery" => AuditPrincipals.HostCli("recover-local-account", "cli (--recover-local-account)"),
            "cli-binding" => AuditPrincipals.HostCli("confirm-identity-binding", "cli (--confirm-identity-binding)"),
            "person" => AuditPrincipals.Person("Ops@Example.com"),
            _ => AuditPrincipals.Person(null),
        };

        Assert.True(await _trail.Audit.TryLogAsync(principal, "test.action", "audit", "x", "x"));

        var row = await LastRowAsync();
        Assert.Equal(kind, row.PrincipalKind);
        Assert.Equal(id, row.PrincipalId);
        Assert.Equal(Enum.Parse<MutationPrincipalKind>(kind), PrincipalIds.KindOf(row.PrincipalId));
        if (kind == "System") Assert.Equal(ProcessIdentity.Current, row.PrincipalSessionId);
    }

    [Fact]
    public async Task The_retention_row_is_written_by_the_retention_component()
    {
        await _trail.LogAsync(2, DateTime.UtcNow.AddDays(-400));
        await _trail.Chain.CheckpointAsync();

        await _trail.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365);

        using var scope = _trail.Scope();
        var row = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .SingleAsync(a => a.Action == AuditChain.RetentionAction);
        Assert.Equal("System", row.PrincipalKind);
        Assert.Equal("system:audit-retention", row.PrincipalId);
        Assert.Equal("system", row.Actor);
    }

    [Fact]
    public async Task A_restore_applied_at_start_is_whiskers_acting_for_its_requester()
    {
        var root = Path.Combine(Path.GetTempPath(), $"inv01-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DataPathOptions(root);
            File.WriteAllText(paths.RestoreAppliedNote, JsonSerializer.Serialize(new RestoreMarker
            {
                Provider = "sqlite", Actor = "ops@example.com", Archive = "a.tar.gz", AppliedAtUtc = DateTime.UtcNow,
            }));

            await RestoreBootHandler.RecordAppliedAsync(paths, _trail.Audit);

            var row = await LastRowAsync();
            Assert.Equal(RestoreBootHandler.RestoredAction, row.Action);
            Assert.Equal("System", row.PrincipalKind);
            Assert.Equal("system:startup", row.PrincipalId);
            Assert.Equal("user:ops@example.com", row.OnBehalfOfId);
            Assert.Equal("ops@example.com", row.Actor);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_recorded_read_names_the_signed_in_person_with_credential_and_session()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Email, "ops@example.com"),
                new Claim(AuthConstants.BindingIdClaim, "b-7"),
                new Claim(AuthConstants.SessionIdClaim, "sid-7"),
            ], AuthConstants.FederatedScheme)),
        };

        Assert.True(await _trail.Audit.TryLogAsync(AuditPrincipals.OfHttpContext(context), AuditExport.Action, "audit",
            "audit-log", "Audit log"));

        var row = await LastRowAsync();
        Assert.Equal("Human", row.PrincipalKind);
        Assert.Equal("user:ops@example.com", row.PrincipalId);
        Assert.Equal("binding:b-7", row.CredentialId);
        Assert.Equal("sid-7", row.PrincipalSessionId);
        Assert.Equal("web", row.ActorType);
    }

    /// <summary>Every call of the audit service in the application passes a principal; the string overloads are left
    /// to the MCP tools' enrichment (<see cref="ToolAuditLog"/> and the tools that call it).</summary>
    [Fact]
    public void No_row_outside_the_tools_enrichment_is_written_with_a_string_actor()
    {
        var src = Path.Combine(AutomationActorSchemeTests.SourceRoot(), "src", "Whiskers");
        var call = new Regex(@"\.(LogAsync|TryLogAsync|TryLogAtAsync)\(\s*([^,()]*(\([^()]*\))?[^,()]*)\s*,", RegexOptions.Singleline);
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (rel.StartsWith("obj/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal)) continue;
            if (rel is "Services/AuditLog/AuditLogService.cs" or "Services/AuditLog/IAuditLogService.cs"
                or "Services/AuditLog/ToolAuditLog.cs") continue;
            var text = File.ReadAllText(file);
            foreach (Match m in call.Matches(text))
            {
                var method = m.Groups[1].Value;
                var first = m.Groups[2].Value;
                // The MCP tools' own reports go to IToolAuditLog, which enriches the gateway event (R1-20).
                if (method == "LogAsync" && rel.StartsWith("Mcp/Tools/", StringComparison.Ordinal)
                    && text.Contains("IToolAuditLog auditLog", StringComparison.Ordinal)) continue;
                if (method == "TryLogAtAsync" || !first.Contains("principal", StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{rel}: {m.Value.Trim()}");
            }
        }
        Assert.Empty(offenders);
    }
}
