using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Mcp;

namespace Whiskers.Services.Agent;

/// <summary>Orchestrates the Claude Code CLI as a subprocess. Configures it via --mcp-config to point
/// at our /mcp endpoint with an agent bearer key → its tool calls run through the same
/// guardrail gate. Reads --output-format stream-json line by line and translates it via
/// ClaudeCodeOutputParser into AgentEvents.</summary>
public sealed class ClaudeCodeRuntime : IClaudeCodeRuntime
{
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IConfiguration _config;
    private readonly IMcpPermissionService? _permissionService;
    private readonly ILogger<ClaudeCodeRuntime>? _logger;
    private readonly Lazy<bool> _available;

    public ClaudeCodeRuntime(
        IOptionsMonitor<AgentSettings> settings, IConfiguration config,
        IMcpPermissionService? permissionService = null, ILogger<ClaudeCodeRuntime>? logger = null)
    {
        _settings = settings;
        _config = config;
        _permissionService = permissionService;
        _logger = logger;
        _available = new Lazy<bool>(DetectCli);
    }

    public bool IsAvailable => _available.Value;

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentContext context, string userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            yield return new AgentEvent.Failed("Claude-Code-CLI ist nicht installiert/erreichbar.");
            yield break;
        }

        var mcpKey = _config["Agent:ClaudeCode:McpKey"];
        if (string.IsNullOrWhiteSpace(mcpKey))
        {
            yield return new AgentEvent.Failed("Agent:ClaudeCode:McpKey ist nicht konfiguriert — Claude Code wird nicht gestartet.");
            yield break;
        }
        // The agent must never exceed the caller's rights: the configured MCP key's level must be ≤ the principal's.
        var keyConfig = _permissionService?.ValidateKey(mcpKey);
        if (keyConfig != null && !McpPermissionLevels.HasAccess(context.Principal.PermissionLevel, keyConfig.PermissionLevel))
        {
            yield return new AgentEvent.Failed("Der konfigurierte Claude-Code-Key hat mehr Rechte als der Aufrufer — abgelehnt.");
            yield break;
        }

        var mcpConfigPath = WriteMcpConfig(mcpKey);
        var process = StartProcess(userMessage, mcpConfigPath);
        if (process == null)
        {
            CleanupFile(mcpConfigPath);
            yield return new AgentEvent.Failed("Claude-Code-Prozess konnte nicht gestartet werden.");
            yield break;
        }

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var sawTerminal = false;

        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                foreach (var ev in ClaudeCodeOutputParser.ParseLine(line))
                {
                    if (ev is AgentEvent.TurnCompleted or AgentEvent.Failed) sawTerminal = true;
                    yield return ev;
                }
            }

            await process.WaitForExitAsync(ct);
            if (!sawTerminal)
            {
                if (process.ExitCode == 0)
                    yield return new AgentEvent.TurnCompleted(AgentStopReason.Stop, new AgentUsage(0, 0));
                else
                {
                    var err = await stderrTask;
                    yield return new AgentEvent.Failed(
                        string.IsNullOrWhiteSpace(err) ? $"Claude Code beendet mit Code {process.ExitCode}." : err.Trim());
                }
            }
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            process.Dispose();
            CleanupFile(mcpConfigPath);
        }
    }

    private Process? StartProcess(string userMessage, string mcpConfigPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(userMessage);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_settings.CurrentValue.Model);
        psi.ArgumentList.Add("--mcp-config");
        psi.ArgumentList.Add(mcpConfigPath);
        psi.ArgumentList.Add("--permission-mode");
        psi.ArgumentList.Add("default");
        psi.ArgumentList.Add("--allowedTools");
        psi.ArgumentList.Add("mcp__serverwatch");
        // Never let the CLI touch the local filesystem/shell — only our guardrailed MCP tools.
        psi.ArgumentList.Add("--disallowedTools");
        psi.ArgumentList.Add("Edit,Write,Bash,NotebookEdit");

        try { return Process.Start(psi); }
        catch (Exception ex) { _logger?.LogError(ex, "Claude-Code-Start fehlgeschlagen"); return null; }
    }

    /// <summary>Writes a temporary MCP config that points Claude Code at our /mcp endpoint with the
    /// agent key. This makes role/guardrails apply server-side.</summary>
    private string WriteMcpConfig(string key)
    {
        var url = _config["Agent:ClaudeCode:McpUrl"] ?? "http://localhost:8080/mcp";

        var json = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["serverwatch"] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = url,
                    ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {key}" },
                }
            }
        };

        var path = Path.Combine(Path.GetTempPath(), $"serverwatch-agent-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json.ToJsonString());
        // The file holds a bearer key — make it owner-only (Linux; the app runs in Linux containers).
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private static void CleanupFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private bool DetectCli()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "claude",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
