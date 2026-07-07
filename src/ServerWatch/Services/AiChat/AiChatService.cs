using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Services.Docker;

namespace ServerWatch.Services.AiChat;

public class ChatMessage
{
    public string Role { get; set; } = ""; // system, user, assistant
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class UserChatHistory
{
    public List<ChatMessage> Messages { get; set; } = new();
}

/// <summary>Persists chat history per user to /app/data/chat/{email-hash}.json</summary>
public class ChatHistoryStore : IChatHistoryStore
{
    private const string BasePath = "/app/data/chat";

    public async Task<List<ChatMessage>> LoadAsync(string userEmail)
    {
        var path = GetPath(userEmail);
        if (!File.Exists(path)) return new();
        try
        {
            var json = await File.ReadAllTextAsync(path);
            var history = System.Text.Json.JsonSerializer.Deserialize<UserChatHistory>(json);
            return history?.Messages ?? new();
        }
        catch { return new(); }
    }

    public async Task SaveAsync(string userEmail, List<ChatMessage> messages)
    {
        Directory.CreateDirectory(BasePath);
        var path = GetPath(userEmail);
        // Keep last 50 messages per user
        var toSave = messages.TakeLast(50).ToList();
        var json = System.Text.Json.JsonSerializer.Serialize(new UserChatHistory { Messages = toSave },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        // Atomic write: a crash mid-write must not leave a truncated/corrupt history file.
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public async Task ClearAsync(string userEmail)
    {
        var path = GetPath(userEmail);
        if (File.Exists(path)) File.Delete(path);
        await Task.CompletedTask;
    }

    private static string GetPath(string email)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(email.ToLowerInvariant())))[..16];
        return Path.Combine(BasePath, $"{hash}.json");
    }
}

