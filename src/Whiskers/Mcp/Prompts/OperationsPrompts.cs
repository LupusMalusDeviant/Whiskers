using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Whiskers.Mcp.Prompts;

/// <summary>
/// User-invokable MCP prompts: ready-made operations workflows that tell the client's model which Whiskers tools
/// to use, in which order, and where to stop for a human. A prompt only returns text — it runs nothing and grants
/// nothing; every tool it names still passes the caller's scope and the guardrails when the model calls it.
/// Tools of a disabled module are simply absent, so each prompt says to skip what is not available.
/// </summary>
[McpServerPromptType]
public sealed class OperationsPrompts
{
    private const string Ground =
        "Work only with the Whiskers MCP tools. If a tool named below is not available to you, skip that step " +
        "and say so. Never call a write or admin tool on your own initiative: propose the exact call and wait for " +
        "the user's go-ahead. If Whiskers answers that human approval is required, tell the user the approval id " +
        "and repeat the identical call only after they confirm it was approved.";

    [McpServerPrompt(Name = "triage_unhealthy_containers", Title = "Triage unhealthy containers")]
    [Description("Read-only diagnosis of unhealthy, stopped or restarting containers, with a ranked list of proposed fixes.")]
    public static string TriageUnhealthyContainers(
        [Description("Optional server id or name to limit the triage to one server.")] string? server = null) =>
        $"""
        Triage the unhealthy containers{Scope(server)}.

        1. Call get_health_summary and list_containers to find containers that are unhealthy, stopped unexpectedly or restarting.
        2. For each affected container: get_container_details, get_container_logs (recent lines) and get_container_metrics.
        3. Call get_host_load for the affected servers — a busy host with idle containers points to a cause outside them.
        4. Call get_action_outcomes before suggesting a restart: if restarts rarely help for this kind of issue, say so.
        5. Report per container: symptom, most likely cause with the evidence, and a proposed fix (the exact tool call).

        {Ground}
        """;

    [McpServerPrompt(Name = "security_review_server", Title = "Security review of a server")]
    [Description("Read-only security review of one server: CVEs, firewall, TLS certificates, exposed services.")]
    public static string SecurityReviewServer(
        [Description("Server id or name to review.")] string server) =>
        $"""
        Do a read-only security review of server "{server}".

        1. get_server_info for the baseline (OS, kernel, uptime).
        2. get_server_cves and get_cve_summary; for the most affected containers get_container_cves. Group by severity and fix availability.
        3. list_firewall_rules: flag ports open to everyone that are not obviously meant to be public.
        4. list_ssl_certificates: flag certificates that expire within 30 days.
        5. list_systemd_services and list_containers: flag services and containers that look unexpected or are failing.
        6. Summarise as a prioritised list (critical → low) with one concrete remediation step each. Do not change anything.

        {Ground}
        """;

    [McpServerPrompt(Name = "plan_safe_update", Title = "Plan a safe container update")]
    [Description("Assess an image update for one container and produce a step-by-step plan with rollback, before any change.")]
    public static string PlanSafeUpdate(
        [Description("Container id or name to update.")] string container,
        [Description("Optional server id or name the container runs on.")] string? server = null) =>
        $"""
        Plan a safe image update for container "{container}"{Scope(server)}.

        1. get_update_status to confirm an update is available and which tag/digest it moves to.
        2. assess_update_risk and get_blast_radius: what depends on this container, what breaks if it does not come back.
        3. get_container_cves for the current image: does the update fix known vulnerabilities?
        4. Write the plan: pre-checks, the exact update_container call, how to verify health afterwards
           (get_container_details, get_container_logs), and the rollback (Whiskers snapshots the container before updating).
        5. Stop and ask the user to confirm before calling update_container.

        {Ground}
        """;

    [McpServerPrompt(Name = "investigate_host_load", Title = "Investigate high host load")]
    [Description("Find out why a server is busy, including load that no container accounts for.")]
    public static string InvestigateHostLoad(
        [Description("Server id or name that looks overloaded.")] string server) =>
        $"""
        Investigate the load on server "{server}".

        1. get_host_load: compare host CPU/memory with what the containers account for.
        2. get_server_metrics for the recent trend, and get_container_metrics for the top consumers.
        3. If the containers do not explain the load, check get_server_logs and list_systemd_services for a process outside them
           (dockerd, backups, a runaway service).
        4. get_whiskers_self_status: make sure Whiskers' own checks are not the load.
        5. Report the cause with evidence and a proposed, reversible mitigation.

        {Ground}
        """;

    private static string Scope(string? server) =>
        string.IsNullOrWhiteSpace(server) ? " across all servers" : $" on server \"{server}\"";
}
