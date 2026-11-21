using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Whiskers.Tests;

/// <summary>Contract 0017 part I: the borg-fleet scripts without Python. <c>borg-enroll</c> is POSIX <c>sh</c> (run here
/// with <c>sh</c>: dash on CI) with the same arguments, outputs and exit codes, one test per exit code against stubs;
/// <c>borg-fleet-client</c> copies SQLite files with the sqlite3 CLI and, without sqlite3, still completes the backup
/// and reports the skipped snapshot; <c>borg-fleet-prune</c> reads the enrolment record with jq.</summary>
public sealed class BorgFleetScriptTests : IDisposable
{
    private readonly ShellScriptHarness _h = new();

    public void Dispose() => _h.Dispose();

    // --- borg-enroll -------------------------------------------------------------------------------------------------

    private static readonly string[] Enroll = ["deploy", "borg-fleet", "borg-enroll"];
    private const string Token = "hz-token-secret";
    private const string McpKey = "mcp-key-secret";
    private static readonly string[] EnrollTools =
        ["jq", "base64", "sha256sum", "awk", "sed", "tr", "head", "tail", "cut", "grep", "cp", "date", "mktemp",
         "rm", "mkdir", "chmod", "dirname", "cat"];

    private string Cfg => Path.Combine(_h.Root, "cfg");

