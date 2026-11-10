using System.Text.Json;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.Guardrails;

/// <summary>
/// Contract 0006: a compose deployment that changes how a service is reachable — published through Tailscale Funnel,
/// a Traefik router or caddy-docker-proxy, or given the host's network, privileges, capabilities, namespaces or devices —
/// needs a confirmation from the agent and from API keys, for the classes in
/// <see cref="Whiskers.Configuration.GuardrailPolicy.ExposureConfirmClasses"/> (default <c>public</c> and <c>host</c>; an
/// empty list turns it off). It reads only the resolved target the gateway adds (<see cref="ResolvedPrefix"/>), never a
/// caller's argument, and classifies with the deciding policy's own label prefixes. Part of
/// <see cref="GuardrailEngine.CreateDefault"/> only: a person in the UI is not asked to confirm their own action (PC-D1).
/// </summary>
public sealed class ExposureChangeRule : IGuardrailRule
{
    public const string RuleId = "exposure-change";

    /// <summary>The prefix of a resolved exposure the policy adapter adds.</summary>
    public const string ResolvedPrefix = "__resolvedExposure";

    /// <summary>The prefix of a resolved exposure that is withheld: classified by its real key, never quoted.</summary>
    public const string ResolvedWithheldPrefix = "__resolvedExposureWithheld";

    /// <summary>The prefix of a resolved exposure whose value only is withheld (review m1): its key is named, never its
    /// value.</summary>
    public const string ResolvedValueWithheldPrefix = "__resolvedExposureValueWithheld";

    public string Id => RuleId;

    public GuardrailVerdict Evaluate(GuardrailRequest request, out string reason)
    {
        var policy = request.Context.Policy;
        var confirm = policy.EffectiveExposureConfirmClasses;
        reason = "";
        if (confirm.Count == 0) return GuardrailVerdict.Allow;
        var prefixes = policy.EffectiveExposureLabelPrefixes;
        foreach (var (key, value) in request.Arguments)
        {
            if (!key.StartsWith(ResolvedPrefix, StringComparison.Ordinal) || value.ValueKind != JsonValueKind.String) continue;
            // A value-only withheld entry is named by its key (Amendment 5); the Reason below never quotes a value.
            var withheld = key.StartsWith(ResolvedWithheldPrefix, StringComparison.Ordinal);
            if (ComposeExposures.Parse(value.GetString() ?? "") is not { } exposure)
            {
                // Something the gateway added but that cannot be read: it is not ignored (fail closed).
                reason = "An exposure change of the deployment cannot be read; it needs a confirmation.";
                return GuardrailVerdict.Confirm;
            }
            var cls = ExposureClasses.Of(exposure.Kind, exposure.Key, prefixes);
            if (cls is null || !confirm.Contains(cls, StringComparer.Ordinal)) continue;
            reason = Reason(exposure.Service, exposure.Kind, exposure.Key, cls, withheld);
            return GuardrailVerdict.Confirm;
        }
        return GuardrailVerdict.Allow;
    }

    /// <summary>"web becomes public through Tailscale Funnel (docktail.funnel.enable)": the service and the key, never a
    /// value; for a withheld entry the kind and the class only.</summary>
    private static string Reason(string service, ExposureKind kind, string key, string cls, bool withheld)
    {
        if (withheld)
            return $"{service} gets a {ExposureClasses.Noun(kind)} (withheld) of class '{cls}'; it needs a confirmation.";
        return kind switch
        {
            ExposureKind.RouteLabel when key.StartsWith("docktail.funnel.", StringComparison.OrdinalIgnoreCase)
                => $"{service} becomes public through Tailscale Funnel ({key}).",
            ExposureKind.RouteLabel when key.StartsWith("traefik.", StringComparison.OrdinalIgnoreCase)
                => $"{service} becomes public through a Traefik router ({key}).",
            ExposureKind.RouteLabel when key.StartsWith("caddy", StringComparison.OrdinalIgnoreCase)
                => $"{service} becomes public through caddy-docker-proxy ({key}).",
            ExposureKind.RouteLabel => $"{service} gets a route label of class '{cls}' ({key}).",
            ExposureKind.PublishedPort => $"{service} publishes port {key}.",
            ExposureKind.HostNetwork => $"{service} runs in the host's network ({key}: host).",
            ExposureKind.Privileged => $"{service} runs privileged.",
            ExposureKind.AddedCapability => $"{service} gets the capability {key}.",
            ExposureKind.HostNamespace => $"{service} shares the host's namespace ({key}: host).",
            ExposureKind.Device => $"{service} gets the host device {key}.",
            ExposureKind.SecurityOpt => $"{service} runs with {key} switched off.",
            ExposureKind.DeviceCgroupRule => $"{service} gets a device cgroup rule.",
            // Contract 0013, U3: the signal only, never a token, key or URL.
            ExposureKind.ExternalRoute when key == ComposeThirdParty.TraefikExposedByDefault
                => $"{service} is a Traefik that routes every container on the server (exposedByDefault).",
            ExposureKind.ExternalRoute when key == ComposeThirdParty.TraefikHost
                => $"{service} becomes public through the Traefik on the server, which routes every container (exposedByDefault).",
            ExposureKind.ExternalRoute when key == ComposeThirdParty.CloudflaredTunnel
                => $"{service} becomes public through a Cloudflare tunnel (cloudflared).",
            ExposureKind.ExternalRoute when key == ComposeThirdParty.TailscaleFunnel
                => $"{service} becomes public through Tailscale Funnel ({key}).",
            ExposureKind.ExternalRoute when key == ComposeThirdParty.TailscaleServe
                => $"{service} is served on the tailnet through Tailscale serve ({key}).",
            ExposureKind.ExternalRoute when key == ComposeThirdParty.TailscaleState
                => $"{service} runs Tailscale with a serve state that cannot be read; it counts as public ({key}).",
            ExposureKind.ExternalRoute => $"{service} is published by a proxy or tunnel ({key}).",
            _ => $"{service} changes its exposure (class '{cls}').",
        };
    }
}
