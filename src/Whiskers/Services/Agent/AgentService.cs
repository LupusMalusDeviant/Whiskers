using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Guardrails;
using Whiskers.Services.Agent.Providers;

namespace Whiskers.Services.Agent;

/// <summary>Creates/manages AgentSessions. Resolves the provider from the current AgentSettings and
/// passes the shared building blocks (catalog, invoker, guardrail engine, registry) into each session.</summary>
public sealed class AgentService : IAgentService
{
    public const string SystemPrompt = """
        Du bist der Whiskers-Agent — ein handelnder Assistent für die Verwaltung von Docker-Containern,
        Servern, Datenbanken, Netzwerken, Deployments und Cloud-Servern über Whiskers.

        IDENTITÄT: Dein Maskottchen und Profilbild ist eine freundliche graue Tabby-Katze mit Headset und
        Lupe — der Whiskers-Kater. Du darfst sympathisch und mit einem dezenten Katzen-Augenzwinkern
        auftreten, aber Präzision und Technik gehen immer vor; kein übertriebenes Rollenspiel.

        ARBEITSWEISE:
        - Nutze die bereitgestellten Tools, um Informationen zu holen und Aktionen auszuführen.
        - Plane in kurzen Schritten: erst lesen/prüfen, dann handeln.
        - Schreibende oder administrative Aktionen können eine Bestätigung des Benutzers erfordern — das ist
          gewollt. Schlage die Aktion klar vor und warte auf die Freigabe.
        - Wird ein Tool durch Guardrails blockiert, akzeptiere das, erkläre es kurz und suche einen erlaubten Weg.
        - Antworte auf Deutsch, präzise und ohne Fülltext. Fasse am Ende das Ergebnis knapp zusammen.

        RICH-WIDGETS (optional): Du kannst in deine Antwort ein Live-Widget einbetten, indem du genau eines
        dieser Token schreibst (die Oberfläche rendert es als echte Komponente):
        - [[chart:container:<containerId>:cpu]] oder :mem — CPU-/RAM-Verlauf eines Containers
        - [[chart:server:<serverId>:cpu]] oder :mem — CPU-/RAM-Verlauf eines Servers
        - [[status:container:<containerId>]] / [[status:server:<serverId>]] — Status-Karte
        Nutze sie nur sparsam, wenn ein Diagramm/Status den Nutzer wirklich weiterbringt, und nur mit echten
        IDs aus den Tool-Ergebnissen. Kein anderes Token-Format wird gerendert.

        Du kannst nie mehr Rechte ausüben als der Benutzer/MCP-Key, der dich ausgelöst hat.
        """;

    private readonly IAgentProviderFactory _factory;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IAgentToolCatalog _catalog;
    private readonly IAgentToolInvoker _invoker;
    private readonly IAgentGuardrailEngine _guardrails;
    private readonly IGuardrailStore? _guardrailStore;
    private readonly IAgentToolRegistry _registry;

    // Bounded in-memory session management: bounded so long-running processes (instruct_agent
    // creates a session per call) cannot grow without limit. Insertion-order eviction.
    private const int MaxSessions = 200;
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly ConcurrentQueue<(string Id, AgentSession Session)> _order = new();

    public AgentService(
        IAgentProviderFactory factory, IOptionsMonitor<AgentSettings> settings,
        IAgentToolCatalog catalog, IAgentToolInvoker invoker, IAgentGuardrailEngine guardrails,
        IAgentToolRegistry registry, IGuardrailStore? guardrailStore = null)
    {
        _factory = factory;
        _settings = settings;
        _catalog = catalog;
        _invoker = invoker;
        _guardrails = guardrails;
        _registry = registry;
        _guardrailStore = guardrailStore;
    }

    public Task<IAgentSession> StartSessionAsync(
        AgentContext context, IReadOnlyList<AgentMessage>? seedHistory = null, CancellationToken ct = default)
    {
        var settings = _settings.CurrentValue;
        var provider = _factory.Resolve(settings);
        // Empty configured prompt → fall back to the built-in default.
        var prompt = string.IsNullOrWhiteSpace(settings.SystemPrompt) ? SystemPrompt : settings.SystemPrompt;
        var session = new AgentSession(context, provider, _catalog, _invoker, _guardrails, _registry, settings, prompt, seedHistory, _guardrailStore);
        _sessions[context.SessionId] = session;
        _order.Enqueue((context.SessionId, session));
        while (_sessions.Count > MaxSessions && _order.TryDequeue(out var old))
        {
            // Only evict if the queued session is still the current one for that id — a re-created session
            // with the same id (a stale queue entry) must never evict the fresh one.
            if (_sessions.TryGetValue(old.Id, out var cur) && ReferenceEquals(cur, old.Session))
                _sessions.TryRemove(old.Id, out _);
        }
        return Task.FromResult<IAgentSession>(session);
    }

    public Task<IAgentSession?> ResumeSessionAsync(string sessionId, CancellationToken ct = default)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult<IAgentSession?>(session);
    }
}
