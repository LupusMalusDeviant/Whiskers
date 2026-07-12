using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Services.Docker;

namespace Whiskers.Services.AiChat;

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
    private readonly string _basePath;

    public ChatHistoryStore(DataPathOptions? dataPaths = null)
        => _basePath = (dataPaths ?? DataPathOptions.Default).ChatDir;

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
        Directory.CreateDirectory(_basePath);
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

    private string GetPath(string email)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(email.ToLowerInvariant())))[..16];
        return Path.Combine(_basePath, $"{hash}.json");
    }
}

/// <summary>
/// Strictly limited AI chat — ONLY for Whiskers operations.
/// No coding, no web search, no general knowledge. Only container/server management.
/// </summary>
public class AiChatService : IAiChatService
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<AiChatSettings> _settings;
    private readonly IDockerService _docker;
    private readonly ILogger<AiChatService> _logger;

    private const string SystemPrompt = """
        You are the Whiskers assistant. You help manage Docker containers, servers, and databases within Whiskers.

        STRICT RULES:
        - You ONLY answer questions about Whiskers, containers, servers, databases, backups, networks, and deployments
        - You do NOT write code, scripts, or programming tutorials
        - You do NOT perform web searches or answer general knowledge questions
        - For questions outside your scope, you reply: "I'm only here to help with managing Whiskers."
        - Answer in the user's language (match the language they write in)
        - Point the user to the right page/tab in Whiskers when possible

        WHISKERS FEATURES — you can recommend these to the user:

        DASHBOARD (home page):
        - Overview of all containers with status, health, CPU/memory
        - Start, stop, restart containers via button
        - Click a container → detail page

        CONTAINER DETAIL (click a container):
        - Tab "Overview": ID, image, status, ports, labels
        - Tab "Stats": CPU/memory charts over time
        - Tab "Logs": view container logs
        - Tab "Terminal": open a shell directly inside the container
        - Tab "Environment": read env vars and edit the .env file. Sensitive values (keys, passwords) are masked
        - Tab "Database" (only for DB containers like PostgreSQL, MySQL, MongoDB, Redis, Neo4j):
          * Query Builder: dropdown-based, SELECT/COUNT/INSERT/UPDATE/DELETE without SQL knowledge
          * Query Editor: free-form SQL text field for complex queries
          * Result table with CSV export
          * Table browser: clickable table cards with schema view (columns, types, keys)
          * DB backup: one-click pg_dump/mysqldump
          * Migrations: upload and run SQL files
          * Seed/import: import SQL, CSV, or JSON

        DATABASE QUERIES:
        - The user can run SQL queries directly in the Database tab
        - Query Builder: choose an action (SELECT/COUNT/etc.) → choose a table → filters/columns → run
        - Results appear as a table, CSV download available
        - Supported DBs: PostgreSQL, MySQL/MariaDB, MongoDB, Redis, Neo4j
        - DB type is auto-detected from the image name

        OTHER PAGES:
        - /logs — Log search: full-text search across all container logs + alert rules (pattern → notification)
        - /graph — Topology: interactive network diagram of all containers and connections
        - /deploy — Deploy: deploy individual containers or Docker Compose stacks
        - /compose — Compose Editor: visual editor for docker-compose.yml
        - /apps — App Store: 30+ preconfigured templates (PostgreSQL, MySQL, Redis, Nginx, WordPress, Grafana, n8n, etc.)
        - /servers — Server management: manage multiple servers (SSH, TCP)
        - /networks — View, create, delete Docker networks
        - /backups — Create and restore volume backups
        - /tasks — Scheduled tasks: cron-based automatic backups, container restarts, cleanup
        - /webhooks — CI/CD webhooks: create a URL for GitHub/GitLab deployments
        - /diff — Container comparison: compare two containers side-by-side (image, env vars, ports, labels)
        - /audit-log — Audit log: track all actions
        - /settings — Settings: Mattermost, Matrix, user roles, secret vault, MCP keys

        CURRENT INFRASTRUCTURE:
        {CONTEXT}

        If the user asks about a database query, explain how to use the Database tab (container detail → tab "Database" → Query Builder or Query Editor).
        If the user is looking for a container, tell them the name and that they can click it in the Dashboard.
        Always suggest concrete actions the user can perform in the UI.
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
            return "AI chat is not configured. Please set an API key in Settings.";

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
            return $"Error: {ex.Message}";
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
            .GetString() ?? "No response.";
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
            .GetString() ?? "No response.";
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
                $"  - {c.Name}: Image={c.Image}, Status={c.State}, Health={c.HealthStatus}, Project={c.ComposeProject}, Server={c.ServerName}{(c.IsDatabase ? $", DB={c.DatabaseType}" : "")}"));
            if (containers.Count > maxDetailed)
                containerList += $"\n  ... and {containers.Count - maxDetailed} more (trimmed by priority)";

            return $"""
                Servers: {containers.Select(c => c.ServerName).Distinct().Count()} servers
                Container: {containers.Count} total, {running} running, {stopped} stopped, {unhealthy} unhealthy
                Database containers: {string.Join(", ", dbs)}
                Projects: {string.Join(", ", projects)}

                All containers:
                {containerList}
                """;
        }
        catch
        {
            return "Context could not be loaded.";
        }
    }
}
