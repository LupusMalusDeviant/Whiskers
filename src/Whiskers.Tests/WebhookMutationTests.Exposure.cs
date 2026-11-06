using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using static Whiskers.Tests.AutomationMutationRunnerTests;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0006 (review M2): the exposure decision and the re-check through the webhook and its Git deploy — a
/// person's Funnel deployment audited and announced, a key's violation stopped, the images a Git deploy built inspected
/// before <c>up</c>.
/// </summary>
public sealed partial class WebhookMutationTests
{
    private IComposeRunner? _composeRunner;
    private readonly Keys _keys = new();

    private static readonly string KeyId = PrincipalIds.Key("k1");

    private const string FunnelShop =
        "name: shop\nservices:\n  web:\n    image: nginx\n    labels:\n      docktail.funnel.enable: \"true\"\n";

    /// <summary>What <c>up</c> starts: a new container of the project, with its inspect document.</summary>
    private void UpCreates(string project, string id, string service, string hostConfig, string labels = "")
    {
        var previous = _host.Effect;
        _host.Effect = command =>
        {
            if (previous?.Invoke(command) is { } result) return result;
            if (!command.Contains(" up -d", StringComparison.Ordinal)) return null;
            _docker.Add(new ContainerInfo
            {
                Id = id, Name = $"{project}-{service}-2", ServerId = "local", State = "running",
                Labels = new() { [ComposeModel.ProjectLabel] = project, [ComposeModel.ServiceLabel] = service },
            });
            _host.Inspect[id] = "{\"Id\":\"" + id + "\",\"Name\":\"/" + project + "-" + service + "-2\"," +
                "\"Config\":{\"Labels\":{\"com.docker.compose.project\":\"" + project + "\",\"com.docker.compose.service\":\"" +
                service + "\"" + labels + "}},\"HostConfig\":" + hostConfig + "}";
            return null;
        };
    }

    private void KeyK1() =>
        _keys.Data.ApiKeys.Add(new McpApiKeyConfig { Id = "k1", Name = "ci", PermissionLevel = McpPermissionLevels.Admin, Enabled = true });

    [Fact]
    public async Task A_person_owned_webhook_deploying_funnel_is_audited_and_announced()
    {
        _composeRunner = new ComposeRunner(_host, _docker, _notify);
        _host.Files["/opt/shop/docker-compose.yml"] = FunnelShop;
        _host.ComposeConfig = """{"services":{"web":{"image":"nginx","labels":{"docktail.funnel.enable":"true"}}}}""";
        _host.Images["nginx"] = ("sha256:1", "{}");
        UpCreates("shop", "shop-web-new", "web", """{"NetworkMode":"shop_default"}""", ""","docktail.funnel.enable":"true" """);
        var webhook = Seed("deploy", "/opt/shop");

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.True(success, output);
        var decided = Assert.Single(_audit.Requests, r => r.Arguments.ContainsKey("exposureShown"));
        Assert.Equal("compose.deploy", decided.CapabilityId);
        Assert.Contains("docktail.funnel.enable", decided.Arguments["exposure"].GetString());
        Assert.Contains(_notify.Events, e => e.EventType == NotificationEventTypes.ExposureChange);
        Assert.Empty(_docker.Actions);
    }

    [Fact]
    public async Task A_key_owned_webhook_deploy_stops_what_up_started_undecided()
    {
        KeyK1();
        _host.ComposeConfig = """{"services":{"web":{"image":"nginx"}}}""";
        _host.Images["nginx"] = ("sha256:1", "{}");
        UpCreates("shop", "shop-web-new", "web", """{"NetworkMode":"host"}""");
        var webhook = Seed("deploy", "/opt/shop", owner: KeyId);

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.Contains("host network", output);
        Assert.Equal([("disable_restart", "shop-web-new"), ("stop", "shop-web-new")],
            _docker.Actions.Where(a => a.ContainerId == "shop-web-new").Select(a => (a.Op, a.ContainerId)));
    }

    [Fact]
    public async Task A_key_owned_git_deploy_webhook_stops_what_up_started_undecided()
    {
        KeyK1();
        Assert.True(await _git.SetOwnerAsync(AppId, KeyId, expectedOwnerId: AdminId));
        _host.ComposeConfig = """{"services":{"web":{"image":"nginx"}}}""";
        _host.Images["nginx"] = ("sha256:1", "{}");
        UpCreates("payments", "payments-web-new", "web", """{"Privileged":true}""");
        var webhook = Seed("git-deploy", AppId, owner: KeyId);

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success, output);
        Assert.Contains(_host.Executed, c => c.Contains(" up -d", StringComparison.Ordinal));
        Assert.Equal([("disable_restart", "payments-web-new"), ("stop", "payments-web-new")],
            _docker.Actions.Where(a => a.ContainerId == "payments-web-new").Select(a => (a.Op, a.ContainerId)));
    }

    [Fact]
    public async Task A_key_owned_git_deploy_inspects_the_images_it_built_and_refuses_a_funnel_label_before_up()
    {
        KeyK1();
        Assert.True(await _git.SetOwnerAsync(AppId, KeyId, expectedOwnerId: AdminId));
        _host.Files[$"/opt/whiskers-git/{AppId}/repo/docker-compose.yml"] = "name: payments\nservices:\n  web:\n    build: .\n";
        _host.ComposeConfig = """{"services":{"web":{"build":{"context":"."}}}}""";
        _host.Effect = command =>
        {
            if (command.Contains("docker compose", StringComparison.Ordinal) && command.Contains(" build", StringComparison.Ordinal)
                && !command.Contains("--no-build", StringComparison.Ordinal))
                _host.Images["payments-web"] = ("sha256:7", """{"docktail.funnel.enable":"true"}""");
            return null;
        };
        var webhook = Seed("git-deploy", AppId, owner: KeyId);

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.Contains("docktail.funnel.enable", output);
        Assert.Contains(_host.Executed, c => c.Contains(" build", StringComparison.Ordinal) && c.Contains("docker compose", StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Executed, c => c.Contains(" up -d", StringComparison.Ordinal));
        Assert.Contains("docker image rm --no-prune 'payments-web'", _host.Executed);
    }
}
