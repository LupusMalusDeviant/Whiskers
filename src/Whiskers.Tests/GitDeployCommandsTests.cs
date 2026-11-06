using System.Text;
using Whiskers.Services.GitDeploy;

namespace Whiskers.Tests;

/// <summary>F5 command-building tests (project rule: everything that turns strings into shell
/// commands gets string assertions). User-controlled inputs are repo URL, branch and compose path —
/// all must be single-quoted; the token must only ever travel base64-encoded. R1-42 (INV-41): quoting does not stop
/// option injection, so the branch is validated (on save and again before a deployment runs) and no operand stands
/// where git reads options.</summary>
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
        // R1-42: the branch is an attached option value and the repository follows "--".
        Assert.Contains("git clone '--branch=main' --single-branch -- 'https://github.com/org/app.git' repo", cmd);
        Assert.Contains("GIT_TERMINAL_PROMPT=0", cmd);
        Assert.DoesNotContain("GIT_ASKPASS", cmd);
    }

    [Fact]
    public void Update_spells_every_ref_in_full_so_no_operand_can_be_an_option()
    {
        var cmd = GitDeployCommands.CloneOrUpdateCommand(AppId, "https://github.com/org/app.git", "release/1.2", withToken: false);
        Assert.Contains("git fetch origin '+refs/heads/release/1.2:refs/remotes/origin/release/1.2'", cmd);
        Assert.Contains("git checkout -q -f -B 'release/1.2' 'refs/remotes/origin/release/1.2'", cmd);
        Assert.Contains("git reset --hard 'refs/remotes/origin/release/1.2'", cmd);
        // No bare branch operand where git reads options: after the quotes are gone, every ref operand starts with
        // "+refs/" or "refs/", and the only bare branch is the value of -B.
        Assert.DoesNotContain("fetch origin 'release", cmd);
        Assert.DoesNotContain("origin/'", cmd);
    }

    [Theory]
    [InlineData("--upload-pack=touch /tmp/pwned")]
    [InlineData("-c")]
    [InlineData("main; reboot")]
    [InlineData("main bad")]
    [InlineData("../main")]
    [InlineData("feature/../main")]
    [InlineData("main.lock")]
    [InlineData("/main")]
    [InlineData("main/")]
    [InlineData("feat//x")]
    [InlineData("feat/.hidden")]
    [InlineData("main@{1}")]
    [InlineData("")]
    public void An_unsafe_branch_is_refused_before_a_command_exists(string branch)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() =>
            GitDeployCommands.CloneOrUpdateCommand(AppId, "https://github.com/org/app.git", branch, withToken: false));
        Assert.IsType<Whiskers.Utils.UnsafeOperandException>(ex);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("release/1.2")]
    [InlineData("feature_x-2")]
    [InlineData("v1.0.3")]
    public void A_plain_branch_name_is_accepted(string branch)
        => Assert.Contains($"'--branch={branch}'",
            GitDeployCommands.CloneOrUpdateCommand(AppId, "https://github.com/org/app.git", branch, withToken: false));

    [Theory]
    [InlineData("--upload-pack=x")]
    [InlineData("ssh://git@example.org/x.git")]
    [InlineData("file:///etc")]
    [InlineData("")]
    public void Only_an_https_repository_reaches_the_command(string repoUrl)
        => Assert.Throws<Whiskers.Utils.UnsafeOperandException>(() =>
            GitDeployCommands.CloneOrUpdateCommand(AppId, repoUrl, "main", withToken: false));

    [Fact]
    public void A_hostile_url_stays_inert_inside_quotes()
    {
        var cmd = GitDeployCommands.CloneOrUpdateCommand(AppId,
            "https://evil.example/x.git; rm -rf /", "main", withToken: false);
        // The dangerous payload must only appear inside single quotes, after "--".
        Assert.Contains("-- 'https://evil.example/x.git; rm -rf /'", cmd);
        // And never as bare shell text (quote-stripped scan).
        var outsideQuotes = System.Text.RegularExpressions.Regex.Replace(cmd, "'[^']*'", "Q");
        Assert.DoesNotContain("rm -rf /", outsideQuotes);
    }

    [Theory]
    [InlineData("--upload-pack=touch /tmp/pwned")]
    [InlineData("-x")]
    [InlineData("a..b")]
    public async Task Saving_an_app_with_an_unsafe_branch_is_refused_and_nothing_is_stored(string branch)
    {
        var (git, host, dir) = NewService();
        try
        {
            await Assert.ThrowsAsync<Whiskers.Utils.UnsafeOperandException>(() => git.SaveAppAsync(new Whiskers.Models.GitDeployApp
            {
                Id = AppId, Name = "app", RepoUrl = "https://example.org/x.git", Branch = branch, ServerId = "local",
            }, token: null, "user:admin@x"));
            Assert.Empty(await git.GetAppsAsync());
            Assert.Empty(host.Executed);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task Saving_trims_a_branch_and_keeps_a_plain_one()
    {
        var (git, _, dir) = NewService();
        try
        {
            var saved = await git.SaveAppAsync(new Whiskers.Models.GitDeployApp
            {
                Id = AppId, Name = "app", RepoUrl = "https://example.org/x.git", Branch = " release/1.2 ", ServerId = "local",
            }, token: null, "user:admin@x");
            Assert.Equal("release/1.2", saved.Branch);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task A_stored_unsafe_branch_is_refused_before_anything_runs_on_the_target()
    {
        // An app stored before R1-42 (or edited in the file) reaches the deployment with its old branch.
        var (git, host, dir) = NewService();
        try
        {
            var app = new Whiskers.Models.GitDeployApp
            {
                Id = AppId, Name = "app", RepoUrl = "https://example.org/x.git", Branch = "--upload-pack=touch /tmp/pwned",
                ServerId = "local",
            };
            await Assert.ThrowsAsync<Whiskers.Utils.UnsafeOperandException>(() =>
                git.DeployAsync(app, (_, proceed) => proceed()));
            Assert.Empty(host.Executed);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    private static (GitDeployService Git, ProtectedResourceResolutionTests.FakeHost Host, string Dir) NewService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sw-r142-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var host = new ProtectedResourceResolutionTests.FakeHost();
        var git = new GitDeployService(host, new ProtectedResourceResolutionTests.NoVault(),
            new Whiskers.Services.Mutations.ComposePlanner(new FakeDocker(), host),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GitDeployService>.Instance, Path.Combine(dir, "apps.json"));
        return (git, host, dir);
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
        var file = GitDeployCommands.ComposeFile(AppId, "deploy/docker compose.yml; reboot");
        Assert.Equal($"/opt/whiskers-git/{AppId}/repo/deploy/docker compose.yml; reboot", file);
        var plan = new Whiskers.Services.Mutations.ComposePlan("local", "deploy", $"/opt/whiskers-git/{AppId}/repo/deploy",
            [file], [], null, HasEnvFile: false, RemoveOrphans: true, [], []);
        Assert.Contains($"-f '/opt/whiskers-git/{AppId}/repo/deploy/docker compose.yml; reboot' build --pull", plan.BuildCommand);
        Assert.EndsWith("up -d --remove-orphans --pull never --no-build", plan.UpCommand);
        Assert.StartsWith("env -u COMPOSE_REMOVE_ORPHANS -u COMPOSE_IGNORE_ORPHANS docker compose -p 'deploy' --project-directory ",
            plan.UpCommand);
    }

    [Fact]
    public void Remove_workspace_targets_only_the_app_dir()
        => Assert.Equal($"rm -rf /opt/whiskers-git/{AppId}", GitDeployCommands.RemoveWorkspaceCommand(AppId));
}
