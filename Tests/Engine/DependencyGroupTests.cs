using ECAssistant.Engine;

namespace ECAssistant.Tests.Engine;

public class DependencyGroupTests
{
    [Fact]
    public void IsParallel_NoToolCalls_ReturnsFalse()
    {
        var group = new DependencyGroup { ToolCalls = new(), GroupIndex = 0 };
        Assert.False(group.IsParallel);
    }

    [Fact]
    public void IsParallel_SingleToolCall_ReturnsFalse()
    {
        var group = new DependencyGroup
        {
            ToolCalls = new List<ToolCallRequest>
            {
                new() { ToolName = "EShellAgent", Index = 0 }
            },
            GroupIndex = 0
        };
        Assert.False(group.IsParallel);
    }

    [Fact]
    public void IsParallel_MultipleToolCalls_ReturnsTrue()
    {
        var group = new DependencyGroup
        {
            ToolCalls = new List<ToolCallRequest>
            {
                new() { ToolName = "EShellAgent", Index = 0 },
                new() { ToolName = "EFileResearchTool", Index = 1 }
            },
            GroupIndex = 0
        };
        Assert.True(group.IsParallel);
    }

    [Fact]
    public void ToString_SingleCall_IncludesCallInfo()
    {
        var group = new DependencyGroup
        {
            ToolCalls = new List<ToolCallRequest>
            {
                new() { ToolName = "EShellAgent", Index = 0 }
            },
            GroupIndex = 0
        };
        var result = group.ToString();
        Assert.Contains("Group 0", result);
        Assert.Contains("1 call", result);
        Assert.Contains("EShellAgent#0", result);
    }

    [Fact]
    public void ToString_MultipleCalls_UsesPluralForm()
    {
        var group = new DependencyGroup
        {
            ToolCalls = new List<ToolCallRequest>
            {
                new() { ToolName = "EShellAgent", Index = 0 },
                new() { ToolName = "EFileResearchTool", Index = 1 }
            },
            GroupIndex = 1
        };
        var result = group.ToString();
        Assert.Contains("Group 1", result);
        Assert.Contains("2 calls", result);
        Assert.Contains("EShellAgent#0", result);
        Assert.Contains("EFileResearchTool#1", result);
    }

    [Fact]
    public void ToString_EmptyGroup_ShowsZeroCalls()
    {
        var group = new DependencyGroup { ToolCalls = new(), GroupIndex = 0 };
        var result = group.ToString();
        Assert.Contains("Group 0", result);
        Assert.Contains("0 call", result);
    }

    [Fact]
    public void GroupIndex_Default_IsZero()
    {
        var group = new DependencyGroup();
        Assert.Equal(0, group.GroupIndex);
    }

    [Fact]
    public void ToolCalls_Default_IsEmptyList()
    {
        var group = new DependencyGroup();
        Assert.NotNull(group.ToolCalls);
        Assert.Empty(group.ToolCalls);
    }
}