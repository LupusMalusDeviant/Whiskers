using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0017: a script an Admin vouched for on one server, pinned to its SHA-256, runs when it is called exactly by
/// its path with literal arguments — read on the server before the decision and again right before the run. Anything
/// else stays as INV-24 has it, and the listed paths are protected from changes.
/// </summary>
public sealed class TrustedScriptTests
{
    private const string Dump = "/usr/local/sbin/burgcloud-db-dump.sh";
    private const string Approved = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    /// <summary>A host that answers the fixed hash command with the hash it currently holds for a path.</summary>
    private sealed class HashHost : IHostCommandExecutor
    {
        public Queue<string?> Hashes { get; } = new();
        public string? Steady { get; set; } = Approved;
        public bool Throws { get; set; }
        public List<string> Commands { get; } = [];

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            Commands.Add($"{serverId}: {command}");
            if (Throws) throw new InvalidOperationException("host unreachable");
            var hash = Hashes.Count > 0 ? Hashes.Dequeue() : Steady;
            return Task.FromResult(command == TrustedScripts.HashCommand(Dump) && hash is not null
                ? new CommandResult { ExitCode = 0, Output = $"{hash}  {Dump}\n" }
                : new CommandResult { ExitCode = 1, Error = "No such file or directory" });
        }
    }

    private static ProtectionHarness Trusting(HashHost host, string serverId = "local", string sha = Approved,
        params string[] protectedResources) =>
        new(p =>
        {
            p.ProtectedResources = protectedResources.Length == 0 ? ["whiskers-db"] : [.. protectedResources];
            p.TrustedScripts = [new TrustedScript { ServerId = serverId, Path = Dump, Sha256 = sha }];
        }, host: host);

    private static Task<MutationOutcome<int>> AsKey(ProtectionHarness h, string command) =>
        h.RunAsync("host.exec", new { serverId = "local", command }, ProtectionHarness.AdminKey, MutationSurface.Mcp);

    private static Task<MutationOutcome<int>> AsTask(ProtectionHarness h, string command) =>
        h.RunAsync("host.exec", new { serverId = "local", command }, ProtectionHarness.AdminsTask(), MutationSurface.Automation);

    // ---------------------------------------------------------------- the call shape

    [Theory]
    [InlineData(Dump, Dump)]
    [InlineData("/usr/local/bin/check-disk-usage.sh 80", "/usr/local/bin/check-disk-usage.sh")]
    [InlineData("  /opt/x/run.sh --mode=full a,b user@host 10%  ", "/opt/x/run.sh")]
    public void A_path_with_literal_arguments_is_a_call(string command, string path) =>
        Assert.Equal(path, TrustedScripts.CallPath(command));

    [Theory]
    [InlineData("burgcloud-db-dump.sh")]                 // relative
    [InlineData("./run.sh")]
    [InlineData("/opt/../etc/run.sh")]                   // a .. segment
    [InlineData("/opt//run.sh")]
    [InlineData("sudo /opt/run.sh")]                     // a prefix
    [InlineData("/opt/run.sh $(date +%s)")]
    [InlineData("/opt/run.sh $VAR")]
    [InlineData("/opt/run.sh `id`")]
    [InlineData("/opt/run.sh 'quoted'")]
    [InlineData("/opt/run.sh; rm -rf /")]
    [InlineData("/opt/run.sh && id")]
    [InlineData("/opt/run.sh | tee x")]
    [InlineData("/opt/run.sh > /tmp/x")]
    [InlineData("/opt/run.sh *")]
    [InlineData("/opt/run.sh {a,b}")]
    [InlineData("/opt/run.sh ~/x")]
    [InlineData("/opt/run.sh # comment")]
    [InlineData("/opt/run.sh\nid")]
    [InlineData("/usr/bin/python3 /tmp/x.py")]           // an interpreter is never a trusted script
    [InlineData("/bin/bash /tmp/x")]
    [InlineData("/usr/bin/docker system prune")]         // nor a container runtime
    public void Anything_else_is_no_call(string command) => Assert.Null(TrustedScripts.CallPath(command));

    [Theory]
    [InlineData("local", Dump, Approved, true)]
    [InlineData("", Dump, Approved, false)]                                // no server
    [InlineData("local", "/usr/bin/python3", Approved, false)]             // an interpreter
    [InlineData("local", "/usr/bin/env", Approved, false)]                 // a command prefix
    [InlineData("local", "/opt/dockerproxy/rotate.sh", Approved, false)]   // under the control plane
    [InlineData("local", "/srv/mtls/renew.sh", Approved, false)]
    [InlineData("local", Dump, "ABC", false)]                              // not a SHA-256
    [InlineData("local", Dump, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)] // not lowercase
    public void An_entry_is_valid_only_when_complete_and_trustable(string server, string path, string sha, bool valid) =>
        Assert.Equal(valid, TrustedScripts.IsValid(new TrustedScript { ServerId = server, Path = path, Sha256 = sha }));

    [Fact]
    public void An_invalid_or_duplicate_entry_makes_the_configuration_invalid()
    {
        var config = GuardrailConfig.SafeDefault();
        config.Presets[0].Policy.TrustedScripts = [new TrustedScript { ServerId = "local", Path = "/usr/bin/python3", Sha256 = Approved }];
        Assert.Equal(GuardrailConfigProblemKind.InvalidTrustedScript, config.Problem()?.Kind);

        var entry = new TrustedScript { ServerId = "local", Path = Dump, Sha256 = Approved };
        config.Presets[0].Policy.TrustedScripts = [entry, entry.Clone()];
        Assert.Equal(GuardrailConfigProblemKind.InvalidTrustedScript, config.Problem()?.Kind);

        config.Presets[0].Policy.TrustedScripts = [entry];
        Assert.Null(config.Problem());
    }

    [Fact]
    public void An_empty_list_leaves_the_stored_policy_unchanged()
    {
        var plain = JsonSerializer.Serialize(GuardrailPolicy.SafeDefault());
        var empty = GuardrailPolicy.SafeDefault();
        empty.TrustedScripts = [];

        Assert.Equal(plain, JsonSerializer.Serialize(empty));
        Assert.DoesNotContain("TrustedScripts", plain);
        Assert.Null(empty.Clone().TrustedScripts);
    }

    // ---------------------------------------------------------------- running it

    [Theory]
    [InlineData(Dump)]
    [InlineData(Dump + " 80")]
    public async Task A_trusted_call_with_the_approved_hash_runs_for_a_key_and_a_scheduled_task(string command)
    {
        var host = new HashHost();
        var h = Trusting(host);

        Assert.Equal(MutationStatus.Executed, (await AsKey(h, command)).Status);
        Assert.Equal(MutationStatus.Executed, (await AsTask(h, command)).Status);
        Assert.Equal(2, h.Runs);
        // Read at the decision and again right before each run.
        Assert.Equal(4, host.Commands.Count);
        Assert.All(host.Commands, c => Assert.Equal($"local: {TrustedScripts.HashCommand(Dump)}", c));
    }

    [Fact]
    public async Task Without_an_entry_the_script_stays_refused_as_hidden_code()
    {
        var host = new HashHost();
        var h = new ProtectionHarness(p => p.ProtectedResources = ["whiskers-db"], host: host);

        var outcome = await AsTask(h, Dump);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("runs a script, an interpreter or a recipe", outcome.Reason);
        Assert.Empty(host.Commands);
        Assert.Equal(0, h.Runs);
    }

    [Fact]
    public async Task A_changed_script_is_refused_before_policy()
    {
        var h = Trusting(new HashHost { Steady = Other });

        var outcome = await AsTask(h, Dump);

        Assert.NotEqual(MutationStatus.Executed, outcome.Status);
        Assert.Contains("changed since it was approved", outcome.Reason);
        Assert.Equal(0, h.Runs);
    }

    [Fact]
    public async Task A_hash_that_cannot_be_read_refuses_the_call()
    {
        var gone = Trusting(new HashHost { Steady = null });
        var down = Trusting(new HashHost { Throws = true });

        Assert.NotEqual(MutationStatus.Executed, (await AsTask(gone, Dump)).Status);
        Assert.NotEqual(MutationStatus.Executed, (await AsTask(down, Dump)).Status);
        Assert.Equal(0, gone.Runs + down.Runs);
    }

    [Fact]
    public async Task A_script_changed_between_decision_and_run_does_not_run()
    {
        var host = new HashHost();
        host.Hashes.Enqueue(Approved);   // the decision
        host.Hashes.Enqueue(Other);      // right before the run
        var h = Trusting(host);

        var outcome = await AsTask(h, Dump);

        Assert.NotEqual(MutationStatus.Executed, outcome.Status);
        Assert.Contains("changed after the decision", outcome.Reason);
        Assert.Equal(2, host.Commands.Count);
        Assert.Equal(0, h.Runs);
    }

    [Fact]
    public async Task An_entry_for_another_server_does_not_trust_the_script_here()
    {
        var host = new HashHost();
        var h = Trusting(host, serverId: "burgcloud");

        var outcome = await AsTask(h, Dump);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("runs a script", outcome.Reason);
        Assert.Empty(host.Commands);
    }

    [Theory]
    [InlineData(Dump + " $(date +%s)")]
    [InlineData(Dump + " $VAR")]
    [InlineData(Dump + " `id`")]
    [InlineData(Dump + "; id")]
    [InlineData(Dump + " > /tmp/out")]
    [InlineData("sudo " + Dump)]
    [InlineData(Dump + " whiskers-db")]          // a protected name among its arguments
    [InlineData(Dump + " /var/run/docker.sock")] // a control-plane path among its arguments
    public async Task Anything_beyond_a_plain_call_stays_refused(string command)
    {
        var h = Trusting(new HashHost());

        Assert.Equal(MutationStatus.Denied, (await AsKey(h, command)).Status);
        Assert.Equal(MutationStatus.Denied, (await AsTask(h, command)).Status);
        Assert.Equal(0, h.Runs);
    }

    // ---------------------------------------------------------------- protecting the listed path

    [Theory]
    [InlineData("echo x > " + Dump)]
    [InlineData("rm " + Dump)]
    [InlineData("cp /tmp/x " + Dump)]
    [InlineData("tee " + Dump)]
    [InlineData("rm -rf /usr/local/sbin")]
    [InlineData("rm /usr/local/sbin/*")]
    [InlineData("cd /usr/local/sbin && rm burgcloud-db-dump.sh")]
    public async Task Changing_a_listed_script_is_refused_and_an_Admin_must_approve_it(string command)
    {
        // Protected even while the policy protects nothing of its own.
        var h = new ProtectionHarness(
            p => p.TrustedScripts = [new TrustedScript { ServerId = "burgcloud", Path = Dump, Sha256 = Approved }],
            host: new HashHost());
        var args = new { serverId = "local", command };

        Assert.Equal(MutationStatus.Denied, (await AsKey(h, command)).Status);
        Assert.Equal(MutationStatus.PendingApproval,
            (await h.RunAsync("host.exec", args, ProtectionHarness.UiAdmin, MutationSurface.Ui)).Status);
        Assert.Equal(0, h.Runs);
    }

    [Fact]
    public void A_caller_cannot_claim_a_trusted_call_the_policy_does_not_list()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.TrustedScripts = [new TrustedScript { ServerId = "local", Path = Dump, Sha256 = Approved }];
        Dictionary<string, JsonElement> Args(string verified, string resolvedServer = "local") =>
            ProtectionHarness.Args(new Dictionary<string, string>
            {
                ["command"] = Dump, [TrustedScripts.ResolvedKey] = verified, ["__resolvedTargetId"] = resolvedServer,
            });

        Assert.Equal(Dump, TrustedScripts.TrustedCallOf(Args($"local\u001f{Dump}\u001f{Approved}"), policy));
        Assert.Null(TrustedScripts.TrustedCallOf(Args($"local\u001f{Dump}\u001f{Other}"), policy));      // other hash
        Assert.Null(TrustedScripts.TrustedCallOf(Args($"web\u001f{Dump}\u001f{Approved}"), policy));      // other server
        Assert.Null(TrustedScripts.TrustedCallOf(Args($"local\u001f/opt/x.sh\u001f{Approved}"), policy)); // not listed
        // A3: verified for one server, but the request resolved to another (or to none).
        Assert.Null(TrustedScripts.TrustedCallOf(Args($"local\u001f{Dump}\u001f{Approved}", resolvedServer: "web"), policy));
        Assert.Null(TrustedScripts.TrustedCallOf(ProtectionHarness.Args(new Dictionary<string, string>
        {
            ["command"] = Dump, [TrustedScripts.ResolvedKey] = $"local\u001f{Dump}\u001f{Approved}",
        }), policy));
    }

    // ---------------------------------------------------------------- amendment 1

    [Theory]
    [InlineData("/usr/bin/awk")]
    [InlineData("/bin/tar")]
    [InlineData("/usr/bin/rsync")]
    [InlineData("/usr/bin/java")]
    [InlineData("/usr/bin/dotnet")]
    [InlineData("/bin/sed")]
    [InlineData("/usr/bin/nsenter")]
    [InlineData("/bin/tcsh")]
    public void A_program_that_runs_code_from_its_arguments_cannot_be_listed(string path) =>
        Assert.False(TrustedScripts.IsValid(new TrustedScript { ServerId = "local", Path = path, Sha256 = Approved }));

    [Fact]
    public async Task A_trusted_call_under_a_pinned_preset_is_verified_against_that_preset()
    {
        var host = new HashHost();
        // The active preset lists nothing; the pinned one lists the script.
        var h = new ProtectionHarness(p => p.ProtectedResources = ["whiskers-db"], host: host);
        var pinned = GuardrailPolicy.SafeDefault();
        pinned.ProtectedResources = ["whiskers-db"];
        pinned.ForbiddenArgPatterns = [];
        pinned.MaxAutonomousLevel = Whiskers.Models.McpPermissionLevels.Admin;
        pinned.RequireConfirmationForWrites = false;
        pinned.TrustedScripts = [new TrustedScript { ServerId = "local", Path = Dump, Sha256 = Approved }];

        var listed = await RunPinnedAsync(h, pinned, Dump);
        pinned.TrustedScripts = null;
        var unlisted = await RunPinnedAsync(h, pinned, Dump);

        Assert.Equal(MutationStatus.Executed, listed.Status);
        Assert.Equal(MutationStatus.Denied, unlisted.Status);
        Assert.Equal(2, host.Commands.Count);   // read at the decision and before the run, for the listed one only
    }

    private static Task<MutationOutcome<int>> RunPinnedAsync(ProtectionHarness h, GuardrailPolicy pinned, string command)
    {
        var capability = new CapabilityCatalog().Find("host.exec")!;
        var args = ProtectionHarness.Args(new { serverId = "local", command });
        var request = new MutationRequest("host.exec", ProtectionHarness.AdminsTask(), MutationSurface.Automation,
            MutationTargets.FromArguments(capability, args), args)
        {
            PinnedPolicy = new Whiskers.Models.Agent.PinnedPolicy("Pinned", pinned.Clone()),
        };
        return h.Gateway.ExecuteAsync(request, (_, _) => Task.FromResult(1));
    }

    /// <summary>A1: the fixed read on a real shell prints a hash only for a regular script file that starts with
    /// <c>#!</c> and is not reached through a symlink.</summary>
    [SkippableFact]
    public void On_a_real_shell_only_a_real_script_yields_a_hash()
    {
        Skip.If(OperatingSystem.IsWindows(), "needs a POSIX shell");
        var dir = Path.Combine("/tmp", "ts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var script = Path.Combine(dir, "ok.sh");
            File.WriteAllText(script, "#!/bin/sh\necho ok\n");
            var plain = Path.Combine(dir, "plain.sh");
            File.WriteAllText(plain, "echo no shebang\n");
            var elf = Path.Combine(dir, "prog");
            File.WriteAllBytes(elf, [0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0]);
            var link = Path.Combine(dir, "link.sh");
            File.CreateSymbolicLink(link, script);
            var linkedDir = Path.Combine(dir, "via");
            Directory.CreateSymbolicLink(linkedDir, dir);
            // Amendment 3 (C2): owned by root, one link, no write bit for group or others.
            var hard = Path.Combine(dir, "hard.sh");
            var hardTarget = Path.Combine(dir, "hard-target.sh");
            File.WriteAllText(hardTarget, "#!/bin/sh\necho ok\n");
            var writable = Path.Combine(dir, "writable.sh");
            File.WriteAllText(writable, "#!/bin/sh\necho ok\n");
            var foreign = Path.Combine(dir, "foreign.sh");
            File.WriteAllText(foreign, "#!/bin/sh\necho ok\n");
            Skip.IfNot(AsRoot("chown", "0:0", script, hardTarget, writable) && AsRoot("chmod", "644", script, hardTarget)
                       && AsRoot("chmod", "664", writable) && AsRoot("chown", "65534", foreign)
                       && AsRoot("ln", hardTarget, hard), "needs root or sudo -n");

            Assert.NotNull(HashOnShell(script));
            Assert.Null(HashOnShell(plain));
            Assert.Null(HashOnShell(elf));
            Assert.Null(HashOnShell(link));
            Assert.Null(HashOnShell(Path.Combine(linkedDir, "ok.sh")));   // a symlink further up the path
            Assert.Null(HashOnShell(Path.Combine(dir, "missing.sh")));
            Assert.Null(HashOnShell(hard));          // two links
            Assert.Null(HashOnShell(hardTarget));
            Assert.Null(HashOnShell(writable));      // group-writable
            Assert.Null(HashOnShell(foreign));       // not owned by root
        }
        finally { AsRoot("rm", "-rf", dir); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // Runs a command as root: directly when the tests run as root (a container), else through a password-less sudo.
    private static bool AsRoot(params string[] command)
    {
        var asRoot = Environment.UserName == "root";
        var psi = new System.Diagnostics.ProcessStartInfo(asRoot ? command[0] : "sudo") { RedirectStandardError = true };
        if (!asRoot)
        {
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add(command[0]);
        }
        foreach (var arg in command.Skip(1)) psi.ArgumentList.Add(arg);
        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static string? HashOnShell(string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(TrustedScripts.HashCommand(path));
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return TrustedScripts.ParseHash(new CommandResult { ExitCode = p.ExitCode, Output = output });
    }

    // A4/A5 on the real store.

    private sealed class RecordingAudit(bool works = true) : Whiskers.Services.AuditLog.IAuditLogService
    {
        public List<(string Action, string? Details)> Entries { get; } = [];

        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true)
        {
            Entries.Add((action, details));
            return Task.CompletedTask;
        }

        public async Task<bool> TryLogAsync(string actor, string actorType, string action, string targetType,
            string targetId, string targetName, string? details = null, string? serverId = null, bool success = true)
        {
            if (!works) return false;
            await LogAsync(actor, actorType, action, targetType, targetId, targetName, details, serverId, success);
            return true;
        }

        public Task<List<AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0, string? actionFilter = null,
            string? targetTypeFilter = null) => Task.FromResult(new List<AuditLogEntity>());

        public Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(0);
    }

    private static readonly Whiskers.Models.Agent.AgentPrincipal StoreAdmin = new(
        Whiskers.Models.Agent.AgentPrincipalKind.WebUser, "admin@x", Whiskers.Models.McpPermissionLevels.Admin, null,
        UserEmail: "admin@x");

    private static async Task<(GuardrailStore Store, string Dir)> StoreAsync(RecordingAudit? audit, bool secondApprover)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ts-store-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var store = GuardrailStoreTestExtensions.FirstStartStore(Path.Combine(dir, "guardrails.json"),
            audit is null ? null : new TestServices(audit));
        await store.StartAsync();
        var config = store.Config;
        config.Presets[0].Policy.RequireSecondApprover = secondApprover;
        await store.SaveConfigAsync(config, StoreAdmin, expectedTrustedAdditions: "");
        return (store, dir);
    }

    private static GuardrailConfig Adding(GuardrailStore store, string sha = Approved)
    {
        var config = store.Config;
        config.Presets[0].Policy.TrustedScripts = [new TrustedScript { ServerId = "local", Path = Dump, Sha256 = sha }];
        return config;
    }

    [Fact]
    public async Task The_store_writes_the_audit_row_before_it_saves_a_change_of_the_list()
    {
        var audit = new RecordingAudit();
        var (store, dir) = await StoreAsync(audit, secondApprover: false);
        try
        {
            var next = Adding(store);
            await store.SaveConfigAsync(next, StoreAdmin, TrustedScripts.Additions(store.Config, next));
            Assert.NotNull(store.Current.TrustedScripts);

            // A save that drops the entry without naming additions (as the preset activation does) is audited too.
            var dropped = store.Config;
            dropped.Presets[0].Policy.TrustedScripts = null;
            await store.SaveConfigAsync(dropped, StoreAdmin);

            Assert.Null(store.Current.TrustedScripts);
            Assert.Equal(2, audit.Entries.Count(e => e.Action == GuardrailStore.TrustedScriptsAuditAction));
            Assert.Contains(audit.Entries, e => e.Details == $"added Standard | local | {Dump} | {Approved}");
            Assert.Contains(audit.Entries, e => e.Details == $"removed Standard | local | {Dump} | {Approved}");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Without_an_audit_row_the_list_is_not_changed()
    {
        foreach (var audit in new[] { null, new RecordingAudit(works: false) })
        {
            var (store, dir) = await StoreAsync(audit, secondApprover: false);
            try
            {
                var next = Adding(store);
                var ex = await Assert.ThrowsAsync<GuardrailConfigException>(() =>
                    store.SaveConfigAsync(next, StoreAdmin, TrustedScripts.Additions(store.Config, next)));
                Assert.Equal(GuardrailConfigProblemKind.TrustedScriptsAuditFailed, ex.Problem.Kind);
                Assert.Null(store.Current.TrustedScripts);
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    [Fact]
    public async Task The_store_refuses_additions_other_than_the_approved_ones_or_none_named_under_a_second_approver()
    {
        var audit = new RecordingAudit();
        var (store, dir) = await StoreAsync(audit, secondApprover: true);
        try
        {
            var approved = TrustedScripts.Additions(store.Config, Adding(store));
            var changed = await Assert.ThrowsAsync<GuardrailConfigException>(() =>
                store.SaveConfigAsync(Adding(store, Other), StoreAdmin, approved));
            var unnamed = await Assert.ThrowsAsync<GuardrailConfigException>(() =>
                store.SaveConfigAsync(Adding(store), StoreAdmin));

            Assert.Equal(GuardrailConfigProblemKind.TrustedScriptsChangedMeanwhile, changed.Problem.Kind);
            Assert.Equal(GuardrailConfigProblemKind.TrustedScriptsNeedSecondApprover, unnamed.Problem.Kind);
            Assert.Null(store.Current.TrustedScripts);
            Assert.DoesNotContain(audit.Entries, e => e.Action == GuardrailStore.TrustedScriptsAuditAction);

            using (ApprovedSave(approved))
                await store.SaveConfigAsync(Adding(store), StoreAdmin, approved);
            Assert.NotNull(store.Current.TrustedScripts);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The scope the gateway opens for a <c>guardrails.save</c> that used a second Admin's approval.</summary>
    private static MutationScope ApprovedSave(string? added = null, string capability = TrustedScripts.SaveCapability,
        string rule = TrustedScriptApprovalRule.RuleId)
    {
        // Amendment 3 (C4): the scope carries the call's arguments, the approved additions among them.
        var args = new Dictionary<string, string> { ["section"] = "guardrails" };
        if (added is not null) args[TrustedScripts.AdditionsArgument] = added;
        var request = new MutationRequest(capability, ProtectionHarness.UiAdmin, MutationSurface.Ui,
            new MutationTarget("config", "guardrails"), ProtectionHarness.Args(args));
        var scope = MutationScope.Begin(capability, "op-" + Guid.NewGuid().ToString("N")[..8], request: request);
        scope.ApprovedRules = new HashSet<string>(StringComparer.Ordinal) { rule };
        return scope;
    }

    // ---------------------------------------------------------------- amendment 2

    [Theory]
    [InlineData("cd /usr/local/sbin && cp /tmp/e burgcloud-db-dump.sh")]
    [InlineData("cd /usr/local/sbin && echo x > burgcloud-db-dump.sh")]
    [InlineData("cd /usr/local/sbin && tee burgcloud-db-dump.sh")]
    [InlineData("cd /usr/local/sbin && sed -i s/a/b/ burgcloud-db-dump.sh")]
    [InlineData("cp /tmp/e/burgcloud-db-dump.sh /tmp/other/")]
    [InlineData("cp /tmp/e /usr/local/sbin/")]
    [InlineData("tar -xf /tmp/a.tar -C /usr/local/sbin")]
    [InlineData("unzip -o /tmp/a.zip -d /usr/local/sbin")]
    [InlineData("cd /usr/local/sbin && tar -xf /tmp/a.tar")]
    [InlineData("cd /usr/local && cp /tmp/e sbin")]
    [InlineData("cd /usr && cp -r /tmp/tree local/sbin")]
    [InlineData("tar -xf /tmp/a.tar -C .")]
    [InlineData("cp -r /tmp/tree/ /")]
    [InlineData("install --target-directory=/usr/local/sbin /tmp/e")]
    // Third review: a redirection writes whatever the command is, and an attached option value names a target.
    [InlineData("cat /tmp/payload > ../usr/local/sbin/burgcloud-db-dump.sh")]
    [InlineData("cat /tmp/payload >> sbin/burgcloud-db-dump.sh")]
    [InlineData("grep x /tmp/a > burgcloud-db-dump.sh")]
    [InlineData("tar -xf /tmp/payload.tar -C/usr/local/sbin")]
    [InlineData("unzip -o /tmp/a.zip -d/usr/local/sbin")]
    [InlineData("tar -xf /tmp/payload.tar -C../usr/local/sbin")]
    public async Task B1_every_spelling_of_a_listed_script_or_its_directory_is_a_finding(string command)
    {
        var h = new ProtectionHarness(
            p => p.TrustedScripts = [new TrustedScript { ServerId = "burgcloud", Path = Dump, Sha256 = Approved }],
            host: new HashHost());

        Assert.Equal(MutationStatus.Denied, (await AsKey(h, command)).Status);
        Assert.Equal(MutationStatus.PendingApproval,
            (await h.RunAsync("host.exec", new { serverId = "local", command }, ProtectionHarness.UiAdmin, MutationSurface.Ui)).Status);
        Assert.Equal(0, h.Runs);
    }

    [Theory]
    [InlineData("ls -l /usr/local/sbin")]
    [InlineData("df -h /")]
    [InlineData("du -sh /usr/local")]
    [InlineData("ls . && grep -c dump sbin/burgcloud-db-dump.sh")]
    [InlineData("cp /tmp/a /tmp/b")]
    [InlineData("systemctl restart nginx")]
    public async Task B1_reading_the_directory_and_unrelated_changes_stay_possible(string command)
    {
        var h = new ProtectionHarness(
            p => p.TrustedScripts = [new TrustedScript { ServerId = "burgcloud", Path = Dump, Sha256 = Approved }],
            host: new HashHost());

        Assert.Equal(MutationStatus.Executed, (await AsKey(h, command)).Status);
    }

    [Fact]
    public async Task A_trusted_call_is_no_finding_because_its_own_path_matches_a_policy_pattern()
    {
        var host = new HashHost();
        var h = Trusting(host, protectedResources: "*burgcloud-db*");

        Assert.Equal(MutationStatus.Executed, (await AsKey(h, Dump)).Status);
        // An argument word that matches stays a finding.
        Assert.Equal(MutationStatus.Denied, (await AsKey(h, Dump + " burgcloud-db-main")).Status);
        // Without the entry, the whole command is matched as before.
        var plain = new ProtectionHarness(p => p.ProtectedResources = ["*burgcloud-db*"], host: new HashHost());
        Assert.Equal(MutationStatus.Denied, (await AsKey(plain, Dump)).Status);
    }

    [Fact]
    public async Task B1_a_trusted_call_still_runs_next_to_the_wider_protection()
    {
        var host = new HashHost();
        var h = Trusting(host);

        Assert.Equal(MutationStatus.Executed, (await AsKey(h, Dump + " --keep 7")).Status);
    }

    [Theory]
    [InlineData("echo x > /proc/self/root/usr/local/sbin/burgcloud-db-dump.sh")]
    [InlineData("cat /proc/1/root/var/run/docker.sock")]
    [InlineData("ls /proc/self/cwd/")]
    [InlineData("cat /proc/123/fd/3")]
    public void B1_a_proc_alias_makes_the_command_unanalysable(string command) =>
        Assert.True(ProtectedResourceMatcher.AnalyseShell(command).Unanalyzable);

    [Theory]
    [InlineData("cat /proc/meminfo")]
    [InlineData("cat /proc/self/status")]
    public void B1_other_proc_files_stay_readable(string command) =>
        Assert.False(ProtectedResourceMatcher.AnalyseShell(command).Unanalyzable);

    [Fact]
    public async Task B2_the_store_saves_additions_under_a_second_approver_only_within_an_approved_save()
    {
        var audit = new RecordingAudit();
        var (store, dir) = await StoreAsync(audit, secondApprover: true);
        try
        {
            var approved = TrustedScripts.Additions(store.Config, Adding(store));
            // The setting was switched on after a decision that needed no approval: no scope, another capability's
            // scope, or a scope whose approval covered another rule.
            var outside = await Assert.ThrowsAsync<GuardrailConfigException>(() =>
                store.SaveConfigAsync(Adding(store), StoreAdmin, approved));
            GuardrailConfigException other, otherRule;
            using (ApprovedSave(approved, capability: "host.exec"))
                other = await Assert.ThrowsAsync<GuardrailConfigException>(() => store.SaveConfigAsync(Adding(store), StoreAdmin, approved));
            using (ApprovedSave(approved, rule: "protected-resource"))
                otherRule = await Assert.ThrowsAsync<GuardrailConfigException>(() => store.SaveConfigAsync(Adding(store), StoreAdmin, approved));

            foreach (var refused in new[] { outside, other, otherRule })
                Assert.Equal(GuardrailConfigProblemKind.TrustedScriptsNeedSecondApprover, refused.Problem.Kind);
            Assert.Null(store.Current.TrustedScripts);

            using (ApprovedSave(approved))
                await store.SaveConfigAsync(Adding(store), StoreAdmin, approved);
            Assert.NotNull(store.Current.TrustedScripts);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task B2_the_gateway_records_the_rules_a_used_approval_covered()
    {
        var h = new ProtectionHarness(p => p.RequireSecondApprover = true);
        await SaveAsync(h, Line(Approved));
        await h.Approvals.DecideAsync(Assert.Single(h.Approvals.GetPending()).Id, true,
            ApprovalResolver.User("second@example.com", AppRole.Admin));
        IReadOnlySet<string>? seen = null;

        var outcome = await h.RunAsync("guardrails.save",
            new Dictionary<string, string> { ["section"] = "guardrails", [TrustedScripts.AdditionsArgument] = Line(Approved) },
            ProtectionHarness.UiAdmin, MutationSurface.Ui, during: () => seen = MutationScope.Current?.ApprovedRules);

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.NotNull(seen);
        Assert.Contains(TrustedScriptApprovalRule.RuleId, seen!);

        // A save without an approval runs with none recorded.
        var plain = new ProtectionHarness();
        IReadOnlySet<string>? none = null;
        await plain.RunAsync("guardrails.save", new Dictionary<string, string> { ["section"] = "guardrails" },
            ProtectionHarness.UiAdmin, MutationSurface.Ui, during: () => none = MutationScope.Current?.ApprovedRules);
        Assert.NotNull(none);
        Assert.Empty(none!);
    }

    [Fact]
    public async Task B3_a_trusted_script_the_caller_set_on_the_target_is_dropped()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.TrustedScripts = [new TrustedScript { ServerId = "local", Path = Dump, Sha256 = Approved }];
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var resolver = new MutationTargetResolver(new TestServices(new FakeDocker([]) { DefaultServerId = "local" },
            new FakeServerConfig(ProtectionHarness.Local), store, new HashHost()));
        var request = new MutationRequest("host.exec", ProtectionHarness.AdminKey, MutationSurface.Mcp,
            new MutationTarget("host", "local", ServerId: "local") { TrustedScript = policy.TrustedScripts[0] },
            ProtectionHarness.Args(new { serverId = "local", command = "uptime" }));

        var resolved = await resolver.ResolveAsync(request, new CapabilityCatalog().Find("host.exec")!, default);

        Assert.NotNull(resolved.Target);
        Assert.Null(resolved.Target!.TrustedScript);
    }

    // ---------------------------------------------------------------- D-1: a second Admin for additions

    private static string Line(string sha, string server = "local") => $"Standard | {server} | {Dump} | {sha}";

    private static Task<MutationOutcome<int>> SaveAsync(ProtectionHarness h, string? added) =>
        h.RunAsync("guardrails.save", added is null
                ? new Dictionary<string, string> { ["section"] = "guardrails" }
                : new Dictionary<string, string> { ["section"] = "guardrails", [TrustedScripts.AdditionsArgument] = added },
            ProtectionHarness.UiAdmin, MutationSurface.Ui);

    [Fact]
    public void Additions_list_new_and_rehashed_entries_but_not_removals()
    {
        GuardrailConfig With(params TrustedScript[] scripts)
        {
            var config = GuardrailConfig.SafeDefault();
            config.Presets[0].Policy.TrustedScripts = [.. scripts];
            return config;
        }
        var a = new TrustedScript { ServerId = "local", Path = Dump, Sha256 = Approved };
        var b = new TrustedScript { ServerId = "web", Path = Dump, Sha256 = Approved };

        Assert.Equal(Line(Approved), TrustedScripts.Additions(With(), With(a)));
        Assert.Equal(Line(Other), TrustedScripts.Additions(With(a), With(new TrustedScript { ServerId = "local", Path = Dump, Sha256 = Other })));
        Assert.Equal("", TrustedScripts.Additions(With(a, b), With(a)));     // a removal
        Assert.Equal("", TrustedScripts.Additions(With(a), With(a)));
    }

    [Fact]
    public async Task With_a_second_approver_required_an_addition_waits_for_another_Admin_and_then_saves()
    {
        var h = new ProtectionHarness(p => p.RequireSecondApprover = true);

        var first = await SaveAsync(h, Line(Approved));
        Assert.Equal(MutationStatus.PendingApproval, first.Status);
        Assert.Contains(TrustedScriptApprovalRule.RuleId, first.MatchedRules);
        var approval = Assert.Single(h.Approvals.GetPending());
        Assert.Equal(ApprovalDecisionResult.RequesterCannotDecide, await h.Approvals.DecideAsync(approval.Id, true,
            ApprovalResolver.User("admin@example.com", AppRole.Admin)));
        Assert.Equal(0, h.Runs);

        Assert.Equal(ApprovalDecisionResult.Decided, await h.Approvals.DecideAsync(approval.Id, true,
            ApprovalResolver.User("second@example.com", AppRole.Admin)));
        Assert.Equal(MutationStatus.Executed, (await SaveAsync(h, Line(Approved))).Status);
        Assert.Equal(1, h.Runs);
    }

    [Fact]
    public async Task An_approval_covers_exactly_the_additions_it_was_raised_for()
    {
        var h = new ProtectionHarness(p => p.RequireSecondApprover = true);
        await SaveAsync(h, Line(Approved));
        await h.Approvals.DecideAsync(Assert.Single(h.Approvals.GetPending()).Id, true,
            ApprovalResolver.User("second@example.com", AppRole.Admin));

        var other = await SaveAsync(h, Line(Other));

        Assert.Equal(MutationStatus.PendingApproval, other.Status);
        Assert.Equal(0, h.Runs);
    }

    [Theory]
    [InlineData(false, "add")]   // the setting is off: a single Admin saves as before
    [InlineData(true, null)]     // nothing added (a removal, or no change of the list)
    public async Task Without_an_addition_or_without_the_setting_a_save_runs_at_once(bool secondApprover, string? add)
    {
        // The listed path in the additions is no finding of its own (D-1, point 5).
        var h = new ProtectionHarness(p =>
        {
            p.RequireSecondApprover = secondApprover;
            p.TrustedScripts = [new TrustedScript { ServerId = "local", Path = Dump, Sha256 = Approved }];
        });

        var outcome = await SaveAsync(h, add is null ? null : Line(Other));

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(1, h.Runs);
    }

    [Fact]
    public void A_callers_own_resolved_key_never_reaches_the_rules()
    {
        var args = ProtectionHarness.Args(new Dictionary<string, string>
        {
            ["serverId"] = "local", ["command"] = Dump, [TrustedScripts.ResolvedKey] = $"local\u001f{Dump}\u001f{Approved}",
        });
        var request = new MutationRequest("host.exec", ProtectionHarness.AdminKey, MutationSurface.Mcp,
            new MutationTarget("host", "local", ServerId: "local"), args);

        Assert.False(GuardrailMutationPolicy.WithResolvedTarget(request).ContainsKey(TrustedScripts.ResolvedKey));
    }

    // ---------------------------------------------------------------- amendment 3

    [Theory]
    [InlineData("tar -xf /tmp/p.tar -C/proc/1/root/usr/local/sbin")]
    [InlineData("tar -xf /tmp/p.tar --directory=/proc/self/root/usr/local/sbin")]
    [InlineData("tar -xf /tmp/p.tar -C /proc/1/task/1/root/usr/local/sbin")]
    [InlineData("cd /proc && tar -xf /tmp/p.tar -C self/root/usr/local/sbin")]
    [InlineData("cd proc && ls")]
    [InlineData("pushd /proc/self")]
    public void C1_proc_aliases_in_option_values_threads_and_cd_are_unanalysable(string command) =>
        Assert.True(ProtectedResourceMatcher.AnalyseShell(command).Unanalyzable);

    [Theory]
    [InlineData("tar -xf /tmp/p.tar -C ~/scripts")]
    [InlineData("rsync -a /tmp/tree/ ~root/scripts/")]
    [InlineData("cp /tmp/e burgcloud-db-dump.s?")]
    [InlineData("cp /tmp/e *.sh")]
    [InlineData("rename .new '' /usr/local/sbin/burgcloud-db-dump.sh.new")]
    [InlineData("perl-rename s/x// /tmp/a")]
    public async Task C1_tilde_globs_on_the_file_name_and_renaming_tools_are_findings(string command)
    {
        var h = new ProtectionHarness(
            p => p.TrustedScripts = [new TrustedScript { ServerId = "burgcloud", Path = Dump, Sha256 = Approved }],
            host: new HashHost());

        Assert.Equal(MutationStatus.Denied, (await AsKey(h, command)).Status);
        Assert.Equal(0, h.Runs);
    }

    [Theory]
    [InlineData("ls ~")]
    [InlineData("cp /tmp/a /tmp/*.txt")]
    public async Task C1_reading_home_and_unrelated_globs_stay_possible(string command)
    {
        var h = new ProtectionHarness(
            p => p.TrustedScripts = [new TrustedScript { ServerId = "burgcloud", Path = Dump, Sha256 = Approved }],
            host: new HashHost());

        Assert.Equal(MutationStatus.Executed, (await AsKey(h, command)).Status);
    }

    [Fact]
    public void C2_the_fixed_read_requires_one_link_owner_root_and_no_foreign_write_bit()
    {
        var read = TrustedScripts.HashCommand(Dump);
        Assert.Contains("stat -c %h:%u", read, StringComparison.Ordinal);
        Assert.Contains("& 022", read, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Dump + "\n", Approved)]
    [InlineData(Dump, Approved + "\n")]
    public void C3_a_trailing_line_break_makes_an_entry_invalid(string path, string sha)
    {
        Assert.False(TrustedScripts.IsValid(new TrustedScript { ServerId = "local", Path = path, Sha256 = sha }));
        Assert.Null(TrustedScripts.CallPath(Dump + "\n"));
    }

    [Fact]
    public async Task C4_an_approved_save_writes_only_the_additions_its_call_named()
    {
        var audit = new RecordingAudit();
        var (store, dir) = await StoreAsync(audit, secondApprover: true);
        try
        {
            var approved = TrustedScripts.Additions(store.Config, Adding(store));
            var otherAdded = TrustedScripts.Additions(store.Config, Adding(store, Other));
            // The approval covered other additions than the store would write (the caller passed matching ones).
            GuardrailConfigException named, missing;
            using (ApprovedSave(approved))
                named = await Assert.ThrowsAsync<GuardrailConfigException>(() =>
                    store.SaveConfigAsync(Adding(store, Other), StoreAdmin, otherAdded));
            using (ApprovedSave())
                missing = await Assert.ThrowsAsync<GuardrailConfigException>(() =>
                    store.SaveConfigAsync(Adding(store), StoreAdmin, approved));

            Assert.Equal(GuardrailConfigProblemKind.TrustedScriptsChangedMeanwhile, named.Problem.Kind);
            Assert.Equal(GuardrailConfigProblemKind.TrustedScriptsChangedMeanwhile, missing.Problem.Kind);
            Assert.Null(store.Current.TrustedScripts);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------------------------------------------------------- fifth review

    [Theory]
    [InlineData("sh -c /tmp/unlisted.sh")]
    [InlineData("sudo sh -c /tmp/unlisted.sh")]
    [InlineData("su -c /tmp/unlisted.sh")]
    [InlineData("bash -c python3")]
    public void The_text_after_a_shells_c_is_a_command_quoted_or_not(string command)
    {
        var shell = ProtectedResourceMatcher.AnalyseShell(command);
        Assert.True(shell.Unanalyzable);
        Assert.True(shell.RunsHiddenCode);
    }

    [Theory]
    [InlineData("sh -c uptime")]
    [InlineData("sh -c 'docker ps'")]
    public void A_plain_command_after_c_stays_analysable(string command) =>
        Assert.False(ProtectedResourceMatcher.AnalyseShell(command).Unanalyzable);

    [Fact]
    public async Task A_trusted_call_cannot_start_an_unlisted_script_through_its_arguments()
    {
        var host = new HashHost();
        var h = Trusting(host);

        Assert.Equal(MutationStatus.Denied, (await AsKey(h, Dump + " sh -c /tmp/unlisted.sh")).Status);
        Assert.Equal(MutationStatus.Denied, (await AsTask(h, Dump + " sh -c /tmp/unlisted.sh")).Status);
        Assert.Equal(0, h.Runs);
    }

    [Theory]
    [InlineData("tar -xf /tmp/p.tar -C /./proc/self/root/usr/local/sbin")]
    [InlineData("tar -xf /tmp/p.tar -C //proc/1/root/usr/local/sbin")]
    [InlineData("tar -xf /tmp/p.tar -C/proc/./1/root/usr/local/sbin")]
    public void A_proc_alias_is_compared_after_normalising(string command) =>
        Assert.True(ProtectedResourceMatcher.AnalyseShell(command).Unanalyzable);

    [Fact]
    public async Task The_second_Admin_sees_server_path_and_hash_of_each_addition()
    {
        var h = new ProtectionHarness(p => p.RequireSecondApprover = true);
        var lines = Line(Approved) + "\n" + Line(Other, server: "web");

        await SaveAsync(h, lines);

        var card = Assert.Single(h.Approvals.GetPending()).ParamsJson;
        Assert.Contains(Dump, card, StringComparison.Ordinal);
        Assert.Contains(Approved, card, StringComparison.Ordinal);
        Assert.Contains(Other, card, StringComparison.Ordinal);
        Assert.Contains("| web |", card, StringComparison.Ordinal);
        Assert.DoesNotContain("withheld", card, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Additions_too_long_for_the_approval_card_are_refused()
    {
        var h = new ProtectionHarness(p => p.RequireSecondApprover = true);
        var many = string.Join("\n", Enumerable.Range(0, 40).Select(i => Line(Approved, server: $"server-{i:00}")));
        Assert.True(many.Length > TrustedScripts.MaxAdditionsChars);

        var outcome = await SaveAsync(h, many);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Empty(h.Approvals.GetPending());
        Assert.Equal(0, h.Runs);
    }

    // ---------------------------------------------------------------- sixth review

    [Theory]
    [InlineData(" python3 payload")]
    [InlineData(" make -f /tmp/evil.mk")]
    [InlineData(" perl x")]
    [InlineData(" sudo id")]
    [InlineData(" docker ps")]
    public async Task No_code_runner_among_a_trusted_calls_arguments(string arguments)
    {
        var host = new HashHost();
        var h = Trusting(host);

        Assert.Equal(MutationStatus.Denied, (await AsKey(h, Dump + arguments)).Status);
        Assert.Equal(0, h.Runs);
    }

    [Fact]
    public async Task Plain_arguments_of_a_trusted_call_still_run()
    {
        var host = new HashHost();
        var h = Trusting(host);

        Assert.Equal(MutationStatus.Executed, (await AsKey(h, Dump + " burgcloud --keep=7")).Status);
    }

    [Theory]
    [InlineData("tar -xf /tmp/p.tar -C /usr/local/sb?n")]
    [InlineData("tar -xf /tmp/p.tar -C/usr/lo*/sbin")]
    [InlineData("cp -r /tmp/tree /usr/*")]
    public async Task A_directory_glob_that_could_reach_a_listed_script_is_a_finding(string command)
    {
        var h = new ProtectionHarness(
            p => p.TrustedScripts = [new TrustedScript { ServerId = "burgcloud", Path = Dump, Sha256 = Approved }],
            host: new HashHost());

        Assert.Equal(MutationStatus.Denied, (await AsKey(h, command)).Status);
    }

    [Fact]
    public async Task The_limit_counts_the_escaped_text_the_card_stores()
    {
        var h = new ProtectionHarness(p => p.RequireSecondApprover = true);
        // Short as raw text, but every quote doubles in the stored JSON.
        var quotes = new string('"', 1300);
        var added = $"{quotes} | local | /opt/jobs/a.sh | {Approved}\n{quotes} | local | /opt/jobs/b.sh | {Approved}";
        Assert.True(added.Length <= TrustedScripts.MaxAdditionsChars);

        var outcome = await SaveAsync(h, added);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Empty(h.Approvals.GetPending());
    }
}
