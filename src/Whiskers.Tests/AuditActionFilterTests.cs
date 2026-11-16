using Whiskers.Models;
using Whiskers.Services.AuditLog;

namespace Whiskers.Tests;

/// <summary>Review 1.0.0 (T3): the audit page's filters Deployment, Backups and Env matched the action by prefix, so they
/// found the rows the service writes (<c>deploy.app</c>) but none of the gateway's, whose action is the capability id
/// (<c>app.deploy</c>).</summary>
public class AuditActionFilterTests
{
    private static readonly string[] Actions =
    [
        "deploy.app", "app.deploy", "compose.deploy", "gitdeploy.deploy", "gitdeploy.save",
        "backup.create", "db.backup", "volume.backup", "selfbackup.create", "selfbackup.restore", "volume.restore",
        "env.update", "container.env_set", "container.env_file_save",
        "container.restart", "config.save", "server.add",
    ];

    private static string[] Matching(string? filter) => AuditActionFilter
        .Apply(Actions.Select(a => new AuditLogEntity { Action = a }).AsQueryable(), filter)
        .Select(e => e.Action).ToArray();

    [Theory]
    [InlineData("deploy", "deploy.app app.deploy compose.deploy gitdeploy.deploy gitdeploy.save")]
    [InlineData("backup", "backup.create db.backup volume.backup selfbackup.create selfbackup.restore volume.restore")]
    [InlineData("env", "env.update container.env_set container.env_file_save")]
    [InlineData("container", "container.restart container.env_set container.env_file_save")]
    [InlineData("config", "config.save")]
    public void A_filter_finds_the_rows_of_the_gateway_and_of_the_service(string filter, string expected)
    {
        Assert.Equal(expected.Split(' ').Order(), Matching(filter).Order());
    }

    [Fact]
    public void No_filter_keeps_every_row()
    {
        Assert.Equal(Actions.Length, Matching(null).Length);
        Assert.Equal(Actions.Length, Matching("").Length);
    }
}
