using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Models.Agent;
using ServerWatch.Services.Agent.Guardrails;
using ServerWatch.Services.Agent.Providers;

namespace ServerWatch.Services.Agent;

/// <summary>Creates/manages AgentSessions. Resolves the provider from the current AgentSettings and
/// passes the shared building blocks (catalog, invoker, guardrail engine, registry) into each session.</summary>
public sealed class AgentService : IAgentService
{
    public const string SystemPrompt = """
        Du bist der ServerWatch-Agent — ein handelnder Assistent für die Verwaltung von Docker-Containern,
        Servern, Datenbanken, Netzwerken, Deployments und Cloud-Servern über ServerWatch.

        ARBEITSWEISE:
        - Nutze die bereitgestellten Tools, um Informationen zu holen und Aktionen auszuführen.
        - Plane in kurzen Schritten: erst lesen/prüfen, dann handeln.
        - Schreibende oder administrative Aktionen können eine Bestätigung des Benutzers erfordern — das ist
          gewollt. Schlage die Aktion klar vor und warte auf die Freigabe.
        - Wird ein Tool durch Guardrails blockiert, akzeptiere das, erkläre es kurz und suche einen erlaubten Weg.
        - Antworte auf Deutsch, präzise und ohne Fülltext. Fasse am Ende das Ergebnis knapp zusammen.

        Du kannst nie mehr Rechte ausüben als der Benutzer/MCP-Key, der dich ausgelöst hat.
        """;

    private readonly IAgentProviderFactory _factory;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IAgentToolCatalog _catalog;
    private readonly IAgentToolInvoker _invoker;
    private readonly IAgentGuardrailEngine _guardrails;
    private readonly IAgentToolRegistry _registry;

    // Bounded in-memory session management: bounded so long-running processes (instruct_agent
    // creates a session per call) cannot grow without limit. Insertion-order eviction.
    private const int MaxSessions = 200;
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly ConcurrentQueue<string> _order = new();

    public AgentService(
        IAgentProviderFactory factory, IOptionsMonitor<AgentSettings> settings,
        IAgentToolCatalog catalog, IAgentToolInvoker invoker, IAgentGuardrailEngine guardrails,
        IAgentToolRegistry registry)
    {
        _factory = factory;
        _settings = settings;
        _catalog = catalog;
        _invoker = invoker;
        _guardrails = guardrails;
        _registry = registry;
    }

    public Task<IAgentSession> StartSessionAsync(
        AgentContext context, IReadOnlyList<AgentMessage>? seedHistory = null, CancellationToken ct = default)
    {
        var settings = _settings.CurrentValue;
        var provider = _factory.Resolve(settings);
        var session = new AgentSession(context, provider, _catalog, _invoker, _guardrails, _registry, settings, SystemPrompt, seedHistory);
        _sessions[context.SessionId] = session;
        _order.Enqueue(context.SessionId);
        while (_sessions.Count > MaxSessions && _order.TryDequeue(out var oldId))
            _sessions.TryRemove(oldId, out _);
        return Task.FromResult<IAgentSession>(session);
    }

    public Task<IAgentSession?> ResumeSessionAsync(string sessionId, CancellationToken ct = default)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult<IAgentSession?>(session);
    }
}
