using Whiskers.Models;
using Whiskers.Services.ImageUpdate;

namespace Whiskers.Tests;

/// <summary>
/// Who else a change touches (GAP-7, 2026-08-28).
///
/// <para>The package exists because the same prediction was made by hand twice on 2026-08-27 and was wrong
/// both times. Adding a compose override to the identity provider was supposed to recreate two services; it recreated all
/// three and restarted the database. The identical thing then happened to the docker-proxy stack. The rule
/// nobody had written down: a change to the project's <em>configuration</em> changes the config hash for every
/// service in it, so <c>compose up -d</c> recreates the lot.</para>
/// </summary>
public class BlastRadiusAssessorTests
{
    private static ContainerInfo C(string name, string project = "idp", string service = "",
        string dependsOn = "", int publishedPort = 0)
    {
        var c = new ContainerInfo { Id = name, Name = name, ServerId = "host-f" };
        if (project.Length > 0) c.Labels["com.docker.compose.project"] = project;
        c.Labels["com.docker.compose.service"] = service.Length > 0 ? service : name;
        if (dependsOn.Length > 0) c.Labels["com.docker.compose.depends_on"] = dependsOn;
        if (publishedPort > 0)
            c.Ports.Add(new PortMapping { PrivatePort = (ushort)publishedPort, PublicPort = (ushort)publishedPort });
        return c;
    }

    [Fact]
    public void A_configuration_change_recreates_every_service_in_the_project()
    {
        // THE case, and it is the one that was got wrong twice by hand. Not "the service you edited" —
        // all of them, database included.
        var worker = C("worker");
        var fleet = new[] { worker, C("server"), C("postgresql") };

        var radius = BlastRadiusAssessor.Assess(worker, fleet,
            serverIsRemoteControlled: false, changesProjectConfig: true);

        Assert.Equal(2, radius.Affected.Count(a => a.Kind == RelationKind.SameProject));
        Assert.Contains(radius.Affected, a => a.Name == "postgresql");
        Assert.Contains(radius.Affected, a => a.WhatHappens.Contains("not only the one you edited"));
    }

    [Fact]
    public void Restarting_one_service_alone_does_not_drag_its_siblings_in()
    {
        // The counter-direction. If every operation reported the whole project, the number would mean
        // nothing and nobody would read the warning that matters.
        var worker = C("worker");
        var fleet = new[] { worker, C("server"), C("postgresql") };

        var radius = BlastRadiusAssessor.Assess(worker, fleet,
            serverIsRemoteControlled: false, changesProjectConfig: false);

        Assert.DoesNotContain(radius.Affected, a => a.Kind == RelationKind.SameProject);
    }

    [Fact]
    public void A_dependent_is_named_together_with_what_it_actually_loses()
    {
        var db = C("postgresql", service: "postgresql");
        var server = C("server", service: "server", dependsOn: "postgresql:service_healthy:true");

        var radius = BlastRadiusAssessor.Assess(db, [db, server],
            serverIsRemoteControlled: false, changesProjectConfig: false);

        var rel = Assert.Single(radius.Affected, a => a.Kind == RelationKind.DependsOnTarget);
        Assert.Equal("server", rel.Name);
        Assert.Contains("its calls fail", rel.WhatHappens);
    }

    [Fact]
    public void What_the_target_itself_waits_for_is_named_too()
    {
        // The direction people forget: if the dependency is unwell the target never comes back, and the
        // symptom looks like the target's fault.
        var db = C("postgresql", service: "postgresql");
        var worker = C("worker", service: "worker", dependsOn: "postgresql:service_healthy:true");

        var radius = BlastRadiusAssessor.Assess(worker, [db, worker],
            serverIsRemoteControlled: false, changesProjectConfig: false);

        var rel = Assert.Single(radius.Affected, a => a.Kind == RelationKind.TargetDependsOn);
        Assert.Equal("postgresql", rel.Name);
        Assert.Contains("look like the target's fault", rel.WhatHappens);
    }

    [Fact]
    public void Cutting_the_control_plane_is_flagged_because_the_command_dies_with_it()
    {
        // EH-6, and it happened for real: recreating ghostunnel on host-f severed the mTLS path the
        // command was travelling through. It only survived because it had been detached first.
        var tunnel = C("ghostunnel", project: "dockerproxy", service: "ghostunnel", publishedPort: 2376);
        var proxy = C("socket-proxy", project: "dockerproxy", service: "socket-proxy");

        var radius = BlastRadiusAssessor.Assess(tunnel, [tunnel, proxy],
            serverIsRemoteControlled: true, changesProjectConfig: false);

        Assert.True(radius.SeversOwnPath);
    }

    [Fact]
    public void A_sibling_of_the_control_plane_severs_it_too()
    {
        // Recreating socket-proxy takes ghostunnel's backend away — same outcome, and the project-hash rule
        // means editing either one can recreate both.
        var tunnel = C("ghostunnel", project: "dockerproxy", service: "ghostunnel", publishedPort: 2376);
        var proxy = C("socket-proxy", project: "dockerproxy", service: "socket-proxy");

        var radius = BlastRadiusAssessor.Assess(proxy, [tunnel, proxy],
            serverIsRemoteControlled: true, changesProjectConfig: false);

        Assert.True(radius.SeversOwnPath);
    }

    [Fact]
    public void An_ordinary_container_on_a_remote_server_does_not_sever_anything()
    {
        // Otherwise every operation on a TCP server would carry the same scary flag, and the flag would stop
        // being read exactly when it was true.
        var app = C("pagebound", project: "pagebound", service: "app");

        var radius = BlastRadiusAssessor.Assess(app, [app],
            serverIsRemoteControlled: true, changesProjectConfig: false);

        Assert.False(radius.SeversOwnPath);
    }

    [Fact]
    public void A_local_server_is_never_reported_as_self_severing()
    {
        // Host-A is reached without a tunnel. A proxy container there is just another container.
        var tunnel = C("ghostunnel", project: "dockerproxy", service: "ghostunnel", publishedPort: 2376);

        var radius = BlastRadiusAssessor.Assess(tunnel, [tunnel],
            serverIsRemoteControlled: false, changesProjectConfig: false);

        Assert.False(radius.SeversOwnPath);
    }

    [Fact]
    public void A_standalone_container_has_no_project_to_drag_along()
    {
        var lone = new ContainerInfo { Id = "solo", Name = "solo", ServerId = "local" };

        var radius = BlastRadiusAssessor.Assess(lone, [lone, C("worker")],
            serverIsRemoteControlled: false, changesProjectConfig: true);

        Assert.Empty(radius.Affected);
    }

    [Fact]
    public void The_answer_always_states_what_it_cannot_see()
    {
        // Compose labels describe one project on one host. A reverse proxy in another stack, a container on
        // another server, an application calling a URL — none of that is visible here, and an empty list
        // would read as "nothing else is affected".
        var radius = BlastRadiusAssessor.Assess(C("worker"), [C("worker")],
            serverIsRemoteControlled: false, changesProjectConfig: false);

        Assert.Empty(radius.Affected);
        Assert.NotEmpty(radius.BlindSpots);
        Assert.Contains(radius.BlindSpots, b => b.Contains("outside this compose project"));
        Assert.Contains(radius.BlindSpots, b => b.Contains("not declared"));
    }
}