    private void EnrollStubs()
    {
        Directory.CreateDirectory(Cfg);
        File.WriteAllText(Path.Combine(Cfg, "token"), Token + "\n");
        File.WriteAllText(Path.Combine(Cfg, "whiskers-mcp-key"), McpKey + "\n");
        File.WriteAllText(Path.Combine(Cfg, "fleet.json"),
            $$$"""{"box_id":7,"hetzner_token_file":"{{{DeploymentScripts.ForBash(Path.Combine(Cfg, "token"))}}}","whiskers_mcp_url":"https://mcp.test/mcp","times":{"other":"01:15"}}""");
        _h.Stub("sleep", "#!/bin/bash\nexit 0\n");
        _h.Stub("ssh-keygen", """
            #!/bin/bash
            while [ $# -gt 0 ]; do [ "$1" = -f ] && f=$2; shift; done
            echo private > "$f"; echo 'ssh-ed25519 AAAAadmin borg-fleet-admin@control-host' > "$f.pub"
            """);
        _h.Stub("sftp", "#!/bin/bash\nexit 0\n");
        _h.Stub("sshpass", """
            #!/bin/bash
            echo "sshpass $* SSHPASS=${SSHPASS:+set}" >> "$STUB_DIR/sshpass.log"
            cat > "$STUB_DIR/sftp.batch"
            src=$(sed -n 's/^put \([^ ]*\) .*/\1/p' "$STUB_DIR/sftp.batch")
            cp "$src" "$STUB_DIR/authorized_keys"
            echo '-rw------- 1 u u 120 Nov 15 .ssh/authorized_keys'
            """);
        _h.Stub("curl", """
            #!/bin/bash
            echo "curl $*" >> "$STUB_DIR/curl.args"
            out=/dev/null; data=; method=GET; url=; dump=; hdrs=
            while [ $# -gt 0 ]; do
              case $1 in
                -o) out=$2; shift ;; -D) dump=$2; shift ;; -X) method=$2; shift ;; -w|--max-time) shift ;;
                --data-binary) data=${2#@}; method=${method/GET/POST}; shift ;;
                -H) case $2 in @*) hdrs="$hdrs$(cat "${2#@}")"$'\n' ;; esac; shift ;;
                -*) ;;
                *) url=$1 ;;
              esac; shift
            done
            body=; [ -n "$data" ] && body=$(cat "$data")
            printf '%s %s\n' "$method" "$url" >> "$STUB_DIR/curl.log"
            printf '%s' "$hdrs" >> "$STUB_DIR/curl.headers"
            code=200; resp='{}'
            case $url in
              https://mcp.test/mcp)
                [ -n "$dump" ] && printf 'HTTP/1.1 200 OK\r\nMcp-Session-Id: sess-1\r\n\r\n' > "$dump"
                case $body in
                  *notifications/initialized*) resp= ;;
                  *'"initialize"'*) resp=$(printf 'event: message\ndata: {"jsonrpc":"2.0","id":1,"result":{}}\n') ;;
                  *"borg-fleet-client prepare"*)
                    if [ "${STUB_PREPARE_EXIT:-0}" != 0 ]; then t=$(printf 'Exit code: %s\nprepare broke' "$STUB_PREPARE_EXIT")
                    else t=$(printf 'BORG_VERSION=1.4.1\nPUBKEY=ssh-ed25519 AAAAserver borg-fleet@neuhof'); fi
                    resp="data: $(jq -cn --arg t "$t" '{jsonrpc:"2.0",id:2,result:{content:[{type:"text",text:$t}]}}')" ;;
                  *"borg-fleet-client init"*)
                    echo "$body" > "$STUB_DIR/init.req"
                    t=$(printf 'REPO=created\nPASSPHRASE=pass-phrase-1\nREPOKEY_B64=%s\nTIMER=Mon 2026-11-16 01:15' "$(printf 'repokey-bytes' | base64)")
                    resp="$(jq -cn --arg t "$t" '{jsonrpc:"2.0",id:3,result:{content:[{type:"text",text:$t}]}}')" ;;
                  *systemd-run*) resp='{"jsonrpc":"2.0","id":4,"result":{"content":[{"type":"text","text":"Running as unit"}]}}' ;;
                esac ;;
              */storage_boxes/7)
                if [ -n "${STUB_HZ_FAIL:-}" ]; then code=500; resp='{"error":"down"}'
                else resp='{"storage_box":{"server":"u1.your-storagebox.de","snapshot_plan":null}}'; fi ;;
              */storage_boxes/7/actions/enable_snapshot_plan) resp='{"action":{"id":1,"status":"running","command":"enable_snapshot_plan"}}' ;;
              */storage_boxes/7/subaccounts)
                if [ "$method" = POST ]; then echo "$body" > "$STUB_DIR/create.req"; resp='{"subaccount":{"id":42}}'
                else resp='{"subaccounts":[{"id":1,"username":"u1-sub1","labels":{"whiskers-server":"other"}}]}'; fi ;;
              */storage_boxes/7/subaccounts/42) resp='{"subaccount":{"id":42,"username":"u1-sub9"}}' ;;
              */reset_subaccount_password) echo "$body" >> "$STUB_DIR/reset.req"; resp='{"action":{"id":5,"status":"running","command":"reset_subaccount_password"}}' ;;
              */storage_boxes/actions/5) resp='{"action":{"id":5,"status":"success","command":"reset_subaccount_password"}}' ;;
              *) code=404; resp='{"error":"not found"}' ;;
            esac
            printf '%s' "$resp" > "$out"
            printf '%s' "$code"
            """);
    }

    private (int Exit, string Stdout, string Stderr) RunEnroll(string[] args, Dictionary<string, string?>? env = null, string[]? tools = null)
    {
        env ??= new();
        env["BORG_FLEET_CFG_DIR"] = DeploymentScripts.ForBash(Cfg);
        return _h.Run("sh", Enroll, args, tools ?? EnrollTools, env);
    }

    private string Stub(string file) => File.Exists(Path.Combine(_h.Root, file)) ? File.ReadAllText(Path.Combine(_h.Root, file)).Replace("\r", "") : "";

    /// <summary>The fixed slot of the former Python version: sha256(id) mod 20 quarter hours after 00:30.</summary>
    private static string Slot(string id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        var h = (int)(new BigInteger(hash, isUnsigned: true, isBigEndian: true) % 20);
        var m = 30 + h * 15;
        return $"{m / 60:00}:{m % 60:00}";
    }

