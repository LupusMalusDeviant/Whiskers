using System.Text;
using Whiskers.Utils;

namespace Whiskers.Services.GitDeploy;

/// <summary>Pure command builders for the git-deploy flow (F5), unit-tested with string assertions
/// (project rule: everything that turns strings into shell commands gets command-building tests).
/// Every user-controlled value is single-quoted via <see cref="ShellUtils.Quote"/> against the shell AND checked by
/// <see cref="CommandOperands"/> against option injection (R1-42) — quoting alone would hand git a token like
/// <c>--upload-pack=…</c> that it parses as an option. Positional operands never stand where git reads options:
/// refs are spelled in full (<c>+refs/heads/…</c>, <c>refs/remotes/origin/…</c>), the branch of a clone is an
/// attached option value (<c>--branch=…</c>) and the repository follows <c>--</c>. The access token
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
    /// fetched + hard-reset (a deploy workspace has no local edits worth keeping). The branch must pass
    /// <see cref="CommandOperands.IsSafeGitRef"/> and the repository must be an <c>https://</c> URL; anything else
    /// throws before a command exists.</summary>
    public static string CloneOrUpdateCommand(string appId, string repoUrl, string branch, bool withToken)
    {
        CommandOperands.RequireGitRef(branch);
        if (string.IsNullOrEmpty(repoUrl) || !repoUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new UnsafeOperandException("repository URL", repoUrl, "only https:// repository URLs are supported");
        var dir = WorkDir(appId);
        var env = withToken ? $"GIT_ASKPASS={dir}/.whiskers/askpass.sh GIT_TERMINAL_PROMPT=0 " : "GIT_TERMINAL_PROMPT=0 ";
        var repo = ShellUtils.Quote(repoUrl);
        var br = ShellUtils.Quote(branch);
        // Fully spelled refs: an operand that starts with "+refs/" or "refs/" can never be read as an option.
        var fetchSpec = ShellUtils.Quote($"+refs/heads/{branch}:refs/remotes/origin/{branch}");
        var tracking = ShellUtils.Quote($"refs/remotes/origin/{branch}");
        var branchOption = ShellUtils.Quote($"--branch={branch}");
        // Contract 0013, D12: the pin file a compose run writes into the checkout (.whiskers/ beside the compose file) is
        // Whiskers' own, never part of the repository's working tree state.
        return $"mkdir -p {dir} && cd {dir} && " +
               $"if [ -d repo/.git ]; then cd repo && {env}git fetch origin {fetchSpec} && git checkout -q -f -B {br} {tracking} && git reset --hard {tracking}; " +
               $"else rm -rf repo && {env}git clone {branchOption} --single-branch -- {repo} repo; fi" +
               $" && cd {dir}/repo && {{ grep -qxF '{ExcludePattern}' .git/info/exclude 2>/dev/null || " +
               $"{{ mkdir -p .git/info && echo '{ExcludePattern}' >> .git/info/exclude; }}; }}";
    }

    /// <summary>The <c>.git/info/exclude</c> line that keeps Whiskers' pin directory out of the checkout's state
    /// (contract 0013, D12): <c>.whiskers/</c> at any depth.</summary>
    public const string ExcludePattern = Whiskers.Services.Mutations.ComposePins.DirectoryName + "/";

    public static string CurrentShaCommand(string appId) =>
        $"cd {WorkDir(appId)}/repo && git rev-parse --short=12 HEAD";

    /// <summary>The absolute path of the app's compose file in its checkout. The build and up commands come from
    /// the deployment's <c>ComposePlan</c> (R1-07), which quotes it.</summary>
    public static string ComposeFile(string appId, string composePath) =>
        $"{WorkDir(appId)}/repo/{composePath.TrimStart('/')}";

    /// <summary>Removes the whole deploy workspace (teardown when the app is deleted).</summary>
    public static string RemoveWorkspaceCommand(string appId) =>
        $"rm -rf {WorkDir(appId)}";
}
