namespace ServerWatch.Services.Agent.Guardrails;

/// <summary>Aggregates all IGuardrailRule with "most-restrictive wins": a Deny beats everything,
/// otherwise a Confirm wins, otherwise Allow. Fully LLM-independent and stateless —
/// the single source of truth on "may the agent do this?".</summary>
public sealed class GuardrailEngine : IAgentGuardrailEngine
{
    private readonly IReadOnlyList<IGuardrailRule> _rules;

    public GuardrailEngine(IEnumerable<IGuardrailRule> rules)
    {
        _rules = rules.ToList();
    }

    /// <summary>The built-in rule set in evaluation order (order is irrelevant to the
    /// result — "most-restrictive wins" — but stable for traceable reasons).</summary>
    public static GuardrailEngine CreateDefault() => new(new IGuardrailRule[]
    {
        new PrincipalCeilingRule(),
        new ReadOnlyModeRule(),
        new ToolDenyListRule(),
        new ToolAllowListRule(),
        new ProtectedResourceRule(),
        new ForbiddenArgumentRule(),
        new ConfirmationRule(),
    });

    public GuardrailDecision Evaluate(GuardrailRequest request)
    {
        var denies = new List<(string Id, string Reason)>();
        var confirms = new List<(string Id, string Reason)>();

        foreach (var rule in _rules)
        {
            var verdict = rule.Evaluate(request, out var reason);
            switch (verdict)
            {
                case GuardrailVerdict.Deny:
                    denies.Add((rule.Id, reason));
                    break;
                case GuardrailVerdict.Confirm:
                    confirms.Add((rule.Id, reason));
                    break;
            }
        }

        if (denies.Count > 0)
            return new GuardrailDecision(
                GuardrailVerdict.Deny, denies[0].Reason, denies.Select(d => d.Id).ToList());

        if (confirms.Count > 0)
            return new GuardrailDecision(
                GuardrailVerdict.Confirm, confirms[0].Reason, confirms.Select(c => c.Id).ToList());

        return new GuardrailDecision(GuardrailVerdict.Allow, "Erlaubt.", Array.Empty<string>());
    }
}
