using Whiskers.Mcp.Tools;
using Whiskers.Models;

namespace Whiskers.Tests;

// Input-validation helpers for Bean Whiskers-izcu: MIT-6 project-name safety (here) and, next,
// NIED-2 container resolution. Pure static helpers → tested directly, no MCP stubs needed.
public class McpInputValidationTests
{
    // ---------------------------------------------------------------- MIT-6: safe deploy project name

    [Theory]
    [InlineData("my-app")]
    [InlineData("stack_1")]
    [InlineData("a.b.c")]
    [InlineData("Web42")]
    public void IsSafeProjectName_accepts_normal_names(string name)
        => Assert.True(McpInputValidation.IsSafeProjectName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("a..b")]         // dots pass the charset — the explicit ".." guard must still reject it
    [InlineData("../etc")]
    [InlineData("-x")]           // leading dash = would-be option/flag
    [InlineData(".hidden")]      // leading non-alphanumeric
    [InlineData("a/b")]          // path separator
    [InlineData("a b")]          // whitespace
    public void IsSafeProjectName_rejects_traversal_and_unsafe_names(string? name)
        => Assert.False(McpInputValidation.IsSafeProjectName(name));

    // ---------------------------------------------------------------- NIED-2: container resolution

    private static ContainerInfo C(string id, string name) => new() { Id = id, Name = name };

    [Fact]
    public void Resolve_exact_id_or_name_wins()
    {
        var list = new List<ContainerInfo> { C("abc123", "web"), C("abcdef", "db") };
        Assert.Equal("abc123", McpInputValidation.Resolve(list, "abc123").Container?.Id);
        Assert.Equal("abcdef", McpInputValidation.Resolve(list, "db").Container?.Id);
    }

    [Fact]
    public void Resolve_unique_prefix_resolves()
    {
        var list = new List<ContainerInfo> { C("abc123", "web"), C("xyz789", "db") };
        var (container, error) = McpInputValidation.Resolve(list, "abc");
        Assert.Null(error);
        Assert.Equal("abc123", container?.Id);
    }

    [Fact]
    public void Resolve_ambiguous_prefix_returns_error_and_no_container()
    {
        var list = new List<ContainerInfo> { C("abc123", "web"), C("abc999", "db") };
        var (container, error) = McpInputValidation.Resolve(list, "abc");
        Assert.Null(container);
        Assert.Contains("mbiguous", error); // "Ambiguous ..."
    }

    [Fact]
    public void Resolve_no_match_returns_error_never_the_raw_id()
    {
        var list = new List<ContainerInfo> { C("abc123", "web") };
        var (container, error) = McpInputValidation.Resolve(list, "nope");
        Assert.Null(container);
        Assert.NotNull(error);
    }
}
