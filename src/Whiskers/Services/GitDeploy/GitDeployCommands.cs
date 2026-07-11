using System.Text;
using Whiskers.Utils;

namespace Whiskers.Services.GitDeploy;

/// <summary>Pure command builders for the git-deploy flow (F5), unit-tested with string assertions
/// (project rule: everything that turns strings into shell commands gets command-building tests).
/// Every user-controlled value is single-quoted via <see cref="ShellUtils.Quote"/>; the access token
/// travels base64-encoded into a 0600 file and is served to git via GIT_ASKPASS — it never appears
/// in a command line or process list.</summary>
public static class GitDeployCommands
{
    /// <summary>Deploy workspace on the target server. The slug comes from the app ID (hex-only).</summary>
    public static string WorkDir(string appId) => $"/opt/whiskers-git/{appId}";

    /// <summary>Writes the askpass helper + token file (0700/0600) from base64 — content never
    /// touches shell quoting. The helper answers git's username prompt with a fixed token user and
    /// the password prompt with the token file's content.</summary>
    public static string WriteCredentialsCommand(string appId, string tokenB64)
    {
        var dir = $"{WorkDir(appId)}/.whiskers";
        var askpass =
            "#!/bin/sh\n" +
            "case \"$1\" in\n" +
            "  Username*) echo x-access-token ;;\n" +
            $"  *) cat {dir}/token ;;\n" +
            "esac\n";
        var askpassB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(askpass));
        return $"mkdir -p {dir} && chmod 700 {dir} && " +
               $"echo {tokenB64} | base64 -d > {dir}/token && chmod 600 {dir}/token && " +
               $"echo {askpassB64} | base64 -d > {dir}/askpass.sh && chmod 700 {dir}/askpass.sh";
    }

    /// <summary>Fresh clone or fast update to the branch tip — idempotent: an existing checkout is
    /// fetched + hard-reset (a deploy workspace has no local edits worth keeping).</summary>
    public static string CloneOrUpdateCommand(string appId, string repoUrl, string branch, bool withToken)
    {
        var dir = WorkDir(appId);
        var env = withToken ? $"GIT_ASKPASS={dir}/.whiskers/askpass.sh GIT_TERMINAL_PROMPT=0 " : "GIT_TERMINAL_PROMPT=0 ";
        var repo = ShellUtils.Quote(repoUrl);
        var br = ShellUtils.Quote(branch);
        return $"mkdir -p {dir} && cd {dir} && " +
               $"if [ -d repo/.git ]; then cd repo && {env}git fetch origin {br} && git checkout -q {br} && git reset --hard origin/{ShellUtils.Quote(branch)}; " +
               $"else rm -rf repo && {env}git clone --branch {br} --single-branch {repo} repo; fi";
    }

    public static string CurrentShaCommand(string appId) =>
        $"cd {WorkDir(appId)}/repo && git rev-parse --short=12 HEAD";

    /// <summary>docker compose build with the repo-relative compose file. Quoted — a crafted
    /// compose path must not become a command.</summary>
    public static string ComposeBuildCommand(string appId, string composePath) =>
        $"cd {WorkDir(appId)}/repo && docker compose -f {ShellUtils.Quote(composePath)} build --pull";

    public static string ComposeUpCommand(string appId, string composePath) =>
        $"cd {WorkDir(appId)}/repo && docker compose -f {ShellUtils.Quote(composePath)} up -d --remove-orphans";

    /// <summary>Removes the whole deploy workspace (teardown when the app is deleted).</summary>
    public static string RemoveWorkspaceCommand(string appId) =>
        $"rm -rf {WorkDir(appId)}";
}
