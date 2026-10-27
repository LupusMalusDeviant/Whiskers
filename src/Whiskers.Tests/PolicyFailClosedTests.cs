using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Guardrails;

namespace Whiskers.Tests;

/// <summary>
/// R1-02 / INV-25: any error while evaluating or loading the guardrail policy refuses the call. An invalid or
/// slow forbidden-argument pattern, an unreadable <c>guardrails.json</c> and an active preset that does not exist
/// all deny, instead of reading as "nothing forbidden".
/// </summary>
public sealed class PolicyFailClosedTests : IDisposable
{
    private static readonly GuardrailEngine Engine = GuardrailEngine.CreateDefault();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"guardrails-{Guid.NewGuid():N}");
    private string FilePath => Path.Combine(_dir, "guardrails.json");

    public PolicyFailClosedTests() => Directory.CreateDirectory(_dir);

    private static AgentPrincipal Admin => new(AgentPrincipalKind.WebUser, "admin", McpPermissionLevels.Admin, null,
        UserEmail: "admin@example.com");

    private static GuardrailDecision Evaluate(GuardrailPolicy policy, string tool, string level,
        params (string Key, string Value)[] args)
    {
        var dict = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value));
        var ctx = new AgentContext("sess-1", Admin, AgentOrigin.WebUi, policy);
        return Engine.Evaluate(new GuardrailRequest(tool, level, dict, ctx));
    }

    private static GuardrailPolicy Permissive() => new()
    {
        MaxAutonomousLevel = McpPermissionLevels.Admin,
        RequireConfirmationForWrites = false,
    };

    [Fact]
    public void An_invalid_pattern_denies_even_a_call_without_arguments()
    {
        var policy = Permissive();
        policy.ForbiddenArgPatterns.Add("[unclosed");

        var d = Evaluate(policy, "restart_container", McpPermissionLevels.Write);

        Assert.Equal(GuardrailVerdict.Deny, d.Verdict);
    }

    [Fact]
    public void A_pattern_that_times_out_denies()
    {
        var policy = Permissive();
        policy.ForbiddenArgPatterns.Add(@"^(a+)+$");   // catastrophic backtracking on a near-match

        var d = Evaluate(policy, "execute_command", McpPermissionLevels.Admin,
            ("command", new string('a', 40_000) + "!"));

        Assert.Equal(GuardrailVerdict.Deny, d.Verdict);
    }

    [Fact]
    public async Task An_unreadable_policy_file_puts_the_store_into_fail_closed()
    {
        await File.WriteAllTextAsync(FilePath, "{ this is not json");
        var store = new GuardrailStore(filePath: FilePath);

        await store.InitializeAsync();

        Assert.NotNull(store.LoadError);
        var current = store.Current;
        Assert.True(current.ReadOnlyMode);
        Assert.Equal(GuardrailVerdict.Deny, Evaluate(current, "restart_container", McpPermissionLevels.Write).Verdict);
        Assert.Equal(GuardrailVerdict.Deny, Evaluate(current, "list_containers", McpPermissionLevels.Read).Verdict);
        Assert.NotEmpty(Directory.GetFiles(_dir, "guardrails.json.unreadable-*"));
    }

    [Fact]
    public async Task An_active_preset_that_does_not_exist_is_not_replaced_by_another()
    {
        var config = new GuardrailConfig
        {
            ActivePreset = "Strict",
            Presets = { new GuardrailPreset { Name = "Open", Policy = Permissive() } },
        };
        await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(config));
        var store = new GuardrailStore(filePath: FilePath);

        await store.InitializeAsync();

        Assert.NotNull(store.LoadError);
        Assert.Equal(GuardrailVerdict.Deny,
            Evaluate(store.Current, "restart_container", McpPermissionLevels.Write).Verdict);   // not "Open"
    }

    [Fact]
    public async Task A_valid_save_by_an_admin_ends_fail_closed()
    {
        await File.WriteAllTextAsync(FilePath, "{ broken");
        var store = new GuardrailStore(filePath: FilePath);
        await store.InitializeAsync();

        await store.SaveConfigAsync(GuardrailConfig.SafeDefault(), Admin);

        Assert.Null(store.LoadError);
        Assert.False(store.Current.ReadOnlyMode);
        var reloaded = new GuardrailStore(filePath: FilePath);
        await reloaded.InitializeAsync();
        Assert.Null(reloaded.LoadError);
    }

    [Theory]
    [InlineData("Missing")]          // active preset absent
    [InlineData("bad-regex")]        // invalid forbidden-argument pattern
    public async Task An_invalid_configuration_is_refused_on_save(string defect)
    {
        var store = new GuardrailStore(filePath: FilePath);
        await store.InitializeAsync();
        var config = GuardrailConfig.SafeDefault();
        if (defect == "Missing") config.ActivePreset = "Missing";
        else config.Presets[0].Policy.ForbiddenArgPatterns.Add("[unclosed");

        var ex = await Assert.ThrowsAsync<GuardrailConfigException>(() => store.SaveConfigAsync(config, Admin));

        Assert.Equal(defect == "Missing" ? GuardrailConfigProblemKind.ActivePresetMissing : GuardrailConfigProblemKind.InvalidPattern,
            ex.Problem.Kind);
        Assert.Null(store.LoadError);
        Assert.DoesNotContain("[unclosed", store.Current.ForbiddenArgPatterns);
    }

    [Theory]
    [InlineData("""{"ActivePreset":"Locked","Presets":null}""")]
    [InlineData("""{"ActivePreset":"Locked","Presets":[]}""")]
    [InlineData("""{"Unrelated":true}""")]
    [InlineData("null")]
    [InlineData("""{"ActivePreset":"Locked","Presets":[],"ReadOnlyMode":false}""")]
    [InlineData("""{"ActivePreset":"Standard","Presets":[{"Name":"Standard"}]}""")]
    [InlineData("""{"ActivePreset":"Standard","Presets":[{"Name":"Standard","Policy":{}}]}""")]
    [InlineData("""{"Presets":[{"Name":"Standard","Policy":{}}]}""")]
    public async Task A_file_without_presets_that_is_no_legacy_policy_fails_closed_and_is_not_overwritten(string json)
    {
        await File.WriteAllTextAsync(FilePath, json);
        var store = new GuardrailStore(filePath: FilePath);

        await store.InitializeAsync();

        Assert.NotNull(store.LoadError);
        Assert.Equal(GuardrailVerdict.Deny,
            Evaluate(store.Current, "restart_container", McpPermissionLevels.Write).Verdict);
        Assert.Equal(json, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task A_legacy_single_policy_file_is_still_migrated()
    {
        await File.WriteAllTextAsync(FilePath, """{"ReadOnlyMode":true,"ProtectedResources":["db*"]}""");
        var store = new GuardrailStore(filePath: FilePath);

        await store.InitializeAsync();

        Assert.Null(store.LoadError);
        Assert.True(store.Current.ReadOnlyMode);
        Assert.Contains("db*", store.Current.ProtectedResources);
        // Fields the old file did not carry keep the restrictive default, not an empty one.
        Assert.Equal(GuardrailPolicy.SafeDefault().ForbiddenArgPatterns, store.Current.ForbiddenArgPatterns);
    }

    [Fact]
    public async Task A_file_written_by_the_store_loads_again()
    {
        var first = new GuardrailStore(filePath: FilePath);
        await first.InitializeAsync();
        var config = first.Config;
        config.Presets.Add(new GuardrailPreset { Name = "Second", Policy = GuardrailPolicy.SafeDefault() });
        config.ActivePreset = "Second";
        await first.SaveConfigAsync(config, Admin);

        var second = new GuardrailStore(filePath: FilePath);
        await second.InitializeAsync();

        Assert.Null(second.LoadError);
        Assert.Equal("Second", second.Config.ActivePreset);
    }

    [Theory]
    [InlineData("ForbiddenArgPatterns")]
    [InlineData("ProtectedResources")]
    [InlineData("ToolAllowList")]
    [InlineData("ToolDenyList")]
    [InlineData("ToolMode")]
    [InlineData("MaxAutonomousLevel")]
    public async Task A_policy_with_a_missing_field_fails_closed_and_stays_recoverable(string field)
    {
        var json = $$$"""{"ActivePreset":"Standard","Presets":[{"Name":"Standard","Policy":{"{{{field}}}":null}}]}""";
        await File.WriteAllTextAsync(FilePath, json);
        var store = new GuardrailStore(filePath: FilePath);

        await store.InitializeAsync();   // must not throw: startup has to reach the Guardrails page

        Assert.NotNull(store.LoadError);
        Assert.Equal(GuardrailVerdict.Deny,
            Evaluate(store.Current, "restart_container", McpPermissionLevels.Write).Verdict);
        await store.SaveConfigAsync(GuardrailConfig.SafeDefault(), Admin);
        Assert.Null(store.LoadError);
    }

    [Fact]
    public async Task A_null_entry_in_a_list_is_refused_on_save()
    {
        var store = new GuardrailStore(filePath: FilePath);
        await store.InitializeAsync();
        var config = GuardrailConfig.SafeDefault();
        config.Presets[0].Policy.ProtectedResources.Add(null!);

        var ex = await Assert.ThrowsAsync<GuardrailConfigException>(() => store.SaveConfigAsync(config, Admin));

        Assert.Equal(GuardrailConfigProblemKind.MissingValue, ex.Problem.Kind);
    }

    [Fact]
    public async Task Readers_during_recovery_never_see_a_policy_that_was_not_enforced()
    {
        await File.WriteAllTextAsync(FilePath, "{ broken");
        var store = new GuardrailStore(filePath: FilePath);
        await store.InitializeAsync();
        var recovered = GuardrailConfig.SafeDefault();
        recovered.Presets[0].Policy.ReadOnlyMode = true;   // the admin recovers with the kill switch on

        using var stop = new CancellationTokenSource();
        var weaker = 0;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                if (!store.Current.ReadOnlyMode) Interlocked.Increment(ref weaker);
        })).ToArray();
        await store.SaveConfigAsync(recovered, Admin);
        await Task.Delay(50);
        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.Equal(0, weaker);   // before: fail-closed; after: the recovered policy — both read-only
        Assert.Null(store.LoadError);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
