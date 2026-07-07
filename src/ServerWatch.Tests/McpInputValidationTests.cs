using ServerWatch.Mcp.Tools;

namespace ServerWatch.Tests;

// Input-validation helpers for Bean ServerWatch-izcu: MIT-6 project-name safety (here) and, next,
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
}
