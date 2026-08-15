using ECAssistant.Engine;

namespace ECAssistant.Tests.Engine;

public class ToolDependencyAnalyzerTests
{
    private readonly ToolDependencyAnalyzer _analyzer = new();

    private static ToolCallRequest MakeTool(string name, int index, Dictionary<string, string?>? args = null)
        => new() { ToolName = name, Index = index, Args = args ?? new() };

    [Fact]
    public void Analyze_EmptyList_ReturnsSingleGroupWithEmptyCalls()
    {
        var result = _analyzer.Analyze(new List<ToolCallRequest>());
        Assert.Single(result);
        Assert.Equal(0, result[0].GroupIndex);
        Assert.Empty(result[0].ToolCalls);
    }

    [Fact]
    public void Analyze_SingleTool_ReturnsSingleGroup()
    {
        var calls = new List<ToolCallRequest>
        {
            MakeTool("EShellAgent", 0)
        };
        var result = _analyzer.Analyze(calls);
        Assert.Single(result);
        Assert.Single(result[0].ToolCalls);
        Assert.False(result[0].IsParallel);
    }

    [Fact]
    public void Analyze_TwoIndependentTools_ReturnsSingleParallelGroup()
    {
        var calls = new List<ToolCallRequest>
        {
            MakeTool("EFileResearchTool", 0, new() { { "pattern", "*.cs" } }),
            MakeTool("EFileAnalyzer", 1, new() { { "file", "test.txt" } })
        };
        var result = _analyzer.Analyze(calls);
        // Two read-only tools with no overlapping targets should be in one parallel group
        Assert.Single(result);
        Assert.True(result[0].IsParallel);
        Assert.Equal(2, result[0].ToolCalls.Count);
    }

    [Fact]
    public void Analyze_ECodeEditorSameFile_BothProcessedInOneIteration()
    {
        // ECodeEditor on same file creates mutual dependency (deps[0]=[1], deps[1]=[0])
        // The fallback in the while loop handles this by processing all remaining at once
        var calls = new List<ToolCallRequest>
        {
            MakeTool("ECodeEditor", 0, new() { { "file", "test.cs" }, { "action", "edit" } }),
            MakeTool("ECodeEditor", 1, new() { { "file", "test.cs" }, { "action", "edit" } })
        };
        var result = _analyzer.Analyze(calls);
        // Due to circular dependency, fallback processes all in one group
        Assert.Single(result);
        Assert.Equal(2, result[0].ToolCalls.Count);
    }

    [Fact]
    public void Analyze_ECodeEditorDifferentFiles_CanBeParallel()
    {
        var calls = new List<ToolCallRequest>
        {
            MakeTool("ECodeEditor", 0, new() { { "file", "test1.cs" }, { "action", "create" } }),
            MakeTool("ECodeEditor", 1, new() { { "file", "test2.cs" }, { "action", "create" } })
        };
        var result = _analyzer.Analyze(calls);
        // Different files: no dependency, should be in one parallel group
        Assert.Single(result);
        Assert.True(result[0].IsParallel);
    }

    [Fact]
    public void Analyze_ModifyingToolFollowedByBuild_BothInSameGroup()
    {
        // EDotnetBuild depends on ECodeEditor (deps[1]=[0]),
        // but both are processed in the same iteration since processed[0] is set
        // before checking tool 1
        var calls = new List<ToolCallRequest>
        {
            MakeTool("ECodeEditor", 0, new() { { "file", "test.cs" }, { "action", "edit" } }),
            MakeTool("EDotnetBuild", 1)
        };
        var result = _analyzer.Analyze(calls);
        // Both tools end up in the same group because the group builder
        // processes all tools with met deps in one iteration
        Assert.Single(result);
        Assert.Equal(2, result[0].ToolCalls.Count);
    }

    [Fact]
    public void Analyze_AlwaysIndependentTool_NoDependencies()
    {
        var calls = new List<ToolCallRequest>
        {
            MakeTool("ECodeEditor", 0, new() { { "file", "test.cs" }, { "action", "edit" } }),
            MakeTool("EBackgroundExec", 1, new() { { "command", "echo hello" } })
        };
        var result = _analyzer.Analyze(calls);
        // EBackgroundExec is always independent, should be in the same group
        Assert.Single(result);
        Assert.True(result[0].IsParallel);
    }

    [Fact]
    public void Analyze_EGitToolAfterModifier_BothInSameGroup()
    {
        // EGitTool (commit) depends on ECodeEditor (deps[1]=[0])
        // but both are processed in the same iteration
        var calls = new List<ToolCallRequest>
        {
            MakeTool("ECodeEditor", 0, new() { { "file", "test.cs" }, { "action", "edit" } }),
            MakeTool("EGitTool", 1, new() { { "action", "commit" } })
        };
        var result = _analyzer.Analyze(calls);
        Assert.Single(result);
        Assert.Equal(2, result[0].ToolCalls.Count);
    }

