using System.Text;
using System.Text.RegularExpressions;

namespace Whiskers.Services.Certificates;

/// <summary>
/// Host scripts that change which client names a host's ghostunnel admits (R1-44, contract 0007 with amendments 1–4).
/// Pure builders, so every line that reaches a host is asserted in a test.
///
/// <para>Protected mTLS layer: these scripts edit the <c>--allow-cn</c> items under ghostunnel's <c>command:</c> and
/// nothing else — not the image, the ports, the listen, target or certificate flags, not the socket proxy. Each change is
/// guarded like a certificate renewal: the compose file is kept as <c>.prev</c>, and a dead-man timer of its own
/// (<c>whiskers-allow-guard-{run}</c>, script <c>allow-guard-{run}.sh</c>, marker <c>.allow-ok-{run}</c>; never the
/// renewal's guard or marker) restores it and recreates ghostunnel unless the run was confirmed. The confirmation
/// (<see cref="Confirm"/>) is one script, run through a one-off connection with the NEW credential: it requires its own
/// guard timer to be still pending, stops it, and only then writes the marker. Every recreate — the step's and the
/// guard's — is exactly <see cref="Recreate"/>, started detached by <c>systemd-run</c>.</para>
///
/// <para>Every value inside a script is allow-listed: client names by <see cref="MtlsClientIdentity.IsValidCommonName"/>,
/// paths by <see cref="IsSafePath"/>, the project by <see cref="IsSafeProject"/>, the run suffix by its own pattern. The
/// scripts travel base64-encoded (<see cref="MtlsCertificateCommands.AsRootScript"/>).</para>
/// </summary>
public static partial class MtlsAllowListCommands
{
    public const string WorkDir = "/opt/dockerproxy";
    public const string GuardUnitPrefix = "whiskers-allow-guard-";
    public const string MarkerPrefix = "/opt/dockerproxy/.allow-ok-";
    public static readonly TimeSpan GuardDelay = MtlsCertificateCommands.GuardDelay;

    /// <summary>The approved ghostunnel arguments apart from <c>--allow-cn</c> (the onboarding template's, pinned by
    /// <c>OnboardingCommandsTests</c>). A host whose running ghostunnel has anything else is refused before any change.</summary>
    public static readonly IReadOnlyList<string> ApprovedArgsWithoutAllowCn =
    [
        "server", "--listen=0.0.0.0:2376", "--target=socket-proxy:2375", "--unsafe-target", "--cert=/certs/server.crt",
        "--key=/certs/server.key", "--cacert=/certs/ca.crt",
    ];

    public const string AllowCnFlag = "--allow-cn=";

    /// <summary>The compose project that runs a host's proxy, as its container labels name it.</summary>
    public sealed record ComposeTarget(string Project, string WorkingDir, string File)
    {
        public ComposeTarget Checked() =>
            IsSafeProject(Project) && IsSafePath(WorkingDir) && IsSafePath(File)
            && (File.EndsWith(".yml", StringComparison.Ordinal) || File.EndsWith(".yaml", StringComparison.Ordinal))
                ? this
                : throw new ArgumentException($"'{Project}' / '{WorkingDir}' / '{File}' is not an allowed compose target.");
    }

    /// <summary>The one recreate command: exactly the project, directory and single file the running container came
    /// from, only the ghostunnel service, nothing it depends on.</summary>
    public static string Recreate(ComposeTarget target)
    {
        var t = target.Checked();
        return $"docker compose -p {t.Project} --project-directory {t.WorkingDir} -f {t.File} up -d --pull never --no-deps --force-recreate ghostunnel";
    }

