using Whiskers.Services.Certificates;
using Whiskers.Services.Onboarding;

namespace Whiskers.Tests;

/// <summary>
/// The allow-list patterns for values that reach a shell line on a host (client names, compose paths, the compose
/// project, the run suffix) end at the end of the text. A .NET <c>$</c> also matches before a final line break, so
/// "name\n" passed the check and its line break reached the script.
/// </summary>
public sealed class MtlsNameAnchorTests
{
    private static readonly MtlsAllowListCommands.ComposeTarget Target =
        new("dockerproxy", "/opt/dockerproxy", "/opt/dockerproxy/docker-compose.yml");

    [Theory]
    [InlineData("whiskers-client-web1\n")]
    [InlineData("web1\n")]
    public void A_client_name_with_a_trailing_line_break_is_not_a_valid_common_name(string name)
    {
        Assert.False(MtlsClientIdentity.IsValidCommonName(name));
        // Without the line break it is a valid name, so it is the line break that is refused.
        Assert.True(MtlsClientIdentity.IsValidCommonName(name.TrimEnd('\n')));
    }

    [Fact]
    public void The_allow_list_script_refuses_a_client_name_with_a_trailing_line_break()
    {
        Assert.Throws<ArgumentException>(() => MtlsAllowListCommands.Apply(Target, ["whiskers-client-web1\n"], "r1"));
        Assert.NotNull(MtlsAllowListCommands.Apply(Target, ["whiskers-client-web1"], "r1"));
    }

    [Fact]
    public void Onboarding_refuses_a_client_name_with_a_trailing_line_break()
    {
        Assert.Throws<ArgumentException>(() => OnboardingCommands.DockerProxyCompose("100.64.0.7", "whiskers-client-web1\n"));
        Assert.NotNull(OnboardingCommands.DockerProxyCompose("100.64.0.7", "whiskers-client-web1"));
    }

    [Fact]
    public void The_compose_target_and_the_run_suffix_refuse_a_trailing_line_break()
    {
        Assert.False(MtlsAllowListCommands.IsSafePath("/opt/dockerproxy/docker-compose.yml\n"));
        Assert.False(MtlsAllowListCommands.IsSafeProject("dockerproxy\n"));
        Assert.False(MtlsAllowListCommands.IsSafeSuffix("r1\n"));
        Assert.Throws<ArgumentException>(() => MtlsAllowListCommands.Apply(Target, ["whiskers-client-web1"], "r1\n"));
        Assert.Throws<ArgumentException>(() =>
            new MtlsAllowListCommands.ComposeTarget("dockerproxy\n", "/opt/dockerproxy", "/opt/dockerproxy/docker-compose.yml").Checked());
        Assert.True(MtlsAllowListCommands.IsSafePath("/opt/dockerproxy/docker-compose.yml"));
        Assert.True(MtlsAllowListCommands.IsSafeProject("dockerproxy"));
        Assert.True(MtlsAllowListCommands.IsSafeSuffix("r1"));
    }
}
