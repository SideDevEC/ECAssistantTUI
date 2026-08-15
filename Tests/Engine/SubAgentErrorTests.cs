using ECAssistant.Engine;
using ECAssistant.Orchestration;

namespace ECAssistant.Tests.Engine;

public class SubAgentErrorTests
{
    [Fact]
    public void DefaultValues_AllPropertiesHaveCorrectDefaults()
    {
        var error = new SubAgentError();
        Assert.Equal(SubAgentErrorKind.None, error.Kind);
        Assert.Equal("", error.Message);
        Assert.Equal("", error.AttemptedAction);
        Assert.Empty(error.SuccessfulActions);
        Assert.Empty(error.FailedActions);
        Assert.Empty(error.FilesModified);
        Assert.Equal("", error.PartialOutput);
        Assert.Equal(OrchestratorStatus.GoalAchieved, error.Status);
        Assert.Equal(0, error.RetryAttempt);
    }

    [Fact]
    public void Kind_SetValue_ReturnsSameValue()
    {
        var error = new SubAgentError { Kind = SubAgentErrorKind.Timeout };
        Assert.Equal(SubAgentErrorKind.Timeout, error.Kind);
    }

    [Fact]
    public void Message_SetValue_ReturnsSameValue()
    {
        var error = new SubAgentError { Message = "Operation failed" };
        Assert.Equal("Operation failed", error.Message);
    }

    [Fact]
    public void AttemptedAction_SetValue_ReturnsSameValue()
    {
        var error = new SubAgentError { AttemptedAction = "Build project" };
        Assert.Equal("Build project", error.AttemptedAction);
    }

    [Fact]
    public void SuccessfulActions_SetValues_ReturnsSameValues()
    {
        var actions = new List<string> { "step1", "step2" };
        var error = new SubAgentError { SuccessfulActions = actions };
        Assert.Equal(2, error.SuccessfulActions.Count);
        Assert.Contains("step1", error.SuccessfulActions);
    }

    [Fact]
    public void FailedActions_SetValues_ReturnsSameValues()
    {
        var actions = new List<string> { "failedStep1" };
        var error = new SubAgentError { FailedActions = actions };
        Assert.Single(error.FailedActions);
    }

    [Fact]
    public void FilesModified_SetValues_ReturnsSameValues()
    {
        var files = new List<string> { "file1.cs" };
        var error = new SubAgentError { FilesModified = files };
        Assert.Single(error.FilesModified);
    }

    [Fact]
    public void PartialOutput_SetValue_ReturnsSameValue()
    {
        var error = new SubAgentError { PartialOutput = "Some partial result" };
        Assert.Equal("Some partial result", error.PartialOutput);
    }

    [Fact]
    public void Status_SetValue_ReturnsSameValue()
    {
        var error = new SubAgentError { Status = OrchestratorStatus.TurnsExhausted };
        Assert.Equal(OrchestratorStatus.TurnsExhausted, error.Status);
    }

    [Fact]
    public void RetryAttempt_SetValue_ReturnsSameValue()
    {
        var error = new SubAgentError { RetryAttempt = 3 };
        Assert.Equal(3, error.RetryAttempt);
    }

    [Fact]
    public void ToStructuredString_IncludesKindAndMessage()
    {
        var error = new SubAgentError
        {
            Kind = SubAgentErrorKind.Timeout,
            Message = "Timed out waiting"
        };
        var str = error.ToStructuredString();
        Assert.Contains("Error Kind: Timeout", str);
        Assert.Contains("Message: Timed out waiting", str);
    }

    [Fact]
    public void ToStructuredString_WithAttemptedAction_IncludesAttempted()
    {
        var error = new SubAgentError
        {
            Kind = SubAgentErrorKind.ToolFailure,
            Message = "Tool broke",
            AttemptedAction = "Run shell command"
        };
        var str = error.ToStructuredString();
        Assert.Contains("Attempted: Run shell command", str);
    }

    [Fact]
    public void ToStructuredString_WithoutAttemptedAction_OmitsAttemptedLine()
    {
        var error = new SubAgentError
        {
            Kind = SubAgentErrorKind.None,
            Message = "Error"
        };
        var str = error.ToStructuredString();
        Assert.DoesNotContain("Attempted:", str);
    }