    /// <summary>
    /// Reads what the host runs: the full <c>docker inspect</c> of ghostunnel and socket-proxy (base64), the repo digests
    /// of their images, the compose file their labels name (base64, only when there is exactly one), and the active Whiskers
    /// guard units by name.
    /// </summary>
    public static string ReadHostState() => MtlsCertificateCommands.AsRootScript("""
        set -e
        gu=$(docker inspect ghostunnel | base64 -w0)
        sp=$(docker inspect socket-proxy | base64 -w0)
        gudig=$(docker image inspect "$(docker inspect ghostunnel --format '{{.Image}}')" --format '{{json .RepoDigests}}' | base64 -w0)
        spdig=$(docker image inspect "$(docker inspect socket-proxy --format '{{.Image}}')" --format '{{json .RepoDigests}}' | base64 -w0)
        files=$(docker inspect ghostunnel --format '{{index .Config.Labels "com.docker.compose.project.config_files"}}')
        compose=''
        case "$files" in
          *,*) ;;
          *) if [ -f "$files" ]; then compose=$(base64 -w0 < "$files"); fi ;;
        esac
        units=$(systemctl list-units --no-legend --plain --state=active,activating 'whiskers-*-guard*' 2>/dev/null | awk '{print $1}' | tr '\n' ' ')
        printf 'ghostunnel=%s\n' "$gu"
        printf 'socketproxy=%s\n' "$sp"
        printf 'ghostunneldigests=%s\n' "$gudig"
        printf 'socketproxydigests=%s\n' "$spdig"
        printf 'compose=%s\n' "$compose"
        printf 'units=%s\n' "$units"
        """);

    /// <summary>
    /// Replaces the <c>--allow-cn</c> items of the compose file in place with exactly <paramref name="allowedCommonNames"/>
    /// (same indentation, at the position of the first one), verifies that nothing else changed, removes a stale marker of
    /// this run, arms the guard, swaps the file and recreates ghostunnel detached.
    /// </summary>
    public static string Apply(ComposeTarget target, IReadOnlyList<string> allowedCommonNames, string runSuffix)
    {
        var t = target.Checked();
        var f = t.File;
        var cns = RequireNames(allowedCommonNames);
        var run = RequireSuffix(runSuffix);
        var expected = string.Join(" ", cns) + " ";
        var guardScript = GuardScriptPath(run);
        var guard = Convert.ToBase64String(Encoding.UTF8.GetBytes(GuardBody(t, run)));
        return MtlsCertificateCommands.AsRootScript($$"""
            set -e
            [ -f {{f}} ] || { echo 'compose file {{f}} is missing' >&2; exit 1; }
            total=$(grep -c -e 'allow-cn' {{f}} || true)
            plain=$(grep -c -E '^[[:space:]]*- --allow-cn=[a-z0-9.-]+[[:space:]]*$' {{f}} || true)
            if [ "$total" -lt 1 ] || [ "$total" != "$plain" ]; then echo 'unexpected --allow-cn lines in {{f}}' >&2; exit 1; fi
            rm -f {{f}}.new
            rm -f {{MarkerPrefix}}*
            cp -p {{f}} {{f}}.prev
            awk -v cns='{{string.Join(" ", cns)}}' '/allow-cn/ { if (!done) { ind = $0; sub(/-.*/, "", ind); n = split(cns, a, " "); for (i = 1; i <= n; i++) print ind "- --allow-cn=" a[i]; done = 1 } next } { print }' {{f}}.prev > {{f}}.new
            [ "$(grep -v -e 'allow-cn' {{f}}.prev)" = "$(grep -v -e 'allow-cn' {{f}}.new)" ] || { rm -f {{f}}.new; echo 'the patch would change more than --allow-cn' >&2; exit 1; }
            [ "$(grep -e 'allow-cn' {{f}}.new | sed -e 's/^[[:space:]]*- --allow-cn=//' -e 's/[[:space:]]*$//' | tr '\n' ' ')" = "{{expected}}" ] || { rm -f {{f}}.new; echo 'the patch did not produce the expected --allow-cn list' >&2; exit 1; }
            chmod --reference={{f}}.prev {{f}}.new
            mkdir -p {{WorkDir}}
            echo {{guard}} | base64 -d > {{guardScript}}
            systemd-run --collect --unit={{GuardUnitPrefix}}{{run}} --on-active={{(int)GuardDelay.TotalSeconds}} /bin/sh {{guardScript}} >/dev/null 2>&1
            mv -f {{f}}.new {{f}}
            systemd-run --collect --unit=whiskers-ghostunnel-recreate-{{run}} --on-active=3 {{Recreate(t)}} >/dev/null 2>&1
            echo applied
            """);
    }

