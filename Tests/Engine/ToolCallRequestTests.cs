using ECAssistant.Engine;

namespace ECAssistant.Tests.Engine;

public class ToolCallRequestTests
{
    [Fact]
    public void ToString_NoArgs_ReturnsFormattedString()
    {
        var req = new ToolCallRequest { ToolName = "EShellAgent", Index = 0 };
        var result = req.ToString();
        Assert.Contains("[0]", result);
        Assert.Contains("EShellAgent", result);
    }

    [Fact]
    public void ToString_WithArgs_IncludesArgs()
    {
        var req = new ToolCallRequest
        {
            ToolName = "EShellAgent",
            Index = 1,
            Args = new Dictionary<string, string?> { { "command", "dotnet build" } }
        };
        var result = req.ToString();
        Assert.Contains("[1]", result);
        Assert.Contains("EShellAgent", result);
        Assert.Contains("command=dotnet build", result);
    }

    [Fact]
    public void ToString_WithMultipleArgs_IncludesAllArgs()
    {
        var req = new ToolCallRequest
        {
            ToolName = "ECodeEditor",
            Index = 2,
            Args = new Dictionary<string, string?>
            {
                { "file", "test.cs" },
                { "action", "create" }
            }
        };
        var result = req.ToString();
        Assert.Contains("[2]", result);
        Assert.Contains("ECodeEditor", result);
        Assert.Contains("file=test.cs", result);
        Assert.Contains("action=create", result);
    }

    [Fact]
    public void ToString_WithNullToolName_IncludesEmptyToolName()
    {
        var req = new ToolCallRequest { ToolName = null, Index = 0 };
        var result = req.ToString();
        Assert.Contains("[0]", result);
        // ToolName null should still produce a valid string
        Assert.NotNull(result);
    }

    [Fact]
    public void ToString_WithLongArgValue_TruncatesValue()
    {
        var longValue = new string('x', 100);
        var req = new ToolCallRequest
        {
            ToolName = "EShellAgent",
            Index = 0,
            Args = new Dictionary<string, string?> { { "command", longValue } }
        };
        var result = req.ToString();
        // Truncate limits to 40 chars + "..."
        Assert.DoesNotContain(new string('x', 100), result);
    }

    [Fact]
    public void ToString_WithNullArgValue_ShowsEmptyValue()
    {
        var req = new ToolCallRequest
        {
            ToolName = "EShellAgent",
            Index = 0,
            Args = new Dictionary<string, string?> { { "command", null } }
        };
        var result = req.ToString();
        Assert.Contains("command=", result);
    }

    [Fact]
    public void ToString_DefaultIndex_IsZero()
    {
        var req = new ToolCallRequest { ToolName = "Test" };
        var result = req.ToString();
        Assert.Contains("[0]", result);
    }
}