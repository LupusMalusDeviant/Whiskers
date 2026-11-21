using Whiskers.Models;

namespace Whiskers.Services.ImageUpdate;

public enum RelationKind
{
    /// <summary>Same compose project. A change to the project's configuration recreates these too.</summary>
    SameProject,

    /// <summary>Declares depends_on the target — it expects the target to be there.</summary>
    DependsOnTarget,

    /// <summary>The target declares depends_on this one — it has to be up first.</summary>
    TargetDependsOn
}

public sealed record ContainerRelation(string Name, RelationKind Kind, string WhatHappens);

/// <param name="SeversOwnPath">True when the operation would cut the connection Whiskers is using to perform
/// it. Then the command has to be detached, or it dies mid-flip and takes the server with it.</param>
/// <param name="BlindSpots">Relationships this cannot see. Never empty.</param>
public sealed record BlastRadius(
    string Target,
    IReadOnlyList<ContainerRelation> Affected,
    bool SeversOwnPath,
    IReadOnlyList<string> BlindSpots);

/// <summary>
/// Who else a change to one container touches (GAP-7, 2026-08-28).
///
/// <para><b>Why this exists, in one sentence:</b> on 2026-08-27 the same prediction was made twice by hand and
/// was wrong both times. Adding a compose override to the identity provider was expected to recreate two services and
/// recreated all three, restarting the database. The same thing then happened to the docker-proxy stack. The
/// rule nobody had written down is that a change to the project's <em>configuration</em> — a new override file
/// included — changes the config hash for every service in it, so <c>compose up -d</c> recreates the lot.</para>
///
/// <para>Reads only the compose labels a container already carries, so it costs nothing: no pull, no probe,
/// no extra Docker call.</para>
/// </summary>
public static class BlastRadiusAssessor
{
    /// <summary>The port a docker-socket proxy publishes for the mTLS control plane. A container publishing it
    /// on a remote-controlled server is part of the path Whiskers itself is talking through.</summary>
    private const ushort DockerApiPort = 2376;

    /// <param name="serverIsRemoteControlled">True for servers Whiskers reaches through a proxy container on
    /// that host (ConnectionType.TCP), rather than locally or over SSH.</param>
    /// <param name="changesProjectConfig">True when the change touches the compose project's configuration —
    /// a new or edited override file, a changed environment. Then every service in the project is recreated,
    /// not just the one being changed. That is the rule that was missed twice.</param>
    public static BlastRadius Assess(
        ContainerInfo target,
        IReadOnlyList<ContainerInfo> onSameServer,
        bool serverIsRemoteControlled,
        bool changesProjectConfig)
    {
        var affected = new List<ContainerRelation>();
        var project = Label(target, "com.docker.compose.project");
        var service = Label(target, "com.docker.compose.service");

        var siblings = string.IsNullOrEmpty(project)
            ? []
            : onSameServer.Where(c =>
                !string.Equals(c.Id, target.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Label(c, "com.docker.compose.project"), project, StringComparison.Ordinal))
              .ToList();

        if (changesProjectConfig)
            foreach (var s in siblings)
                affected.Add(new ContainerRelation(s.Name, RelationKind.SameProject,
                    "Recreated as well: changing the project's configuration changes the config hash for " +
                    "every service in it, so compose recreates all of them — not only the one you edited."));

        // Who is left holding a broken reference while the target is down.
        foreach (var c in siblings)
        {
            if (affected.Any(a => a.Name == c.Name)) continue;
            if (string.IsNullOrEmpty(service)) continue;
            if (DependsOn(c).Contains(service, StringComparer.Ordinal))
                affected.Add(new ContainerRelation(c.Name, RelationKind.DependsOnTarget,
                    $"Depends on '{service}'. It keeps running, but its calls fail for as long as the target " +
                    "is down — and whether it recovers on its own depends on the application, not on Docker."));
        }

        foreach (var dep in DependsOn(target))
        {
            var match = siblings.FirstOrDefault(c =>
                string.Equals(Label(c, "com.docker.compose.service"), dep, StringComparison.Ordinal));
            if (match is not null && affected.All(a => a.Name != match.Name))
                affected.Add(new ContainerRelation(match.Name, RelationKind.TargetDependsOn,
                    $"The target waits for '{dep}' to be healthy before it starts. If this one is unwell the " +
                    "target will not come back, and the cause will look like the target's fault."));
        }

        // EH-6: the branch you are sitting on. Recreating the container that terminates the control plane
        // kills the command mid-flight — on 2026-08-27 that was ghostunnel on web-02, and the only reason
        // it came back was that the command had been detached first.
        var severs = serverIsRemoteControlled &&
                     (PublishesDockerApi(target) || siblings.Any(PublishesDockerApi));

        return new BlastRadius(target.Name, affected, severs, BlindSpots(serverIsRemoteControlled));
    }

    private static bool PublishesDockerApi(ContainerInfo c)
        => c.Ports.Any(p => p.PrivatePort == DockerApiPort || p.PublicPort == DockerApiPort);

    private static string Label(ContainerInfo c, string key)
        => c.Labels.TryGetValue(key, out var v) ? v : string.Empty;

    private static IReadOnlyList<string> DependsOn(ContainerInfo c)
        => Label(c, "com.docker.compose.depends_on")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // Compose writes entries as "service:condition:bool"; only the service name matters here.
            .Select(e => e.Split(':')[0])
            .Where(e => e.Length > 0)
            .ToList();

    /// <summary>
    /// What this cannot see. Compose labels describe one project on one host; plenty of real dependencies
    /// live outside that.
    /// </summary>
    private static IReadOnlyList<string> BlindSpots(bool remote)
    {
        var spots = new List<string>
        {
            "Anything outside this compose project — a reverse proxy in another stack, a container on " +
            "another server, a cron job, a person with a bookmark.",
            "Application-level dependencies that are not declared: one service calling another by URL says " +
            "nothing to Docker.",
            "Whether a dependent actually recovers when the target returns. Docker restarts containers; it " +
            "does not reconnect the program inside them."
        };

        if (remote)
            spots.Add("Whether the control plane comes back by itself. It normally does — but if it does " +
                      "not, this server is out of reach from here.");

        return spots;
    }
}