    /// <summary>
    /// The dead-man timer's body (amendments 5–7). It waits for the run's lock without a time limit — it never skips
    /// because the lock is held (a hanging confirmation is killed after 20 s, see <see cref="Confirm"/>) — and then rolls
    /// back unless the marker of its own run is present: the compose file goes back from <c>.prev</c> and ghostunnel is
    /// recreated detached (<c>whiskers-allow-guard-recreate-{run}</c>). Afterwards it checks that ghostunnel runs with the
    /// restored <c>--allow-cn</c> list, recreates once more if not, and writes the result to the system journal
    /// (<c>logger -t whiskers-allow-guard</c>).
    /// </summary>
    internal static string GuardBody(ComposeTarget target, string runSuffix)
    {
        var t = target.Checked();
        var run = RequireSuffix(runSuffix);
        var tag = "whiskers-allow-guard";
        return $$$"""
            exec 9>{{{LockPath(run)}}}
            flock 9
            if [ -f {{{MarkerPrefix}}}{{{run}}} ]; then
              rm -f {{{MarkerPrefix}}}{{{run}}} {{{t.File}}}.prev
              logger -t {{{tag}}} 'run {{{run}}}: confirmed, nothing to restore'
            elif [ -f {{{t.File}}}.prev ]; then
              mv -f {{{t.File}}}.prev {{{t.File}}}
              want=$(grep -e 'allow-cn' {{{t.File}}} | sed -e 's/^[[:space:]]*- //' -e 's/[[:space:]]*$//' | tr '\n' ' ')
              restored() {
                [ "$(docker inspect ghostunnel --format '{{.State.Running}}' 2>/dev/null)" = "true" ] &&
                [ "$(docker inspect ghostunnel --format '{{range .Args}}{{println .}}{{end}}' 2>/dev/null | grep -e '^--allow-cn=' | tr '\n' ' ')" = "$want" ]
              }
              systemd-run --collect --unit={{{GuardUnitPrefix}}}recreate-{{{run}}} --on-active=1 {{{Recreate(t)}}} >/dev/null 2>&1 || true
              sleep 30
              if restored; then
                logger -t {{{tag}}} 'run {{{run}}}: restored the previous allow-list'
              else
                systemd-run --collect --unit={{{GuardUnitPrefix}}}recreate2-{{{run}}} --on-active=1 {{{Recreate(t)}}} >/dev/null 2>&1 || true
                sleep 30
                if restored; then
                  logger -t {{{tag}}} 'run {{{run}}}: restored the previous allow-list on the second recreate'
                else
                  logger -p user.err -t {{{tag}}} 'run {{{run}}}: ghostunnel does not run with the restored allow-list after two recreates'
                fi
              fi
            fi
            rm -f {{{GuardScriptPath(run)}}}
            """;
    }

    /// <summary>
    /// The confirmation of a run, as one script, sent through a one-off connection with the NEW credential (amendments
    /// 4–7). Its locked section runs under <c>timeout 20</c> (a hang inside the lock is killed, the lock released, and the
    /// guard then rolls back): it requires the guard timer to be loaded and still waiting (<c>SubState=waiting</c>; a
    /// <c>dead</c>, elapsed or not-loaded timer fails) and the guard service not active, stops the timer, requires
    /// <c>.prev</c> (the guard has not restored anything), writes the marker and drops <c>.prev</c>. The guard never runs
    /// after that; the marker is removed by <see cref="Cleanup"/> once Whiskers has recorded the phase.
    /// </summary>
    public static string Confirm(ComposeTarget target, string runSuffix)
    {
        var t = target.Checked();
        var run = RequireSuffix(runSuffix);
        var locked = Convert.ToBase64String(Encoding.UTF8.GetBytes(ConfirmBody(t, run)));
        return MtlsCertificateCommands.AsRootScript($$"""
            mkdir -p {{WorkDir}}
            rc=0
            echo {{locked}} | base64 -d | timeout -k 5 20 flock -w 30 {{LockPath(run)}} sh -s || rc=$?
            if [ "$rc" -eq 124 ] || [ "$rc" -eq 137 ]; then echo 'the confirmation did not finish within 20 seconds inside the lock' >&2; fi
            exit $rc
            """);
    }

