using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>Contract 0006: the rule that asks for a confirmation of an exposure change, its configuration and the
/// redaction of what it reads.</summary>
public sealed class ExposureChangeRuleTests
{
    private static GuardrailPolicy Admin() => new()
    {
        MaxAutonomousLevel = McpPermissionLevels.Admin,
        RequireConfirmationForWrites = false,
    };

    private static GuardrailRequest Request(GuardrailPolicy policy, params (string Key, string Value)[] args) =>
        new("deploy_compose", McpPermissionLevels.Admin,
            args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value)),
            new AgentContext("s", new AgentPrincipal(AgentPrincipalKind.McpKey, "key", McpPermissionLevels.Admin, null),
                AgentOrigin.McpDirect, policy));

    private static string Resolved(ExposureKind kind, string key, string value, string service = "web") =>
        ComposeExposures.Identity(service, kind, key, value);

    private static GuardrailVerdict Verdict(GuardrailPolicy policy, string identity, out string reason, bool withheld = false) =>
        new ExposureChangeRule().Evaluate(Request(policy,
            ((withheld ? ExposureChangeRule.ResolvedWithheldPrefix : ExposureChangeRule.ResolvedPrefix) + "0", identity)), out reason);

    [Theory]
    [InlineData(ExposureKind.RouteLabel, "docktail.funnel.enable", "true", GuardrailVerdict.Confirm)]       // public
    [InlineData(ExposureKind.RouteLabel, "traefik.http.routers.a.rule", "Host(`a.x`)", GuardrailVerdict.Confirm)]
    [InlineData(ExposureKind.RouteLabel, "caddy", "a.x", GuardrailVerdict.Confirm)]
    [InlineData(ExposureKind.HostNetwork, "network_mode", "host", GuardrailVerdict.Confirm)]               // host
    [InlineData(ExposureKind.Privileged, "privileged", "true", GuardrailVerdict.Confirm)]
    [InlineData(ExposureKind.HostNamespace, "pid", "host", GuardrailVerdict.Confirm)]
    [InlineData(ExposureKind.PublishedPort, "80/tcp", "0.0.0.0:8080", GuardrailVerdict.Allow)]            // published
    [InlineData(ExposureKind.RouteLabel, "docktail.service.enable", "true", GuardrailVerdict.Allow)]
    [InlineData(ExposureKind.RouteLabel, "com.example.team", "blue", GuardrailVerdict.Allow)]             // no prefix
    public void The_default_classes_public_and_host_need_a_confirmation(ExposureKind kind, string key, string value,
        GuardrailVerdict expected) =>
        Assert.Equal(expected, Verdict(Admin(), Resolved(kind, key, value), out _));

    [Fact]
    public void Each_class_is_confirmed_only_when_listed()
    {
        var policy = Admin();
        policy.ExposureConfirmClasses = [ExposureClasses.Published];

        Assert.Equal(GuardrailVerdict.Confirm, Verdict(policy, Resolved(ExposureKind.PublishedPort, "80/tcp", "0.0.0.0:80"), out _));
        Assert.Equal(GuardrailVerdict.Allow, Verdict(policy, Resolved(ExposureKind.Privileged, "privileged", "true"), out _));
    }

    [Fact]
    public void An_empty_list_means_no_confirmation()
    {
        var policy = Admin();
        policy.ExposureConfirmClasses = [];

        Assert.Equal(GuardrailVerdict.Allow, Verdict(policy, Resolved(ExposureKind.RouteLabel, "docktail.funnel.enable", "true"), out _));
    }

    [Fact]
    public void The_reason_names_the_service_and_the_label_but_never_its_value()
    {
        Verdict(Admin(), Resolved(ExposureKind.RouteLabel, "docktail.funnel.enable", "s3cr3t", "shop"), out var reason);

        Assert.Equal("shop becomes public through Tailscale Funnel (docktail.funnel.enable).", reason);
        Assert.DoesNotContain("s3cr3t", reason);
    }

    [Fact]
    public void A_withheld_entry_is_classified_by_its_real_key_and_named_by_kind_only()
    {
        var verdict = Verdict(Admin(), Resolved(ExposureKind.RouteLabel, "docktail.funnel.enable", "from-env"), out var reason,
            withheld: true);

        Assert.Equal(GuardrailVerdict.Confirm, verdict);
        Assert.DoesNotContain("docktail", reason);
        Assert.DoesNotContain("from-env", reason);
        Assert.Contains("label (withheld)", reason);
    }

    [Fact]
    public void An_entry_that_cannot_be_read_needs_a_confirmation() =>
        Assert.Equal(GuardrailVerdict.Confirm, Verdict(Admin(), "garbage", out _));

    [Fact]
    public void Operator_prefixes_come_from_the_deciding_policy()
    {
        var policy = Admin();
        policy.ExposureLabelPrefixes = ["my.proxy."];

        Assert.Equal(GuardrailVerdict.Confirm, Verdict(policy, Resolved(ExposureKind.RouteLabel, "my.proxy.host", "x"), out _));
        // The built-in prefixes are no longer the policy's: a Funnel label is no exposure under it.
        Assert.Equal(GuardrailVerdict.Allow, Verdict(policy, Resolved(ExposureKind.RouteLabel, "docktail.funnel.enable", "true"), out _));
    }

    [Fact]
    public void It_is_part_of_the_agent_rule_set_and_not_of_the_one_for_persons()
    {
        var request = Request(Admin(), (ExposureChangeRule.ResolvedPrefix + "0",
            Resolved(ExposureKind.RouteLabel, "docktail.funnel.enable", "true")));

        var agent = GuardrailEngine.CreateDefault().Evaluate(request);
        var person = GuardrailEngine.CreateForHumans().Evaluate(request);

        Assert.Equal(GuardrailVerdict.Confirm, agent.Verdict);
        Assert.Contains(ExposureChangeRule.RuleId, agent.MatchedRuleIds);
        Assert.Equal(GuardrailVerdict.Allow, person.Verdict);
    }

    [Fact]
    public void Only_the_resolved_target_is_read_and_a_callers_own_resolved_keys_are_stripped()
    {
        // A caller cannot hand the rule an exposure (nor hide one): the gateway drops every "__" key it was sent and adds
        // the resolved target's exposures itself.
        var sent = new Dictionary<string, JsonElement>
        {
            [ExposureChangeRule.ResolvedPrefix + "0"] = JsonSerializer.SerializeToElement("garbage"),
            ["serverId"] = JsonSerializer.SerializeToElement("local"),
        };
        var target = new MutationTarget("server", "local", ServerId: "local");
        var principal = new MutationPrincipal(MutationPrincipalKind.ApiKey, "k", "k", McpPermissionLevels.Admin);

        var args = GuardrailMutationPolicy.WithResolvedTarget(new MutationRequest("compose.deploy", principal, MutationSurface.Mcp, target, sent));

        Assert.DoesNotContain(args, a => a.Key.StartsWith(ExposureChangeRule.ResolvedPrefix, StringComparison.Ordinal));
        Assert.Equal(GuardrailVerdict.Allow, new ExposureChangeRule().Evaluate(
            new GuardrailRequest("deploy_compose", McpPermissionLevels.Admin, args,
                new AgentContext("s", new AgentPrincipal(AgentPrincipalKind.McpKey, "k", McpPermissionLevels.Admin, null),
                    AgentOrigin.McpDirect, Admin())), out _));
    }

    [Fact]
    public void Exposure_arguments_are_left_to_this_rule()
    {
        // The protected-resource matcher and the forbidden-argument patterns do not read the exposure lists: a
        // Funnel label's key is no container name.
        var policy = Admin();
        policy.ForbiddenArgPatterns = ["funnel"];
        policy.ProtectedResources = ["docktail*"];
        var request = Request(policy,
            ("exposure", Resolved(ExposureKind.RouteLabel, "docktail.funnel.enable", "true")),
            ("exposureWithheld", Resolved(ExposureKind.RouteLabel, "docktail.funnel.port", "443")),
            (ExposureChangeRule.ResolvedPrefix + "0", Resolved(ExposureKind.RouteLabel, "docktail.funnel.enable", "true")));

        Assert.Equal(GuardrailVerdict.Allow, new ForbiddenArgumentRule().Evaluate(request, out _));
        Assert.Equal(GuardrailVerdict.Allow, new ProtectedResourceRule().Evaluate(request, out _));
    }

    // ---------------------------------------------------------------- configuration

    [Fact]
    public void A_stored_policy_without_the_new_fields_loads_the_defaults()
    {
        var json = JsonSerializer.Serialize(new GuardrailPolicy());
        var stripped = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .Where(p => p.Key is not (nameof(GuardrailPolicy.ExposureLabelPrefixes) or nameof(GuardrailPolicy.ExposureConfirmClasses)))
            .ToDictionary(p => p.Key, p => p.Value);

        var old = JsonSerializer.Deserialize<GuardrailPolicy>(JsonSerializer.Serialize(stripped))!;
        var nulls = JsonSerializer.Deserialize<GuardrailPolicy>(
            """{"ExposureLabelPrefixes":null,"ExposureConfirmClasses":null}""")!;

        foreach (var policy in new[] { old, nulls })
        {
            Assert.Equal(GuardrailPolicy.DefaultExposureLabelPrefixes, policy.EffectiveExposureLabelPrefixes);
            Assert.Equal(GuardrailPolicy.DefaultExposureConfirmClasses, policy.EffectiveExposureConfirmClasses);
        }
        // A round trip keeps what was set, an empty list included (it turns the confirmation off).
        var set = new GuardrailPolicy { ExposureLabelPrefixes = ["x."], ExposureConfirmClasses = [] };
        var back = JsonSerializer.Deserialize<GuardrailPolicy>(JsonSerializer.Serialize(set))!;
        Assert.Equal(["x."], back.EffectiveExposureLabelPrefixes);
        Assert.Empty(back.EffectiveExposureConfirmClasses);
    }

    [Fact]
    public void Clone_and_the_pages_copy_carry_both_fields()
    {
        var policy = new GuardrailPolicy { ExposureLabelPrefixes = ["a."], ExposureConfirmClasses = ["published"] };

        foreach (var copy in new[] { policy.Clone(), Whiskers.Components.Pages.GuardrailsView.ClonePolicy(policy) })
        {
            Assert.Equal(["a."], copy.ExposureLabelPrefixes);
            Assert.Equal(["published"], copy.ExposureConfirmClasses);
            Assert.NotSame(policy.ExposureLabelPrefixes, copy.ExposureLabelPrefixes);
            Assert.NotSame(policy.ExposureConfirmClasses, copy.ExposureConfirmClasses);
        }
        var unset = new GuardrailPolicy { ExposureLabelPrefixes = null, ExposureConfirmClasses = null };
        Assert.Null(Whiskers.Components.Pages.GuardrailsView.ClonePolicy(unset).ExposureLabelPrefixes);
        Assert.Null(unset.Clone().ExposureConfirmClasses);
    }

    [Fact]
    public void The_new_defaults_change_the_policy_version()
    {
        var withDefaults = new GuardrailPolicy();
        var without = new GuardrailPolicy { ExposureLabelPrefixes = null, ExposureConfirmClasses = null };
        var other = new GuardrailPolicy { ExposureConfirmClasses = ["public"] };

        Assert.NotEqual(PolicySnapshot.Of("p", withDefaults).VersionHash, PolicySnapshot.Of("p", other).VersionHash);
        Assert.NotEqual(PolicySnapshot.Of("p", withDefaults).VersionHash, PolicySnapshot.Of("p", without).VersionHash);
    }

    // ---------------------------------------------------------------- redaction and notifications

    [Theory]
    [InlineData("exposureWithheld")]
    [InlineData("relatedWithheld")]
    public void The_withheld_lists_are_payloads_that_are_never_quoted(string argument)
    {
        Assert.True(SinkRedaction.IsPayload(argument));
        var policy = Admin();
        policy.ProtectedResources = ["s3cr3t*"];
        var request = Request(policy, (argument, "web: RouteLabel caddy.tls.dns=s3cr3t-token"));

        var verdict = new ProtectedResourceRule().Evaluate(request, out var reason);

        if (verdict != GuardrailVerdict.Allow) Assert.DoesNotContain("s3cr3t", reason);
    }

    [Fact]
    public void An_exposure_change_may_wait_for_quiet_hours_and_a_violation_never_does()
    {
        Assert.Contains(NotificationEventTypes.ExposureChange, NotificationEventTypes.SuppressibleEvents);
        Assert.DoesNotContain(NotificationEventTypes.ExposureChange, NotificationEventTypes.UnsuppressibleEvents);
        Assert.Contains(NotificationEventTypes.ExposureViolation, NotificationEventTypes.UnsuppressibleEvents);
        Assert.DoesNotContain(NotificationEventTypes.ExposureViolation, NotificationEventTypes.SuppressibleEvents);
    }
}
