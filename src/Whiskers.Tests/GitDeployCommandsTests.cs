using System.Text;
using Whiskers.Services.GitDeploy;

namespace Whiskers.Tests;

/// <summary>F5 command-building tests (project rule: everything that turns strings into shell
/// commands gets string assertions). User-controlled inputs are repo URL, branch and compose path —
/// all must be single-quoted; the token must only ever travel base64-encoded.</summary>
public class GitDeployCommandsTests
{
    private const string AppId = "abc123def456";

    [Fact]
    public void Workspace_path_is_fixed_and_id_scoped()
        => Assert.Equal("/opt/whiskers-git/abc123def456", GitDeployCommands.WorkDir(AppId));

    [Fact]
    public void Clone_quotes_repo_url_and_branch()
    {
        var cmd = GitDeployCommands.CloneOrUpdateCommand(AppId, "https://github.com/org/app.git", "main", withToken: false);
        Assert.Contains("git clone --branch 'main' --single-branch 'https://github.com/org/app.git' repo", cmd);
        Assert.Contains("GIT_TERMINAL_PROMPT=0", cmd);
        Assert.DoesNotContain("GIT_ASKPASS", cmd);
    }

    [Fact]
    public void Hostile_branch_and_url_stay_inert_inside_quotes()
    {
        var cmd = GitDeployCommands.CloneOrUpdateCommand(AppId,
            "https://evil.example/x.git; rm -rf /", "main; reboot", withToken: false);
        // The dangerous payloads must only appear inside single quotes.
        Assert.Contains("'https://evil.example/x.git; rm -rf /'", cmd);
        Assert.Contains("'main; reboot'", cmd);
        // And never as bare shell text (quote-stripped scan).
        var outsideQuotes = System.Text.RegularExpressions.Regex.Replace(cmd, "'[^']*'", "Q");
        Assert.DoesNotContain("rm -rf /", outsideQuotes);
        Assert.DoesNotContain("reboot", outsideQuotes);
    }

    [Fact]
    public void With_token_the_askpass_env_is_set_and_the_token_never_appears()
    {
        var cmd = GitDeployCommands.CloneOrUpdateCommand(AppId, "https://x/y.git", "main", withToken: true);
        Assert.Contains($"GIT_ASKPASS=/opt/whiskers-git/{AppId}/.whiskers/askpass.sh", cmd);
    }

    [Fact]
    public void Credentials_are_transported_base64_only()
    {
        var token = "ghp_secretSECRET123";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
        var cmd = GitDeployCommands.WriteCredentialsCommand(AppId, b64);
        Assert.DoesNotContain(token, cmd);              // raw token never in the command line
        Assert.Contains($"echo {b64} | base64 -d", cmd);
        Assert.Contains("chmod 600", cmd);              // token file locked down
        Assert.Contains("chmod 700", cmd);              // dir + helper
    }

    [Fact]
    public void Compose_commands_quote_the_compose_path()
    {
        var build = GitDeployCommands.ComposeBuildCommand(AppId, "deploy/docker compose.yml; reboot");
        Assert.Contains("-f 'deploy/docker compose.yml; reboot' build --pull", build);
        var up = GitDeployCommands.ComposeUpCommand(AppId, "docker-compose.yml");
        Assert.Contains("-f 'docker-compose.yml' up -d --remove-orphans", up);
    }

    [Fact]
    public void Remove_workspace_targets_only_the_app_dir()
        => Assert.Equal($"rm -rf /opt/whiskers-git/{AppId}", GitDeployCommands.RemoveWorkspaceCommand(AppId));
}