/// <summary>
/// Strictly limited AI chat — ONLY for ServerWatch operations.
/// No coding, no web search, no general knowledge. Only container/server management.
/// </summary>
public class AiChatService : IAiChatService
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<AiChatSettings> _settings;
    private readonly IDockerService _docker;
    private readonly ILogger<AiChatService> _logger;

    private const string SystemPrompt = """
        Du bist der ServerWatch-Assistent. Du hilfst bei der Verwaltung von Docker-Containern, Servern und Datenbanken innerhalb von ServerWatch.

        STRENGE REGELN:
        - Du beantwortest NUR Fragen zu ServerWatch, Containern, Servern, Datenbanken, Backups, Netzwerken und Deployments
        - Du schreibst KEINEN Code, keine Skripte, keine Anleitungen für Programmierung
        - Du machst KEINE Web-Suchen, keine allgemeinen Wissensfragen
        - Bei Fragen außerhalb deines Bereichs antwortest du: "Ich bin nur für die ServerWatch-Verwaltung zuständig."
        - Antworte kurz, präzise und auf Deutsch
        - Verweise den User auf die richtige Seite/Tab in ServerWatch wenn möglich

        SERVERWATCH FEATURES — das kannst du dem User empfehlen:

        DASHBOARD (Startseite):
        - Übersicht aller Container mit Status, Health, CPU/Memory
        - Container starten, stoppen, neustarten per Button
        - Klick auf Container → Detailseite

        CONTAINER-DETAIL (Klick auf einen Container):
        - Tab "Übersicht": ID, Image, Status, Ports, Labels
        - Tab "Statistiken": CPU/Memory Charts über Zeit
        - Tab "Logs": Container-Logs anzeigen
        - Tab "Terminal": Shell direkt im Container öffnen
        - Tab "Umgebungsvariablen": Env-Vars lesen und .env-Datei bearbeiten. Sensible Werte (Keys, Passwords) sind maskiert
        - Tab "Datenbank" (nur bei DB-Containern wie PostgreSQL, MySQL, MongoDB, Redis, Neo4j):
          * Query Builder: Dropdown-basiert, SELECT/COUNT/INSERT/UPDATE/DELETE ohne SQL-Kenntnisse
          * Query Editor: Freies SQL-Textfeld für komplexe Queries
          * Ergebnis-Tabelle mit CSV-Export
          * Tabellen-Browser: Klickbare Tabellen-Karten mit Schema-Ansicht (Spalten, Typen, Keys)
          * DB-Backup: Ein-Klick pg_dump/mysqldump
          * Migrationen: SQL-Dateien hochladen und ausführen
          * Seed/Import: SQL, CSV oder JSON importieren

        DATENBANK-ABFRAGEN:
        - Der User kann im Datenbank-Tab SQL-Queries direkt ausführen
        - Query Builder: Aktion wählen (SELECT/COUNT/etc.) → Tabelle wählen → Filter/Spalten → Ausführen
        - Ergebnisse erscheinen als Tabelle, CSV-Download möglich
        - Unterstützte DBs: PostgreSQL, MySQL/MariaDB, MongoDB, Redis, Neo4j
        - DB-Typ wird automatisch am Image-Namen erkannt

        WEITERE SEITEN:
        - /logs — Log-Suche: Volltextsuche über alle Container-Logs + Alert-Regeln (Pattern → Benachrichtigung)
        - /graph — Topologie: Interaktives Netzwerk-Diagramm aller Container und Verbindungen
        - /deploy — Bereitstellen: Einzelne Container oder Docker Compose deployen
        - /compose — Compose Editor: Visueller Editor für docker-compose.yml
        - /apps — App Store: 30+ vorkonfigurierte Templates (PostgreSQL, MySQL, Redis, Nginx, WordPress, Grafana, n8n, etc.)
        - /servers — Server-Verwaltung: Mehrere Server (SSH, TCP) verwalten
        - /networks — Docker-Netzwerke anzeigen, erstellen, löschen
        - /backups — Volume-Backups erstellen und wiederherstellen
        - /tasks — Geplante Tasks: Cron-basierte automatische Backups, Container-Neustarts, Cleanup
        - /webhooks — CI/CD Webhooks: URL für GitHub/GitLab Deployments erstellen
        - /diff — Container-Vergleich: Zwei Container side-by-side vergleichen (Image, Env-Vars, Ports, Labels)
        - /audit-log — Audit-Protokoll: Alle Aktionen nachverfolgen
        - /settings — Einstellungen: Mattermost, Matrix, Benutzerrollen, Secret Vault, MCP-Keys

        AKTUELLE INFRASTRUKTUR:
        {CONTEXT}

        Wenn der User nach einer Datenbank-Abfrage fragt, erkläre ihm wie er den Datenbank-Tab nutzen kann (Container-Detail → Tab "Datenbank" → Query Builder oder Query Editor).
        Wenn der User einen Container sucht, nenne ihm den Namen und sage dass er im Dashboard darauf klicken kann.
        Schlage immer konkrete Aktionen vor die der User in der UI ausführen kann.
        """;

    public AiChatService(HttpClient httpClient, IOptionsMonitor<AiChatSettings> settings,
        IDockerService docker, ILogger<AiChatService> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _docker = docker;
        _logger = logger;
    }

    public bool IsEnabled => _settings.CurrentValue.Enabled && !string.IsNullOrEmpty(_settings.CurrentValue.ApiKey);

    public async Task<string> ChatAsync(string userMessage, List<ChatMessage>? history = null)
    {
        var config = _settings.CurrentValue;
        if (!config.Enabled || string.IsNullOrEmpty(config.ApiKey))
            return "AI-Chat ist nicht konfiguriert. Bitte API-Key in den Einstellungen hinterlegen.";

        // Build context with current infrastructure state
        var context = await BuildContextAsync();
        var systemMsg = SystemPrompt.Replace("{CONTEXT}", context);

        // Build messages
        var messages = new List<object> { new { role = "system", content = systemMsg } };
        if (history != null)
        {
            // Anthropic requires the transcript to start with a user turn — after truncation the window may
            // begin with an assistant reply, so drop leading assistant turns and keep only user/assistant.
            var recent = history.TakeLast(10)
                .Where(m => m.Role is "user" or "assistant")
                .SkipWhile(m => m.Role == "assistant");
            foreach (var msg in recent)
                messages.Add(new { role = msg.Role, content = msg.Content });
        }
        messages.Add(new { role = "user", content = userMessage });

        try
        {
            if (config.Provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
                return await CallAnthropicAsync(config, messages, systemMsg);
            else
                return await CallOpenAiAsync(config, messages);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI chat failed");
            return $"Fehler: {ex.Message}";
        }
    }

    private async Task<string> CallOpenAiAsync(AiChatSettings config, List<object> messages)
    {
        var url = config.ApiUrl ?? "https://api.openai.com/v1/chat/completions";

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = config.Model,
            messages,
            max_tokens = 500,
            temperature = 0.3
        });

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return json?.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "Keine Antwort.";
    }

    private async Task<string> CallAnthropicAsync(AiChatSettings config, List<object> messages, string systemMsg)
    {
        var url = config.ApiUrl ?? "https://api.anthropic.com/v1/messages";

        // Anthropic uses a different format: system is separate, messages don't include system
        var anthropicMessages = messages.Skip(1) // skip system message
            .Select(m =>
            {
                var json = JsonSerializer.SerializeToElement(m);
                return new { role = json.GetProperty("role").GetString()!, content = json.GetProperty("content").GetString()! };
            }).ToList();

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-api-key", config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new
        {
            model = config.Model,
            system = systemMsg,
            messages = anthropicMessages,
            max_tokens = 500
        });

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return json?.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "Keine Antwort.";
    }

    private async Task<string> BuildContextAsync()
    {
        try
        {
            var containers = await _docker.ListAllContainersAsync(all: true);
            var running = containers.Count(c => c.State == "running");
            var stopped = containers.Count(c => c.State != "running");
            var unhealthy = containers.Count(c => c.HealthStatus == "unhealthy");
            var dbs = containers.Where(c => c.IsDatabase).Select(c => $"{c.Name} ({c.DatabaseType}, {c.State})");
            var projects = containers.Select(c => c.ComposeProject).Distinct().Where(p => p != "Standalone");

            // Cap the detailed list so a large fleet can't blow the model context — prioritise the
            // interesting containers (unhealthy, then stopped). The aggregate counts above stay full,
            // and small fleets keep their original order.
            const int maxDetailed = 50;
            var detailed = containers.AsEnumerable();
            if (containers.Count > maxDetailed)
                detailed = containers
                    .OrderByDescending(c => c.HealthStatus == "unhealthy")
                    .ThenByDescending(c => c.State != "running")
                    .Take(maxDetailed);

            var containerList = string.Join("\n", detailed.Select(c =>
                $"  - {c.Name}: Image={c.Image}, Status={c.State}, Health={c.HealthStatus}, Projekt={c.ComposeProject}, Server={c.ServerName}{(c.IsDatabase ? $", DB={c.DatabaseType}" : "")}"));
            if (containers.Count > maxDetailed)
                containerList += $"\n  … und {containers.Count - maxDetailed} weitere (nach Priorität gekürzt)";

            return $"""
                Server: {containers.Select(c => c.ServerName).Distinct().Count()} Server
                Container: {containers.Count} gesamt, {running} laufend, {stopped} gestoppt, {unhealthy} unhealthy
                Datenbank-Container: {string.Join(", ", dbs)}
                Projekte: {string.Join(", ", projects)}

                Alle Container:
                {containerList}
                """;
        }
        catch
        {
            return "Kontext konnte nicht geladen werden.";
        }
    }
}