    [SkippableFact]
    public void Borg_enroll_exits_0_after_a_full_enrolment_with_the_same_outputs()
    {
        EnrollStubs();
        var (exit, stdout, stderr) = RunEnroll(["neuhof", "--first-run"]);

        Assert.True(exit == 0, stdout + stderr);
        var slot = Slot("neuhof");
        Assert.Equal(
            "box: snapshot plan enabled (daily 06:30 UTC, keep 14)\n" +
            "[neuhof] borg 1.4.1 -> remote borg-1.4\n" +
            "[neuhof] created sub-account u1-sub9\n" +
            $"[neuhof] repo created ssh://u1-sub9@u1.your-storagebox.de:23/./borg, timer {slot} (next Mon 2026-11-16 01:15)\n" +
            "[neuhof] first backup started (journalctl -u borg-fleet-first)\n",
            stdout.Replace("\r", ""));

        // The authorized_keys: the server key forced to append-only, then the admin key.
        Assert.Equal(
            "command=\"borg-1.4 serve --append-only --restrict-to-repository borg\",restrict ssh-ed25519 AAAAserver borg-fleet@neuhof\n" +
            "ssh-ed25519 AAAAadmin borg-fleet-admin@control-host\n", Stub("authorized_keys"));
        Assert.Contains("-P 23", Stub("sshpass.log"));
        Assert.Contains("SSHPASS=set", Stub("sshpass.log"));
        Assert.Contains("u1-sub9@u1.your-storagebox.de", Stub("sshpass.log"));

        // The sub-account the Python version created.
        using (var create = JsonDocument.Parse(Stub("create.req")))
        {
            var root = create.RootElement;
            Assert.Equal("whiskers-neuhof", root.GetProperty("name").GetString());
            Assert.Equal("whiskers-neuhof", root.GetProperty("home_directory").GetString());
            Assert.Equal("neuhof", root.GetProperty("labels").GetProperty("whiskers-server").GetString());
            Assert.StartsWith("Bf7#", root.GetProperty("password").GetString());
            Assert.Equal(32, root.GetProperty("password").GetString()!.Length);
        }
        Assert.Contains($"init 'ssh://u1-sub9@u1.your-storagebox.de:23/./borg' {slot} borg-1.4", Stub("init.req"));

        // Escrow and record.
        Assert.Equal("pass-phrase-1\n", File.ReadAllText(Path.Combine(Cfg, "escrow", "neuhof", "passphrase")).Replace("\r", ""));
        Assert.Equal("repokey-bytes", File.ReadAllText(Path.Combine(Cfg, "escrow", "neuhof", "repokey")));
        using (var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(Cfg, "servers", "neuhof.json"))))
        {
            var r = record.RootElement;
            Assert.Equal("neuhof", r.GetProperty("server_id").GetString());
            Assert.Equal("u1-sub9", r.GetProperty("subaccount").GetString());
            Assert.Equal(42, r.GetProperty("subaccount_id").GetInt32());
            Assert.Equal("ssh://u1-sub9@u1.your-storagebox.de:23/./borg", r.GetProperty("repo").GetString());
            Assert.Equal("borg-1.4", r.GetProperty("remote_path").GetString());
            Assert.Equal(slot, r.GetProperty("time").GetString());
            Assert.Equal("1.4.1", r.GetProperty("borg_version").GetString());
        }

        // The secrets travel in header files or on stdin, never in a program's arguments (review M1): neither the API
        // token, the MCP key, nor any sub-account password — the one used for the upload and the final lock one.
        var args = Stub("curl.args") + Stub("argv.log") + Stub("sshpass.log");
        Assert.Contains("jq ", Stub("argv.log"));   // the wrapped tools did log
        var passwords = new List<string> { JsonDocument.Parse(Stub("create.req")).RootElement.GetProperty("password").GetString()! };
        passwords.AddRange(Stub("reset.req").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("password").GetString()!));
        Assert.Equal(2, passwords.Count);
        Assert.NotEqual(passwords[0], passwords[1]);   // the lock password is a fresh one
        foreach (var secret in passwords.Append(Token).Append(McpKey))
            Assert.DoesNotContain(secret, args);
        Assert.Contains($"Authorization: Bearer {Token}", Stub("curl.headers"));
        Assert.Contains($"Authorization: Bearer {McpKey}", Stub("curl.headers"));
        Assert.Contains("Mcp-Session-Id: sess-1", Stub("curl.headers"));
        Assert.Contains("POST https://api.hetzner.com/v1/storage_boxes/7/subaccounts/42/actions/reset_subaccount_password", Stub("curl.log"));
        Assert.Contains("GET https://api.hetzner.com/v1/storage_boxes/actions/5", Stub("curl.log"));   // waited for the action
    }

    [SkippableFact]
    public void Borg_enroll_takes_the_time_from_the_argument_or_fleet_json()
    {
        EnrollStubs();
        Assert.Equal(0, RunEnroll(["neuhof", "--time", "03:45"]).Exit);
        Assert.Contains("init 'ssh://u1-sub9@u1.your-storagebox.de:23/./borg' 03:45 borg-1.4", Stub("init.req"));
        Assert.Equal(0, RunEnroll(["other"]).Exit);
        Assert.Contains("01:15 borg-1.4", Stub("init.req"));
    }

    [SkippableTheory]
    [InlineData("usage")]
    [InlineData("missing-tool")]
    [InlineData("hetzner-api")]
    [InlineData("prepare-failed")]
    [InlineData("bad-time")]
    [InlineData("hour-24")]
    [InlineData("empty-id")]
    public void Borg_enroll_exits_1_with_a_message_on_every_failure(string failure)
    {
        EnrollStubs();
        var env = new Dictionary<string, string?>();
        string[] args = ["neuhof"];
        var tools = EnrollTools;
        string expected;
        switch (failure)
        {
            case "usage": args = []; expected = "borg-enroll: usage: borg-enroll <whiskers-server-id>"; break;
            case "missing-tool": _h.Unstub("sshpass"); expected = "borg-enroll: missing tool: sshpass"; break;
            case "hetzner-api": env["STUB_HZ_FAIL"] = "1"; expected = "borg-enroll: Hetzner API GET /storage_boxes/7 -> 500: {\"error\":\"down\"}"; break;
            case "prepare-failed": env["STUB_PREPARE_EXIT"] = "3"; expected = "borg-enroll: prepare failed on neuhof:\nExit code: 3\nprepare broke"; break;
            case "hour-24": args = ["neuhof", "--time", "24:00"]; expected = "borg-enroll: invalid time slot: 24:00"; break;
            case "empty-id": args = [""]; expected = "borg-enroll: invalid server id: ''"; break;
            default: args = ["neuhof", "--time", "1;reboot"]; expected = "borg-enroll: invalid time slot"; break;
        }

        var (exit, stdout, stderr) = RunEnroll(args, env, tools);

        Assert.True(exit == 1, $"exit {exit}: {stdout}{stderr}");
        Assert.Contains(expected, stderr.Replace("\r", ""));
        Assert.False(File.Exists(Path.Combine(Cfg, "servers", "neuhof.json")));
    }

    // --- borg-fleet-client backup ------------------------------------------------------------------------------------

    private static readonly string[] Client = ["deploy", "borg-fleet", "borg-fleet-client"];
    private static readonly string[] ClientTools =
        ["date", "rm", "install", "mv", "head", "od", "tr", "sed", "find", "cat", "mkdir"];

    private string TestRoot => Path.Combine(_h.Root, "host");

    private void ClientStubs()
    {
        foreach (var dir in new[] { "etc/borg-fleet", "var/lib/borg-fleet", "var/backups/borg-fleet", "opt/app", "srv" })
            Directory.CreateDirectory(Path.Combine(TestRoot, dir));
        File.WriteAllText(Path.Combine(TestRoot, "etc/borg-fleet/borg-fleet.env"), "BORG_REPO=ssh://u@h:23/./borg\n");
        _h.Stub("flock", "#!/bin/bash\nexit 0\n");
        _h.Stub("borg", "#!/bin/bash\necho \"borg $*\" >> \"$STUB_DIR/borg.log\"\nexit 0\n");
    }

    private (int Exit, string Output, JsonElement Status) RunBackup(params string[] hostTools)
    {
        var env = new Dictionary<string, string?> { ["BORG_FLEET_TEST_ROOT"] = DeploymentScripts.ForBash(TestRoot) };
        var (exit, stdout, stderr) = _h.Run("bash", Client, ["backup"], [.. ClientTools, .. hostTools], env);
        var statusFile = Path.Combine(TestRoot, "var/lib/borg-fleet/last-status.json");
        Assert.True(File.Exists(statusFile), stdout + stderr);
        using var doc = JsonDocument.Parse(File.ReadAllText(statusFile));
        return (exit, stdout + stderr, doc.RootElement.Clone());
    }

    private string SnapshotDir => Path.Combine(TestRoot, "var/backups/borg-fleet/sqlite");

    [SkippableFact]
    public void Without_sqlite3_the_backup_still_runs_and_the_status_reports_the_skipped_snapshot()
    {
        ClientStubs();
        File.WriteAllBytes(Path.Combine(TestRoot, "opt/app/data.db"), SqliteHeaderFile());

        var (exit, output, status) = RunBackup();   // no sqlite3 on the PATH

        Assert.True(exit == 0, output);
        Assert.Contains("borg create", Stub("borg.log"));                      // the backup itself ran
        Assert.Contains("WARN: sqlite3 or timeout missing — SQLite snapshot skipped", output);
        Assert.Equal("skipped", status.GetProperty("sqlite_snapshot").GetString());
        Assert.Equal("warning", status.GetProperty("result").GetString());
        Assert.Empty(Directory.GetFileSystemEntries(SnapshotDir));
    }

    [SkippableFact]
    public void With_sqlite3_every_sqlite_file_is_copied_whole_and_nothing_else()
    {
        ClientStubs();
        var db = Path.Combine(TestRoot, "opt/app/data.db");
        var made = DeploymentScripts.Run(DeploymentScripts.FindBash() ?? "bash",
            ["-c", "sqlite3 \"$0\" 'create table t(x); insert into t values (42);'", DeploymentScripts.ForBash(db)]);
        if (made.Exit != 0)
        {
            if (DeploymentScripts.InCi) Assert.Fail("sqlite3 is required on CI: " + made.Stderr);
            Skip.If(true, "no sqlite3 to make a database");
        }
        File.WriteAllText(Path.Combine(TestRoot, "srv/notes.txt"), new string('x', 4096));   // not SQLite
        File.WriteAllBytes(Path.Combine(TestRoot, "srv/tiny.db"), SqliteHeaderFile()[..100]); // below 512 bytes

        var (exit, output, status) = RunBackup("sqlite3", "timeout");

        Assert.True(exit == 0, output);
        Assert.Equal("ok", status.GetProperty("sqlite_snapshot").GetString());
        Assert.Equal("ok", status.GetProperty("result").GetString());
        var copy = Assert.Single(Directory.GetFiles(SnapshotDir));
        Assert.EndsWith("__opt__app__data.db", copy.Replace('\\', '/'));
        Assert.Equal("SQLite format 3\0", Encoding.ASCII.GetString(File.ReadAllBytes(copy)[..16]));
        Assert.Empty(Directory.GetFiles(SnapshotDir, ".partial"));
    }

    [SkippableFact]
    public void A_failed_copy_leaves_no_half_copy_and_the_status_reports_the_errors()
    {
        ClientStubs();
        File.WriteAllBytes(Path.Combine(TestRoot, "opt/app/data.db"), SqliteHeaderFile());
        // A sqlite3 that writes half a file and fails, as a copy cut off by its deadline would.
        _h.Stub("sqlite3", "#!/bin/bash\nprintf 'SQLite format 3' > .partial\nexit 1\n");

        var (exit, output, status) = RunBackup("timeout");

        Assert.True(exit == 0, output);
        Assert.Contains("WARN: sqlite snapshot had errors", output);
        Assert.Equal("errors", status.GetProperty("sqlite_snapshot").GetString());
        Assert.Equal("warning", status.GetProperty("result").GetString());   // review m5: errors make the run a warning
        Assert.Contains("borg create", Stub("borg.log"));
        Assert.Empty(Directory.GetFileSystemEntries(SnapshotDir));
    }

    [SkippableFact]
    public void A_copy_that_cannot_be_moved_into_place_leaves_no_partial_and_counts_as_an_error()
    {
        ClientStubs();
        File.WriteAllBytes(Path.Combine(TestRoot, "opt/app/data.db"), SqliteHeaderFile());
        // The copy itself succeeds; moving it into place fails (a full disk, a read-only mount).
        _h.Stub("sqlite3", "#!/bin/bash\nprintf 'SQLite format 3\\0padding' > .partial\nexit 0\n");
        _h.Stub("mv", "#!/bin/bash\ncase \"$*\" in *.partial*) echo 'mv: no space left' >&2; exit 1 ;; esac\nexec /usr/bin/mv \"$@\"\n");

        var (exit, output, status) = RunBackup("timeout");

        Assert.True(exit == 0, output);
        Assert.Contains("cannot move the copy into place", output);
        Assert.Equal("errors", status.GetProperty("sqlite_snapshot").GetString());
        Assert.Equal("warning", status.GetProperty("result").GetString());
        Assert.Contains("borg create", Stub("borg.log"));
        Assert.Empty(Directory.GetFileSystemEntries(SnapshotDir));   // no .partial, no half copy
    }

    /// <summary>Review 1.0.0 (Deploy): the Whiskers container ships the PostgreSQL client tools (its self-backup), so the
    /// former check "has pg_dumpall" took it for a PostgreSQL server, its dump failed and every night ended as a
    /// "warning". A server is recognised by its environment or the <c>postgres</c> server binary; it is still dumped as
    /// its own <c>POSTGRES_USER</c>.</summary>
    [SkippableFact]
    public void Only_a_postgres_server_is_dumped_not_a_container_with_the_client_tools()
    {
        ClientStubs();
        // Two running containers: "whiskers" (client tools, no server, no POSTGRES_* env) and "pg" (a server).
        _h.Stub("docker", """
            #!/bin/bash
            echo "docker $*" >> "$STUB_DIR/docker.log"
            case "$1 $2" in
              "ps --format") printf 'whiskers\npg\n' ;;
              "ps -q") ;;
              "inspect -f")
                case "$3" in
                  *Config.Env*) [ "$4" = pg ] && printf 'POSTGRES_USER=app\nPOSTGRES_PASSWORD=x\n' ;;
                  *) ;;
                esac ;;
              "exec whiskers")
                case "$*" in
                  *"command -v pg_dumpall"*) exit 0 ;;
                  *pg_dumpall*) echo 'pg_dumpall: error: connection to server failed' >&2; exit 2 ;;
                  *) exit 1 ;;
                esac ;;
              "exec pg")
                case "$*" in
                  *"command -v gitlab-ctl"*) exit 1 ;;
                  *pg_dumpall*) echo "-- dump as ${POSTGRES_USER:-app}" ;;
                  *) exit 1 ;;
                esac ;;
            esac
            exit 0
            """);
        // No SQLite file is present; the tools only have to exist so the snapshot is not reported as skipped.
        _h.Stub("sqlite3", "#!/bin/bash\nexit 0\n");
        _h.Stub("timeout", "#!/bin/bash\nshift; exec \"$@\"\n");

        var (exit, output, status) = RunBackup("gzip", "grep", "xargs", "sort");

        Assert.True(exit == 0, output);
        Assert.Equal("ok", status.GetProperty("result").GetString());   // no failed dump, no warning
        Assert.DoesNotContain("WARN: dump of whiskers failed", output);
        Assert.DoesNotContain("dump postgres whiskers", output);
        Assert.Contains("dump postgres pg", output);
        var dumps = Path.Combine(TestRoot, "var/backups/borg-fleet/db");
        Assert.True(File.Exists(Path.Combine(dumps, "pg.pg_dumpall.sql.gz")));
        Assert.False(File.Exists(Path.Combine(dumps, "whiskers.pg_dumpall.sql.gz")));
        Assert.Contains("exec pg sh -c exec pg_dumpall -U \"${POSTGRES_USER:-postgres}\"", Stub("docker.log"));
        Assert.Contains("WARN: whiskers has PostgreSQL client tools (pg_dumpall) but no PostgreSQL server; not dumped", output);
    }

    /// <summary>Review of T6: a PostgreSQL server without <c>POSTGRES_*</c> variables and without <c>postgres</c> on the
    /// PATH is still recognised, by Debian's versioned server binary or by a running <c>postgres</c> process. The client's
    /// real check runs here against a fake root per container (its <c>bin</c> on the PATH, its <c>usr/lib</c> and
    /// <c>proc</c>); a container with only the client tools is named in a WARN and not dumped.</summary>
    [SkippableFact]
    public void A_server_found_by_its_binary_or_its_process_is_dumped_and_client_tools_alone_are_named()
    {
        ClientStubs();
        var ctr = Path.Combine(_h.Root, "ctr");
        void Make(string container, string relative, string content)
        {
            var path = Path.Combine(ctr, container, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            DeploymentScripts.WriteExecutable(path, content);
        }
        Make("pgbin", "usr/lib/postgresql/18/bin/postgres", "#!/bin/sh\n");
        Make("pgbin", "proc/1/comm", "sleep\n");
        Make("pgproc", "proc/7/comm", "postgres\n");
        Make("tools", "bin/pg_dumpall", "#!/bin/sh\n");
        Make("tools", "proc/1/comm", "dotnet\n");
        Make("plain", "proc/1/comm", "nginx\n");
        _h.Stub("docker", """
            #!/bin/bash
            echo "docker $*" >> "$STUB_DIR/docker.log"
            case "$1" in
              ps) [ "$2" = --format ] && printf 'pgbin\npgproc\ntools\nplain\n'; exit 0 ;;
              inspect) exit 0 ;;   # no environment, no mounts
              exec)
                c=$2; script=$5
                case $script in
                  *"exec pg_dumpall -U"*) [ "$c" = tools ] && exit 2; echo "-- dump of $c"; exit 0 ;;
                esac
                root="$(cd "$STUB_DIR" && pwd)/ctr/$c"
                s=$(printf '%s' "$script" | sed "s#/usr/lib/postgresql#$root/usr/lib/postgresql#g; s#/proc/#$root/proc/#g")
                PATH="$root/bin:$PATH" exec /bin/sh -c "$s" ;;
            esac
            exit 0
            """);
        _h.Stub("sqlite3", "#!/bin/bash\nexit 0\n");
        _h.Stub("timeout", "#!/bin/bash\nshift; exec \"$@\"\n");

        var (exit, output, status) = RunBackup("gzip", "grep", "xargs", "sort");

        Assert.True(exit == 0, output);
        Assert.Equal("ok", status.GetProperty("result").GetString());
        Assert.Contains("dump postgres pgbin", output);
        Assert.Contains("dump postgres pgproc", output);
        Assert.DoesNotContain("dump postgres tools", output);
        Assert.DoesNotContain("dump postgres plain", output);   // neither dumped nor warned about
        Assert.DoesNotContain("WARN: plain", output);
        Assert.Contains("WARN: tools has PostgreSQL client tools (pg_dumpall) but no PostgreSQL server; not dumped", output);
        var dumps = Path.Combine(TestRoot, "var/backups/borg-fleet/db");
        Assert.Equal(["pgbin.pg_dumpall.sql.gz", "pgproc.pg_dumpall.sql.gz"],
            Directory.GetFiles(dumps, "*.gz").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Review 1.0.0 (Deploy): the restore in the README used the fixed role <c>postgres</c>, which an image
    /// started with another <c>POSTGRES_USER</c> does not have. It restores as the role the dump was taken with.</summary>
    [Fact]
    public void The_restore_uses_the_role_the_dump_was_taken_with()
    {
        var readme = DeploymentScripts.ReadRepoFile("deploy", "borg-fleet", "README.md");
        Assert.DoesNotContain("psql -U postgres", readme);
        Assert.Contains("""docker exec -i <c> sh -c 'exec psql -U "${POSTGRES_USER:-postgres}" -d postgres'""", readme);
        Assert.Contains("""exec pg_dumpall -U "${POSTGRES_USER:-postgres}" """.TrimEnd(),
            DeploymentScripts.ReadRepoFile("deploy", "borg-fleet", "borg-fleet-client"));
    }

    /// <summary>1 KiB that starts with the SQLite header (enough for the header check, not a database).</summary>
    private static byte[] SqliteHeaderFile()
    {
        var bytes = new byte[1024];
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(bytes, 0);
        return bytes;
    }

    // --- borg-fleet-prune --------------------------------------------------------------------------------------------

    [SkippableFact]
    public void Borg_fleet_prune_reads_the_record_with_jq_and_a_quote_in_the_id_stays_data()
    {
        Directory.CreateDirectory(Path.Combine(Cfg, "servers"));
        const string id = "x'y";
        File.WriteAllText(Path.Combine(Cfg, "servers", id + ".json"),
            """{"repo":"ssh://u1-sub9@u1.your-storagebox.de:23/./borg","remote_path":"borg-1.4"}""");
        _h.Stub("ssh", "#!/bin/bash\nexit 0\n");
        _h.Stub("borg", """
            #!/bin/bash
            echo "borg $* REPO=$BORG_REPO REMOTE=$BORG_REMOTE_PATH" >> "$STUB_DIR/borg.log"
            [ "$1" = list ] && printf 'a1\na2\n'
            exit 0
            """);
        _h.Stub("sftp", """
            #!/bin/bash
            echo "sftp $*" >> "$STUB_DIR/sftp.log"
            read -r cmd a b
            [ "$cmd" = get ] && printf '[repository]\nappend_only = 1\n' > "$b"
            exit 0
            """);
        var env = new Dictionary<string, string?> { ["BORG_FLEET_CFG_DIR"] = DeploymentScripts.ForBash(Cfg) };

        var (exit, stdout, stderr) = _h.Run("bash", ["deploy", "borg-fleet", "borg-fleet-prune"], [],
            ["jq", "sed", "grep", "comm", "sort", "mktemp", "wc", "rm", "cat", "basename"], env);

        Assert.True(exit == 0, stdout + stderr);
        Assert.Contains($"[{id}] pruned (rc=0), 2 archives kept", stdout);
        Assert.Contains("REPO=ssh://u1-sub9@u1.your-storagebox.de:23/./borg REMOTE=borg-1.4", Stub("borg.log"));
        Assert.Contains("u1-sub9@u1.your-storagebox.de", Stub("sftp.log"));

        _h.Unstub("borg");
        var missing = _h.Run("bash", ["deploy", "borg-fleet", "borg-fleet-prune"], [],
            ["jq", "sed", "grep", "comm", "sort", "mktemp", "wc", "rm", "cat", "basename"], env);
        Assert.Equal(1, missing.Exit);
        Assert.Contains("missing tool: borg", missing.Stderr);
    }
}
