using Whiskers.Services.Templates;
using Xunit;

namespace Whiskers.Tests;

public class TemplateServiceTests
{
    private static readonly TemplateService Service = new();

    [Fact]
    public void Plausible_HasDatabaseWiring()
    {
        var t = Service.GetTemplate("plausible");
        Assert.NotNull(t);
        Assert.Contains("DATABASE_URL=postgres://", t!.ComposeContent);
        Assert.Contains("CLICKHOUSE_DATABASE_URL=", t.ComposeContent);
        // The Postgres password must be a required variable, not a hardcoded default.
        Assert.Contains("DB_PASSWORD", t.RequiredEnvVars);
        Assert.DoesNotContain("POSTGRES_PASSWORD=postgres", t.ComposeContent);
    }

    [Fact]
    public void N8n_DropsDeadBasicAuth()
    {
        var t = Service.GetTemplate("n8n");
        Assert.NotNull(t);
        // N8N_BASIC_AUTH_* was removed in n8n 1.x — no such env entry may remain (the explanatory
        // comment that references the old name by concept is fine; we assert on the YAML list form).
        Assert.DoesNotContain("- N8N_BASIC_AUTH", t!.ComposeContent);
        Assert.Contains("N8N_ENCRYPTION_KEY", t.RequiredEnvVars);
    }

    [Fact]
    public void RocketChat_UsesMongoReplicaSet()
    {
        var t = Service.GetTemplate("rocketchat");
        Assert.NotNull(t);
        // Rocket.Chat needs MongoDB as a replica set (oplog) or it won't start.
        Assert.Contains("replicaSet=rs0", t!.ComposeContent);
        Assert.Contains("MONGODB_REPLICA_SET_MODE", t.ComposeContent);
    }

    [Fact]
    public void AllTemplates_RequiredVarsAreNamesNotValues()
    {
        // Sanity: required entries are variable NAMES, never "NAME=value" (no hardcoded secret leaks).
        foreach (var t in Service.GetTemplates())
            Assert.All(t.RequiredEnvVars, v => Assert.DoesNotContain("=", v));
    }
}
