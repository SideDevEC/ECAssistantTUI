using ECAssistant.Engine;
using ECAssistant.Tools;

namespace ECAssistant.Tests.Engine;

public class StepMapperTests
{
    [Fact]
    public void ExecutionPlan_Default_IsInvalidWithNoCalls()
    {
        var plan = new ExecutionPlan();
        Assert.False(plan.IsValid);
        Assert.Empty(plan.Calls);
    }

    [Fact]
    public void ExecutionPlan_ToPromptString_InvalidPlan_ReturnsNoPlanMessage()
    {
        var plan = new ExecutionPlan { IsValid = false };
        var result = plan.ToPromptString();
        Assert.Contains("No execution plan", result);
    }

    [Fact]
    public void ExecutionPlan_ToPromptString_EmptyCalls_ReturnsNoPlanMessage()
    {
        var plan = new ExecutionPlan { IsValid = true, Calls = new() };
        var result = plan.ToPromptString();
        Assert.Contains("No execution plan", result);
    }

    [Fact]
    public void ExecutionPlan_ToPromptString_ValidPlan_ReturnsFormattedPlan()
    {
        var plan = new ExecutionPlan
        {
            IsValid = true,
            Calls = new List<PlannedToolCall>
            {
                new()
                {
                    ToolName = "EShellAgent",
                    Description = "Build the project",
                    Args = new() { { "command", "dotnet build" } },
                    CoversSubTasks = new() { 0 }
                }
            }
        };
        var result = plan.ToPromptString();
        Assert.Contains("EXECUTION PLAN", result);
        Assert.Contains("EShellAgent", result);
        Assert.Contains("dotnet build", result);
        Assert.Contains("Build the project", result);
    }

    [Fact]
    public void ExecutionPlan_ToPromptString_MultipleCalls_ShowsAllCalls()
    {
        var plan = new ExecutionPlan
        {
            IsValid = true,
            Calls = new List<PlannedToolCall>
            {
                new()
                {
                    ToolName = "EShellAgent",
                    Description = "Build",
                    Args = new() { { "command", "dotnet build" } },
                    CoversSubTasks = new() { 0 }
                },
                new()
                {
                    ToolName = "ECodeEditor",
                    Description = "Edit file",
                    Args = new() { { "file", "test.cs" }, { "action", "edit" } },
                    CoversSubTasks = new() { 1 }
                }
            }
        };
        var result = plan.ToPromptString();
        Assert.Contains("Call 1", result);
        Assert.Contains("Call 2", result);
        Assert.Contains("EShellAgent", result);
        Assert.Contains("ECodeEditor", result);
    }

    [Fact]
    public void ExecutionPlan_ToPromptString_WithCovers_ShowsStepNumbers()
    {
        var plan = new ExecutionPlan
        {
            IsValid = true,
            Calls = new List<PlannedToolCall>
            {
                new()
                {
                    ToolName = "EShellAgent",
                    Args = new(),
                    CoversSubTasks = new() { 0, 1, 2 }
                }
            }
        };
        var result = plan.ToPromptString();
        Assert.Contains("Covers steps: 1, 2, 3", result);
    }

    [Fact]
    public void PlannedToolCall_Default_HasEmptyValues()
    {
        var call = new PlannedToolCall();
        Assert.Equal("", call.ToolName);
        Assert.Equal("", call.Description);
        Assert.Empty(call.Args);
        Assert.Empty(call.CoversSubTasks);
    }

    [Fact]
    public void PlannedToolCall_WithValues_PreservesValues()
    {
        var call = new PlannedToolCall
        {
            ToolName = "EShellAgent",
            Description = "Test",
            Args = new() { { "key", "value" } },
            CoversSubTasks = new() { 0, 1 }
        };
        Assert.Equal("EShellAgent", call.ToolName);
        Assert.Equal("Test", call.Description);
        Assert.Equal("value", call.Args["key"]);
        Assert.Equal(2, call.CoversSubTasks.Count);
    }
}