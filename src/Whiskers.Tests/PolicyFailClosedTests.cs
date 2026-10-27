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

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveConfigAsync(config, Admin));

        Assert.Null(store.LoadError);
        Assert.DoesNotContain("[unclosed", store.Current.ForbiddenArgPatterns);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
