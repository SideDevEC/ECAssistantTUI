using ECAssistant.Engine;
using ECAssistant.Orchestration;

namespace ECAssistant.Tests.Engine;

public class SubAgentResultTests
{
    [Fact]
    public void DefaultValues_AllPropertiesHaveCorrectDefaults()
    {
        var result = new SubAgentResult();
        Assert.False(result.Succeeded);
        Assert.Equal("", result.FinalOutput);
        Assert.Equal(0, result.ToolCallsMade);
        Assert.Equal(TimeSpan.Zero, result.Duration);
        Assert.Null(result.Error);
        Assert.Empty(result.FilesCreated);
        Assert.Empty(result.FilesModified);
        Assert.Empty(result.ToolCallLog);
    }

    [Fact]
    public void ErrorString_WithError_ReturnsErrorMessage()
    {
        var result = new SubAgentResult
        {
            Succeeded = false,
            Error = new SubAgentError { Message = "Something went wrong" }
        };
        Assert.Equal("Something went wrong", result.ErrorString);
    }

    [Fact]
    public void ErrorString_WithoutErrorAndSucceeded_ReturnsEmpty()
    {
        var result = new SubAgentResult { Succeeded = true };
        Assert.Equal("", result.ErrorString);
    }

    [Fact]
    public void ErrorString_WithoutErrorAndFailed_ReturnsUnknownError()
    {
        var result = new SubAgentResult { Succeeded = false };
        Assert.Equal("Unknown error", result.ErrorString);
    }

    [Fact]
    public void ToContextString_Succeeded_ReturnsSuccessFormat()
    {
        var result = new SubAgentResult
        {
            Succeeded = true,
            ToolCallsMade = 5,
            Duration = TimeSpan.FromSeconds(3.5),
            FinalOutput = "Task completed"
        };
        var str = result.ToContextString();
        Assert.Contains("✅", str);
        Assert.Contains("succeeded", str);
        Assert.Contains("5 tool calls", str);
        Assert.Contains("3.5s", str);
        Assert.Contains("Task completed", str);
    }

    [Fact]
    public void ToContextString_FailedWithNoError_ReturnsFailureFormat()
    {
        var result = new SubAgentResult
        {
            Succeeded = false,
            Duration = TimeSpan.FromSeconds(2.0)
        };
        var str = result.ToContextString();
        Assert.Contains("❌", str);
        Assert.Contains("failed", str);
        Assert.Contains("2.0s", str);
    }

    [Fact]
    public void ToContextString_FailedWithError_IncludesErrorDetails()
    {
        var result = new SubAgentResult
        {
            Succeeded = false,
            Duration = TimeSpan.FromSeconds(1.5),
            Error = new SubAgentError
            {
                Kind = SubAgentErrorKind.Timeout,
                Message = "Timed out",
                Status = OrchestratorStatus.TurnsExhausted
            }
        };
        var str = result.ToContextString();
        Assert.Contains("❌", str);
        Assert.Contains("Error Kind: Timeout", str);
        Assert.Contains("Message: Timed out", str);
    }

    [Fact]
    public void ToContextString_FailedWithFilesCreated_IncludesFilesCreated()
    {
        var result = new SubAgentResult
        {
            Succeeded = false,
            Duration = TimeSpan.FromSeconds(1.0),
            FilesCreated = new List<string> { "file1.cs", "file2.cs" }
        };
        var str = result.ToContextString();
        Assert.Contains("Files created (partial)", str);
        Assert.Contains("file1.cs", str);
        Assert.Contains("file2.cs", str);
    }

    [Fact]
    public void ToContextString_FailedWithFilesModified_IncludesFilesModified()
    {
        var result = new SubAgentResult
        {
            Succeeded = false,
            Duration = TimeSpan.FromSeconds(1.0),
            FilesModified = new List<string> { "mod1.cs" }
        };
        var str = result.ToContextString();
        Assert.Contains("Files modified (partial)", str);
        Assert.Contains("mod1.cs", str);
    }

    [Fact]
    public void ToContextString_FailedWithPartialOutput_IncludesPartialOutput()
    {
        var result = new SubAgentResult
        {
            Succeeded = false,
            Duration = TimeSpan.FromSeconds(1.0),
            FinalOutput = "Partial work done"
        };
        var str = result.ToContextString();
        Assert.Contains("Partial output:", str);
        Assert.Contains("Partial work done", str);
    }

    [Fact]
    public void ToContextString_Succeeded_NoFilesOrError_OnlySuccessLine()
    {
        var result = new SubAgentResult
        {
            Succeeded = true,
            ToolCallsMade = 0,
            Duration = TimeSpan.Zero,
            FinalOutput = "Done"
        };
        var str = result.ToContextString();
        Assert.Contains("✅", str);
        Assert.DoesNotContain("❌", str);
        Assert.DoesNotContain("Files created", str);
        Assert.DoesNotContain("Files modified", str);
    }

    [Fact]
    public void ToolCallsMade_SetValue_ReturnsSameValue()
    {
        var result = new SubAgentResult { ToolCallsMade = 42 };
        Assert.Equal(42, result.ToolCallsMade);
    }

    [Fact]
    public void Duration_SetValue_ReturnsSameValue()
    {
        var ts = TimeSpan.FromMinutes(2);
        var result = new SubAgentResult { Duration = ts };
        Assert.Equal(ts, result.Duration);
    }

    [Fact]
    public void FilesCreated_SetValues_ReturnsSameValues()
    {
        var files = new List<string> { "a.cs", "b.cs", "c.cs" };
        var result = new SubAgentResult { FilesCreated = files };
        Assert.Equal(3, result.FilesCreated.Count);
    }

    [Fact]
    public void ToolCallLog_SetValues_ReturnsSameValues()
    {
        var log = new List<string> { "call1", "call2" };
        var result = new SubAgentResult { ToolCallLog = log };
        Assert.Equal(2, result.ToolCallLog.Count);
    }
}