    [Fact]
    public void ToStructuredString_WithSuccessfulActions_IncludesActions()
    {
        var error = new SubAgentError
        {
            Kind = SubAgentErrorKind.Exception,
            Message = "Crashed",
            SuccessfulActions = new List<string> { "step1", "step2" }
        };
        var str = error.ToStructuredString();
        Assert.Contains("Succeeded before failure:", str);
        Assert.Contains("step1", str);
        Assert.Contains("step2", str);
    }

    [Fact]
    public void ToStructuredString_WithFailedActions_IncludesFailedActions()
    {
        var error = new SubAgentError
        {
            Kind = SubAgentErrorKind.ToolFailure,
            Message = "Failed",
            FailedActions = new List<string> { "badStep" }
        };
        var str = error.ToStructuredString();
        Assert.Contains("Failed actions:", str);
        Assert.Contains("badStep", str);
    }

    [Fact]
    public void ToStructuredString_WithFilesModified_IncludesFiles()
    {
        var error = new SubAgentError
        {
            Kind = SubAgentErrorKind.ResourceLimitExceeded,
            Message = "Too many files",
            FilesModified = new List<string> { "file1.cs", "file2.cs" }
        };
        var str = error.ToStructuredString();
        Assert.Contains("Files modified:", str);
        Assert.Contains("file1.cs", str);
        Assert.Contains("file2.cs", str);
    }

    [Fact]
    public void ToStructuredString_WithPartialOutput_IncludesPartialOutput()
    {
        var error = new SubAgentError
        {
            Kind = SubAgentErrorKind.CancelledByMainAgent,
            Message = "Cancelled",
            PartialOutput = "Half done"
        };
        var str = error.ToStructuredString();
        Assert.Contains("Partial output:", str);
        Assert.Contains("Half done", str);
    }

    [Fact]
    public void ToStructuredString_IncludesStatus()
    {
        var error = new SubAgentError
        {
            Kind = SubAgentErrorKind.TurnsExhausted,
            Message = "Out of turns",
            Status = OrchestratorStatus.TurnsExhausted
        };
        var str = error.ToStructuredString();
        Assert.Contains("Status:", str);
        Assert.Contains("TurnsExhausted", str);
    }

    [Fact]
    public void ToStructuredString_WithRetryAttempt_IncludesRetry()
    {
        var error = new SubAgentError
        {
            Kind = SubAgentErrorKind.MaxRetriesExceeded,
            Message = "Gave up",
            RetryAttempt = 3
        };
        var str = error.ToStructuredString();
        Assert.Contains("Retry attempt: 3", str);
    }

    [Fact]
    public void ToStructuredString_WithoutRetryAttempt_OmitsRetryLine()
    {
        var error = new SubAgentError
        {
            Kind = SubAgentErrorKind.None,
            Message = "Error",
            RetryAttempt = 0
        };
        var str = error.ToStructuredString();
        Assert.DoesNotContain("Retry attempt:", str);
    }

    [Fact]
    public void ToStructuredString_AllFieldsPopulated_IncludesAllSections()
    {
        var error = new SubAgentError
        {
            Kind = SubAgentErrorKind.ToolFailure,
            Message = "Complete failure",
            AttemptedAction = "Do everything",
            SuccessfulActions = new List<string> { "step1" },
            FailedActions = new List<string> { "step2" },
            FilesModified = new List<string> { "file1.cs" },
            PartialOutput = "Partial",
            Status = OrchestratorStatus.GoalAchieved,
            RetryAttempt = 2
        };
        var str = error.ToStructuredString();
        Assert.Contains("Error Kind:", str);
        Assert.Contains("Message:", str);
        Assert.Contains("Attempted:", str);
        Assert.Contains("Succeeded before failure:", str);
        Assert.Contains("Failed actions:", str);
        Assert.Contains("Files modified:", str);
        Assert.Contains("Partial output:", str);
        Assert.Contains("Status:", str);
        Assert.Contains("Retry attempt:", str);
    }
}