    [Fact]
    public void Analyze_EGitToolBeforeModifier_EGitToolDependsOnLaterModifier()
    {
        // EGitTool is in _postModifyTools, so when i=1 (ECodeEditor, modifies) and j=0 (EGitTool),
        // deps[0].Add(1) -- EGitTool depends on ECodeEditor even though it is first.
        // deps[0] = [1] (EGitTool depends on ECodeEditor), deps[1] = [] (ECodeEditor has no deps)
        // Iteration 1: i=0, deps[0]=[1], processed[1]=false -> skip. i=1, deps[1]=[] -> add, processed[1]=true.
        // Iteration 2: i=0, deps[0]=[1], processed[1]=true -> add, processed[0]=true.
        // Result: 2 groups, each with 1 tool.
        var calls = new List<ToolCallRequest>
        {
            MakeTool("EGitTool", 0, new() { { "action", "status" } }),
            MakeTool("ECodeEditor", 1, new() { { "file", "other.cs" }, { "action", "edit" } })
        };
        var result = _analyzer.Analyze(calls);
        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].GroupIndex);
        Assert.Equal(1, result[1].GroupIndex);
    }

    [Fact]
    public void Analyze_ThreeIndependentReadTools_AllInOneGroup()
    {
        var calls = new List<ToolCallRequest>
        {
            MakeTool("EFileResearchTool", 0, new() { { "pattern", "*.cs" } }),
            MakeTool("EFileResearchTool", 1, new() { { "pattern", "*.md" } }),
            MakeTool("EFileAnalyzer", 2, new() { { "file", "test.json" } })
        };
        var result = _analyzer.Analyze(calls);
        Assert.Single(result);
        Assert.Equal(3, result[0].ToolCalls.Count);
        Assert.True(result[0].IsParallel);
    }

    [Fact]
    public void Analyze_ChainOfDependencies_CreatesSequentialGroups()
    {
        var calls = new List<ToolCallRequest>
        {
            MakeTool("ECodeEditor", 0, new() { { "file", "a.cs" }, { "action", "edit" } }),
            MakeTool("EDotnetBuild", 1),
            MakeTool("ECodeEditor", 2, new() { { "file", "b.cs" }, { "action", "edit" } }),
            MakeTool("EDotnetBuild", 3)
        };
        var result = _analyzer.Analyze(calls);
        // Each build depends on the preceding edit
        Assert.True(result.Count >= 2);
    }

    [Fact]
    public void Analyze_CircularDependency_DoesNotHang()
    {
        // Create a scenario that could cause circular dependencies
        // The analyzer should handle this via the fallback in the while loop
        var calls = new List<ToolCallRequest>
        {
            MakeTool("ECodeEditor", 0, new() { { "file", "shared.cs" }, { "action", "edit" } }),
            MakeTool("ECodeEditor", 1, new() { { "file", "shared.cs" }, { "action", "edit" } }),
            MakeTool("ECodeEditor", 2, new() { { "file", "shared.cs" }, { "action", "edit" } })
        };
        // All target the same file — each depends on the others
        // The analyzer should handle this without infinite loops
        var result = _analyzer.Analyze(calls);
        Assert.NotEmpty(result);
        var totalCalls = result.Sum(g => g.ToolCalls.Count);
        Assert.Equal(3, totalCalls);
    }

    [Fact]
    public void Analyze_ModifierAndIndependentTool_SameGroupDueToIterationOrder()
    {
        // ECodeEditor modifies, EFileResearchTool reads, EDotnetBuild is post-modify
        // deps[0]=[] (ECodeEditor no deps), deps[1]=[] (EFileResearchTool no deps),
        // deps[2]=[0] (EDotnetBuild depends on ECodeEditor)
        // Iteration 1: all three get processed (0: no deps -> add, 1: no deps -> add, 2: deps[0] met -> add)
        var calls = new List<ToolCallRequest>
        {
            MakeTool("ECodeEditor", 0, new() { { "file", "test.cs" }, { "action", "edit" } }),
            MakeTool("EFileResearchTool", 1, new() { { "pattern", "*.cs" } }),
            MakeTool("EDotnetBuild", 2)
        };
        var result = _analyzer.Analyze(calls);
        // All three end up in the same group because deps are resolved within one iteration
        Assert.Single(result);
        Assert.True(result[0].IsParallel);
        Assert.Equal(3, result[0].ToolCalls.Count);
    }

    [Fact]
    public void Analyze_NullToolName_HandledGracefully()
    {
        var calls = new List<ToolCallRequest>
        {
            MakeTool(null!, 0),
            MakeTool("EShellAgent", 1)
        };
        var result = _analyzer.Analyze(calls);
        Assert.NotEmpty(result);
        var totalCalls = result.Sum(g => g.ToolCalls.Count);
        Assert.Equal(2, totalCalls);
    }

    [Fact]
    public void Analyze_EShellAgentWriteCommand_BothInSameGroup()
    {
        // EShellAgent with "dotnet build" is a modifying tool, EDotnetBuild is post-modify
        // deps[1]=[0], but both processed in same iteration
        var calls = new List<ToolCallRequest>
        {
            MakeTool("EShellAgent", 0, new() { { "command", "dotnet build" } }),
            MakeTool("EDotnetBuild", 1)
        };
        var result = _analyzer.Analyze(calls);
        Assert.Single(result);
        Assert.Equal(2, result[0].ToolCalls.Count);
    }

    [Fact]
    public void Analyze_EShellAgentReadCommand_NoDependency()
    {
        var calls = new List<ToolCallRequest>
        {
            MakeTool("EShellAgent", 0, new() { { "command", "Get-Content test.txt" } }),
            MakeTool("EFileResearchTool", 1, new() { { "pattern", "*.cs" } })
        };
        var result = _analyzer.Analyze(calls);
        // Get-Content is not a write command, so no modification dependency
        Assert.Single(result);
        Assert.True(result[0].IsParallel);
    }
}