    /// <summary>The locked section of <see cref="Confirm"/> (run by <c>flock … sh -s</c> under <c>timeout 20</c>).</summary>
    internal static string ConfirmBody(ComposeTarget target, string runSuffix)
    {
        var t = target.Checked();
        var run = RequireSuffix(runSuffix);
        return $$"""
            set -e
            [ "$(systemctl show -p LoadState --value {{GuardUnitPrefix}}{{run}}.timer)" = "loaded" ] || { echo 'the guard timer of this run is not loaded' >&2; exit 1; }
            [ "$(systemctl show -p SubState --value {{GuardUnitPrefix}}{{run}}.timer)" = "waiting" ] || { echo 'the guard timer of this run is no longer waiting' >&2; exit 1; }
            if systemctl is-active --quiet {{GuardUnitPrefix}}{{run}}.service; then echo 'the guard of this run is running' >&2; exit 1; fi
            systemctl stop {{GuardUnitPrefix}}{{run}}.timer
            [ -f {{t.File}}.prev ] || { echo 'the guard already restored the previous allow-list' >&2; exit 1; }
            touch {{MarkerPrefix}}{{run}}
            rm -f {{t.File}}.prev {{GuardScriptPath(run)}}
            echo confirmed
            """;
    }

    /// <summary>After Whiskers recorded the phase: removes the run's marker and lock.</summary>
    public static string Cleanup(string runSuffix)
    {
        var run = RequireSuffix(runSuffix);
        return MtlsCertificateCommands.AsRootScript($"""
            rm -f {MarkerPrefix}{run} {LockPath(run)}
            echo cleaned
            """);
    }

    private static string LockPath(string run) => $"{WorkDir}/.migrate-{run}.lock";

    /// <summary>Reads whether ghostunnel runs and its running arguments (the retire check and the readiness line):
    /// prints <c>true|false</c>, a space and the JSON array of arguments.</summary>
    public static string ReadArgs() => "docker inspect ghostunnel --format '{{.State.Running}} {{json .Args}}'";

    private static string GuardScriptPath(string run) => $"{WorkDir}/allow-guard-{run}.sh";

    /// <summary>An absolute path the scripts may name: plain characters, no "..".</summary>
    public static bool IsSafePath(string? path) =>
        path is not null && PathRegex().IsMatch(path) && !path.Contains("..", StringComparison.Ordinal);

    /// <summary>A compose project name the scripts may name.</summary>
    public static bool IsSafeProject(string? project) => project is not null && ProjectRegex().IsMatch(project);

    /// <summary>A run suffix (the unit and marker names carry it).</summary>
    public static bool IsSafeSuffix(string? suffix) => suffix is not null && SuffixRegex().IsMatch(suffix);

    private static IReadOnlyList<string> RequireNames(IReadOnlyList<string> names)
    {
        if (names.Count == 0) throw new ArgumentException("An allow-list needs at least one name.", nameof(names));
        foreach (var n in names)
            if (!MtlsClientIdentity.IsValidCommonName(n)) throw new ArgumentException($"'{n}' is not an allowed client name.", nameof(names));
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new ArgumentException("An allow-list names each client once.", nameof(names));
        return names;
    }

    private static string RequireSuffix(string suffix) =>
        IsSafeSuffix(suffix) ? suffix : throw new ArgumentException($"'{suffix}' is not a run suffix.", nameof(suffix));

    [GeneratedRegex("^/[A-Za-z0-9._/-]+$")]
    private static partial Regex PathRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,62}$")]
    private static partial Regex ProjectRegex();

    [GeneratedRegex("^[a-z0-9]{1,24}$")]
    private static partial Regex SuffixRegex();
}
