using ECAssistant.Engine;

namespace ECAssistant.Tests.Engine;

public class FailureEntryTests
{
    [Fact]
    public void DefaultValues_AllPropertiesHaveCorrectDefaults()
    {
        var entry = new FailureEntry();
        Assert.Equal("", entry.ToolName);
        Assert.Equal("", entry.ErrorMessage);
        Assert.Equal("", entry.Command);
        Assert.Equal(default(DateTime), entry.Timestamp);
    }

    [Fact]
    public void ToolName_SetValue_ReturnsSameValue()
    {
        var entry = new FailureEntry { ToolName = "EShellAgent" };
        Assert.Equal("EShellAgent", entry.ToolName);
    }

    [Fact]
    public void ErrorMessage_SetValue_ReturnsSameValue()
    {
        var entry = new FailureEntry { ErrorMessage = "Command not found" };
        Assert.Equal("Command not found", entry.ErrorMessage);
    }

    [Fact]
    public void Command_SetValue_ReturnsSameValue()
    {
        var entry = new FailureEntry { Command = "dotnet build --invalid" };
        Assert.Equal("dotnet build --invalid", entry.Command);
    }

    [Fact]
    public void Timestamp_SetValue_ReturnsSameValue()
    {
        var ts = new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var entry = new FailureEntry { Timestamp = ts };
        Assert.Equal(ts, entry.Timestamp);
    }

    [Fact]
    public void Timestamp_SetNow_ReturnsCurrentTime()
    {
        var now = DateTime.Now;
        var entry = new FailureEntry { Timestamp = now };
        Assert.Equal(now, entry.Timestamp);
    }

    [Fact]
    public void AllProperties_SetTogether_ReturnAllValues()
    {
        var entry = new FailureEntry
        {
            ToolName = "EGitTool",
            ErrorMessage = "Merge conflict",
            Command = "git merge feature",
            Timestamp = new DateTime(2025, 6, 1, 12, 0, 0)
        };
        Assert.Equal("EGitTool", entry.ToolName);
        Assert.Equal("Merge conflict", entry.ErrorMessage);
        Assert.Equal("git merge feature", entry.Command);
        Assert.Equal(new DateTime(2025, 6, 1, 12, 0, 0), entry.Timestamp);
    }
}