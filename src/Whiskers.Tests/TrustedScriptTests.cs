using System.Text.Json;
using Whiskers.Configuration;
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
        Assert.All(host.Commands, c => Assert.Equal($"local: sha256sum -- '{Dump}'", c));
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
        Dictionary<string, JsonElement> Args(string verified) => ProtectionHarness.Args(new Dictionary<string, string>
        {
            ["command"] = Dump, [TrustedScripts.ResolvedKey] = verified,
        });

        Assert.Equal(Dump, TrustedScripts.TrustedCallOf(Args($"local\u001f{Dump}\u001f{Approved}"), policy));
        Assert.Null(TrustedScripts.TrustedCallOf(Args($"local\u001f{Dump}\u001f{Other}"), policy));      // other hash
        Assert.Null(TrustedScripts.TrustedCallOf(Args($"web\u001f{Dump}\u001f{Approved}"), policy));      // other server
        Assert.Null(TrustedScripts.TrustedCallOf(Args($"local\u001f/opt/x.sh\u001f{Approved}"), policy)); // not listed
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
}
